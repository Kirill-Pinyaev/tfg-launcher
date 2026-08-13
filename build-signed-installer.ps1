$ErrorActionPreference = 'Stop'
if (-not $env:TFG_SIGN_CERT) { throw 'Задайте TFG_SIGN_CERT — thumbprint сертификата Code Signing.' }

$root = $PSScriptRoot
dotnet publish "$root\TFGLauncher.csproj" -c Release -o "$root\bin\publish"
signtool.exe sign /sha1 $env:TFG_SIGN_CERT /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 "$root\bin\publish\TFG Launcher.exe"

$sign = "signtool.exe sign /sha1 $env:TFG_SIGN_CERT /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 `$f"
& ISCC.exe "/DSignedBuild" "/Stfgauthenticode=$sign" "$root\installer\TFGLauncher.iss"
signtool.exe verify /pa /all "$root\artifacts\TFG-Launcher-Setup-1.1.0.exe"
Get-FileHash "$root\artifacts\TFG-Launcher-Setup-1.1.0.exe" -Algorithm SHA256
