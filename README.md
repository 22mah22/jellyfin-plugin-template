# Jellyfin GIF Generator Plugin

This repository now contains a Jellyfin plugin that lets authenticated users generate GIF clips from local video library items using Jellyfin's configured ffmpeg binary.

## Features

- Authenticated API for GIF creation.
- Uses Jellyfin's configured encoder path (`EncoderAppPath`) so generation runs through the server ffmpeg setup.
- ffmpeg discovery supports Jellyfin Docker/common Linux paths when `EncoderAppPath` is unset (for example `/usr/lib/jellyfin-ffmpeg*/ffmpeg` and `/usr/bin/ffmpeg`).
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
