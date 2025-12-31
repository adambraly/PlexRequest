using System;

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
