# PlexRequest

PlexRequest is a lightweight automation tool that processes media requests from a Google Sheet and routes them to Sonarr (TV/Anime) or Radarr (Movies) running on a seedbox.

It is designed to be:
- ID-driven (no ambiguous title matching)
- Idempotent (safe to re-run repeatedly)
- Cron-friendly (unattended operation)
- Explicit about state and user responsibility

The Google Sheet functions as a simple, durable request queue and state machine.

---

## Overview

Users submit requests by filling out a Google Sheet. PlexRequest:

- Reads requests from the sheet
- Validates required IDs
- Ensures the ID matches the requested title
- Requires a season selection for TV/Anime requests
- Optionally checks a local Plex server and refuses to re-download media already there
- Adds the request to Sonarr or Radarr if appropriate
- Writes progress and completion status back to the sheet
- Ignores completed or intentionally skipped entries on future runs

PlexRequest does not move files, manage Plex libraries, or interact with local storage. It only manages requests on the seedbox (plus an optional read-only check of a local Plex server).

---

## Google Sheet Layout

The sheet must follow this column layout:

| Column | Name   | Description |
|------|--------|-------------|
| A | TITLE | Human-readable title (for users) |
| B | TYPE | `TV`, `ANIME`, or `MOVIE` |
| C | ID | Required ID (TVDB for TV/Anime, TMDB for Movie) |
| D | SEASON | Required for TV/Anime (see below); ignored for movies |
| E | RESULT | Output written by PlexRequest |
| F | STATUS | Request lifecycle state |

Example range:
```Requests!A3:F``` <br/>
Row 1–2 are typically headers or instructions and are ignored.  

---

## ID Requirements (Important)

PlexRequest requires IDs to avoid ambiguity.

- TV / ANIME requests require a **TVDB ID**
- MOVIE requests require a **TMDB ID**

Title-only requests are not supported.

If the ID does not exist or does not match the title, the request will not be processed.

---

## SEASON Requirements (TV/Anime)

TV and Anime requests must specify which season(s) to download in column D.
This prevents accidentally grabbing an entire 23-season back catalog.

Accepted values:

- `3` or `S3` — a single season
- `1,3` — a list of seasons
- `2-5` — a range of seasons
- `1,3-5` — combinations
- `LATEST` — the most recent season
- `ALL` — the entire series (explicit opt-in)

If SEASON is blank or invalid, STATUS is set to `NEEDS_SEASON` and the row is
re-checked every run until corrected. If a requested season does not exist
(e.g. `S30` of a 23-season show), RESULT explains which seasons exist.

Movies ignore the SEASON column.

---

## STATUS Lifecycle

The STATUS column controls how requests are handled.

### Active / Reprocessed States

- blank or `NEW`  
  The request is eligible for processing.

- `NEEDS_ID`  
  The request is missing or has an invalid/mismatched ID.  
  PlexRequest will re-check this row on every run until corrected.

- `NEEDS_SEASON`  
  The TV/Anime request is missing or has an invalid SEASON value (column D).  
  PlexRequest will re-check this row on every run until corrected.

- `IN_PROGRESS`  
  The request is currently searching indexers or is currently downloading.

### Terminal States

- `DONE`  
  The request is complete and will never be processed again.

- `TRANSFERRED`  
  Media has been manually moved off the seedbox. PlexRequest ignores it permanently.

- `SKIP`  
  The request is intentionally ignored (bad request, already owned, etc.).

- `STALE`  
  Requests that have been active for multiple days and are unable to be retrieved by indexers are marked as stale.

- `ON_PLEX`  
  The requested media (or all requested seasons) already exists on the local
  Plex server. Nothing is downloaded. Requires the optional Plex check to be
  configured.

Terminal states are never reprocessed.

---

## Behavior by Type

### TV / ANIME (Sonarr)

- Matches existing series by TVDB ID
- Adds new series using the provided TVDB ID
- Monitors **only the requested seasons** and triggers a search per season
  (`ALL` requests monitor and search the whole series)
- For series already in Sonarr, newly requested seasons are switched to
  monitored and searched — already-monitored seasons are left alone
- Tracks progress by counting episodes in the requested seasons only
- Marks `DONE` when all episodes in the requested seasons have files
- Ignores specials entirely

### MOVIE (Radarr)

- Matches existing movies by TMDB ID
- Adds new movies using the provided TMDB ID
- Marks `DONE` once the movie file exists

---

## Local Plex Check (Optional)

If `PLEX_URL` and `PLEX_TOKEN` are set, PlexRequest queries the local (home)
Plex server at the start of each run and:

- Marks movie requests `ON_PLEX` when the movie already exists locally
  (matched by TMDB ID)
- Drops requested seasons that already exist locally (matched by TVDB ID,
  season by season) — if **all** requested seasons are local, the row is
  marked `ON_PLEX`; if only some are, the remaining seasons are requested
  and RESULT notes what was skipped
- A season only counts as "on Plex" when the server holds every episode of
  the season (per Sonarr, unaired included) — half-transferred seasons are
  downloaded rather than skipped, and still-airing seasons keep tracking as
  IN_PROGRESS instead of freezing at ON_PLEX. When the series is unknown to
  Sonarr, any local episodes count as having the season.

The check is read-only and fail-open: if Plex is unreachable, a warning is
logged and the run proceeds without the check. The seedbox side is already
covered — Sonarr/Radarr know what exists there.

The seedbox must be able to reach the Plex server (public Plex port,
VPN/Tailscale, etc.).

---

## Title vs ID Validation

To prevent incorrect downloads caused by mistyped IDs:

- PlexRequest fetches the canonical title for the provided ID
- It compares the normalized canonical title to the user-provided title
- If they do not match:
  - The request is not processed
  - STATUS is set to `NEEDS_ID`
  - RESULT explains what the ID actually refers to

This prevents cases like entering the wrong Serenity TMDB ID and downloading a different movie.

---

## Configuration

All configuration is done via environment variables.

### Google Sheets  
```
GOOGLE_APPLICATION_CREDENTIALS=/path/to/service-account.json
GOOGLE_SHEET_ID=...
GOOGLE_SHEET_RANGE=Requests!A3:F
GOOGLE_SHEET_START_ROW=3
```

### Sonarr  
```
SONARR_URL=https://sonarr.example.com
SONARR_API_KEY=...
SONARR_ROOT_TV=/path/to/TV
SONARR_ROOT_ANIME=/path/to/Anime
SONARR_QUALITY_PROFILE=HD - 720p/1080p
```

### Radarr 
```
RADARR_URL=https://radarr.example.com
RADARR_API_KEY=...
RADARR_ROOT_MOVIES=/path/to/Movies
RADARR_QUALITY_PROFILE=HD - 720p/1080p
```

### Local Plex (optional)

```
PLEX_URL=http://plex.example.com:32400
PLEX_TOKEN=...
PLEX_PROXY=http://localhost:1055
PLEX_NAME=DS1019

PLEX2_URL=http://localhost:24158
PLEX2_TOKEN=...
PLEX2_PROXY=...
PLEX2_NAME=Seedbox
```

Leave `PLEX_URL`/`PLEX_TOKEN` unset to disable the library check entirely.
Servers are checked in order (PLEX first, then PLEX2). `PLEX2_*` is an
optional second server — e.g. the seedbox's own Plex, which catches files
on the seedbox that Sonarr/Radarr no longer track. `PLEX2_TOKEN` defaults
to `PLEX_TOKEN` (an account token works on every server you own).

`*_PROXY` (optional) routes only that server's traffic through an HTTP
proxy — useful when reaching home over a Tailscale userspace proxy.
`*_NAME` (optional) is the server name used in RESULT messages, e.g.
"Already on DS1019 (S1-S8)"; defaults to "local Plex" / "seedbox Plex".

These are typically sourced from a `.env` file when running under cron.

---

## Execution Model

PlexRequest is designed to be run repeatedly.

Typical usage:
- Run every 5–15 minutes via cron
- Each run:
  - Processes only eligible rows
  - Updates progress
  - Skips completed or ignored requests
- Safe to run concurrently with manual sheet edits

The tool makes no assumptions about timing or ordering.

---

## What PlexRequest Does Not Do

- Move files off the seedbox
- Deduplicate against external storage (beyond the optional read-only Plex check)
- Manage Plex metadata
- Provide a UI beyond the Google Sheet

Those actions are intentionally manual or handled elsewhere.

---

## Design Philosophy

- Explicit is better than implicit
- IDs are authoritative
- Users must opt into ambiguity (by fixing IDs)
- The sheet is the source of truth
- Re-running should never cause harm

This makes PlexRequest suitable for unattended operation while remaining understandable and controllable by humans.
