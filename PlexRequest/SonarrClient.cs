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
    private List<SonarrSeries> _existingSeries = new();

    public SonarrClient(string baseUrl, string apiKey, string rootTv, string rootAnime, string profileName)
    {
        _rootTv = rootTv;
        _rootAnime = rootAnime;
        _profileName = profileName;

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

    public SonarrSeries? FindByTvdbId(int tvdbId)
        => _existingSeries.FirstOrDefault(s => s.TvdbId == tvdbId);

    public (string result, string? status) DescribeProgress(SonarrSeries existing)
    {
        // Use episodes for accurate completion, ignoring specials (season 0)
        var episodes = GetEpisodes(existing.Id);

        var normal = episodes.Where(e => e.SeasonNumber > 0).ToList();
        if (normal.Count == 0)
            return ("Already added in Sonarr (no episodes returned)", null);

        var have = normal.Count(e => e.HasFile);
        var total = normal.Count;

        var pct = (total == 0) ? 0.0 : (have * 100.0 / total);

        if (have >= total)
            return ("Complete in Sonarr", "DONE");

        if (existing.NextAiring != null)
        {
            var local = existing.NextAiring.Value.ToLocalTime();
            return ($"In progress ({have}/{total}, {pct:0.0}%) — Next episode airs {local:MMM d yy}", null);
        }

        return ($"In progress ({have}/{total}, {pct:0.0}%) — Waiting for next episode", null);
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


    public string AddAndSearch(string title, string typeUpper, int tvdbId)
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
        if (added == null) return "Failed to add series (unknown error)";

        PostJson<object>("api/v3/command", new SonarrCommandRequest { Name = "SeriesSearch", SeriesId = added.Id });

        _existingSeries.Add(added);
        return "Added to Sonarr + searching";
    }

    public string? GetCanonicalTitleByTvdbId(int tvdbId)
    {
        // Sonarr supports lookup by tvdb:<id>
        var term = Uri.EscapeDataString($"tvdb:{tvdbId}");
        var list = GetJson<List<SonarrLookupSeries>>($"api/v3/series/lookup?term={term}");
        return (list != null && list.Count > 0) ? list[0].Title : null;
    }

    // ---------- DTOs ----------
    private sealed class SonarrRootFolder { [JsonPropertyName("path")] public string? Path { get; set; } }
    private sealed class SonarrQualityProfile { [JsonPropertyName("id")] public int Id { get; set; } [JsonPropertyName("name")] public string? Name { get; set; } }

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
