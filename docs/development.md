# Working on Rip

## Repository layout

| Path | Purpose |
| --- | --- |
| `src/Rip.Core` | Requests, policies, lifecycle, and download orchestration |
| `src/Rip.Infrastructure` | yt-dlp, FFmpeg, FFprobe, process execution, and file handling |
| `src/Rip.App` | Avalonia UI, composition, first-launch setup, and updates |
| `tests` | Matching Core, Infrastructure, and App test projects |
| `packaging` | Windows installer build and verification |
| `.github/workflows/release.yml` | Tests, packaging, and public releases |

There is one application. `app.py` only launches the .NET project. Historical migration plans and retired packaging scripts remain available in Git history before the repository cleanup.

## Build and test

Use the SDK in `global.json`. Package versions live in `Directory.Packages.props`, with committed lock files in each project. Restore in locked mode so dependency changes require an explicit lock-file update.

```sh
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet test -c Release --no-restore
```

Run the full suite on Linux or WSL. Infrastructure tests execute local shell fixtures. On Windows, run the Core and App test projects individually with `dotnet test --project <path-to-csproj>`. GitHub runs both test environments.

Tests do not contact YouTube or read browser sessions. Live downloads and installed update/relaunch checks are separate tests. Warnings fail builds; NuGet audits direct and transitive dependencies during restore.

## Keep changes contained

Core depends on its own interfaces. Infrastructure implements them. App owns UI state and composition. Media passes through temporary files and FFprobe checks before publication; an existing destination is never overwritten.

Build output, test results, virtual environments, downloaded media, and local tool manifests are ignored. Keep credentials and machine-specific paths out of tracked files. Run `git diff --check` and inspect the staged files before committing.

See [tool setup](tool-manifest.md) for external executables and [the release runbook](release-runbook.md) for packaging and updates.
