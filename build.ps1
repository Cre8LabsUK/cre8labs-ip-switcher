# Builds the app and the installer into .\dist
#
#   powershell -ExecutionPolicy Bypass -File build.ps1              # version from src\IPSwitcher.csproj
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Version 1.2.0
#
# Needs the .NET SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup).
param([string]$Version)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src = Join-Path $root 'src'
$dist = Join-Path $root 'dist'
$out = Join-Path $root 'release-build'

if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $src 'IPSwitcher.csproj'))).Project.PropertyGroup |
        ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1
}
Write-Host "Building Cre8Labs IP Switcher $Version" -ForegroundColor Cyan

# 1. App (clean build, so the admin manifest is never stale)
Remove-Item -Recurse -Force (Join-Path $src 'obj'), (Join-Path $src 'bin'), $out, $dist -ErrorAction SilentlyContinue
dotnet build $src -c Release -o $out --no-incremental "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw 'App build failed' }

# 2. Installer
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }
& $iscc /Q "/DAppVersion=$Version" (Join-Path $root 'installer\IPSwitcher.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }

# 3. Portable exe and checksums
Copy-Item (Join-Path $out 'Cre8Labs-IPSwitcher.exe') (Join-Path $dist "Cre8Labs-IPSwitcher-$Version-portable.exe")
Get-ChildItem $dist -Filter *.exe | ForEach-Object {
    "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower())  $($_.Name)"
} | Set-Content (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii

Write-Host "`nDone:" -ForegroundColor Green
Get-ChildItem $dist | ForEach-Object { Write-Host "  dist\$($_.Name)  ($([math]::Round($_.Length / 1KB)) KB)" }
