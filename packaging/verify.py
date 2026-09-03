#!/usr/bin/env python3
"""Verify an unpacked Unifi Downloader package without contacting a provider."""
from __future__ import annotations

import hashlib
import json
import platform
import subprocess
import sys
from pathlib import Path


def digest(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def host_rid() -> str | None:
    arch = {"x86_64": "x64", "amd64": "x64", "aarch64": "arm64", "arm64": "arm64"}.get(platform.machine().lower())
    if sys.platform.startswith("linux") and arch:
        return f"linux-{arch}"
    if sys.platform == "win32" and arch:
        return f"win-{arch}"
    return None


def verify(package: Path) -> None:
    required = ("PROVENANCE.json", "tool-provenance.json", "unifi-downloader.tools.example.json", "SBOM.spdx.json", "NOTICE.md", "RELEASE-GATES.md", "SHA256SUMS")
    missing = [name for name in required if not (package / name).is_file()]
    if missing:
        raise RuntimeError("missing package files: " + ", ".join(missing))
    provenance = json.loads((package / "PROVENANCE.json").read_text(encoding="utf-8"))
    rid = provenance.get("application", {}).get("targetRid")
    if rid not in {"linux-arm64", "linux-x64", "win-x64"}:
        raise RuntimeError("provenance has no supported target RID")
    if provenance.get("application", {}).get("artifactType") != "self-contained-directory":
        raise RuntimeError("package is not marked self-contained")
    if provenance.get("claims", {}).get("signing") != "not-performed":
        raise RuntimeError("signing status is not explicit")
    if provenance.get("claims", {}).get("nativeRuntimeQualified") is not False:
        raise RuntimeError("native-runtime qualification must remain false")
    for line in (package / "SHA256SUMS").read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        expected, _, relative = line.partition("  ")
        path = package / relative
        if not path.is_file() or digest(path).lower() != expected.lower():
            raise RuntimeError(f"checksum mismatch: {relative}")
    app_name = "UnifiDownloader.App.exe" if rid.startswith("win-") else "UnifiDownloader.App"
    app = package / app_name
    if not app.is_file():
        raise RuntimeError(f"missing launcher: {app_name}")
    current = host_rid()
    if current == rid:
        result = subprocess.run([str(app), "--deterministic-smoke"], cwd=package, text=True, capture_output=True, check=False)
        print(f"native deterministic smoke exit code: {result.returncode}")
        if result.stdout:
            print(result.stdout, end="")
        if result.returncode != 0:
            if result.stderr:
                print(result.stderr, end="", file=sys.stderr)
            raise RuntimeError("native deterministic smoke failed")
    else:
        print(f"native runtime smoke skipped: package={rid}, host={current or 'unknown'}")
    print(f"verified package: {package}")
    print(f"verified files: {sum(1 for path in package.rglob('*') if path.is_file())}")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print(f"usage: {sys.argv[0]} PACKAGE_DIRECTORY", file=sys.stderr)
        raise SystemExit(2)
    try:
        verify(Path(sys.argv[1]).resolve())
    except (OSError, ValueError, json.JSONDecodeError, RuntimeError) as error:
        print(f"verification failed: {error}", file=sys.stderr)
        raise SystemExit(1)
