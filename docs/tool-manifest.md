# Media tools

## Windows setup

On first launch, Rip downloads the Windows x64 tools listed in [tool-bootstrap.json](../src/Rip.App/Setup/tool-bootstrap.json). The catalog pins versions, upstream URLs, archive hashes, and executable hashes for yt-dlp, Deno, FFmpeg, and FFprobe.

Setup verifies SHA-256 hashes before enabling a tool. It stores the files in `%LOCALAPPDATA%/RipData/tools` and writes `rip.tools.json`. A changed catalog triggers setup again after an app update. Tool files stay outside the installed app's `current` directory.

An interrupted download cannot publish a partial tool manifest. Failed setup shows a retry button. A corrupt cached executable is rejected; remove its directory under `RipData/tools` before retrying setup.

## Custom manifests

Set `RIP_TOOL_MANIFEST` to an absolute path to manage tools yourself. This skips automatic setup. Without an override, Rip checks for `rip.tools.json` beside the executable, then in `RipData/tools`. Linux source builds require a custom manifest.

The manifest is strict JSON. Relative executable paths resolve from the manifest's directory.

| Field | Value |
| --- | --- |
| `schemaVersion` | `1` |
| `executionTargetRid` | The current runtime identifier, such as `win-x64` or `linux-x64` |
| `allowedRepositories` | Approved HTTPS source repository URLs |
| `tools` | Entries keyed by `YtDlp`, `Deno`, `Ffmpeg`, and `Ffprobe` |
| `trustedExpectations` | Matching expected metadata for each tool |

Each tool needs `key`, `assetName`, `sourceRepository`, `version`, `targetRid`, `expectedSha256`, `executablePath`, and `isVerified: true`. Its expectation repeats the metadata and hash with `requireVerified: true`. The hash describes the executable, not its compressed download. Optional `manifestSha256` and `apiSha256` must match that executable hash when supplied.

The two maps check consistency. They do not independently establish where a manually configured binary came from. Verify custom binaries against their upstream releases before writing the manifest. Keep local manifests out of Git because they contain machine-specific paths.

## Runtime checks

Rip verifies executable hashes, versions, target architecture, and local availability before enabling downloads. FFmpeg writes the output; FFprobe inspects its streams, duration, container, selected resolution, and UniFi properties before publication.

yt-dlp uses the configured Deno executable and ignores external yt-dlp configuration. Browser session access is off by default and applies only to a download where the user enables it. Setup downloads pinned tools; extraction does not enable yt-dlp's remote components or arbitrary plugins.
