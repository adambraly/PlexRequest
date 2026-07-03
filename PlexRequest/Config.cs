using System;
using System.Collections.Generic;

namespace PlexRequest;

public sealed class Config
{
    // Sheets
    public string GoogleSheetId { get; }
    public string GoogleSheetRange { get; }
    public string SheetTabName { get; }
    public int SheetStartRow { get; }

    // Sonarr
    public string SonarrUrl { get; }
    public string SonarrApiKey { get; }
    public string SonarrRootTv { get; }
    public string SonarrRootAnime { get; }
    public string SonarrQualityProfile { get; }

    // Radarr
    public string RadarrUrl { get; }
    public string RadarrApiKey { get; }
    public string RadarrRootMovies { get; }
    public string RadarrQualityProfile { get; }

    public int ReleaseCheckMinutes { get; }
    public int StaleAfterDays { get; }

    // Plex servers to check for already-owned media (optional — check is
    // skipped when none configured). PLEX_* is the primary (home) server,
    // PLEX2_* an optional second (e.g. the seedbox's own Plex).
    public sealed class PlexServerConfig
    {
        public string Url { get; init; } = "";
        public string Token { get; init; } = "";
        public string? Proxy { get; init; }
        public string Name { get; init; } = "";
    }

    public IReadOnlyList<PlexServerConfig> PlexServers { get; }


    public Config()
    {
        GoogleSheetId = Env("GOOGLE_SHEET_ID");
        GoogleSheetRange = Env("GOOGLE_SHEET_RANGE");
        SheetTabName = GoogleSheetRange.Split('!')[0];
        SheetStartRow = EnvInt("GOOGLE_SHEET_START_ROW", 3);

        SonarrUrl = Env("SONARR_URL").TrimEnd('/');
        SonarrApiKey = Env("SONARR_API_KEY");
        SonarrRootTv = Env("SONARR_ROOT_TV");
        SonarrRootAnime = Env("SONARR_ROOT_ANIME");
        SonarrQualityProfile = Environment.GetEnvironmentVariable("SONARR_QUALITY_PROFILE") ?? "HD - 720p/1080p";

        RadarrUrl = Env("RADARR_URL").TrimEnd('/');
        RadarrApiKey = Env("RADARR_API_KEY");
        RadarrRootMovies = Env("RADARR_ROOT_MOVIES");
        RadarrQualityProfile = Environment.GetEnvironmentVariable("RADARR_QUALITY_PROFILE") ?? "HD - 720p/1080p";

        ReleaseCheckMinutes = EnvInt("RELEASE_CHECK_MINUTES", 1440);
        StaleAfterDays = EnvInt("STALE_AFTER_DAYS", 30);

        var plexServers = new List<PlexServerConfig>();

        var plexUrl = Environment.GetEnvironmentVariable("PLEX_URL")?.TrimEnd('/');
        var plexToken = Environment.GetEnvironmentVariable("PLEX_TOKEN");
        if (!string.IsNullOrWhiteSpace(plexUrl) && !string.IsNullOrWhiteSpace(plexToken))
        {
            plexServers.Add(new PlexServerConfig
            {
                Url = plexUrl!,
                Token = plexToken!,
                Proxy = Environment.GetEnvironmentVariable("PLEX_PROXY"),
                Name = Environment.GetEnvironmentVariable("PLEX_NAME") ?? "local Plex"
            });
        }

        var plex2Url = Environment.GetEnvironmentVariable("PLEX2_URL")?.TrimEnd('/');
        var plex2Token = Environment.GetEnvironmentVariable("PLEX2_TOKEN") ?? plexToken; // account token works on all owned servers
        if (!string.IsNullOrWhiteSpace(plex2Url) && !string.IsNullOrWhiteSpace(plex2Token))
        {
            plexServers.Add(new PlexServerConfig
            {
                Url = plex2Url!,
                Token = plex2Token!,
                Proxy = Environment.GetEnvironmentVariable("PLEX2_PROXY"),
                Name = Environment.GetEnvironmentVariable("PLEX2_NAME") ?? "seedbox Plex"
            });
        }

        PlexServers = plexServers;
    }

    private static string Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(v))
            throw new Exception($"Missing env var: {name}");
        return v;
    }

    private static int EnvInt(string name, int fallback)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return int.TryParse(v, out var n) ? n : fallback;
    }
}
