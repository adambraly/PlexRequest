using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlexRequest;

/// <summary>
/// Optional read-only client for the local (home) Plex server.
/// Used to detect media that already exists locally so requests for it
/// are marked ON_PLEX instead of being re-downloaded on the seedbox.
/// If Plex is unreachable, the check is skipped for the run (fail open).
/// </summary>
public sealed class PlexClient
{
    private readonly HttpClient _http;

    private readonly HashSet<int> _movieTmdbIds = new();
    private readonly Dictionary<int, string> _showRatingKeyByTvdbId = new();
    private readonly Dictionary<int, HashSet<int>> _showSeasonsCache = new();

    public bool IsAvailable { get; private set; }

    public PlexClient(string baseUrl, string token, string? proxyUrl = null)
    {
        // proxyUrl routes only Plex traffic (e.g. through a Tailscale userspace
        // HTTP proxy) without affecting Sonarr/Radarr/Sheets clients.
        HttpMessageHandler handler = new HttpClientHandler
        {
            Proxy = string.IsNullOrWhiteSpace(proxyUrl) ? null : new System.Net.WebProxy(proxyUrl),
            UseProxy = !string.IsNullOrWhiteSpace(proxyUrl)
        };

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(15)
        };
        _http.DefaultRequestHeaders.Add("X-Plex-Token", token);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public void Initialize()
    {
        try
        {
            var sections = GetJson<PlexResponse<PlexSection>>("library/sections")?.MediaContainer?.Directory ?? new();

            foreach (var section in sections)
            {
                if (section.Type != "movie" && section.Type != "show") continue;
                if (string.IsNullOrWhiteSpace(section.Key)) continue;

                var items = GetJson<PlexResponse<PlexItem>>($"library/sections/{section.Key}/all?includeGuids=1")?.MediaContainer?.Metadata ?? new();

                foreach (var item in items)
                {
                    foreach (var guid in item.Guid ?? new List<PlexGuid>())
                    {
                        if (section.Type == "movie" && TryParseGuid(guid.Id, "tmdb", out var tmdb))
                            _movieTmdbIds.Add(tmdb);
                        else if (section.Type == "show" && TryParseGuid(guid.Id, "tvdb", out var tvdb) && !string.IsNullOrWhiteSpace(item.RatingKey))
                            _showRatingKeyByTvdbId[tvdb] = item.RatingKey!;
                    }
                }
            }

            IsAvailable = true;
            Console.WriteLine($"Plex library loaded: {_movieTmdbIds.Count} movie(s), {_showRatingKeyByTvdbId.Count} show(s)");
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            Console.WriteLine($"Plex unreachable — local library check disabled this run :: {ex.Message}");
        }
    }

    public bool HasMovie(int tmdbId) => IsAvailable && _movieTmdbIds.Contains(tmdbId);

    /// <summary>
    /// Season numbers (specials excluded) that exist locally with at least one episode.
    /// Empty set when the show is not on the local server or Plex is unavailable.
    /// </summary>
    public HashSet<int> GetShowSeasons(int tvdbId)
    {
        if (!IsAvailable || !_showRatingKeyByTvdbId.TryGetValue(tvdbId, out var ratingKey))
            return new HashSet<int>();

        if (_showSeasonsCache.TryGetValue(tvdbId, out var cached))
            return cached;

        var result = new HashSet<int>();
        try
        {
            var seasons = GetJson<PlexResponse<PlexItem>>($"library/metadata/{ratingKey}/children")?.MediaContainer?.Metadata ?? new();
            foreach (var s in seasons)
            {
                if (s.Index.HasValue && s.Index.Value > 0 && (s.LeafCount ?? 0) > 0)
                    result.Add(s.Index.Value);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Plex season lookup failed for tvdb {tvdbId} :: {ex.Message}");
        }

        _showSeasonsCache[tvdbId] = result;
        return result;
    }

    private static bool TryParseGuid(string? guid, string scheme, out int id)
    {
        id = 0;
        if (string.IsNullOrEmpty(guid)) return false;
        var prefix = scheme + "://";
        if (!guid!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        return int.TryParse(guid.Substring(prefix.Length), out id) && id > 0;
    }

    // Case-sensitive: Plex returns both a scalar "guid" (string) and an array
    // "Guid" (external IDs). Web defaults match case-insensitively and would map
    // the scalar onto the array property, so matching must stay exact.
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = false };

    private T? GetJson<T>(string path)
        => _http.GetFromJsonAsync<T>(path, JsonOpts).GetAwaiter().GetResult();

    // ---------- DTOs ----------
    private sealed class PlexResponse<T> { [JsonPropertyName("MediaContainer")] public PlexMediaContainer<T>? MediaContainer { get; set; } }
    private sealed class PlexMediaContainer<T>
    {
        [JsonPropertyName("Directory")] public List<T>? Directory { get; set; }
        [JsonPropertyName("Metadata")] public List<T>? Metadata { get; set; }
    }
    private sealed class PlexSection
    {
        [JsonPropertyName("key")] public string? Key { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
    }
    private sealed class PlexItem
    {
        [JsonPropertyName("ratingKey")] public string? RatingKey { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("index")] public int? Index { get; set; }
        [JsonPropertyName("leafCount")] public int? LeafCount { get; set; }
        [JsonPropertyName("Guid")] public List<PlexGuid>? Guid { get; set; }
    }
    private sealed class PlexGuid { [JsonPropertyName("id")] public string? Id { get; set; } }
}
