using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace PlexRequest;

public sealed class SonarrClient
{
    private readonly HttpClient _http;
    private readonly string _rootTv;
    private readonly string _rootAnime;
    private readonly string _profileName;

    private int _qualityProfileId;
    private readonly int _releaseCheckMinutes;
    private readonly int _staleAfterDays;

    private List<SonarrSeries> _existingSeries = new();
    private readonly Dictionary<int, DateTime> _lastReleaseCheckUtc = new();
    private readonly Dictionary<int, DateTime> _firstNoReleaseSeenUtc = new();

    public SonarrClient(string baseUrl, string apiKey, string rootTv, string rootAnime, string profileName, int releaseCheckMinutes, int staleAfterDays)
    {
        _rootTv = rootTv;
        _rootAnime = rootAnime;
        _profileName = profileName;
        _releaseCheckMinutes = releaseCheckMinutes;
        _staleAfterDays = staleAfterDays;

        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        _http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

    }

    public void Initialize()
    {
        var roots = GetJson<List<SonarrRootFolder>>("api/v3/rootfolder") ?? new();
        if (!HasRoot(roots, _rootTv)) throw new Exception($"Sonarr root folder not found: {_rootTv}");
        if (!HasRoot(roots, _rootAnime)) throw new Exception($"Sonarr root folder not found: {_rootAnime}");

        var profiles = GetJson<List<SonarrQualityProfile>>("api/v3/qualityprofile") ?? new();
        if (profiles.Count == 0) throw new Exception("No Sonarr quality profiles found.");
        _qualityProfileId = PickProfileId(profiles, _profileName, "Sonarr");

        _existingSeries = GetJson<List<SonarrSeries>>("api/v3/series") ?? new();
    }

    /// <summary>
    /// TV/Anime use TVDB IDs in Sonarr. We require TVDB ID in sheet column C for TV/ANIME rows.
    /// </summary>
    public (string result, string? status, RequestAction action) ProcessSeriesByTvdbId(int tvdbId, string titleForDisplay, string typeUpper)
    {
        var existing = _existingSeries.FirstOrDefault(s => s.TvdbId == tvdbId);
        if (existing != null)
        {
            return DescribeProgress(existing);
        }

        var (addedResult, addedAction) = AddAndSearch(titleForDisplay, typeUpper, tvdbId);

        if (addedAction == RequestAction.None)
            return (addedResult, null, RequestAction.None);

        return (addedResult, null, RequestAction.Added);
    }


    private (string result, string? status, RequestAction action) DescribeProgress(SonarrSeries existing)
    {
        // Use episodes for accurate completion, ignoring specials (season 0)
        var episodes = GetEpisodes(existing.Id);

        var normal = episodes.Where(e => e.SeasonNumber > 0).ToList();
        if (normal.Count == 0)
            return ("Already added in Sonarr (no episodes returned)", null, RequestAction.Updated);

        var have = normal.Count(e => e.HasFile);
        var total = normal.Count;

        var pct = (total == 0) ? 0.0 : (have * 100.0 / total);

        if (have >= total)
            return ("Complete in Sonarr", "DONE", RequestAction.Completed);

        if (existing.NextAiring != null)
        {
            var local = existing.NextAiring.Value.ToLocalTime();
            return ($"In progress ({have}/{total}, {pct:0.0}%) — Next episode airs {local:MMM d, yyyy}", null, RequestAction.Updated);
        }

        var (msg, stale) = MaybeNoReleasesMessage(existing.Id);
        if (!string.IsNullOrWhiteSpace(msg))
        {
            if (stale)
                return (msg, "STALE", RequestAction.Stale);

            return ($"In progress ({have}/{total}, {pct:0.0}%) — {msg}", null, RequestAction.Updated);
        }

        return ($"In progress ({have}/{total}, {pct:0.0}%) — Waiting for next episode", null, RequestAction.Updated);
    }


    private (string result, RequestAction action) AddAndSearch(string title, string typeUpper, int tvdbId)
    {
        var chosenRoot = (typeUpper == "ANIME") ? _rootAnime : _rootTv;
        var seriesType = (typeUpper == "ANIME") ? "anime" : "standard";

        var addReq = new SonarrAddSeriesRequest
        {
            Title = title,
            TvdbId = tvdbId,
            QualityProfileId = _qualityProfileId,
            RootFolderPath = chosenRoot,
            SeriesType = seriesType,
            Monitored = true,
            SeasonFolder = true,
            AddOptions = new SonarrAddOptions { SearchForMissingEpisodes = true }
        };

        var added = PostJson<SonarrSeries>("api/v3/series", addReq);
        if (added == null) return ("Failed to add series (unknown error)", RequestAction.None);

        PostJson<object>("api/v3/command", new SonarrCommandRequest { Name = "SeriesSearch", SeriesId = added.Id });

        _existingSeries.Add(added);
        return ("Added to Sonarr + searching", RequestAction.Added);
    }

    public string? GetCanonicalTitleByTvdbId(int tvdbId)
    {
        // Sonarr supports lookup by tvdb:<id>
        var term = Uri.EscapeDataString($"tvdb:{tvdbId}");
        var list = GetJson<List<SonarrLookupSeries>>($"api/v3/series/lookup?term={term}");
        return (list != null && list.Count > 0) ? list[0].Title : null;
    }

    private (string? message, bool stale) MaybeNoReleasesMessage(int seriesId)
    {
        var now = DateTime.UtcNow;

        // Throttle indexer-backed release calls
        if (_lastReleaseCheckUtc.TryGetValue(seriesId, out var last) &&
            (now - last).TotalMinutes < _releaseCheckMinutes)
        {
            return (null, false);
        }

        _lastReleaseCheckUtc[seriesId] = now;

        var releases = GetJson<List<SonarrRelease>>($"api/v3/release?seriesId={seriesId}") ?? new();

        if (releases.Count > 0)
        {
            _firstNoReleaseSeenUtc.Remove(seriesId);
            return (null, false);  // throttled
        }

        // First time we saw "no releases"
        if (!_firstNoReleaseSeenUtc.ContainsKey(seriesId))
            _firstNoReleaseSeenUtc[seriesId] = now;

        var ageDays = (now - _firstNoReleaseSeenUtc[seriesId]).TotalDays;

        if (ageDays >= _staleAfterDays)
            return ("No releases found — marked STALE", true);

        return ("No releases found yet (monitoring)", false);
    }

    public void DebugEpisodes(SonarrSeries series)
    {
        var episodes = GetEpisodes(series.Id);

        Console.WriteLine($"DEBUG Episodes for '{series.Title}' (seriesId={series.Id}, tvdbId={series.TvdbId}): total={episodes.Count}");

        var bySeason = episodes
            .GroupBy(e => e.SeasonNumber)
            .OrderBy(g => g.Key);

        foreach (var g in bySeason)
        {
            var total = g.Count();
            var have = g.Count(e => e.HasFile);
            Console.WriteLine($"  Season {g.Key}: {have}/{total} hasFile");
        }
    }


    // ---------- DTOs ----------
    private sealed class SonarrRootFolder { [JsonPropertyName("path")] public string? Path { get; set; } }
    private sealed class SonarrQualityProfile { [JsonPropertyName("id")] public int Id { get; set; } [JsonPropertyName("name")] public string? Name { get; set; } }
    private sealed class SonarrRelease { [JsonPropertyName("title")] public string? Title { get; set; } }


    private sealed class SonarrEpisode
    {
        [JsonPropertyName("seasonNumber")] public int SeasonNumber { get; set; }
        [JsonPropertyName("episodeNumber")] public int EpisodeNumber { get; set; }
        [JsonPropertyName("hasFile")] public bool HasFile { get; set; }
    }
    private List<SonarrEpisode> GetEpisodes(int seriesId)
    {
        return GetJson<List<SonarrEpisode>>($"api/v3/episode?seriesId={seriesId}") ?? new();
    }

    private sealed class SonarrLookupSeries
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("tvdbId")] public int? TvdbId { get; set; }
    }

    public sealed class SonarrSeriesStatistics
    {
        [JsonPropertyName("episodeCount")] public int EpisodeCount { get; set; }
        [JsonPropertyName("episodeFileCount")] public int EpisodeFileCount { get; set; }
        [JsonPropertyName("percentOfEpisodes")] public decimal PercentOfEpisodes { get; set; }
    }

    public sealed class SonarrSeries
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("tvdbId")] public int? TvdbId { get; set; }
        [JsonPropertyName("statistics")] public SonarrSeriesStatistics? Statistics { get; set; }
        [JsonPropertyName("nextAiring")] public DateTime? NextAiring { get; set; }
    }

    private sealed class SonarrAddOptions { [JsonPropertyName("searchForMissingEpisodes")] public bool SearchForMissingEpisodes { get; set; } = true; }
    private sealed class SonarrAddSeriesRequest
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("tvdbId")] public int? TvdbId { get; set; }
        [JsonPropertyName("qualityProfileId")] public int QualityProfileId { get; set; }
        [JsonPropertyName("rootFolderPath")] public string? RootFolderPath { get; set; }
        [JsonPropertyName("monitored")] public bool Monitored { get; set; } = true;
        [JsonPropertyName("seasonFolder")] public bool SeasonFolder { get; set; } = true;
        [JsonPropertyName("seriesType")] public string SeriesType { get; set; } = "standard";
        [JsonPropertyName("addOptions")] public SonarrAddOptions AddOptions { get; set; } = new();
    }

    private sealed class SonarrCommandRequest { [JsonPropertyName("name")] public string? Name { get; set; } [JsonPropertyName("seriesId")] public int? SeriesId { get; set; } }

    // ---------- helpers ----------
    private static bool HasRoot(List<SonarrRootFolder> roots, string path)
    {
        static string Norm(string p) => (p ?? "").Trim().TrimEnd('/');
        var target = Norm(path);
        return roots.Any(r => string.Equals(Norm(r.Path ?? ""), target, StringComparison.Ordinal));
    }

    private static int PickProfileId(List<SonarrQualityProfile> profiles, string preferredName, string systemName)
    {
        var match = profiles.FirstOrDefault(p => string.Equals(p.Name?.Trim(), preferredName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match != null) return match.Id;

        Console.WriteLine($"Warning: {systemName} quality profile '{preferredName}' not found. Falling back to first profile.");
        return profiles[0].Id;
    }

    private T? GetJson<T>(string path)
    {
        try { return _http.GetFromJsonAsync<T>(path).GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            Console.WriteLine($"HTTP GET failed: {path} :: {ex.Message}");
            return default;
        }
    }

    private T? PostJson<T>(string path, object body)
    {
        try
        {
            var resp = _http.PostAsJsonAsync(path, body).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
            {
                var txt = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Console.WriteLine($"HTTP POST failed: {path} :: {(int)resp.StatusCode} {resp.ReasonPhrase} :: {txt}");
                return default;
            }
            if (typeof(T) == typeof(object)) return (T)(object)new object();
            return resp.Content.ReadFromJsonAsync<T>().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"HTTP POST failed: {path} :: {ex.Message}");
            return default;
        }
    }
}
