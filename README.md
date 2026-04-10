# Jellyfin GIF Generator Plugin

This repository now contains a Jellyfin plugin that lets authenticated users generate GIF clips from local video library items using Jellyfin's configured ffmpeg binary.

## Features

- Authenticated API for GIF creation.
- Uses Jellyfin's configured encoder path (`EncoderAppPath`) so generation runs through the server ffmpeg setup.
- Input parameters: source video item id, clip start time, and clip length.
- Secure GIF download endpoint for generated files.
- Configurable maximum GIF length, default FPS, and default width.

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
  "fps": 12
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

## Configuration UI

Open the plugin settings page in Jellyfin to control:

- Maximum GIF length (seconds)
- Default FPS
- Default width

The same page now includes a **Test GIF Generation** form so you can quickly exercise the API from the Jellyfin dashboard (while authenticated).

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
