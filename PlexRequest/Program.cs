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

            var kind = GetKind(r.Type);
            var typeUpper = (r.Type ?? "").Trim().ToUpperInvariant(); // keep for display/logs
            var idKind = GetIdKind(kind);
            var mediaKind = GetMediaKind(kind);

            // If TYPE is invalid, tell them that before complaining about ID
            if (kind == RequestKind.Unknown)
            {
                var result = $"Unknown TYPE '{r.Type}' (use TV/ANIME/MOVIE)";
                sheets.WriteResult(r.SheetRowNumber, result);

                WriteStatusFromAction(sheets, r.SheetRowNumber, null, RequestAction.BadType);
                LogFromAction(r.SheetRowNumber, "Request", r.Title, typeUpper, "ID", null, result, RequestAction.BadType);

                processed++;
                continue;

            }

            // ID required for ALL request types now
            if (r.Id == null)
            {
                var result = kind == RequestKind.Movie
                     ? "TMDB ID required (column C)"
                     : "TVDB ID required (column C)";
                sheets.WriteResult(r.SheetRowNumber, result);

                WriteStatusFromAction(sheets, r.SheetRowNumber, null, RequestAction.NeedsId);
                LogFromAction(r.SheetRowNumber, mediaKind, r.Title, typeUpper, idKind, null, result, RequestAction.NeedsId);

                processed++;
                continue;
            }

            switch (kind)
            {
                case RequestKind.Movie:
                    {
                        // Validate title matches TMDB ID
                        var canonMovieTitle = radarr.GetCanonicalTitleByTmdbId(r.Id.Value);
                        if (string.IsNullOrWhiteSpace(canonMovieTitle) || !TitlesMatch(r.Title, canonMovieTitle))
                        {
                            HandleIdMismatch(sheets, r.SheetRowNumber, mediaKind, r.Title, 
                                typeUpper, idKind,r.Id.Value, canonMovieTitle);

                            processed++;
                            break;
                        }

                        var (result, newStatus, action) = radarr.ProcessMovieByTmdbId(r.Id.Value, r.Title);

                        sheets.WriteResult(r.SheetRowNumber, result);
                        WriteStatusFromAction(sheets, r.SheetRowNumber, newStatus, action);
                        
                        LogFromAction(r.SheetRowNumber, mediaKind, r.Title, typeUpper, idKind, r.Id.Value, result, action);
                        processed++;
                        break;
                    }

                case RequestKind.Series:
                    {
                        // Validate title matches TVDB ID
                        var canonSeriesTitle = sonarr.GetCanonicalTitleByTvdbId(r.Id.Value);
                        if (string.IsNullOrWhiteSpace(canonSeriesTitle) || !TitlesMatch(r.Title, canonSeriesTitle))
                        {
                            HandleIdMismatch(sheets, r.SheetRowNumber, mediaKind, r.Title,
                                typeUpper, idKind, r.Id.Value, canonSeriesTitle);

                            processed++;
                            break;
                        }

                        var (result, newStatus, action) = sonarr.ProcessSeriesByTvdbId(r.Id.Value, r.Title, typeUpper);
                        
                        sheets.WriteResult(r.SheetRowNumber, result);
                        WriteStatusFromAction(sheets, r.SheetRowNumber, newStatus, action);

                        LogFromAction(r.SheetRowNumber, mediaKind, r.Title, typeUpper, idKind, r.Id.Value, result, action);
                        processed++;
                        break;
                    }

                default:
                    {
                        sheets.WriteResult(r.SheetRowNumber, $"Unknown TYPE '{r.Type}' (use TV/ANIME/MOVIE)");
                        processed++;
                        break;
                    }
            }
        }

        var duration = DateTime.UtcNow - startTime;
        Log($"Processed {processed} row(s) in {duration.TotalSeconds:0.0}s");
        Log("PlexRequest run finished");
    }

    // ---------- helpers ----------
    private enum RequestKind { Movie, Series, Unknown }

    private static RequestKind GetKind(string? type)
    {
        var t = (type ?? "").Trim().ToUpperInvariant();
        return t switch
        {
            "MOVIE" => RequestKind.Movie,
            "TV" => RequestKind.Series,
            "ANIME" => RequestKind.Series,
            _ => RequestKind.Unknown
        };
    }

    private static string GetIdKind(RequestKind kind)
    {
        return kind == RequestKind.Movie ? "TMDB"
             : kind == RequestKind.Series ? "TVDB"
             : "ID";
    }

    private static string GetMediaKind(RequestKind kind)
    {
        return kind == RequestKind.Movie ? "Movie"
             : kind == RequestKind.Series ? "Series"
             : "Request";
    }

    private static void HandleIdMismatch(GoogleSheetsClient sheets, int row, string mediaKind, string title, 
        string typeUpper, string idKind, int idValue, string? canonicalTitle)
    {
        var msg = $"ID/title mismatch for '{title}'. {idKind} {idValue} is '{canonicalTitle ?? "NOT FOUND"}'";

        sheets.WriteResult(row, msg);

        WriteStatusFromAction(sheets, row, null, RequestAction.IdMismatch);
        LogFromAction(row, mediaKind, title, typeUpper, idKind, idValue, msg, RequestAction.IdMismatch);
    }


    private static void WriteStatusFromAction(GoogleSheetsClient sheets, int row, string? explicitStatus, RequestAction action)
    {
        // If client explicitly returned a status, trust it.
        if (!string.IsNullOrWhiteSpace(explicitStatus))
        {
            sheets.WriteStatus(row, explicitStatus);
            return;
        }

        // Infer status from action
        switch (action)
        {
            case RequestAction.Added:
            case RequestAction.Updated:
                sheets.WriteStatus(row, "IN_PROGRESS");
                break;

            case RequestAction.Completed:
                sheets.WriteStatus(row, "DONE");
                break;

            case RequestAction.Stale:
                sheets.WriteStatus(row, "STALE");
                break;

            case RequestAction.NeedsId:
            case RequestAction.IdMismatch:
                sheets.WriteStatus(row, "NEEDS_ID");
                break;

            case RequestAction.BadType:
                // Intentionally do NOT write STATUS
                // User fixes TYPE and row will be retried
                break;

            case RequestAction.None:
            default:
                // No-op
                break;
        }
    }


    private static void LogFromAction(int row, string mediaKind, string title, string typeUpper, 
            string idKind, int? id, string result, RequestAction action)
    {
        var actionText = action switch
        {
            RequestAction.Added => "ADDED",
            RequestAction.Updated => "UPDATED",
            RequestAction.Completed => "DONE",
            RequestAction.Stale => "STALE",
            RequestAction.NeedsId => "NEEDS_ID",
            RequestAction.IdMismatch => "ID_MISMATCH",
            RequestAction.BadType => "BAD_TYPE",
            _ => "NOOP"
        };

        LogRow(row, actionText, mediaKind, title, typeUpper, idKind, id, $"result='{result}'");
    }


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
