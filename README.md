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
- Adds the request to Sonarr or Radarr if appropriate
- Writes progress and completion status back to the sheet
- Ignores completed or intentionally skipped entries on future runs

PlexRequest does not move files, manage Plex, or interact with local storage. It only manages requests on the seedbox.

---

## Google Sheet Layout

The sheet must follow this column layout:

| Column | Name   | Description |
|------|--------|-------------|
| A | TITLE | Human-readable title (for users) |
| B | TYPE | `TV`, `ANIME`, or `MOVIE` |
| C | ID | Required ID (TVDB for TV/Anime, TMDB for Movie) |
| D | RESULT | Output written by PlexRequest |
| E | STATUS | Request lifecycle state |

Example range:
```Requests!A3:E``` <br/>
Row 1–2 are typically headers or instructions and are ignored.  

---

## ID Requirements (Important)

PlexRequest requires IDs to avoid ambiguity.

- TV / ANIME requests require a **TVDB ID**
- MOVIE requests require a **TMDB ID**

Title-only requests are not supported.

If the ID does not exist or does not match the title, the request will not be processed.

---

## STATUS Lifecycle

The STATUS column controls how requests are handled.

### Active / Reprocessed States

- blank or `NEW`  
  The request is eligible for processing.

- `NEEDS_ID`  
  The request is missing or has an invalid/mismatched ID.  
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

Terminal states are never reprocessed.

---

## Behavior by Type

### TV / ANIME (Sonarr)

- Matches existing series by TVDB ID
- Adds new series using the provided TVDB ID
- Tracks progress by counting non-special episodes (`seasonNumber > 0`)
- Marks `DONE` when all normal episodes have files
- Ignores specials entirely

### MOVIE (Radarr)

- Matches existing movies by TMDB ID
- Adds new movies using the provided TMDB ID
- Marks `DONE` once the movie file exists

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
GOOGLE_SHEET_RANGE=Requests!A3:E
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
- Check a local NAS or Plex library
- Deduplicate against external storage
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
