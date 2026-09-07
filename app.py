"""Launch the sole application: the Avalonia/.NET desktop app.

Python is not part of the downloader pipeline and needs no third-party packages.
"""
from __future__ import annotations

import os
from pathlib import Path
import shutil
import subprocess
import sys


def main() -> int:
    root = Path(__file__).resolve().parent
    local_data = Path(os.environ.get("LOCALAPPDATA", Path.home() / ".local/share"))
    app_data = local_data / "RipData"
    isolated_sdk = app_data / "devtools/dotnet" / ("dotnet.exe" if os.name == "nt" else "dotnet")
    dotnet = str(isolated_sdk) if isolated_sdk.is_file() else shutil.which("dotnet")
    if not dotnet:
        print("Install the .NET SDK specified in global.json, then run python app.py again.", file=sys.stderr)
        return 1
    environment = os.environ.copy()
    environment["DOTNET_ROOT"] = str(Path(dotnet).resolve().parent)
    manifest = app_data / "tools/rip.tools.json"
    if manifest.is_file():
        environment.setdefault("RIP_TOOL_MANIFEST", str(manifest))
    project = root / "src/Rip.App/Rip.App.csproj"
    restored = subprocess.run([dotnet, "restore", str(project), "--locked-mode"], cwd=root, env=environment)
    if restored.returncode:
        return restored.returncode
    return subprocess.run(
        [dotnet, "run", "--project", str(project), "--no-restore", "--no-launch-profile"],
        cwd=root, env=environment,
    ).returncode


if __name__ == "__main__":
    raise SystemExit(main())
