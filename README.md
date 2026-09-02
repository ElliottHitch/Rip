# YouTube downloader for Unifi Connect

This local Tkinter desktop app downloads one YouTube video, gets the best separate video and audio formats it can resolve with yt-dlp, and produces one MP4 for a Unifi Connect workflow. FFmpeg remuxes streams that already fit the target or transcodes them when they do not.

Use the app only for videos you are allowed to access, download, and use. It does not bypass login, age, region, policy, PO-token, rate, or other service restrictions.

## What the app supports

- One single-video URL per download. Playlist and multi-video extractor results are rejected.
- Separate video and audio stream downloads, followed by remuxing or transcoding.
- A destination folder chosen in the app. The default is `~/Videos/Unifi Downloads`.
- Output names based on the video title. Filenames are cleaned for filesystem use, and an existing name is never overwritten. A collision becomes `Title (1).mp4`, then `Title (2).mp4`, and so on.
- A Test Environment action that reports yt-dlp, FFmpeg, FFprobe, and NVENC status.
- A Cancel action, visible stages, progress when a size or duration is available, and an Activity Log with URL and credential-like values redacted from error details.

The app targets this Unifi output contract:

| Property | Target |
| --- | --- |
| Container | MP4 |
| Video | H.264, High profile, `yuv420p` for transcoded output |
| Audio | AAC, stereo, 192 kbps for transcoded output |
| Frame rate | 24, 25, or 30 fps |
| Video bitrate | 40 Mbps target, with a 46 Mbps maximum rate during transcoding |
| File size | At most 5 GB as displayed by the app, implemented as `5 * 1024**3` bytes |

When both selected streams already meet the app's checks, it remuxes them without re-encoding. A remuxed stream can retain source properties that the app does not inspect, so treat the table as the app's target and check the resulting file in your own Unifi workflow. Files over the size limit are saved with a visible warning and are marked not Unifi-compliant.

## Pipeline overview

![Six-stage downloader pipeline from a single-video URL through metadata and format resolution, one bounded 403 refresh, separate video and audio downloads, remux or transcode, and non-overwriting publication of a verified MP4. HTTP 429 is not automatically re-resolved; failed recovery ends with Try Again and does not bypass service restrictions.](docs/assets/pipeline/downloader-pipeline.svg)

If the SVG is unavailable, the same flow is:

1. Enter a single-video URL and choose a destination folder.
2. yt-dlp fetches metadata and selects separate video and audio formats.
3. The app downloads the video stream and audio stream into temporary files.
4. FFmpeg remuxes compatible streams or transcodes them to the target profile. NVENC is used when the environment probe succeeds; CPU x264 is the fallback.
5. FFmpeg writes to a staging file in the destination folder. The app publishes a non-empty MP4 under a unique name without overwriting an existing file.
6. The app reports completion only after publication and output verification.

## Prerequisites

The tested environment used Python 3.11.16, Tkinter, FFmpeg 6.1.1, and FFprobe 6.1.1. The repository pins yt-dlp to `2026.8.19` in `requirements.txt`.

You need:

- Python with the `venv` module
- Tkinter for your Python installation
- `ffmpeg` on `PATH` (required to create the output)
- `ffprobe` on `PATH` (optional, but needed for a clean environment report)
- A network connection and a URL that the service makes available to you

On Ubuntu or Debian, the system packages can be installed with:

```sh
sudo apt-get update
sudo apt-get install -y python3 python3-venv python3-tk ffmpeg
```

The `ffmpeg` package provides the `ffmpeg` and `ffprobe` commands on the tested Ubuntu setup. On another operating system, install the equivalent Python/Tkinter and FFmpeg packages, then confirm that both commands are on `PATH`.

## Install and run in an isolated environment

From the repository root, create a virtual environment and install the pinned dependency:

```sh
python3 -m venv .venv
. .venv/bin/activate
python -m pip install -r requirements.txt
python app.py
```

Keep the virtual environment active when launching the app again:

```sh
. .venv/bin/activate
python app.py
```

On Windows PowerShell, use the equivalent activation command `.venv\Scripts\Activate.ps1`. The application entry point remains `app.py`. If Tkinter cannot open a window, install the Tkinter package supplied for your Python distribution and launch the app from a graphical desktop session.

When the window opens:

1. Paste a URL for one video. Playlist URLs are not supported.
2. Keep the default folder or choose another folder with Browse....
3. Select Test Environment if you want to refresh the dependency and encoder report.
4. Select Start Download.
5. Wait for the final status. Use Open Folder after completion if needed.

The first environment report may show `NVENC available: No`. That is normal on systems without a working NVIDIA encoder. The app uses CPU x264 in that case. FFmpeg is required to create the MP4. FFprobe is reported by the check, but the current pipeline does not invoke it as a required processing step.

## 403, 429, and retry recovery

The app separates metadata failures from stream failures.

For a stream HTTP 403:

1. The app removes partial temporary streams from the current attempt.
2. It waits up to 5 seconds in a cancellation-aware loop.
3. It fetches fresh metadata and format details once, then retries the stream download.
4. If the fresh attempt also fails, the app stops, shows the 403 explanation, and changes the action to Try Again.

This is one bounded, best-effort refresh. It can help when a previously resolved stream URL is stale. It is not a guaranteed fix and does not bypass service restrictions. A 403 can reflect access, login, age, region, policy, PO-token, rate, or other service conditions.

For HTTP 429, the app identifies the response as rate limiting and does not start its fresh-resolution retry flow. Wait before trying again and avoid repeated requests. The yt-dlp call also has its configured `retries=1` and `fragment_retries=1` settings, but the app does not add a second recovery cycle for a 429.

A metadata HTTP 403 is reported immediately with access and restriction guidance. It is not treated as a stream-refresh case. The app never asks for or documents cookies, credentials, or access-control workarounds.

## Troubleshooting

### `yt-dlp is required`

The virtual environment is not active, or the dependency installation did not complete. From the repository root, run:

```sh
. .venv/bin/activate
python -m pip install -r requirements.txt
```

Then choose Test Environment and try again.

### `FFmpeg is required`

Install FFmpeg for your operating system and make sure `ffmpeg` is on `PATH`. Start the app again, or choose Test Environment after fixing `PATH`.

### FFprobe is missing

The environment report will list FFprobe as missing. The current download path requires FFmpeg, while FFprobe is informational and is not called by the processing pipeline. Install the matching FFmpeg package if you want the environment report to be clean.

### The URL cannot be read

Check that the URL is complete, points to one video, and is available to you in the service. The app does not process playlist or multi-video results. For a metadata 403, check access and service restrictions rather than repeatedly retrying.

### The app reports HTTP 403 after refresh

Check that the video is available to your account and location, then wait before selecting Try Again. Do not interpret Try Again as a bypass mechanism. Live service behavior, client requirements, PO-token interactions, and rate limits can change outside this repository.

### The app reports HTTP 429

Stop making requests for a while and try later. Repeated immediate attempts can prolong rate limiting. The app does not perform its bounded 403-style refresh for 429 responses.

### FFmpeg fails or disk space is low

The app stages output in the selected destination folder, so that filesystem must have enough free space and support the final non-overwriting publication step. Check disk space and write permission for the selected folder, then choose Try Again. Failed FFmpeg work is staged rather than published as a final output, and partial temporary files are cleaned when possible.

### A cancellation appears to take time

Cancel changes the button to Stopping..., terminates an active FFmpeg process, and escalates to a kill after a short grace period if needed. A network call inside yt-dlp can still wait for its socket timeout before the worker returns. During the 5-second refresh wait, cancellation is checked repeatedly.

A normal cancellation reports `Download cancelled. No completed file was saved.` Temporary stream and staging files are removed when possible. If cleanup fails, the Activity Log and status include a cleanup warning, and leftover temporary files may remain. If cancellation arrives after the final output has already been published, the app preserves that output and says so instead of claiming that no file was saved.

## Development and tests

The test suite uses deterministic yt-dlp fixtures and does not contact YouTube. Run it from the repository root with the virtual environment active:

```sh
python -m unittest discover -s tests -v
python -m py_compile app.py tests/test_pipeline.py
git diff --check
```

The tests cover bounded 403 refresh and exhaustion, 429 handling, metadata and playlist rejection, cancellation, redacted diagnostics, staging and non-overwriting output publication, FFmpeg failure cleanup, oversized output warnings, and unknown-duration progress behavior. They do not prove live service availability, real YouTube responses, account access, PO-token behavior, or every real-media FFmpeg combination.

To inspect the approved pipeline asset locally, run this optional command from the repository root if ImageMagick is installed:

```sh
mkdir -p /tmp/pipeline-asset-check
convert -background none docs/assets/pipeline/downloader-pipeline.svg /tmp/pipeline-asset-check/downloader-pipeline.png
identify docs/assets/pipeline/downloader-pipeline.svg /tmp/pipeline-asset-check/downloader-pipeline.png
```

The generated PNG is an inspection artifact and is not part of the repository.

## Known limits

- The app handles one video at a time and rejects playlists and multi-video extractor results.
- Download success depends on current service behavior, network conditions, video availability, and the formats exposed by yt-dlp. No live-network guarantee is made.
- A stream 403 gets only one fresh metadata/format resolution. The app does not bypass access controls or add authentication/session scraping.
- HTTP 429 is not automatically recovered through the app's bounded refresh flow.
- FFprobe is displayed by the environment check but is not currently used for output validation.
- NVENC is optional. CPU x264 is the fallback, and transcoding can take longer without a supported GPU.
- Unknown source duration or stream size produces indeterminate progress rather than a fabricated percentage.
- The 5 GB warning is based on the final file size. The app does not prevent a file from exceeding the limit.
- Final publication uses a hard link from a staging file in the destination folder. Filesystem support and available disk space can affect that step.
- No real-media or live-service download is part of the deterministic test gate.
