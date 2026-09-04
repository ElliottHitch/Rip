# YouTube downloader for Unifi Connect

This repository is migrating from the preserved Python/Tkinter launcher to a .NET 10 desktop application with an Avalonia shell. The target entry point is `src/UnifiDownloader.App`, a single-window shell over typed Core and Infrastructure boundaries. The shell gate is implemented and deterministic tests pass, but platform, packaging, live-media, and release gates remain open. It is not a release artifact.

Use the app only for videos you are allowed to access, download, and use. It does not bypass login, age, region, policy, PO-token, rate, or other service restrictions. The preserved `app.py` path is a rollback/reference path during migration, not the target GUI.

## Target behavior and contracts

The shell exposes these controls and policies. It uses an explicit local tool manifest and fails closed when the manifest or any required capability is missing. Deterministic local evidence is not proof that a live provider or media run succeeds.

- One single-video URL per download. Playlist and multi-video extractor results are rejected.
- Metadata, Video, and Audio are separate run choices. Metadata resolves safe information only and publishes no media.
- Separate video and audio stream downloads (or one progressive stream when that is the only available option), followed by generic lossless remuxing or compatibility transcoding.
- An output folder field for the destination. Generic Matroska output is the default; UniFi MP4 is an explicit opt-in compatibility profile.
- Output names based on the video title. Filenames are cleaned for filesystem use. An existing destination is rejected with a safe publication-conflict message; the publisher never overwrites it or adds a collision suffix.
- Compatibility mode preserves source rates already at 24, 25, or 30 FPS and converts unsupported or unknown rates to 30 FPS. Generic mode preserves the source frame rate.
- A Test Environment action that reports safe local statuses for yt-dlp, Deno, FFmpeg, FFprobe, and the .NET runtime.
- A Cancel action, visible stages, progress when a size or duration is available, and an Activity Log with URL and credential-like values redacted from error details.
- An optional, per-download browser-session control. When explicitly enabled, yt-dlp reads the selected local browser session through its supported `cookiesfrombrowser` option; the default path reads no browser data.
- An `Open in Browser` action that opens only the verified, completed local output through the operating system's default handler.

When enabled, the app targets this UniFi compatibility contract:

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

1. Enter a single-video URL. For Video or Audio, enter a destination folder and file stem. Browser-session access is off by default.
2. Optionally check `Use browser session`, review the disclosure, and choose one supported browser. This permission applies to this download only; the shell accepts no profile field.
3. yt-dlp fetches metadata and selects a dedicated video/audio pair, falling back to one progressive format when necessary.
4. yt-dlp stages the selected signed media streams into temporary files; the default path does not send signed URLs through a second HTTP client.
5. FFmpeg losslessly remuxes generic output or transcodes it to the explicit UniFi compatibility profile. The current target adapter uses CPU x264; GPU acceleration is not qualified by this shell gate.
6. FFmpeg writes to a staging file. The publisher copies the owned source to a private temporary file in the selected destination, verifies its positive exact length, and commits it with a same-directory rename that does not overwrite an existing file. An existing destination is a safe publication conflict, not a request for a suffix or overwrite.
7. The app re-verifies the final non-empty MP4 or Matroska file before reporting completion. It then cleans and unregisters the source when possible. If cleanup fails after the final file is committed, the verified output is preserved and the app reports a cleanup warning. `Open in Browser` is enabled only after the final output passes its separate verification gate.

Publication uses the selected destination as an existing regular folder. Invalid destinations, unsafe file stems, and overwrite requests fail safely. The same-directory rename is the publication commit step, but this local contract does not claim whole-operation atomicity or hard-link support across all filesystems. Filesystem races between validation and later operations, live provider/media behavior, and platform-specific opener behavior remain outside this verification gate.

## Prerequisites

The target shell uses the .NET SDK pinned in `global.json`, currently `10.0.400`, and Avalonia `12.1.2` packages. It does not download or discover runtime tools. Supply an approved `unifi-downloader.tools.json` manifest before a media run. The default manifest location is beside the application binary; `UNIFI_DOWNLOADER_TOOL_MANIFEST` selects another file. Relative executable paths resolve from the manifest directory. Missing, malformed, unverified, hash-mismatched, or wrong-RID entries produce unavailable capabilities and keep Start disabled.

The legacy Python path pins `yt-dlp[default]` so its EJS scripts are installed. YouTube extraction also requires a supported local JavaScript runtime; Deno is the recommended/default runtime. See the official [yt-dlp dependencies](https://github.com/yt-dlp/yt-dlp#dependencies) and [EJS wiki](https://github.com/yt-dlp/yt-dlp/wiki/EJS). The app checks EJS and Deno readiness locally before starting a download; it does not enable remote components or download runtimes/scripts automatically.

The manifest must name yt-dlp, Deno, FFmpeg, and FFprobe with exact versions, target RID, HTTPS source repository, executable path, SHA-256 digest, and `isVerified: true`. A separate `trustedExpectations` entry is required for each tool. The loader rejects HTTP repositories, duplicate or unmatched entries, comment/trailing-comma JSON, and a manifest for another RID. Follow [the tool manifest and provenance guide](docs/tool-manifest.md); do not put credentials, cookies, signed URLs, or profile paths in the file.

The initial support rows are Windows 10/11 x64 and Linux x64 on the Ubuntu/Debian family using X11 or XWayland, subject to platform evidence. ARM64, native Wayland, other distributions, and macOS are not claimed. The shell's Avalonia controls are not claimed to be native Win32 or GTK controls. [The release runbook](docs/release-runbook.md) separates local tests, cross-publishing, native qualification, signing, publication, rollback, and authorized live-media checks.

## Build and run the Avalonia shell

From the repository root, restore the pinned dependency graph and start the target project:

```sh
dotnet restore --locked-mode
dotnet run --project src/UnifiDownloader.App/UnifiDownloader.App.csproj
```

For a Release build:

```sh
dotnet build --configuration Release --no-restore
```

## Reproducible local packages

The unsigned, self-contained directory package workflow is in `packaging/README.md`. It uses only the pinned SDK and Python standard library, performs locked restore, publishes `linux-arm64`, `linux-x64`, or `win-x64`, validates a clean copied install, emits `PROVENANCE.json`, `SBOM.spdx.json`, `NOTICE.md`, and checksums, and creates a fixed-metadata Linux tar.gz or Windows ZIP. For example:

```sh
./packaging/publish.sh --rid linux-arm64
```

The workflow never downloads or bundles yt-dlp, Deno, FFmpeg, or FFprobe. It records their versions, repositories, target assets, digests, and blockers in `tool-provenance.json`; the application still requires a separately approved `unifi-downloader.tools.json` with verified local paths and trusted expectations. Cross-published x64 artifacts are not native runtime qualification. Signing, publication, updates, rollback exercise, installer packaging, and live-media tests remain separate release gates.

The window has Request, Output, Browser session, Environment, Run controls, Activity, Completion, and status regions. Choose one operation: `Metadata`, `Video`, or `Audio`. Metadata disables output controls and reports safe metadata without publishing a file. Video and Audio use the output settings. The frame-rate compatibility policy preserves an allowed source rate or converts unsupported and unknown rates to 30 FPS; the UI no longer exposes a manual frame-rate selector.

When a desktop storage provider is available, `Choose Output Folder` opens the platform folder picker after the window is ready. It allows one folder, returns only a safe local filesystem path, and uses the current typed path only as a best-effort suggested location. The output field remains editable. Canceling the dialog, returning an unsupported URI, or failing to initialize the provider leaves the field unchanged and explains that you can type a destination. The control is disabled for Metadata and while a run is active. Native picker behavior still needs interactive qualification on the declared target platforms.

`Test Environment` reports safe local capability statuses for yt-dlp, Deno, FFmpeg, FFprobe, and the .NET runtime. It does not start a download. Start remains disabled until all required capabilities report `Available` and the form is valid. A successful build, manifest load, or environment probe does not prove live provider access, real-media behavior, native handlers, accessibility, packaging, or release readiness.

The current candidate package shape is an unsigned, self-contained per-target directory. Use the [release runbook](docs/release-runbook.md) for reproducible `dotnet publish` commands and the evidence required before a ZIP, tar.gz, installer, signing operation, update, or publication. Cross-publishing from this Linux ARM64 host does not qualify Windows x64 or Linux x64 runtime behavior.

## Optional browser-session access

Browser-session access is a local, explicit opt-in for a single download. Keep it off unless you need the session already authorized in your own browser. The Avalonia shell offers `Chromium`, `Chrome`, `Edge`, and `Firefox`, and requires one of those choices when consent is on. It accepts no profile path or cookie-file input. The app never asks for a password, automates login or CAPTCHA, exports cookies, writes a cookie file, persists browser data, uploads browser data, rotates proxies, spoofs fingerprints or headers, or bypasses login, age, region, policy, PO-token, rate, or other service restrictions.

When enabled, the provider adapter receives only the selected browser kind and uses the supported local `cookiesfrombrowser` mechanism. No cookie values, profile paths, signed URLs, or authentication diagnostics cross the safe presentation boundary. The consent checkbox and browser choice are locked while a run is active and are cleared after success, failure, cancellation, or `Start New Run`.

Browser-session availability depends on the installed browser, OS permissions, profile locking, keyring/decryption support, and yt-dlp support. The shell has no live browser-profile qualification. A session that loads successfully still does not guarantee access to a video or recovery from service restrictions.

## Open in Browser versus browser-session access

These are separate actions. `Open in Browser` is post-download only.

- Browser-session access affects yt-dlp metadata and stream requests only after explicit consent. It is not a bot-detection workaround or a guarantee of access.
- `Open in Browser` runs only after a non-empty final MP4 has been published and verified. It sends an encoded local `file://` URI to the default browser and makes no YouTube request. It does not retry a download, repair a failed metadata request, solve bot detection, or change the access/session setting.

On headless systems or systems without a default file handler, opening may fail. The shell reports a safe opener failure without exposing the URI or full path. If the completed file has been moved or deleted, the action is disabled after the local verification gate fails. A completed file over 5 GB remains eligible for Open in Browser while retaining the existing Unifi-compliance warning.

## 403, 429, and retry recovery

The app separates metadata failures from stream failures.

For a stream HTTP 403:

1. The app removes partial temporary streams from the current attempt.
2. It waits up to 5 seconds in a cancellation-aware loop.
3. It fetches fresh metadata and format details once, then retries the stream download.
4. If the fresh attempt also fails, the app stops, shows the 403 explanation, and changes the action to Try Again.

This is one bounded, best-effort refresh. It can help when a previously resolved stream URL is stale. It is not a guaranteed fix and does not bypass service restrictions. A 403 can reflect access, login, age, region, policy, PO-token, rate, or other service conditions.

For HTTP 429, the app identifies the response as rate limiting and does not start its fresh-resolution retry flow. Wait before trying again and avoid repeated requests. The yt-dlp call also has its configured `retries=1` and `fragment_retries=1` settings, but the app does not add a second recovery cycle for a 429.

A metadata HTTP 403 is reported immediately with access and restriction guidance. It is not treated as a stream-refresh case. Browser-session access, when explicitly selected, uses only yt-dlp's supported local browser-cookie mechanism and does not bypass service restrictions.

## Troubleshooting

### yt-dlp is unavailable

The target composition has no approved tool configuration by default. Run `Test Environment` after supplying an approved local configuration. The shell does not download or silently substitute a provider component.

### `FFmpeg is required`

Install FFmpeg for your operating system, record its exact executable path and approved SHA-256 in `unifi-downloader.tools.json`, and rerun `Test Environment`. Adding `ffmpeg` to `PATH` alone does not satisfy the manifest gate.

### FFprobe is missing

The environment report will list FFprobe as missing. The current download path requires FFmpeg, while FFprobe is informational and is not called by the processing pipeline. Install the matching FFmpeg package if you want the environment report to be clean.

### The URL cannot be read

Check that the URL is complete, points to one video, and is available to you in the service. The app does not process playlist or multi-video results. For a metadata 403, check access and service restrictions rather than repeatedly retrying.

### Browser session unavailable

Check that the selected browser is installed and closed, and that the operating system permits local browser-session access. The shell accepts no profile path or cookie-file input and does not save or export cookies. If the browser or operating system is unsupported, choose another listed browser or turn off the browser-session option.

### Browser session loaded but access is still refused

Confirm that the video is available to the independently signed-in account and refresh the page in the same browser. A browser session does not bypass service restrictions or guarantee access. The existing bounded 403 behavior and no-automatic-429-recovery behavior remain unchanged.

### Open in Browser cannot open the file

This action opens only an already-completed local MP4 and does not contact YouTube. Confirm that the file still exists and that the operating system has a default browser/file handler. The shell reports failure without exposing the local URI or full path.

### The app reports HTTP 403 after refresh

Check that the video is available to your account and location, then wait before selecting Try Again. Do not interpret Try Again as a bypass mechanism. Live service behavior, client requirements, PO-token interactions, and rate limits can change outside this repository.

### The app reports HTTP 429

Stop making requests for a while and try later. Repeated immediate attempts can prolong rate limiting. The app does not perform its bounded 403-style refresh for 429 responses.

### FFmpeg fails or disk space is low

The app stages output in the selected destination folder, so the existing regular folder must have enough free space and support the final non-overwriting publication step. Check disk space and write permission for the selected folder, then choose Try Again. If the destination already contains the requested MP4, choose another folder or change the file stem. The app does not overwrite the existing file or create a numbered suffix. Failed FFmpeg work is staged rather than published as a final output, and partial temporary files are cleaned when possible.

### A cancellation appears to take time

Cancel changes the button to Stopping..., terminates an active FFmpeg process, and escalates to a kill after a short grace period if needed. A network call inside yt-dlp can still wait for its socket timeout before the worker returns. During the 5-second refresh wait, cancellation is checked repeatedly.

A normal cancellation reports `Download cancelled. No completed file was saved.` Temporary stream and staging files are removed when possible. If cleanup fails, the Activity Log and status include a cleanup warning, and leftover temporary files may remain. If cancellation arrives after the final output has already been published, the app preserves that output and says so instead of claiming that no file was saved.

## Development and tests

The .NET suite uses deterministic fixtures and does not contact YouTube. Run it from the repository root:

```sh
dotnet restore --locked-mode
dotnet test --configuration Release --no-restore
dotnet run --project src/UnifiDownloader.App/UnifiDownloader.App.csproj --configuration Release --no-build --no-restore -- --deterministic-smoke
git diff --check
```

The preserved Python suite remains a compatibility gate during migration:

```sh
python -m unittest discover -s tests -v
python -m py_compile app.py tests/test_pipeline.py
```

These tests cover one-video validation, metadata-only versus media operations, typed container and frame-rate mapping, bounded 403 refresh and no automatic 429 recovery, cancellation and stale-event rejection, redacted diagnostics, staged non-overwriting publication, cleanup warnings, output verification, browser-session option propagation and reset, encoded local file URIs, safe opener failures, and missing-capability states. They do not prove live service availability, real browser-profile behavior, account access, native default-handler integration on every OS, every real-media FFmpeg combination, whole-operation atomicity, or the absence of check-then-use filesystem races.

During migration, `app.py` remains the runnable legacy and rollback path until the later shell, platform, packaging, and release gates pass. Rollback means selecting a prior verified artifact or launching that preserved path; it never moves, overwrites, or deletes downloaded media.

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
- A stream 403 gets only one fresh metadata/format resolution. Optional browser-session access uses yt-dlp's supported local mechanism but does not bypass access controls or add authentication/session scraping.
- HTTP 429 is not automatically recovered through the app's bounded refresh flow.
- FFprobe is displayed by the environment check but is not currently used for output validation.
- The current target media adapter uses CPU x264. NVENC and other GPU paths are not qualified by the shell gate.
- Unknown source duration or stream size produces indeterminate progress rather than a fabricated percentage.
- The 5 GB warning is based on the final file size. The app does not prevent a file from exceeding the limit.
- Publication rejects an existing destination without overwriting it or adding a collision suffix. It copies the owned source to a private temporary file in the selected destination, verifies the copied length, commits with a same-directory non-overwriting rename, and re-verifies the final MP4. Cleanup is best effort; a post-commit cleanup warning preserves the verified output.
- The verified local contract does not claim whole-operation atomicity, hard-link support, or universal filesystem/platform behavior. Check-then-use races and later platform/opener qualification remain release gates.
- No real-media or live-service download is part of the deterministic test gate.
