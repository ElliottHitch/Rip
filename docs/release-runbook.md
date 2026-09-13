# Rip release runbook

## Automatic releases

`.github/workflows/release.yml` validates pull requests, pushes to `main`, and manual dispatches. Every successful push to `main` publishes the next `0.1.x` GitHub release automatically. A manual dispatch on `main` publishes only when **Publish the next 0.1.x GitHub release after validation** is selected; otherwise it only validates and retains artifacts.

For `main`, deterministic tests and release publishing run on the ARM64 Docker runner `rip-vps-docker` on the VPS. Windows installer packaging, the packaged executable smoke check, packaging verification, and Windows Core/App tests run on GitHub's Windows runner. Pull-request jobs and manual validation of other branches use GitHub-hosted runners. See [the Docker runner operations guide](../infra/runner/README.md).

Only the release job has repository write permission. Build jobs do not retain Git credentials, and Actions dependencies are pinned to commit hashes. The VPS runner has no Docker socket, host home directory, or general GitHub token mounted into it. Keep untrusted jobs on GitHub-hosted runners; the local job-start hook additionally rejects events other than pushes/manual dispatches on this repository's `main` branch.

Published versions remain in the `0.1.x` line. The Windows build selects one above the highest existing stable `v0.1.x` release (among the latest 100 releases). For example, after `v0.1.0`, the next release is `v0.1.1`. Main-branch workflows are serialized and do not cancel active releases. GitHub concurrency can replace a pending run with a newer push, so a burst of pushes can release only the latest queued commit.

The release job uses the `public-release` environment, which currently has no approval or wait rules. Adding such rules later will pause automatic publishing. Merge only changes intended for release into `main` and retain the required validation below. A successful automated run does not establish the manual Windows installation/update checks below.

Public releases contain only `Rip-win-Setup.exe`, the portable package, the full update package, `releases.win.json`, and `SHA256SUMS`. GitHub also provides its automatic source archives for every tag. Delta generation is disabled initially.

`packaging/verify-windows.ps1` validates the complete internal Windows release set before publication, verifies checksums and manifests, confirms the third-party notice is embedded in distributable archives, and rejects common debug, environment, private-key, and signing-key file types.

## Local installer

Run `packaging/windows.ps1 -Version 0.1.0` with the pinned SDK on PATH. Output goes to `artifacts/windows/0.1.0/releases`. This builds only; it never uploads or signs artifacts. Install `Rip-win-Setup.exe` to test. The executable is `Rip.exe`, package ID `Rip`, and desktop/Start menu shortcuts are named Rip. The packaged application includes `THIRD-PARTY-NOTICES.md`.

The installed app checks `https://github.com/ElliottHitch/Rip` only when the user selects **Check for updates**. New releases are offered, not silently installed. Update and restart is blocked during downloads; failed checks/downloads are recoverable. Velopack validates package content and owns replacement/relaunch. Media and tools are stored outside `current/` and retained during updates.

## Tool setup

`src/Rip.App/Setup/tool-bootstrap.json` pins upstream Windows artifacts and release SHA-256 digests. On first launch, Rip downloads from upstream, verifies each artifact, extracts the tools, and writes its local manifest. FFmpeg is not redistributed in the installer. Tool updates require a reviewed catalog change and Rip release. Review upstream licenses and hashes when changing the catalog. Advanced users may supply `RIP_TOOL_MANIFEST` explicitly.

## Required validation

- Locked restore, strict Release build, Linux full suite, Windows Core/App suites.
- Installer startup, automatic tool setup, desktop/Start menu shortcuts, uninstall registration.
- Separate streams, selected resolution, muxed audio/video validation, and UniFi conversion.
- Install A, publish a higher B, accept the offer, verify relaunch at B and media retention.
- Offline and failed updates, active-download guards.
- Audit tracked files, release assets, workflow logs, and reachable Git history before changing visibility or publishing.

Windows signing is not configured. Artifacts are unsigned; do not describe them as publisher-verified. Linux/macOS installers, physical-device playback, and universal accessibility are outside the current Windows release evidence.

## Rollback

Install a prior verified Rip release manually if needed. Keep media in its destination folder. There is no legacy Python fallback. Retired migration plans and packaging tools remain in Git history.
