# Downloader pipeline visual

`downloader-pipeline.svg` is a deterministic, static README visual. The SVG is both the deliverable and the editable source; it uses only local vector shapes and text, with no external media, fonts, URLs, or hosted services.

## Storyboard

- Header: identifies the Unifi-ready MP4 pipeline and states the quality gate: resolve, recover within a bound, then publish after FFmpeg and output checks.
- 01 Input URL: accepts a single-video URL; playlist input is rejected by the single-video contract.
- 02 Resolve metadata + formats: yt-dlp extracts metadata and selects separate video and audio formats.
- 03 Bounded retry / re-resolve: the recovery gate highlights one fresh metadata/format refresh with a 5-second cancellation-aware backoff. HTTP 429 is explicitly not auto-retried.
- 04 Download streams: video and audio are downloaded separately; partial stream files are cleaned up on failure or cancellation.
- 05 Remux or transcode: streams are packaged as H.264/AAC MP4 at the supported 24/25/30 FPS target, using CPU x264 when the NVENC path is unavailable or fails.
- 06 Publish atomically: FFmpeg writes to a destination-filesystem staging path, then the app publishes without overwriting an existing filename and verifies a non-empty MP4.
- HTTP 403 branch: one fresh stream-detail resolution is attempted as best-effort recovery. The diagram explicitly says this does not bypass access, login, age, region, policy, PO-token, or rate restrictions.
- Retry exhausted branch: failure remains visible and exposes Try Again rather than claiming success. The branch notes that 403 causes are service-dependent and should be diagnosed by checking availability and waiting before another request.

The visual is intentionally static: it communicates control flow and recovery semantics more reliably than an animation, and its compact SVG renders directly in GitHub Markdown.

## Reproduce and inspect

From the repository root:

```sh
mkdir -p /tmp/pipeline-asset-check
convert -background none docs/assets/pipeline/downloader-pipeline.svg /tmp/pipeline-asset-check/downloader-pipeline.png
identify docs/assets/pipeline/downloader-pipeline.svg /tmp/pipeline-asset-check/downloader-pipeline.png
```

Expected render metadata from ImageMagick 6.9.11-60 on the build host:

- SVG: 1200x560, 10,573 bytes at creation/inspection time.
- PNG inspection render: 1200x560, 183,082 bytes at the verified render.

The PNG is an inspection artifact only and is not committed; regenerate it when reviewing the SVG.
