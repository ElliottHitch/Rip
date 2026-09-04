Packaging and provenance workflow

The package is an unsigned, self-contained YouTube Downloader .NET directory artifact. The workflow does not download yt-dlp, Deno, FFmpeg, or FFprobe and does not read credentials. Those tools remain explicit local prerequisites validated by the application's `unifi-downloader.tools.json` contract. For the operator manifest and digest procedure, see [`docs/tool-manifest.md`](../docs/tool-manifest.md). For native qualification and release approval, see [`docs/release-runbook.md`](../docs/release-runbook.md).

Prerequisites

- .NET SDK 10.0.400 from global.json.
- Python 3.10 or newer from the standard library only.
- A clean enough workspace for generated artifacts under artifacts/.

Publish one target

Unix:

    ./packaging/publish.sh --rid linux-arm64
    ./packaging/publish.sh --rid linux-x64
    ./packaging/publish.sh --rid win-x64

Windows PowerShell:

    .\packaging\publish.ps1 -Rid win-x64

The command performs locked restore, self-contained onedir publish, deterministic clean-install validation, provenance/SBOM/notice generation, an internal file checksum list, and a fixed-metadata tar.gz (Linux) or ZIP (Windows). It prints the output directory, archive, and SHA-256. Re-run the generated root check with `sha256sum -c SHA256SUMS` on Unix; on Windows use `Get-FileHash` against the listed archive.

Outputs

    artifacts/<version>/<rid>/youtube-downloader-<rid>/
    artifacts/<version>/<rid>/youtube-downloader-<version>-<rid>.tar.gz|.zip
    artifacts/<version>/<rid>/SHA256SUMS

The directory contains PROVENANCE.json, tool-provenance.json, SBOM.spdx.json, NOTICE.md, RELEASE-GATES.md, a non-runnable example tools manifest, and SHA256SUMS. A runtime tools manifest is intentionally not supplied: the app must fail closed until release engineering records verified local paths and trusted hashes for all four external tools.

Evidence boundary

- linux-arm64 is the only target that can run native smoke on the current Linux ARM64 host.
- linux-x64 and win-x64 may be cross-published when the SDK runtime packs are available, but the workflow reports file-only clean-install validation and does not claim native runtime qualification.
- Signing, notarization, publication, updates, rollback exercise, live provider/media access, and installer packaging are separate gates and are not performed.
- If restore or publish fails, the command exits nonzero and writes artifacts/<version>/<rid>/BUILD-BLOCKER.txt with the exact reproducible command, exit code, and blocker. It never substitutes an unverified or partially published release artifact.
