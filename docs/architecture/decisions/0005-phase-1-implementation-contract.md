# ADR-0005: Phase 1 implementation boundary

- Status: Accepted historical Phase 1 boundary; Phase 1 complete; Phase 2 shell is implemented
- Date: 2026-09-03 UTC
- Decision owners: Engineering
- Supersedes: the proposal's former pending-approval wording for Phase 1

## Outcome

At the time of this decision, Phase 1 could begin immediately under ADR-0001 through ADR-0004. It created a buildable, framework-independent `UnifiDownloader.Core` and deterministic tests; it did not create a runnable target product. Phase 1 is complete. The later Phase 2 work now provides the runnable Avalonia shell described in `../phase2-app-shell-implementation.md`.

## In scope

- C#/.NET 10 solution and exact SDK/package pinning, subject to the verified toolchain.
- Immutable request, media/output, browser-session, run identity, cancellation, error, and lifecycle-event records/enums/interfaces.
- Core ports for provider, media execution, filesystem/publication, browser-session, diagnostics, observer, clock, and process execution.
- Pure policies for one-video validation, format/FPS/passthrough decisions, safe filenames, collisions, output/publication states, bounded stream-403 retry, no automatic 429 recovery, terminal-event truth, and stale-run rejection.
- Deterministic tests that use no UI, display, network, subprocess, browser, concrete filesystem, live service, or real profile.

## Out of scope

The Phase 1 boundary did not add Avalonia UI, real yt-dlp/FFmpeg/browser/filesystem/process adapters, EJS/Deno packaging, package artifacts, CI/release workflows, launcher changes, a Python sidecar, or a compatibility bridge. Do not modify the legacy Python baseline or its tests. Those exclusions describe Phase 1 only. The current target shell is a later Phase 2 deliverable; the legacy `app.py` remains the rollback/reference path during migration.

## Completion contract

Phase 1 was complete when `dotnet build` and `dotnet test` passed for the new Core solution, dependency inspection proved Core had no forbidden implementation references, exact pins and restore instructions were reproducible, the existing 39-test Python suite remained unchanged and passing, and scope checks showed no unrelated files changed. Later phases may implement side-effect adapters and the shell, but they may not silently settle FFmpeg licensing, EJS/Deno ownership, semantic MP4/Unifi validation, support rows, accessibility tier, or release packaging.
