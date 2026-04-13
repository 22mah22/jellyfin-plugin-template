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
Use the dedicated in-app route:

`#!/gif-generator`

If a user opens this route without a valid Jellyfin session token/current user context, the page immediately redirects to Jellyfin login and returns to the same route after sign-in.

If item-level actions are needed in the future, they can be reintroduced in a separate, explicitly versioned enhancement pass.

## User Access Contract

- **Canonical UI path:** the authenticated user generation experience is `#!/gif-generator`.
- **Authentication source:** UI access and API calls use Jellyfin session context from `ApiClient` (session token + current user).
- **API contract:** plugin API endpoints remain protected with `[Authorize]`; no anonymous plugin endpoints are exposed.
- **Separation of concerns:** `Configuration/configPage.html` is for admin-managed defaults only, while GIF generation is user-facing via the canonical route.
- **Optional enhancements:** item-detail actions or similar entry points are optional helpers, must be non-blocking, and must never replace `#!/gif-generator` as canonical.

### Future UI Addition Checklist

- Must tolerate missing/renamed DOM hooks without breaking core generation flow.
- Must not rely on aggressive mutation polling loops to function.
- Must degrade gracefully (feature absent is acceptable; user route and API remain functional).

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

## IChannel POC (non-functional)

This plugin includes a minimal Jellyfin channel proof-of-concept named **Plugin Demo Channel**.

- The channel exposes one static item: **POC Item (No Playback)**.
- The channel item is intentionally non-functional (no real stream URL, no transcoding, no library writes, no background jobs).
- Temporary informational logs are emitted when channel query and media-info callback methods are invoked.

### Registration and behavior

- `ServiceRegistrator` registers `DemoChannel` as an `IChannel` service at startup.
- `DemoChannel.GetChannelItems(...)` returns a single static media item.
- `DemoChannel.GetChannelItemMediaInfo(...)` returns a deterministic placeholder media source using `plugin-demo://not-implemented`.

### Validation checklist (local Jellyfin instance)

1. Install and load this plugin in Jellyfin.
2. Login as a standard non-admin user.
3. Navigate to Channels and confirm **Plugin Demo Channel** is visible.
4. Open **POC Item (No Playback)** and confirm the placeholder/non-functional outcome (no crash).
5. Confirm server logs contain the channel invocation lines.

### Proof artifact: log snippet

A sample log snippet is captured at `docs/ichannel-poc-log-snippet.txt`.

### Proof artifact: screenshot

A runtime screenshot must be captured from a live Jellyfin instance during manual validation. This repository change set does not include a generated screenshot because no Jellyfin server/web-client runtime is available in this execution environment.
