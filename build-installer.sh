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
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$project_dir/TFGLauncher.csproj")
installer="$project_dir/artifacts/TFG-Launcher-Setup-$version.exe"
private_key=${TFG_UPDATE_PRIVATE_KEY:-"$HOME/.config/tfg-launcher-signing/update-private.pem"}
if [[ ! -f "$private_key" ]]; then
  echo "Закрытый ключ обновлений не найден: $private_key" >&2
  exit 1
fi
openssl dgst -sha256 -sign "$private_key" -out "$installer.sig" "$installer"
public_key="$(dirname "$private_key")/update-public.pem"
openssl dgst -sha256 -verify "$public_key" -signature "$installer.sig" "$installer"
sha256sum "$installer"
