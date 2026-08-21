$ErrorActionPreference = 'Stop'

$repo = if ($env:BASEPORT_REPO) { $env:BASEPORT_REPO } else { 'baseporteu/baseport' }
$root = if ($env:LOCALAPPDATA) { $env:LOCALAPPDATA } else { $HOME }
$dir  = if ($env:BASEPORT_DIR) { $env:BASEPORT_DIR } else { Join-Path $root 'Baseport' }
$api  = "https://api.github.com/repos/$repo/releases"
$installer = "https://raw.githubusercontent.com/$repo/main/Scripts/install.ps1"

$osArch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
if ($osArch -ne 'X64') {
    throw "There is no Baseport build for $osArch. Releases ship linux-x64 and win-x64."
}

$tag = $env:BASEPORT_VERSION
if (-not $tag) {
    try { $tag = (Invoke-RestMethod -Uri "$api/latest").tag_name }
    catch { $tag = $null }
}
if (-not $tag) {
    try { $tag = @(Invoke-RestMethod -Uri "${api}?per_page=1")[0].tag_name }
    catch { $tag = $null }
}
if (-not $tag) { throw "Could not resolve a release tag from $repo." }

$asset = "Baseport-$tag-win-x64.zip"
$base  = "https://github.com/$repo/releases/download/$tag"
$tmp   = Join-Path ([System.IO.Path]::GetTempPath()) ("baseport-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null

try {
    $archive = Join-Path $tmp $asset
    $sumFile = "$archive.sha256"

    Write-Host "Installing $tag into $dir (override with BASEPORT_DIR)"
    try { Invoke-WebRequest -Uri "$base/$asset" -OutFile $archive }
    catch { throw "Release $tag has no asset named $asset." }
    try { Invoke-WebRequest -Uri "$base/$asset.sha256" -OutFile $sumFile }
    catch { throw "Release $tag has no checksum for $asset." }

    $expected = ((Get-Content $sumFile -Raw).Trim() -split '\s+')[0]
    $actual   = (Get-FileHash $archive -Algorithm SHA256).Hash
    if ($actual -ne $expected) { throw "Checksum mismatch for $asset. Refusing to install." }

    $payload = Join-Path $tmp 'payload'
    Expand-Archive -Path $archive -DestinationPath $payload -Force

    foreach ($keep in 'baseport.db', 'baseport.db-shm', 'baseport.db-wal', 'baseport.key', 'log', 'uploads', 'backups') {
        Remove-Item -Path (Join-Path $payload $keep) -Recurse -Force -ErrorAction SilentlyContinue
    }

    $update = Test-Path (Join-Path $dir 'Baseport.exe')
    if (Test-Path (Join-Path $dir 'appsettings.json')) {
        Remove-Item -Path (Join-Path $payload 'appsettings.json') -Force -ErrorAction SilentlyContinue
    }

    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    Copy-Item -Path (Join-Path $payload '*') -Destination $dir -Recurse -Force

    $shim = @"
@echo off
if /I "%~1"=="update" goto :bpupdate
if /I "%~1"=="logs" goto :bplogs
if /I "%~1"=="-h" goto :bphelp
if /I "%~1"=="--help" goto :bphelp
cd /d "%~dp0"
"%~dp0Baseport.exe" %*
exit /b %errorlevel%

:bphelp
cd /d "%~dp0"
"%~dp0Baseport.exe" help
exit /b %errorlevel%

:bpupdate
set "BASEPORT_REPO=$repo"
set "BASEPORT_DIR=$dir"
powershell -NoProfile -ExecutionPolicy Bypass -Command "iwr $installer | iex"
exit /b %errorlevel%

:bplogs
cd /d "%~dp0"
if not exist "log\baseport-*.log" (
  echo No log files in %~dp0log yet.>&2
  exit /b 1
)
set "COUNT=%~2"
if not defined COUNT set "COUNT=200"
powershell -NoProfile -Command "Get-Content (Get-ChildItem 'log\baseport-*.log' | Sort-Object LastWriteTime | Select-Object -Last 1).FullName -Tail %COUNT% -Wait"
exit /b %errorlevel%
"@
    Set-Content -Path (Join-Path $dir 'baseport.cmd') -Value $shim -Encoding ASCII

    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if ($userPath -notlike "*$dir*") {
        $joined = if ($userPath) { "$userPath;$dir" } else { $dir }
        [Environment]::SetEnvironmentVariable('Path', $joined, 'User')
        $addedToPath = $true
    }
}
finally {
    Remove-Item -Path $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
if ($update) {
    Write-Host "Baseport updated to $tag in $dir."
} else {
    Write-Host "Baseport $tag installed in $dir."
}
Write-Host ""
Write-Host "  baseport                             start on http://localhost:5000"
Write-Host "  baseport --urls http://0.0.0.0:5000  start on every interface"
Write-Host "  baseport logs                        follow the log files"
Write-Host "  baseport help                        everything else"
Write-Host ""
Write-Host "Console http://localhost:5000/_/admin, first start prints a one-time admin login."

$here = (Get-Location).Path
if ((Test-Path (Join-Path $here 'Baseport.exe')) -and ($here -ne $dir)) {
    Write-Host ""
    Write-Host "Warning: another Baseport sits in $here. It was not updated."
    Write-Host "If that is the one you run:"
    Write-Host "  `$env:BASEPORT_DIR='$here'; iwr $installer | iex"
}

$task = Get-ScheduledTask -TaskName 'Baseport' -ErrorAction SilentlyContinue
if ($task) {
    Write-Host ""
    Write-Host "Restart the scheduled task:"
    Write-Host "  Stop-ScheduledTask -TaskName Baseport; Start-ScheduledTask -TaskName Baseport"
}

if ($addedToPath) {
    Write-Host ""
    Write-Host "Added $dir to your user PATH. Open a new terminal before using `"baseport`"."
}
