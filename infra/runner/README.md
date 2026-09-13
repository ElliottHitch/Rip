# Rip VPS runner

The ARM64 VPS runs `rip-actions-runner` through Docker Compose. The GitHub
runner is registered to `ElliottHitch/Rip` as `rip-vps-docker`, with labels
`self-hosted`, `Linux`, `ARM64`, and `rip-release`.

Pushes to `main` run tests here, package and validate the Windows installer on
GitHub's Windows runner, then publish the next `0.1.x` release here. Pull
requests never select this runner. A job-start hook additionally checks the
repository, branch, and event. Windows execution remains on Windows so the
existing executable smoke check and platform coverage are retained.

## Operations on this VPS

The deployment checkout is `/home/ubuntu/rip-runner-setup`.

```sh
cd /home/ubuntu/rip-runner-setup/infra/runner
docker compose ps
docker compose logs --tail 100 -f
docker compose restart
docker compose stop
docker compose up -d
```

Docker restarts the container after a host reboot. The container is limited to
2 CPUs and 4 GiB RAM, runs without root privileges, drops Linux capabilities,
and has no published ports, Docker socket, or host directory mounts. Runner
state and the build user's home are separate Docker named volumes. The runner
needs outbound HTTPS access. This container is not a VM security boundary:
never route untrusted PR workflows here or grant build code access to VPS
credentials. Keep the VPS and base image patched.

`RUNNER_MANUALLY_TRAP_SIG=1` makes the runner forward Docker shutdown signals
and close its GitHub session cleanly, preventing stale-session conflicts on
restart.

`docker compose ps` shows whether the process is running. Verify actual GitHub
connectivity in repository Settings → Actions → Runners, or with:

```sh
gh api repos/ElliottHitch/Rip/actions/runners \
  --jq '.runners[] | {name,status,busy}'
```

Docker logs rotate at 10 MiB, retaining three files. The Actions runner also
writes diagnostic logs under `/runner/_diag`; inspect their disk usage during
maintenance. Repository workspaces and NuGet caches persist between jobs.
Never run `docker compose down -v` casually: it deletes runner registration
and caches.

## First installation / replacement host

Requires ARM64 Linux, Docker Compose, and a host-side `gh` login allowed to
register a runner in the repository. No general GitHub token is stored in the
image or injected into the running service.

```sh
docker compose build
set -o pipefail
gh api --method POST repos/ElliottHitch/Rip/actions/runners/registration-token \
  --jq .token | docker compose run --rm -T runner register
docker compose up -d
```

The short-lived registration token is passed over stdin. Registration writes
runner-specific credentials to the state volume; protect Docker access and
any backups of that volume. Do not commit or publicly upload its contents.
Job publishing uses only the workflow's temporary `github.token`.

## Updates

The image pins .NET SDK 10.0.400 and the ARM64 base-image digest. When changing
`global.json`, update the Dockerfile to the same SDK, rebuild, and recreate the
container before merging the SDK change. The test job uses the image's SDK.

```sh
docker compose build --pull
docker compose up -d --force-recreate
```

The GitHub runner initially installs version 2.337.0 from a checksum-verified
archive and then retains normal automatic runner updates in its state volume.
Updating the Dockerfile runner version affects new volumes; existing volumes
keep their self-updated installation. Maintenance should happen while the
runner is idle, so a restart does not interrupt a release.

## Rollback

To stop automatic publication immediately, disable the release workflow in
GitHub Actions. To return to hosted execution, restore the preceding
`.github/workflows/release.yml` (which also restores manual-only publication)
and stop this Compose service after active jobs finish. Leave volumes intact
until the runner is removed from repository settings. Existing published
releases are unchanged by a workflow rollback.

## Windows cross-build feasibility

Checked on this ARM64 Linux image with .NET 10.0.400 and Velopack 1.2.0:

- The existing locked restore and `dotnet publish -r win-x64 --self-contained
  true` successfully compile the Windows application without application-code
  changes.
- The unchanged `packaging/windows.ps1` stops at its Windows smoke-check line:
  `Start-Process -WindowStyle Hidden` is unsupported on Linux. Directly running
  the resulting Windows x64 `Rip.exe` also fails with `Exec format error`.
- Running the packaging command with `vpk "[win]" pack` successfully produces
  the Windows installer, portable ZIP, and full update package on Linux.
- Generated checksums and the existing `packaging/verify-windows.ps1` pass;
  all nine cases in `tests/packaging.test.ps1` also pass on Linux PowerShell.

The experiment used version `0.1.999` only in a temporary container; it was
not published. Cross-compilation can move to the VPS in a future workflow
change, but it must pass the built Windows executable to a Windows job for
the existing execution check. Linux packaging success alone does not prove
Windows startup, installation, or updates work. The deployed workflow keeps
Windows packaging and execution together on GitHub as agreed.
