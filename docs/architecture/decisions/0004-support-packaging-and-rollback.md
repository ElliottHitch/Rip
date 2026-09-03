# ADR-0004: Initial support tier, packaging, and rollback

- Status: Accepted initial shape; release evidence remains gated
- Date: 2026-09-03 UTC
- Decision owners: Product owner, engineering, and operations
- Evidence: `[PKG]`, `[UX]`, `[TEST]`, `[DOC]`, and sections 8-10 of `../refactor-proposal.md`

## Decision

The initial declared support tier is Windows 10/11 x64 and Linux x64 on the Ubuntu/Debian family using X11 or XWayland. Native Wayland, ARM64, other distributions, and macOS are separately qualified or best-effort and are not silently promised. The support matrix must name evidence for every released row, including process behavior, paths/permissions, default handlers, scaling, startup, and accessibility. Windows UIA and Linux AT-SPI2 behavior are release gates; no native-control claim is implied.

The initial packaging shape is self-contained, per-target .NET artifacts: Windows ZIP/onedir first and Linux tar.gz first. A directory artifact must work before an installer is considered. The repository provides reproducible unsigned `dotnet publish` commands for `linux-x64` and `win-x64` in the [release runbook](../../release-runbook.md). These commands create candidate directories only; they do not qualify native execution, create installers, sign, publish, update, or prove rollback. `.deb`, `.rpm`, AppImage, Flatpak, signing, installers, and updates require separate clean-install and ownership evidence. There is no automatic updater initially.

Every artifact records application/source revision, target OS/architecture, exact SDK and dependency versions, yt-dlp/EJS/Deno/FFmpeg provenance, checksums, SBOM, notices, build environment, and signing status. Builds happen per supported target; cross-build equivalence is not assumed. FFmpeg remains external initially; a bundled Deno runtime is conditional on ADR-0002's gates.

Rollback is deliberately boring: select a prior verified artifact or launch the preserved legacy `app.py` path during migration. Rollback never moves, overwrites, or deletes downloaded media. Legacy removal requires the replacement to be the default, G0-G5 evidence, supported documentation, exercised rollback, a defined compatibility window, and owner sign-off.

## Consequences

The product can start implementation without promising universal Linux packaging, installers, updates, bundled runtime licensing, or untested desktop accessibility. Release work is evidence-driven and reversible, while the legacy launcher remains available as an operational fallback.

## Validation and release gates

- Clean-install/startup/upgrade/rollback smoke on both declared target platforms, including paths with spaces and missing-prerequisite diagnostics.
- UIA/AT-SPI2, keyboard/focus, scaling, file dialog, local opener, and process-tree evidence for supported rows.
- Complete checksums/SBOM/notices/provenance and legal inventory before publication or signing.
- Prove media retention across uninstall and rollback; no automatic updater until update/rollback support is verified.
- Failed package, provenance, support-matrix, accessibility, or rollback gates are no-go conditions for release.
