# UnifiDownloader.Core

This assembly is the framework-independent contract and policy boundary for the downloader. It intentionally contains no Avalonia, UI toolkit, yt-dlp, FFmpeg, process, browser, network, or concrete filesystem dependency.

The implementation stops at ports and pure state/policy decisions. A later Infrastructure phase owns executable/process arguments, media work, staging/publication, browser-session integration, and local opening; a later App phase owns Avalonia and manual composition. Browser selections are opaque in-memory choices and are never serialized by Core. `app.py` remains the runnable rollback path until an explicit cutover gate.
