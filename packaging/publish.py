#!/usr/bin/env python3
"""Build a self-contained, unsigned Rip directory artifact.

The workflow deliberately packages only the .NET application. yt-dlp, Deno,
FFmpeg, and FFprobe remain explicit local prerequisites and are never fetched.
"""
from __future__ import annotations

import argparse
import gzip
import hashlib
import json
import os
import platform
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import zipfile
from pathlib import Path
from xml.etree import ElementTree

RIDS = ("linux-arm64", "linux-x64", "win-x64")
APP_PROJECT = Path("src/Rip.App/Rip.App.csproj")
TOOL_KEYS = ("yt-dlp", "deno", "ffmpeg", "ffprobe")
SHA256_RE = re.compile(r"^[0-9a-fA-F]{64}$")


class PublishFailure(RuntimeError):
    def __init__(self, message: str, command: list[str] | None = None, exit_code: int | None = None):
        super().__init__(message)
        self.command = command
        self.exit_code = exit_code


def run_checked(command: list[str], root: Path) -> subprocess.CompletedProcess[str]:
    print("$ " + " ".join(command))
    result = subprocess.run(command, cwd=root, text=True, capture_output=True, check=False, env={**os.environ, "SOURCE_DATE_EPOCH": "0"})
    if result.stdout:
        print(result.stdout, end="")
    if result.returncode != 0:
        if result.stderr:
            print(result.stderr, end="", file=sys.stderr)
        raise PublishFailure("command failed", command, result.returncode)
    return result


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def source_tree_digest(root: Path) -> str:
    digest = hashlib.sha256()
    ignored = {".git", "artifacts", "bin", "obj", ".pytest_cache", "__pycache__"}
    for path in sorted(root.rglob("*")):
        if not path.is_file() or any(part in ignored for part in path.relative_to(root).parts):
            continue
        relative = path.relative_to(root).as_posix().encode("utf-8")
        digest.update(relative + b"\0")
        with path.open("rb") as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(chunk)
        digest.update(b"\0")
    return digest.hexdigest()


def write_json(path: Path, value: object) -> None:
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def git_value(root: Path, *args: str, fallback: str) -> str:
    result = subprocess.run(["git", *args], cwd=root, text=True, capture_output=True, check=False)
    return result.stdout.strip() if result.returncode == 0 and result.stdout.strip() else fallback


def app_version(root: Path) -> str:
    tree = ElementTree.parse(root / APP_PROJECT)
    version = next((node.text for node in tree.getroot().iter() if node.tag.rsplit("}", 1)[-1] == "Version"), None)
    if not version or not re.fullmatch(r"\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?", version):
        raise PublishFailure("App project must declare an explicit semantic Version")
    return version


def sdk_version(root: Path) -> str:
    data = json.loads((root / "global.json").read_text(encoding="utf-8"))
    version = data.get("sdk", {}).get("version")
    if not isinstance(version, str) or not version:
        raise PublishFailure("global.json must declare an SDK version")
    return version


def dependency_pins(root: Path) -> dict[str, str]:
    tree = ElementTree.parse(root / "Directory.Packages.props")
    return {
        node.attrib["Include"]: node.attrib["Version"]
        for node in tree.getroot().iter()
        if node.tag.rsplit("}", 1)[-1] == "PackageVersion"
        and "Include" in node.attrib and "Version" in node.attrib
    }


def load_tool_provenance(root: Path, rid: str) -> tuple[dict, dict]:
    path = root / "packaging" / "tool-provenance.json"
    data = json.loads(path.read_text(encoding="utf-8"))
    if data.get("schemaVersion") != 1 or not isinstance(data.get("tools"), dict):
        raise PublishFailure("tool-provenance.json has an unsupported schema")
    tools = data["tools"]
    if set(tools) != set(TOOL_KEYS):
        raise PublishFailure("tool-provenance.json must enumerate exactly yt-dlp, deno, ffmpeg, and ffprobe")
    selected = {}
    for key in TOOL_KEYS:
        item = tools[key]
        if not isinstance(item, dict) or not item.get("version") or item.get("status") not in {
            "pinned-not-bundled", "external-unqualified"
        }:
            raise PublishFailure(f"tool provenance for {key} is incomplete")
        repository = item.get("sourceRepository", "")
        if not isinstance(repository, str) or not repository.startswith("https://"):
            raise PublishFailure(f"tool provenance for {key} must use an HTTPS source repository")
        target = item.get("targets", {}).get(rid)
        if not isinstance(target, dict) or not target.get("assetName"):
            raise PublishFailure(f"tool provenance has no explicit {rid} target for {key}")
        expected = target.get("expectedSha256")
        if item["status"] == "pinned-not-bundled" and not isinstance(expected, str):
            if not target.get("blocker"):
                raise PublishFailure(f"pinned {key} provenance lacks a verified SHA-256 or explicit blocker for {rid}")
        if isinstance(expected, str) and not SHA256_RE.fullmatch(expected):
            raise PublishFailure(f"tool provenance for {key} has a malformed SHA-256 for {rid}")
        if item["status"] == "external-unqualified" and not target.get("blocker"):
            raise PublishFailure(f"external {key} provenance lacks an explicit blocker for {rid}")
        selected[key] = {
            "version": item["version"],
            "sourceRepository": repository,
            "distribution": item["distribution"],
            "status": item["status"],
            "assetName": target["assetName"],
            "expectedSha256": expected,
            "blocker": target.get("blocker"),
        }
    return data, selected


def example_manifest(rid: str, tool_data: dict[str, dict]) -> dict:
    tools = {}
    expectations = {}
    for key, item in tool_data.items():
        enum_key = {"yt-dlp": "YtDlp", "deno": "Deno", "ffmpeg": "Ffmpeg", "ffprobe": "Ffprobe"}[key]
        executable = item["assetName"]
        tools[enum_key] = {
            "Key": enum_key,
            "AssetName": item["assetName"],
            "SourceRepository": item["sourceRepository"],
            "Version": item["version"],
            "TargetRid": rid,
            "ExpectedSha256": item["expectedSha256"] or "",
            "IsVerified": False,
            "ExecutablePath": f"tools/{executable}",
        }
        expectations[enum_key] = {
            "Key": enum_key,
            "AssetName": item["assetName"],
            "SourceRepository": item["sourceRepository"],
            "Version": item["version"],
            "TargetRid": rid,
            "ExpectedSha256": item["expectedSha256"] or "",
            "RequireVerified": False,
        }
    return {
        "SchemaVersion": 1,
        "ExecutionTargetRid": rid,
        "AllowedRepositories": sorted({item["sourceRepository"] for item in tool_data.values()}),
        "Tools": tools,
        "TrustedExpectations": expectations,
        "README": "Example only: IsVerified and RequireVerified are false so the application fails closed until release engineering supplies approved local tools and trusted digests.",
    }


def sbom(version: str, rid: str, sdk: str, pins: dict[str, str]) -> dict:
    packages = [{"SPDXID": "SPDXRef-Application", "name": "Rip", "versionInfo": version, "downloadLocation": "NOASSERTION", "licenseConcluded": "NOASSERTION", "licenseDeclared": "NOASSERTION"}]
    for name, package_version in sorted(pins.items()):
        packages.append({"SPDXID": "SPDXRef-" + re.sub(r"[^A-Za-z0-9.-]", "-", name), "name": name, "versionInfo": package_version, "downloadLocation": "NOASSERTION", "licenseConcluded": "NOASSERTION", "licenseDeclared": "NOASSERTION"})
    packages.append({"SPDXID": "SPDXRef-DotNet-SDK", "name": ".NET SDK", "versionInfo": sdk, "downloadLocation": "NOASSERTION", "licenseConcluded": "NOASSERTION", "licenseDeclared": "NOASSERTION"})
    return {"spdxVersion": "SPDX-2.3", "dataLicense": "CC0-1.0", "SPDXID": "SPDXRef-DOCUMENT", "name": f"Rip-{version}-{rid}", "documentNamespace": f"https://example.invalid/Rip/provenance/{version}/{rid}", "creationInfo": {"created": "1970-01-01T00:00:00Z", "creators": ["Tool: Rip packaging workflow"]}, "packages": packages}


def archive_tar(source: Path, destination: Path) -> None:
    with destination.open("wb") as stream:
        with gzip.GzipFile(fileobj=stream, mode="wb", compresslevel=9, mtime=0) as compressed:
            with tarfile.open(fileobj=compressed, mode="w", format=tarfile.PAX_FORMAT) as archive:
                for path in sorted(source.rglob("*")):
                    if not path.is_file():
                        continue
                    relative = path.relative_to(source)
                    info = tarfile.TarInfo(str(Path(source.name) / relative))
                    info.size = path.stat().st_size
                    info.mode = 0o755 if os.access(path, os.X_OK) else 0o644
                    info.mtime = 0
                    info.uid = 0
                    info.gid = 0
                    info.uname = ""
                    info.gname = ""
                    with path.open("rb") as file_stream:
                        archive.addfile(info, file_stream)


def archive_zip(source: Path, destination: Path) -> None:
    with zipfile.ZipFile(destination, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for path in sorted(source.rglob("*")):
            if not path.is_file():
                continue
            relative = path.relative_to(source)
            entry = zipfile.ZipInfo(str(Path(source.name) / relative), date_time=(1980, 1, 1, 0, 0, 0))
            entry.compress_type = zipfile.ZIP_DEFLATED
            entry.external_attr = (0o755 if os.access(path, os.X_OK) else 0o644) << 16
            archive.writestr(entry, path.read_bytes())


def write_checksums(directory: Path, output: Path) -> None:
    lines = []
    for path in sorted(directory.rglob("*")):
        if path.is_file() and path != output:
            lines.append(f"{sha256(path)}  {path.relative_to(directory).as_posix()}")
    output.write_text("\n".join(lines) + "\n", encoding="utf-8")


def host_rid() -> str | None:
    machine = platform.machine().lower()
    arch = {"x86_64": "x64", "amd64": "x64", "aarch64": "arm64", "arm64": "arm64"}.get(machine)
    if sys.platform.startswith("linux") and arch:
        return f"linux-{arch}"
    if sys.platform == "win32" and arch:
        return f"win-{arch}"
    return None


def clean_install_check(package_dir: Path, rid: str, root: Path) -> None:
    app_name = "Rip.exe" if rid.startswith("win-") else "Rip"
    app = package_dir / app_name
    if not app.is_file():
        raise PublishFailure(f"clean-install check did not find {app_name}")
    with tempfile.TemporaryDirectory(prefix="rip-install-") as temp:
        clean = Path(temp) / package_dir.name
        shutil.copytree(package_dir, clean)
        copied = clean / app_name
        if host_rid() == rid:
            result = subprocess.run([str(copied), "--deterministic-smoke"], cwd=clean, text=True, capture_output=True, check=False)
            print(f"clean-install smoke ({rid}) exit code: {result.returncode}")
            if result.stdout:
                print(result.stdout, end="")
            if result.returncode != 0:
                if result.stderr:
                    print(result.stderr, end="", file=sys.stderr)
                raise PublishFailure("clean-install deterministic smoke failed", [str(copied), "--deterministic-smoke"], result.returncode)
        else:
            print(f"clean-install file validation only ({rid}); native execution is unavailable on {host_rid() or 'this host'}")


def write_blocker(root: Path, version: str, rid: str, failure: PublishFailure) -> Path:
    output = root / "artifacts" / version / rid
    output.mkdir(parents=True, exist_ok=True)
    command = " ".join(failure.command) if failure.command else "validation"
    text = (
        "Packaging blocker\n\n"
        f"RID: {rid}\n"
        f"Command: {command}\n"
        f"Exit code: {failure.exit_code if failure.exit_code is not None else 'not applicable'}\n"
        f"Reason: {failure}\n\n"
        "No release, signing, publication, or native-runtime claim is made. Re-run the same command after resolving the stated prerequisite.\n"
    )
    path = output / "BUILD-BLOCKER.txt"
    path.write_text(text, encoding="utf-8")
    return path


def publish(root: Path, rid: str, configuration: str) -> Path:
    version = app_version(root)
    sdk = sdk_version(root)
    pins = dependency_pins(root)
    tool_manifest, tools = load_tool_provenance(root, rid)
    dotnet = shutil.which("dotnet")
    if not dotnet:
        raise PublishFailure("dotnet was not found on PATH")

    output_root = root / "artifacts" / version / rid
    package_dir = output_root / f"Rip-{rid}"
    if output_root.exists():
        shutil.rmtree(output_root)
    package_dir.mkdir(parents=True)
    # RuntimeIdentifiers on the App project keep every supported RID in the lock
    # graph. Restoring the whole solution without -r avoids rewriting referenced
    # project lock files to one transient RID.
    restore = [dotnet, "restore", "--locked-mode"]
    run_checked(restore, root)
    publish_command = [dotnet, "publish", str(APP_PROJECT), "--configuration", configuration, "--runtime", rid, "--self-contained", "true", "--no-restore", "--output", str(package_dir), "-p:ContinuousIntegrationBuild=true", "-p:DebugSymbols=false", "-p:DebugType=None", "-p:UseAppHost=true", "-p:PublishSingleFile=false"]
    run_checked(publish_command, root)

    revision = git_value(root, "rev-parse", "HEAD", fallback="unknown")
    dirty = bool(git_value(root, "status", "--porcelain", "--untracked-files=no", fallback=""))
    provenance = {
        "schemaVersion": 1,
        "application": {"name": "Rip", "version": version, "targetRid": rid, "artifactType": "self-contained-directory", "sourceRevision": revision, "sourceDirty": dirty, "sourceTreeDigest": source_tree_digest(root)},
        "build": {"sdkVersion": sdk, "dependencyPins": pins, "configuration": configuration, "restore": "locked", "selfContained": True, "publishSingleFile": False, "sourceDateEpoch": 0, "commandIntent": "dotnet publish --configuration Release --runtime <rid> --self-contained true --no-restore"},
        "tools": tools,
        "toolManifestSource": "packaging/tool-provenance.json",
        "claims": {"nativeRuntimeQualified": False, "liveMediaQualified": False, "signing": "not-performed", "publication": "not-performed", "updates": "not-exercised", "rollback": "not-exercised"},
    }
    write_json(package_dir / "PROVENANCE.json", provenance)
    write_json(package_dir / "tool-provenance.json", tool_manifest)
    write_json(package_dir / "rip.tools.example.json", example_manifest(rid, tools))
    write_json(package_dir / "SBOM.spdx.json", sbom(version, rid, sdk, pins))
    for filename in ("NOTICE.md", "RELEASE-GATES.md"):
        shutil.copy2(root / "packaging" / filename, package_dir / filename)
    write_checksums(package_dir, package_dir / "SHA256SUMS")
    clean_install_check(package_dir, rid, root)

    archive = output_root / (f"Rip-{version}-{rid}.zip" if rid.startswith("win-") else f"Rip-{version}-{rid}.tar.gz")
    if rid.startswith("win-"):
        archive_zip(package_dir, archive)
    else:
        archive_tar(package_dir, archive)
    root_checksums = output_root / "SHA256SUMS"
    root_checksums.write_text(f"{sha256(archive)}  {archive.name}\n", encoding="utf-8")
    print(f"archive: {archive}")
    print(f"archive sha256: {sha256(archive)}")
    print(f"directory: {package_dir}")
    print(f"directory files: {sum(1 for p in package_dir.rglob('*') if p.is_file())}")
    return output_root


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", choices=RIDS, required=True)
    parser.add_argument("--configuration", default="Release")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    try:
        output = publish(root, args.rid, args.configuration)
        verify = [sys.executable, str(root / "packaging" / "verify.py"), str(output / f"Rip-{args.rid}")]
        run_checked(verify, root)
        return 0
    except (PublishFailure, OSError, json.JSONDecodeError, ElementTree.ParseError) as failure:
        version = "unknown"
        try:
            version = app_version(root)
        except Exception:
            pass
        blocker = write_blocker(root, version, args.rid, failure if isinstance(failure, PublishFailure) else PublishFailure(str(failure)))
        print(f"BLOCKED: {blocker}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
