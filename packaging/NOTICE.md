Third-party notices inventory

This is an engineering provenance inventory, not legal clearance for redistribution.

- Unifi Downloader application: repository-local source, version recorded in PROVENANCE.json.
- .NET SDK: exact version is recorded in PROVENANCE.json and global.json.
- Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent: exact pinned versions are recorded in PROVENANCE.json and Directory.Packages.props.
- yt-dlp: official standalone executable provenance is recorded in tool-provenance.json. It is not bundled by this workflow.
- Deno: external operator-provided runtime. It is not bundled; version, digest, license inventory, and clean-install evidence remain release gates.
- FFmpeg and FFprobe: external operator-provided tools. They are not bundled; exact build, configure flags, license mode, and digest remain release gates.

No signing, installer publication, update service, or live-media qualification is performed by the local packaging workflow. Before redistribution, review the licenses and source-offer obligations for the exact binaries selected by release engineering.
