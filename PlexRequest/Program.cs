using System;
using System.Linq;
using PlexRequest;

internal static class Program
{
    // Sheet: A TITLE, B TYPE, C ID (required), D RESULT, E STATUS
    //   TV/ANIME => ID is TVDB
    //   MOVIE    => ID is TMDB
    private static void Main()
    {
        var startTime = DateTime.UtcNow;
        Log("PlexRequest run started");

        var cfg = new Config();

        var sheets = new GoogleSheetsClient(cfg.GoogleSheetId, cfg.SheetTabName);

        var sonarr = new SonarrClient(
            cfg.SonarrUrl, cfg.SonarrApiKey,
            cfg.SonarrRootTv, cfg.SonarrRootAnime,
            cfg.SonarrQualityProfile,
            cfg.ReleaseCheckMinutes,
            cfg.StaleAfterDays);

        var radarr = new RadarrClient(
            cfg.RadarrUrl, cfg.RadarrApiKey,
            cfg.RadarrRootMovies,
            cfg.RadarrQualityProfile,
            cfg.ReleaseCheckMinutes,
            cfg.StaleAfterDays);

        sonarr.Initialize();
        radarr.Initialize();

        var rows = sheets.ReadRows(cfg.GoogleSheetRange, cfg.SheetStartRow);
        Log($"Read {rows.Count} row(s) from {cfg.GoogleSheetRange}");

        var processed = 0;

        foreach (var r in rows)
        {
            if (string.IsNullOrWhiteSpace(r.Title))
                continue;

            // STATUS is column E
            var status = sheets.ReadStatusCell(r.SheetRowNumber);
            var statusUpper = (status ?? "").Trim().ToUpperInvariant();

            bool isProcessable =
                string.IsNullOrWhiteSpace(status) ||
                statusUpper == "NEW" ||
                statusUpper == "NEEDS_ID" ||
                statusUpper == "IN_PROGRESS";

            bool isIgnored =
                statusUpper == "DONE" ||
                statusUpper == "TRANSFERRED" ||
                statusUpper == "SKIP" ||
                statusUpper == "STALE";

            if (!isProcessable || isIgnored)
                continue;

            var typeUpper = (r.Type ?? "").Trim().ToUpperInvariant();

            // ID required for ALL request types now
            if (r.Id == null)
            {
                LogRow(r.SheetRowNumber, "NEEDS_ID", "Request", r.Title, typeUpper,
                    typeUpper == "MOVIE" ? "TMDB" : "TVDB", null);

                var msg = typeUpper == "MOVIE"
                    ? "TMDB ID required (column C)"
                    : "TVDB ID required (column C)";

                sheets.WriteResult(r.SheetRowNumber, msg);
                sheets.WriteStatus(r.SheetRowNumber, "NEEDS_ID");
                processed++;
                continue;
            }

            if (typeUpper == "MOVIE")
            {
                // Validate title matches TMDB ID
                var canonMovieTitle = radarr.GetCanonicalTitleByTmdbId(r.Id.Value);
                if (string.IsNullOrWhiteSpace(canonMovieTitle) || !TitlesMatch(r.Title, canonMovieTitle))
                {
                    LogRow(r.SheetRowNumber, "ID_MISMATCH", "Movie", r.Title, typeUpper, "TMDB", r.Id,
                        $"canonical='{canonMovieTitle ?? "NOT FOUND"}'");

                    sheets.WriteResult(r.SheetRowNumber, $"ID/title mismatch. TMDB {r.Id.Value} is '{canonMovieTitle ?? "NOT FOUND"}'");
                    sheets.WriteStatus(r.SheetRowNumber, "NEEDS_ID");
                    processed++;
                    continue;
                }

                // Now that it's valid, clear NEEDS_ID (if present)
                if (statusUpper == "NEEDS_ID")
                    sheets.WriteStatus(r.SheetRowNumber, "");

                var (result, newStatus) = radarr.ProcessMovieByTmdbId(r.Id.Value, r.Title);
                sheets.WriteResult(r.SheetRowNumber, result);

                if (result.StartsWith("Added to Radarr"))
                {
                    LogRow(r.SheetRowNumber, "ADDED", "Movie", r.Title, typeUpper, "TMDB", r.Id);
                }

                if (!string.IsNullOrWhiteSpace(newStatus))
                {
                    sheets.WriteStatus(r.SheetRowNumber, newStatus);
                    if (newStatus == "DONE")
                        LogRow(r.SheetRowNumber, "DONE", "Movie", r.Title, typeUpper, "TMDB", r.Id);
                }
                else
                {
                    // Valid + tracked, but not complete/terminal
                    sheets.WriteStatus(r.SheetRowNumber, "IN_PROGRESS");
                }

                if (newStatus == "STALE")
                {
                    LogRow(r.SheetRowNumber, "STALE", "Movie", r.Title, typeUpper, "TMDB", r.Id, $"result='{result}'");
                }

                processed++;
                continue;
            }

            if (typeUpper != "TV" && typeUpper != "ANIME")
            {
                sheets.WriteResult(r.SheetRowNumber, $"Unknown TYPE '{r.Type}' (use TV/ANIME/MOVIE)");
                processed++;
                continue;
            }

            // Validate title matches TVDB ID
            var canonSeriesTitle = sonarr.GetCanonicalTitleByTvdbId(r.Id.Value);
            if (string.IsNullOrWhiteSpace(canonSeriesTitle) || !TitlesMatch(r.Title, canonSeriesTitle))
            {
                LogRow(r.SheetRowNumber, "ID_MISMATCH", "Series", r.Title, typeUpper, "TVDB", r.Id,
                    $"canonical='{canonSeriesTitle ?? "NOT FOUND"}'");
                sheets.WriteResult(r.SheetRowNumber, $"ID/title mismatch. TVDB {r.Id.Value} is '{canonSeriesTitle ?? "NOT FOUND"}'");
                sheets.WriteStatus(r.SheetRowNumber, "NEEDS_ID");
                processed++;
                continue;
            }

            // Now that it's valid, clear NEEDS_ID (if present)
            if (statusUpper == "NEEDS_ID")
                sheets.WriteStatus(r.SheetRowNumber, "");

            var existing = sonarr.FindByTvdbId(r.Id.Value);
            if (existing != null)
            {
                // TEMP: remove once verified
                // sonarr.DebugEpisodes(existing);

                var (result, newStatus) = sonarr.DescribeProgress(existing);
                sheets.WriteResult(r.SheetRowNumber, result);
                if (!string.IsNullOrWhiteSpace(newStatus))
                {
                    sheets.WriteStatus(r.SheetRowNumber, newStatus);
                    if (newStatus == "DONE")
                       LogRow(r.SheetRowNumber, "DONE", "Series", r.Title, typeUpper, "TVDB", r.Id);
                }
                else
                {
                    sheets.WriteStatus(r.SheetRowNumber, "IN_PROGRESS");
                }

                if (newStatus == "STALE")
                {
                    LogRow(r.SheetRowNumber, "STALE", "Series", r.Title, typeUpper, "TVDB", r.Id, $"result='{result}'");
                }
                
                if (string.IsNullOrWhiteSpace(newStatus))
                {
                    LogRow(r.SheetRowNumber, "UPDATED", "Series", r.Title, typeUpper, "TVDB", r.Id,
                        $"result='{result}'");
                }
                
                processed++;
                continue;
            }

            var addedResult = sonarr.AddAndSearch(r.Title, typeUpper, r.Id.Value);
            sheets.WriteResult(r.SheetRowNumber, addedResult);
            sheets.WriteStatus(r.SheetRowNumber, "IN_PROGRESS");
            LogRow(r.SheetRowNumber, "ADDED", "Series", r.Title, typeUpper, "TVDB", r.Id);
            processed++;
        }

        var duration = DateTime.UtcNow - startTime;
        Log($"Processed {processed} row(s) in {duration.TotalSeconds:0.0}s");
        Log("PlexRequest run finished");
    }

    // ---------- helpers ----------
    private static string NormalizeTitle(string s)
    {
        s = (s ?? "").Trim().ToUpperInvariant();

        // Keep letters/numbers/spaces only
        var chars = s.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
        var cleaned = new string(chars);

        // Collapse whitespace
        return string.Join(" ", cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool TitlesMatch(string userTitle, string canonicalTitle)
    {
        var a = NormalizeTitle(userTitle);
        var b = NormalizeTitle(canonicalTitle);

        if (a.Length == 0 || b.Length == 0) return false;
        if (a == b) return true;

        // forgiving match either direction
        return a.Contains(b) || b.Contains(a);
    }
    private static void LogRow(int rowNumber, string action, string mediaKind, string title, string typeUpper, string idKind, int? id, string? details = null)
    {
        var idPart = id.HasValue ? $"{idKind} {id.Value}" : $"{idKind} (missing)";
        var detailsPart = string.IsNullOrWhiteSpace(details) ? "" : $" [{details}]";
        Log($"Row {rowNumber}: {action} {mediaKind} — '{title}' ({typeUpper}, {idPart}){detailsPart}");
    }

    private static void Log(string message)
    {
        Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC] {message}");
    }


}
