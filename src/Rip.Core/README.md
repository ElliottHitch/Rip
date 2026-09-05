# Rip.Core

Core defines download requests, media plans, lifecycle state, error types, and application ports. It has no UI, network, process, or filesystem dependencies.

`DownloadApplicationService` coordinates provider resolution, separate stream downloads, media processing, and publication through those ports. Infrastructure implements the side effects; App binds the use case to Avalonia controls. See [the repository layout](../../docs/development.md).
