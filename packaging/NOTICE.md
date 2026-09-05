# External components

Rip's Windows installer contains the self-contained .NET runtime, Avalonia, Velopack, and their runtime dependencies. Exact NuGet versions are recorded in the project lock files. Test packages are not part of the installer.

| Component | Upstream source |
| --- | --- |
| .NET | https://github.com/dotnet/runtime |
| Avalonia | https://github.com/AvaloniaUI/Avalonia |
| Velopack | https://github.com/velopack/velopack |
| yt-dlp | https://github.com/yt-dlp/yt-dlp |
| Deno | https://github.com/denoland/deno |
| FFmpeg and FFprobe build | https://github.com/GyanD/codexffmpeg |

Rip downloads yt-dlp, Deno, FFmpeg, and FFprobe directly from upstream on first launch. They are not included in the installer. Their versions, download URLs, and hashes are in `src/Rip.App/Setup/tool-bootstrap.json`.

Review the upstream licenses and notices when changing dependencies or distributing different binaries. This inventory does not assign a license to Rip's own source code.
