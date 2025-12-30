using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace PlexRequest;

public sealed class RadarrClient
{
    private readonly HttpClient _http;
    private readonly string _rootMovies;
    private readonly string _profileName;

    private int _qualityProfileId;
    private List<RadarrMovie> _existingMovies = new();

    public RadarrClient(string baseUrl, string apiKey, string rootMovies, string profileName)
    {
        _rootMovies = rootMovies;
        _profileName = profileName;

        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        _http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public void Initialize()
    {
        var roots = GetJson<List<RadarrRootFolder>>("api/v3/rootfolder") ?? new();
        if (!HasRoot(roots, _rootMovies)) throw new Exception($"Radarr root folder not found: {_rootMovies}");

        var profiles = GetJson<List<RadarrQualityProfile>>("api/v3/qualityprofile") ?? new();
        if (profiles.Count == 0) throw new Exception("No Radarr quality profiles found.");
        _qualityProfileId = PickProfileId(profiles, _profileName, "Radarr");

        _existingMovies = GetJson<List<RadarrMovie>>("api/v3/movie") ?? new();
    }

    /// <summary>
    /// Movies use TMDB IDs in Radarr. We require the user to provide TMDB ID in sheet column C for MOVIE rows.
    /// </summary>
    public (string result, string? status) ProcessMovieByTmdbId(int tmdbId, string titleForDisplay)
    {
        var existing = _existingMovies.FirstOrDefault(m => m.TmdbId == tmdbId);
        if (existing != null)
        {
            if (existing.HasFile) return ("Complete in Radarr", "DONE");
            return ("In progress (Radarr - no file yet)", null);
        }

        // Lookup by tmdbId to get canonical title/year (nice for user feedback)
        var lookup = LookupMovieByTmdbId(tmdbId);
        if (lookup == null)
            return ($"TMDB ID {tmdbId} not found in Radarr lookup", "SKIP");

        var addReq = new RadarrAddMovieRequest
        {
            Title = lookup.Title ?? titleForDisplay,
            TmdbId = lookup.TmdbId,
            Year = lookup.Year,
            QualityProfileId = _qualityProfileId,
            RootFolderPath = _rootMovies,
            Monitored = true,
            AddOptions = new RadarrAddOptions { SearchForMovie = true }
        };

        var added = PostJson<RadarrMovie>("api/v3/movie", addReq);
        if (added == null) return ("Failed to add movie (unknown error)", null);

        PostJson<object>("api/v3/command", new RadarrCommandRequest
        {
            Name = "MoviesSearch",
            MovieIds = new List<int> { added.Id }
        });

        _existingMovies.Add(added);
        return ("Added to Radarr + searching", null);
    }

    public string? GetCanonicalTitleByTmdbId(int tmdbId)
    {
        var lookup = LookupMovieByTmdbId(tmdbId);
        return lookup?.Title;
    }


    // ---------- DTOs ----------
    private sealed class RadarrRootFolder { [JsonPropertyName("path")] public string? Path { get; set; } }
    private sealed class RadarrQualityProfile { [JsonPropertyName("id")] public int Id { get; set; } [JsonPropertyName("name")] public string? Name { get; set; } }

    private sealed class RadarrMovie
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("tmdbId")] public int? TmdbId { get; set; }
        [JsonPropertyName("year")] public int? Year { get; set; }
        [JsonPropertyName("hasFile")] public bool HasFile { get; set; }
    }

    private sealed class RadarrLookupMovie
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("tmdbId")] public int? TmdbId { get; set; }
        [JsonPropertyName("year")] public int? Year { get; set; }
    }

    private sealed class RadarrAddOptions { [JsonPropertyName("searchForMovie")] public bool SearchForMovie { get; set; } = true; }

    private sealed class RadarrAddMovieRequest
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("tmdbId")] public int? TmdbId { get; set; }
        [JsonPropertyName("year")] public int? Year { get; set; }
        [JsonPropertyName("qualityProfileId")] public int QualityProfileId { get; set; }
        [JsonPropertyName("rootFolderPath")] public string? RootFolderPath { get; set; }
        [JsonPropertyName("monitored")] public bool Monitored { get; set; } = true;
        [JsonPropertyName("addOptions")] public RadarrAddOptions AddOptions { get; set; } = new();
    }

    private sealed class RadarrCommandRequest
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("movieIds")] public List<int>? MovieIds { get; set; }
    }

    // ---------- helpers ----------
    private RadarrLookupMovie? LookupMovieByTmdbId(int tmdbId)
    {
        // Radarr supports lookup by tmdb:<id>
        var term = Uri.EscapeDataString($"tmdb:{tmdbId}");
        var list = GetJson<List<RadarrLookupMovie>>($"api/v3/movie/lookup?term={term}");
        return (list != null && list.Count > 0) ? list[0] : null;
    }

    private static bool HasRoot(List<RadarrRootFolder> roots, string path)
    {
        static string Norm(string p) => (p ?? "").Trim().TrimEnd('/');
        var target = Norm(path);
        return roots.Any(r => string.Equals(Norm(r.Path ?? ""), target, StringComparison.Ordinal));
    }

    private static int PickProfileId(List<RadarrQualityProfile> profiles, string preferredName, string systemName)
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
