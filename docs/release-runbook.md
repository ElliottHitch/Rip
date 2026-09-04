# Release runbook

Status: release preparation only. The current repository has deterministic local evidence and a publishable .NET project shape, but it is not a release artifact. Composition staging-root cleanup is implemented and verified. Do not treat the local suite or package workflow as release approval until the gates below have evidence.

Scope and safety

Use the downloader only for media you are authorized to access, download, and use. The product does not collect or export credentials or cookies, automate login or CAPTCHA, bypass access or service restrictions, rotate proxies, spoof fingerprints or headers, or automatically hammer rate-limited services. Browser-session access is a separate, explicit per-run opt-in. `Open in Browser` opens only a freshly verified local media file and does not contact the provider.

Evidence lanes

Keep these results separate in the release record:

- Deterministic local evidence uses fake tools, local fixtures, injected HTTP handlers, and no provider request. It proves contracts such as fail-closed tool validation, lifecycle state, staged publication, and safe output handling.
- Cross-compilation or cross-publishing creates files for another RID. It does not prove that the artifact starts, that its native dialogs or handlers work, or that its tools run on that OS.
- Native platform qualification runs the artifact on the declared OS, architecture, desktop session, and filesystem. It must cover startup, paths with spaces, permissions, process cancellation, folder picking, local opening, scaling, keyboard/focus, and accessibility.
- Signing and publication are release operations. They require approved identities, artifact review, checksums, SBOM, notices, provenance, and owner authorization. None is performed by this runbook.
- Update and rollback evidence covers install, upgrade, downgrade or rollback, and media retention. A prior verified artifact or the preserved `app.py` path is the migration fallback. There is no automatic updater in the initial shape.
- Authorized live-media validation is a separate, maintainer-approved check. It must use an authorized test video without collecting credentials or browser profiles, avoid repeated retries, and never replace deterministic or security gates.

Inputs to record

Before publishing a candidate, record:

- application source revision (`git rev-parse HEAD`), clean/dirty working-tree state, and release version;
- SDK from `global.json` (`10.0.400` at the current revision), Avalonia package version (`12.1.2` at the current revision), target RID, and build host;
- exact yt-dlp, Deno, FFmpeg, and FFprobe asset names, versions, HTTPS source repositories, target RIDs, official release digests, computed SHA-256 values, and notices;
- whether FFmpeg is an external prerequisite or an approved bundled asset. The current decision is external prerequisite initially;
- signing status, SBOM location, license/notice inventory, and the person who reviewed each item.

Use `docs/tool-manifest.md` to create `unifi-downloader.tools.json`. Do not put secrets, private paths, credentials, cookies, signed URLs, or profile data in that file or in release evidence.

Local build and test gate

Run from the repository root:

```sh
dotnet restore --locked-mode
dotnet build UnifiDownloader.sln --configuration Release --no-restore
dotnet test UnifiDownloader.sln --configuration Release --no-restore
dotnet run --project src/UnifiDownloader.App/UnifiDownloader.App.csproj --configuration Release --no-build --no-restore -- --deterministic-smoke
python3 -m pytest -q
git diff --check
```

The deterministic smoke argument exits before Avalonia and Infrastructure initialization. It must not be described as a desktop startup or provider test. The .NET and Python suites must not contact a live service.

Candidate publish commands

The repository includes an unsigned, self-contained directory packaging workflow. It does not download yt-dlp, Deno, FFmpeg, or FFprobe. Those tools remain external local prerequisites validated by the application's `unifi-downloader.tools.json` contract. The workflow reads `packaging/tool-provenance.json`, emits provenance/SBOM/notices and an intentionally non-runnable example manifest, and fails with `BUILD-BLOCKER.txt` when a restore or publish prerequisite is unavailable.

Use the wrapper for one target:

```sh
./packaging/publish.sh --rid linux-arm64
./packaging/publish.sh --rid linux-x64
./packaging/publish.sh --rid win-x64
```

On Windows PowerShell:

```powershell
.\packaging\publish.ps1 -Rid win-x64
```

The wrapper runs locked restore, self-contained onedir publish, file-level clean-install validation, provenance/SBOM/notice generation, checksums, and a fixed-metadata archive. Linux targets produce a tar.gz; Windows produces a ZIP. The output is under `artifacts/<version>/<rid>/`, with a directory named `youtube-downloader-<rid>`, an archive named `youtube-downloader-<version>-<rid>`, a root `SHA256SUMS`, and `BUILD-BLOCKER.txt` only when the workflow is blocked. The output directory includes `PROVENANCE.json`, `tool-provenance.json`, `SBOM.spdx.json`, `NOTICE.md`, `RELEASE-GATES.md`, `unifi-downloader.tools.example.json`, and its own `SHA256SUMS`.

The wrapper runs `packaging/verify.py` before returning success. To recheck an unpacked directory manually:

```sh
python3 packaging/verify.py artifacts/<version>/<rid>/youtube-downloader-<rid>
sha256sum -c artifacts/<version>/<rid>/SHA256SUMS
```

On Windows, use `Get-FileHash` against the archive and listed files. Replace `<version>` with the `Version` in `src/UnifiDownloader.App/UnifiDownloader.App.csproj`, currently `1.0.0`. If a command fails, preserve and report the exact nonzero result and blocker file. Do not substitute a partial output or call it a release.

The current workflow can run native deterministic smoke only when the package RID matches the host. On this Linux ARM64 host, `linux-arm64` is the only native-smoke candidate. `linux-x64` and `win-x64` are cross-published or file-validated only; that does not qualify their native runtime behavior. `packaging/README.md` contains the same package contract and [the release gates](../packaging/RELEASE-GATES.md) list what the workflow does not prove.

A prior attempt at the current checkout recorded this blocker in `artifacts/1.0.0/linux-arm64/BUILD-BLOCKER.txt`: `packaging/tool-provenance.json` has no approved SHA-256 for the pinned yt-dlp asset. If that evidence directory is no longer present, rerun the command and retain the generated blocker. Resolve the provenance decision before rerunning. The Deno, FFmpeg, and FFprobe entries also remain external and unqualified, with blockers recorded in `packaging/tool-provenance.json`; they are not bundled by this workflow.

Native qualification

For each declared row, use a native desktop host and a clean test account or workspace. Record the OS version, architecture, desktop backend, display server, and exact artifact revision. Verify:

1. The app starts from a path containing spaces and reports missing tools without attempting work when no valid manifest exists.
2. A valid target-RID manifest enables `Test Environment` only after all four tools and Runtime report `Available`.
3. The folder picker opens after the window is ready, allows one folder, safely returns a local path, and leaves the output field editable. Cancellation or unsupported URI schemes leave the field unchanged and show the safe fallback text.
4. A permitted local or controlled test run publishes a non-empty Matroska file in generic mode or MP4 in UniFi compatibility mode without overwriting an existing destination. A collision reports a conflict and does not create a numbered suffix.
5. Cancellation, FFmpeg failure, missing permissions, long paths, and low disk space do not produce a false completion. A post-commit cleanup warning preserves the verified output.
6. `Open in Browser` is enabled only for the verified published output, rechecks the file, and uses the operating system's default handler. Verify a missing or moved file and a missing handler without exposing the URI or full path.
7. Keyboard traversal, focus, status announcements, minimum size, scaling, and the agreed screen-reader tier pass. Windows UI Automation and Linux AT-SPI2 are evidence gates, not assumptions from Avalonia documentation.

Current evidence does not qualify the interactive picker, Windows x64, Linux x64, ARM64 product support, native Wayland, any screen reader, every desktop handler, or real-media combinations.

Signing, package publication, and updates

Do not sign or publish from this runbook. Before a release owner authorizes those operations, attach:

- per-target unsigned artifact and a reproducible build record;
- SHA-256 checksums, SBOM, dependency/license notices, yt-dlp/EJS/Deno/FFmpeg/FFprobe provenance, and source revision;
- approved signing result and verification instructions;
- clean-install startup evidence, including a missing-prerequisite path;
- upgrade and rollback evidence showing that downloaded media remains untouched;
- the support matrix with native evidence for every claimed row.

The initial packaging decision is Windows ZIP/onedir and Linux tar.gz first. An installer, `.deb`, `.rpm`, AppImage, Flatpak, automatic updater, or universal Linux claim needs its own evidence and owner decision. Cross-publishing is not a substitute for a native smoke test. No signing credentials or publication target are configured in this repository.

Rollback procedure

During migration, select the last verified artifact or launch the preserved `app.py` path from a controlled checkout. Rollback must not move, overwrite, or delete downloaded media. Do not remove the legacy path until the replacement is the default, the declared support rows and accessibility tier have evidence, rollback has been exercised, documentation is published, and the owner signs off.

Release readiness checklist

A release candidate is blocked until every applicable item is checked with a source, command, result, and reviewer:

- [ ] Locked restore, Release build, full .NET tests, Python compatibility tests, deterministic smoke, and diff checks pass.
- [ ] A valid per-target tool manifest has independently verified hashes, separate trusted expectations, HTTPS approved repositories, exact versions, and matching RIDs.
- [ ] yt-dlp/EJS/Deno behavior is local and restricted. No implicit remote component fetch occurs.
- [ ] FFmpeg redistribution, configuration, license, notices, source-offer obligations, and update ownership are resolved, or the external-prerequisite policy is documented for the target.
- [ ] Local media and publication tests cover non-empty verification, collisions, cancellation, cleanup, and no overwrite.
- [ ] Native Windows and Linux startup, process, path, permission, folder-picker, local-opener, scaling, and keyboard evidence exists for each claimed row.
- [ ] UI Automation and AT-SPI2 or the approved accessibility evidence exists. Avalonia's documented capability alone is not proof.
- [ ] Clean install and launch work on each claimed target. The artifact, dependency manifest, notices, checksums, SBOM, and signing status are retained.
- [ ] Uninstall, upgrade, rollback, and media-retention behavior are recorded. No automatic updater is enabled without this evidence.
- [ ] An authorized live-media result, if required by the release owner, is recorded separately without credentials, profile data, bypass behavior, or repeated retries.
- [ ] The owner has reviewed unresolved risks and explicitly approved the support matrix and release.

Until the boxes have evidence, the correct status is release preparation, not release ready.
