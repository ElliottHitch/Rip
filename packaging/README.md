# Rip packaging

The Windows installer/update pipeline is `windows.ps1`, invoked by `.github/workflows/release.yml`.

```powershell
./packaging/windows.ps1 -Version 1.0.0
```

It uses locked .NET dependencies, publishes a self-contained Windows x64 application, and packs Rip-win-Setup.exe plus its update feed with pinned Velopack 1.2.0. Installation creates desktop and Start menu shortcuts. Unsigned artifacts go to artifacts/windows/releases.

The standard-library `publish.py` / `verify.py` scripts and shell wrappers remain for existing directory-package experiments on Linux and other RIDs. They package the same Rip application and are outside the Windows automatic release path. Their historical provenance/release-gate records do not describe the Windows installer.

See [the release runbook](../docs/release-runbook.md).
