# Jellyfin GIF Generator Plugin

This repository now contains a Jellyfin plugin that lets authenticated users generate GIF clips from local video library items using Jellyfin's configured ffmpeg binary.

## Features

- Authenticated API for GIF creation.
- The dedicated GIF Generator page requires an authenticated Jellyfin session and redirects unauthenticated access to login.
- Uses Jellyfin's configured encoder path (`EncoderAppPath`) when available and falls back to Jellyfin-aware ffmpeg discovery (`JELLYFIN_FFMPEG`, `FFMPEG_PATH`, then `ffmpeg` on `PATH`).
- Input parameters: source video item id, clip start time, and clip length.
- Optional internal subtitle stream selection for burn-in rendering.
- Secure GIF download endpoint for generated files.
- Configurable maximum GIF length, default FPS, and default width.
- Automatic cleanup of generated GIFs in `DataPath/plugins/gif-generator/generated` based on configurable retention (default 7 days), with a minimum retention floor and count-based pruning guardrail.

## API

All endpoints require an authenticated Jellyfin user session.

### Create GIF

`POST /Plugins/GifGenerator/Create`

Example request body:

```json
{
  "itemId": "11111111-2222-3333-4444-555555555555",
  "startSeconds": 12.5,
  "lengthSeconds": 4.0,
  "width": 480,
  "fps": 12,
  "subtitleStreamIndex": 3,
  "subtitleFontSize": 22,
  "subtitleTimingOffset": "+500ms"
}
```

Example response:

```json
{
  "fileName": "11111111222233334444555555555555_20260409010203.gif",
  "downloadUrl": "Plugins/GifGenerator/Download/11111111222233334444555555555555_20260409010203.gif"
}
```

### Download GIF

`GET /Plugins/GifGenerator/Download/{fileName}`

Returns the generated GIF file.

### List subtitle streams

`GET /Plugins/GifGenerator/Subtitles/{itemId}`

Example response:

```json
{
  "itemId": "11111111-2222-3333-4444-555555555555",
  "subtitles": [
    {
      "streamIndex": 2,
      "language": "eng",
      "displayTitle": "English",
      "isDefault": true,
      "isForced": false
    },
    {
      "streamIndex": 3,
      "language": "nor",
      "displayTitle": "Norwegian",
      "isDefault": false,
      "isForced": false
    }
  ]
}
```

`subtitleStreamIndex` is optional in create requests. If provided, the selected internal subtitle stream is burned directly into GIF frames. GIF files do not support switchable subtitle tracks after generation.

Additional optional subtitle controls:

- `subtitleFontSize`: numeric font size override for burn-in subtitles.
- `subtitleTimingOffset`: offset string to shift subtitle timing relative to video.
  - Examples: `+500ms`, `-1.2s`, `+00:01.250`, `-01:02:03.5`.
  - Positive values delay subtitles, negative values show subtitles earlier.
  - Maximum absolute offset is 30 seconds.

## Configuration UI

Open the plugin settings page in Jellyfin to control:

- Maximum GIF length (seconds)
- Default FPS
- Default width
- GIF retention window (hours) used by periodic cleanup during create/download requests

Generated GIF files are cleaned up automatically when plugin endpoints are used:

- Files older than `GifRetentionHours` are deleted using UTC file timestamps.
- Retention is clamped to a safe floor/ceiling (minimum 1 hour, maximum 8760 hours).
- If storage keeps growing, cleanup also prunes oldest files beyond the built-in max file count guardrail.

Daily GIF creation is exposed as a dedicated **GIF Generator** user page in the main menu.
Use Jellyfin's resolved route format:

`#!/configurationpage?name=gifGeneratorPage`

If a user opens this route without a valid Jellyfin session token/current user context, the page immediately redirects to Jellyfin login and returns to the same route after sign-in.

Video detail-page actions can still be used as an enhancement in supported clients/layouts and now route users to the dedicated page with the current item id pre-filled.

## Installing via a custom plugin repository

Jellyfin expects a **JSON manifest URL**, not the GitHub repository home page.

Use this repository URL in **Dashboard → Plugins → Repositories**:

`https://raw.githubusercontent.com/22mah22/jellyfin-plugin-template/master/manifest.json`

If you point Jellyfin to `https://github.com/22mah22/jellyfin-plugin-template`, Jellyfin will fail with a deserialization error because that URL returns HTML, not the manifest JSON.

### What must exist in this repo

For Jellyfin to install from a custom repository, the repo must expose:

1. `manifest.json` in the plugin-repository format (array of plugin entries).
2. A downloadable plugin package zip referenced by `versions[].sourceUrl`.

### Releasing a new installable version

This repo now includes the **📦 Build Repository Package** GitHub Actions workflow (`.github/workflows/repository-release.yaml`) to automate packaging and manifest updates.

1. Open **Actions → 📦 Build Repository Package → Run workflow**.
2. Set:
   - `version` (example `1.0.1.0`)
   - `target_abi` (usually `10.11.0.0` for Jellyfin 10.11.x)
3. The workflow will:
   - Build the plugin zip.
   - Compute MD5 checksum.
   - Update `manifest.json`.
   - Commit the manifest change.
   - Create a GitHub release and upload `jellyfin-plugin-gif-generator.zip`.

After that, Jellyfin clients using the manifest URL above can install/update from the repository.
