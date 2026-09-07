# Windows packaging

With the pinned .NET SDK on PATH, run:

```powershell
./packaging/windows.ps1 -Version 1.0.0
```

The script restores locked dependencies, publishes a self-contained Windows x64 app, runs its startup smoke check, and builds the installer with Velopack. Each version gets its own directory under `artifacts/windows/<version>`, with `publish` and `releases` subdirectories.

`verify-windows.ps1` checks every release file's SHA-256 digest and verifies that the update feed names the correct package, version, size, and hash. Run it separately with `-Directory <release-directory> -Version <version>`.

The script does not install, upload, or sign the result. The [release workflow](../.github/workflows/release.yml) publishes only after its build and test jobs pass. See [the release runbook](../docs/release-runbook.md).
