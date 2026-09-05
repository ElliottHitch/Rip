Release gates and honest scope

The local package workflow proves only that a self-contained .NET application artifact can be produced for a requested RID, that its files can be inspected, and that checksums/provenance can be rechecked.

Not proved by this workflow:

- Native startup, desktop integration, accessibility, filesystem behavior, or live-media behavior on Windows x64 or Linux x64. The current verification host is Linux ARM64 without a desktop display.
- A working provider/media environment. yt-dlp, Deno, FFmpeg, and FFprobe are external or not qualified. The application remains fail-closed until a local rip.tools.json manifest contains verified paths, exact versions, approved HTTPS repositories, and matching trusted SHA-256 expectations.
- Semantic MP4/Unifi compliance, browser-session behavior, update/rollback exercise, installer ownership, or clean-install behavior on every supported OS.
- Signing, notarization, publication, or an automatic updater. No signing credentials are read or required.

The directory artifact must pass native clean-install and runtime checks before an installer or update channel is considered. A cross-published artifact is not native runtime qualification.
