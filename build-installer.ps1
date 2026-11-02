# Builds EveryDns-Setup-<version>.exe: publishes a self-contained release build, then compiles the Inno Setup script.
# Usage:  powershell -ExecutionPolicy Bypass -File build-installer.ps1
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }

# Optional code signing: set EVERYDNS_PFX (path to a .pfx) and EVERYDNS_PFX_PASSWORD to sign the app and the installer.
$signtool = $null
if ($env:EVERYDNS_PFX) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $signtool) { throw 'EVERYDNS_PFX is set but signtool.exe was not found. Install the Windows SDK signing tools.' }
}
function Sign-File($path) {
    if (-not $signtool) { return }
    & $signtool sign /f $env:EVERYDNS_PFX /p $env:EVERYDNS_PFX_PASSWORD /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $path
    if ($LASTEXITCODE -ne 0) { throw "Signing failed for $path" }
}

# Xray-core ships inside the app. It is not stored in git: download it once (checked against its published SHA-256).
if (-not (Test-Path core\xray.exe)) {
    Write-Host 'Downloading Xray-core...'
    $url = 'https://github.com/XTLS/Xray-core/releases/latest/download/Xray-windows-64.zip'
    $zip = Join-Path $env:TEMP 'xray-windows-64.zip'
    curl.exe -sSfL $url -o $zip; if ($LASTEXITCODE -ne 0) { throw "Could not download $url" }
    $digest = (curl.exe -sSfL "$url.dgst") -join "`n"
    if ($digest -notmatch 'SHA2-256\s*=\s*([0-9a-fA-F]{64})') { throw 'No SHA-256 published for the Xray-core download.' }
    if ((Get-FileHash $zip -Algorithm SHA256).Hash -ne $Matches[1].ToUpper()) { throw 'Xray-core download does not match its checksum.' }
    New-Item -ItemType Directory -Force core | Out-Null
    Expand-Archive $zip -DestinationPath core -Force
    Remove-Item $zip
}
Get-Process EveryDns -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item publish -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish EveryDns.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -o publish --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Sign-File publish\EveryDns.exe

& $iscc installer\EveryDns.iss
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compile failed' }

Get-ChildItem installer\Output\*.exe | ForEach-Object { Sign-File $_.FullName }
Get-ChildItem installer\Output\*.exe | ForEach-Object { "{0}  ({1:N0} MB)" -f $_.FullName, ($_.Length / 1MB) }
