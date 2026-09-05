# Rip release runbook

## Automatic releases

`.github/workflows/release.yml` runs on pull requests, pushes to `main`, and manual dispatch. Linux runs the complete deterministic suite. Windows builds and verifies the installer and update feed, then runs presentation and Core tests. A separate release job publishes successful main-branch builds. Only that job has repository write permission. Build jobs do not retain Git credentials, and Actions dependencies are pinned to commit hashes.

Versions are `1.<GITHUB_RUN_NUMBER>.<GITHUB_RUN_ATTEMPT>`. Do not reset the run counter or reuse versions. Reruns receive a higher patch version within that run. Rerunning an older workflow does not replace a newer release as GitHub's latest download. Releases contain Rip-win-Setup.exe, the full update package, releases.win.json, checksums, and Velopack metadata. Delta generation is disabled initially.

## Local installer

Run `packaging/windows.ps1 -Version 1.0.0` with the pinned SDK on PATH. Output goes to `artifacts/windows/1.0.0/releases`. This builds only; it never uploads or signs artifacts. Install Rip-win-Setup.exe to test. The executable is Rip.exe, package ID Rip, and desktop/Start menu shortcuts are named Rip.

The installed app checks https://github.com/ElliottHitch/Rip at startup. New releases are offered, not silently installed. Update and restart is blocked during downloads; failed checks/downloads are recoverable. Velopack validates package content and owns replacement/relaunch. Media and tools are stored outside current/ and retained during updates.

## Tool setup

`src/Rip.App/Setup/tool-bootstrap.json` pins upstream Windows artifacts and release SHA-256 digests. On first launch, Rip downloads from upstream, verifies each artifact, extracts the tools, and writes its local manifest. FFmpeg is not redistributed in the installer. Tool updates require a reviewed catalog change and Rip release. Review upstream licenses and hashes when changing the catalog. Advanced users may supply RIP_TOOL_MANIFEST explicitly.

## Required validation

- Locked restore, strict Release build, Linux full suite, Windows Core/App suites.
- Installer startup, automatic tool setup, desktop/Start menu shortcuts, uninstall registration.
- Separate streams, selected resolution, muxed audio/video validation, and UniFi conversion.
- Install A, publish a higher B, accept the offer, verify relaunch at B and media retention.
- Offline and failed updates, active-download guards.
- Audit tracked files and history before changing visibility or publishing.

Windows signing is not configured. Artifacts are unsigned; do not describe them as publisher-verified. Linux/macOS installers, physical-device playback, and universal accessibility are outside the current Windows release evidence.

## Rollback

Install a prior verified Rip release manually if needed. Keep media in its destination folder. There is no legacy Python fallback. Retired migration plans and packaging tools remain in Git history.
