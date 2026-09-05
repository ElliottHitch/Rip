# Local tool manifest and provenance

The Avalonia app does not download or discover runtime tools. Before a media run, it validates an operator-supplied manifest for yt-dlp, Deno, FFmpeg, and FFprobe. A missing, malformed, unverified, hash-mismatched, or wrong-RID entry leaves that capability unavailable and keeps Start disabled.

The manifest is named `rip.tools.json`. By default, the app reads it beside the application binary. Set `RIP_TOOL_MANIFEST` to select another manifest. Use an absolute path for this override so the selected file is unambiguous. Relative executable paths in the manifest resolve from the manifest directory.

This file is configuration, not proof of provenance. Keep the source release page, downloaded asset name, version, target RID, release checksum, manifest checksum, API checksum, and verification record in the release evidence for the same artifact. Do not put credentials, cookies, signed URLs, private repositories, or profile paths in the manifest or its evidence.

Required top-level fields

- `schemaVersion`: `1`.
- `executionTargetRid`: omit to use the current runtime RID, or set it to the exact RID for this app instance, such as `linux-x64`, `win-x64`, or the host's `linux-arm64`. The app rejects a mismatched RID.
- `allowedRepositories`: one or more HTTPS repository URLs. Use only approved public source repositories. HTTP and duplicate entries are rejected.
- `tools`: a map keyed by `YtDlp`, `Deno`, `Ffmpeg`, and `Ffprobe`.
- `trustedExpectations`: a separate map with a matching entry for every tool. An expectation without a matching tool is rejected.

Each `tools` entry must contain:

- `key`, matching the map key exactly.
- `assetName`, the release asset name.
- `sourceRepository`, which must appear in `allowedRepositories` and use HTTPS.
- `version` and `targetRid`.
- `expectedSha256`, a 64-character SHA-256 digest for the executable.
- `isVerified: true`.
- `executablePath`, absolute or relative to the manifest directory.
- `manifestSha256` and `apiSha256` when those independent release digests are available. When present, each must match `expectedSha256`.

Each `trustedExpectations` entry repeats the approved `key`, `assetName`, `sourceRepository`, `version`, `targetRid`, and `expectedSha256`, with `requireVerified: true`. Keeping this map separate prevents a candidate file from approving itself.

Create a manifest from approved artifacts

1. Choose a target RID and obtain the official release assets through the approved provenance process. The current initial support rows are `linux-x64` and `win-x64`; `linux-arm64` is useful for this host but is not an initial support claim.
2. Record the exact asset URL or release reference in release evidence. Use only an approved HTTPS source. Do not enable remote yt-dlp components or silently download missing tools from the app.
3. Store the executable files in a controlled directory. On Linux, make each executable runnable and check it without adding the directory to an untrusted search path:

   ```sh
   chmod u+x yt-dlp_linux deno ffmpeg ffprobe
   ./yt-dlp_linux --version
   ./deno --version
   ./ffmpeg -version
   ./ffprobe -version
   sha256sum yt-dlp_linux deno ffmpeg ffprobe
   ```

   On Windows PowerShell, use the corresponding `.exe` assets and:

   ```powershell
   Get-FileHash .\yt-dlp.exe -Algorithm SHA256
   Get-FileHash .\deno.exe -Algorithm SHA256
   Get-FileHash .\ffmpeg.exe -Algorithm SHA256
   Get-FileHash .\ffprobe.exe -Algorithm SHA256
   .\yt-dlp.exe --version
   .\deno.exe --version
   .\ffmpeg.exe -version
   .\ffprobe.exe -version
   ```

4. Compare the computed digest with the official release digest and record both values, the source release, verifier, date, executable filename, and target RID. Do not mark a file verified when the comparison is unavailable.
5. Write the manifest with the same target RID, exact versions, source repositories, and digests in both maps. Keep the manifest beside the app or set `RIP_TOOL_MANIFEST` to its path.
6. Start the app and select `Test Environment`. It must report `Available` for yt-dlp, Deno, FFmpeg, FFprobe, and Runtime before Start becomes available. A successful version probe proves local discovery and validation only. It does not prove live service access or media compatibility.

The loader accepts JSON only. Comments, trailing commas, an unknown schema version, a missing repository list, a missing expectations map, duplicate tool keys, a mismatched RID, a non-HTTPS repository, or an invalid path fail closed. The validator also checks that the executable is a regular file, executable on Unix, present, and byte-for-byte equal to the trusted SHA-256 value.

Deno and yt-dlp policy

The app passes the configured Deno executable as a local `deno:` runtime and adds `--ignore-config`, `--no-js-runtimes`, and `--no-remote-components` to yt-dlp invocations. It rejects config/plugin/remote-component arguments. Missing Deno is reported as an unavailable capability. The app does not enable `--remote-components ejs:npm` or `ejs:github` and does not fetch a replacement at runtime.

The first release still needs an owner-approved Deno version/provenance decision, a clean-install check, and a restricted-permission review. FFmpeg and FFprobe remain explicit local tools. FFmpeg is used by the current processing path; FFprobe is currently checked and reported but is not used for semantic output validation.

Safe failure behavior

- Missing or invalid manifest: capabilities are unavailable and a run cannot start.
- Wrong target RID: the manifest is rejected. Cross-compiling an app does not qualify its tools or runtime on the target OS.
- Version command fails or reports below the Deno floor of `2.3.0`: the capability is unavailable.
- Tool hash, repository, asset, or trusted expectation mismatch: the capability is unavailable.
- Tool diagnostics are reduced to safe statuses. Raw command lines, signed URLs, browser data, and credentials must not enter the UI or logs.

Never copy a real browser profile, cookie file, token, signed stream URL, or credential into a manifest, fixture, support bundle, or release directory.
