# ADR-0001: Approved target stack and dependency direction

- Status: Accepted default; shell implementation complete; platform and release gates remain
- Date: 2026-09-03 UTC
- Decision owners: Product owner and engineering
- Evidence: `[STACK]`, `[ARCH]`, `[UX]`, and the matrix in `../refactor-proposal.md`

## Context

The product must leave Python GUI technology behind while preserving the tested downloader, privacy, and rollback contracts. The stack evaluation scored Avalonia/.NET 436/500 and Tauri 2/Rust 396/500. Those scores are product-specific decision aids, not benchmarks or proof of an unbuilt application.

## Decision

Build the target in C# on .NET 10 LTS (`net10.0`) with a thin Avalonia 12.x presentation shell. The current implementation pins the .NET SDK to `10.0.400` in `global.json` and Avalonia to `12.1.2` in `Directory.Packages.props`. The target is:

- `Rip.Core`: pure domain/application records, policies, events, ports, and deterministic tests.
- `Rip.Infrastructure`: explicit yt-dlp, FFmpeg, filesystem, browser-session, local-opener, environment, process, and redacted-observer adapters.
- `Rip.App`: Avalonia shell and the manual composition root.
- Separate deterministic tests for Core, adapters, application orchestration, and presentation.

Dependency direction is one-way: App/presentation depends on Core; Infrastructure depends on Core ports and implements them; the composition root selects concrete adapters. Core never references Avalonia, any UI toolkit, yt-dlp, FFmpeg, process APIs, browser APIs, network APIs, or concrete filesystem APIs. There is no DI container; composition is explicit and manual. Views never access provider, session, network, or process handles directly.

The target GUI explicitly excludes Python GUI frameworks, Tkinter, PySide6, webview shells, and a Python sidecar. Tauri 2/Rust remains a contingent alternative only if a later decision reopens the stack after WebView, sidecar-permission, runtime, and accessibility gates fail or materially change. It is not the selected target.

## Consequences

The migration gets typed contracts and testable policy seams without carrying Python GUI/runtime coupling into the product. Avalonia's controls are not claimed to be native Win32 or GTK controls; UI Automation, AT-SPI2, keyboard, scaling, and screen-reader behavior must be tested on declared platforms. The existing `app.py` remains the runnable rollback reference until explicit removal criteria pass.

## Validation and release gates

- Pin and record the exact .NET SDK and Avalonia patch with reproducible restore/build inputs.
- Exercise a Windows 10/11 x64 and Linux x64 Ubuntu/Debian-family X11/XWayland spike, including keyboard/focus, UIA/AT-SPI2, process cancellation, and startup.
- Do not infer package support, artifact footprint, startup time, native-control behavior, or accessibility results from documentation or the score matrix.
- A failed spike reopens the stack decision; it does not authorize a silent Python GUI or Tauri substitution.
