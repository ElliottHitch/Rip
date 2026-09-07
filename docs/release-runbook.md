# Rip release runbook

## CI and explicitly approved releases

`.github/workflows/release.yml` runs on pull requests, pushes to `main`, and manual dispatch. Every trigger runs the deterministic suite, builds and verifies the Windows installer and update feed, runs the presentation and Core tests, and retains the resulting files as a short-lived Actions artifact.

Ordinary pushes to `main` do not publish a GitHub release. Publication is normally performed from a manual workflow dispatch on `main` with **Publish a GitHub release after all validation checks pass** selected and an explicit semantic version such as `0.2.0`.

The initial public release is `v0.1.0`. Future releases should follow semantic versioning and must use a version higher than the currently published release. Release versions are chosen explicitly; they are not derived from the GitHub Actions run number.

The release job targets the `public-release` environment. Before publishing, configure that environment in repository settings with an appropriate required reviewer or wait timer; prevent self-review where the team structure allows it. Merely naming an environment in the workflow does not create an approval rule.

Protect `main` with a branch ruleset that requires a pull request and the repository's test checks, blocks force pushes and deletion, and applies to administrators unless an emergency bypass is intentionally retained. Repository settings are part of the release boundary and cannot be enforced by this file alone.

Before publishing, review the exact commit, complete the required validation below, and confirm that the release is intended for users. A successful CI build is evidence, not release approval.

Releases contain Rip-win-Setup.exe, the portable package, the full update package, release manifests, checksums, and Velopack metadata. Delta generation is disabled initially.

`packaging/verify-windows.ps1` requires the complete release file set, verifies every checksum and manifest, confirms the third-party notice is embedded in both distributable archives, and rejects common debug, environment, private-key, and signing-key file types.

## Local installer

Run `packaging/windows.ps1 -Version 0.1.0` with the pinned SDK on PATH. Output goes to `artifacts/windows/0.1.0/releases`. This builds only; it never uploads or signs artifacts. Install Rip-win-Setup.exe to test. The executable is Rip.exe, package ID Rip, and desktop/Start menu shortcuts are named Rip. The packaged application includes `THIRD-PARTY-NOTICES.md`.

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

Install a prior verified Rip release manually if needed. Keep media in its destination folder. There is no legacy Python fallback.
