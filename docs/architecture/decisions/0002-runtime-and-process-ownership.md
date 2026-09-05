# ADR-0002: Runtime, yt-dlp, and media-process ownership

- Status: Accepted default; operational and release gates remain
- Date: 2026-09-03 UTC
- Decision owners: Engineering, release, and legal for redistribution questions
- Evidence: `[STACK]`, `[PKG]`, and sources S7-S10 and S28-S32 in `../refactor-proposal.md`

## Decision

The .NET target launches the official per-platform standalone yt-dlp executable. Each release records the exact version, source, hash, provenance, notices, and update owner. Invocation uses .NET `ProcessStartInfo.ArgumentList`, `UseShellExecute=false`, redirected stdout/stderr, asynchronous bounded reads, and platform-specific descendant termination. Shell interpolation is forbidden. Raw argv, source URLs, signed stream URLs, cookies, profile paths, authorization values, and unfiltered child diagnostics never cross the safe observer boundary.

Yt-dlp work remains three separate operations: metadata resolution, video stream, and audio stream. The adapter preserves one-video-only rejection, the existing format policy, one bounded fresh resolution after a stream HTTP 403, and no application-level automatic HTTP 429 recovery. Yt-dlp's own bounded retry behavior must remain distinguishable from application policy. Only allowlisted machine-readable metadata and bounded progress are parsed.

The process ownership boundary is:

1. Core owns typed requests, media plans, safe errors, retry decisions, and lifecycle events.
2. The yt-dlp adapter owns executable arguments, metadata/stream translation, and yt-dlp-specific classification.
3. The FFmpeg adapter owns local-stream argv, remux/transcode execution, bounded output parsing, exit classification, and cancellation.
4. The publisher owns staging, non-overwrite publication, verification, collision handling, and cleanup.
5. The composition root now loads an explicit local `rip.tools.json` manifest. The default location is beside the application binary, with `RIP_TOOL_MANIFEST` as an override. Relative executable paths resolve from the manifest directory. Schema version, HTTPS repositories, separate trusted expectations, verified SHA-256 values, and the current execution RID are required. Missing or invalid configuration produces safe unavailable capabilities and cannot start a run. The app never downloads a missing tool or treats PATH discovery as approval. See [`docs/tool-manifest.md`](../../tool-manifest.md) for the operator and provenance procedure.

FFmpeg receives selected local stream paths and a typed media plan, never a URL or browser-session object. FFmpeg is an explicit external/system prerequisite for the first release with a truthful diagnostic. Bundling is deferred until a separate decision records source, version, configure flags, architecture, hash, license/notices, source-offer obligations, patent policy, security owner, and update cadence. FFprobe is diagnostic initially; semantic MP4/Unifi compliance is an open product/release gate.

EJS/runtime behavior is local and explicit. Development/runtime receives a diagnosed pinned JavaScript runtime path. A release spike may package a versioned Deno runtime beside the official yt-dlp executable only after license, provenance, permission, and clean-install checks. Missing Deno is a truthful capability error. Remote component fetches are disabled: never enable `--remote-components ejs:npm` or `ejs:github` implicitly.

## Consequences

The target contains no embedded Python yt-dlp runtime and no Python sidecar. Process failures, cancellation, stream deadlocks, and descendant cleanup become testable adapter contracts rather than UI concerns. A missing external prerequisite is visible and actionable instead of silently downloaded or substituted.

## Validation and release gates

- Verify exact argv/options with deterministic fakes and redaction assertions.
- Test stdout/stderr draining, bounded cancellation, process-tree cleanup, and encoding on Windows and Linux.
- Prove yt-dlp/EJS/Deno discovery and restricted permissions in offline/local fixtures.
- Record FFmpeg licensing/provenance and semantic FFprobe/Unifi requirements before release.
- No live YouTube, authentication, browser profile, package publication, or release operation is part of this ADR freeze.
