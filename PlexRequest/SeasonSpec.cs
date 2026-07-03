using System;
using System.Collections.Generic;
using System.Linq;

namespace PlexRequest;

/// <summary>
/// Parsed SEASON column value for TV/ANIME requests.
/// Accepts: a number ("3" or "S3"), a list ("1,3"), a range ("2-5"),
/// combinations ("1,3-5"), "LATEST", or "ALL".
/// </summary>
public sealed class SeasonSpec
{
    public bool All { get; private init; }
    public bool Latest { get; private init; }

    private readonly SortedSet<int> _seasons = new();
    public IReadOnlyCollection<int> Seasons => _seasons;

    private SeasonSpec() { }

    public static bool TryParse(string? raw, out SeasonSpec spec, out string error)
    {
        spec = new SeasonSpec();
        error = "";

        var s = (raw ?? "").Trim().ToUpperInvariant();
        if (s.Length == 0)
        {
            error = "SEASON required (column D): number (3), list (1,3), range (2-5), LATEST, or ALL";
            return false;
        }

        if (s == "ALL")
        {
            spec = new SeasonSpec { All = true };
            return true;
        }

        if (s == "LATEST")
        {
            spec = new SeasonSpec { Latest = true };
            return true;
        }

        var result = new SeasonSpec();
        foreach (var token in s.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryParseToken(token, result._seasons))
            {
                error = $"Invalid SEASON '{raw}': use number (3), list (1,3), range (2-5), LATEST, or ALL";
                return false;
            }
        }

        if (result._seasons.Count == 0)
        {
            error = $"Invalid SEASON '{raw}': use number (3), list (1,3), range (2-5), LATEST, or ALL";
            return false;
        }

        spec = result;
        return true;
    }

    /// <summary>
    /// Resolves this spec against the seasons that actually exist for the show
    /// (specials excluded). Returns an error if a requested season does not exist.
    /// </summary>
    public (List<int>? seasons, string? error) Resolve(List<int> knownSeasons)
    {
        if (knownSeasons.Count == 0)
            return (null, "No seasons found for this show");

        if (All)
            return (knownSeasons.OrderBy(n => n).ToList(), null);

        if (Latest)
            return (new List<int> { knownSeasons.Max() }, null);

        var missing = _seasons.Where(n => !knownSeasons.Contains(n)).ToList();
        if (missing.Count > 0)
            return (null, $"{Format(missing)} not found — show has {Format(knownSeasons)}");

        return (_seasons.ToList(), null);
    }

    /// <summary>Formats season numbers compactly, e.g. "S1-S18" or "S1, S3-S5".</summary>
    public static string Format(IEnumerable<int> seasons)
    {
        var s = seasons.Distinct().OrderBy(n => n).ToList();
        if (s.Count == 0) return "no seasons";

        var parts = new List<string>();
        var i = 0;
        while (i < s.Count)
        {
            var j = i;
            while (j + 1 < s.Count && s[j + 1] == s[j] + 1) j++;
            parts.Add(j > i ? $"S{s[i]}-S{s[j]}" : $"S{s[i]}");
            i = j + 1;
        }
        return string.Join(", ", parts);
    }

    private static bool TryParseToken(string token, SortedSet<int> into)
    {
        token = token.Trim();
        if (token.Length == 0) return false;

        var parts = token.Split('-', 2);
        if (parts.Length == 2)
        {
            if (!TryParseNumber(parts[0], out var lo) || !TryParseNumber(parts[1], out var hi) || lo > hi)
                return false;
            for (var n = lo; n <= hi; n++) into.Add(n);
            return true;
        }

        if (!TryParseNumber(token, out var single)) return false;
        into.Add(single);
        return true;
    }

    private static bool TryParseNumber(string s, out int n)
    {
        s = s.Trim();
        if (s.StartsWith("SEASON", StringComparison.OrdinalIgnoreCase)) s = s.Substring(6).Trim();
        else if (s.StartsWith("S", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
        return int.TryParse(s, out n) && n > 0 && n <= 200;
    }
}
