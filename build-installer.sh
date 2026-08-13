#!/usr/bin/env bash
set -euo pipefail

project_dir=$(cd "$(dirname "$0")" && pwd)
dotnet publish "$project_dir/TFGLauncher.csproj" -c Release -o "$project_dir/bin/publish"

iscc=${ISCC:-"$HOME/.wine/drive_c/users/$USER/AppData/Local/Programs/Inno Setup 6/ISCC.exe"}
if [[ ! -f "$iscc" ]]; then
  echo "Inno Setup 6 не найден: $iscc" >&2
  exit 1
fi
wine "$iscc" "$(winepath -w "$project_dir/installer/TFGLauncher.iss")"
sha256sum "$project_dir/artifacts/TFG-Launcher-Setup-1.1.0.exe"
