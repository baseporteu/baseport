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
for %%V in (update logs status doctor uninstall) do if /I "%~1"=="%%V" goto :bptools
if /I "%~1"=="-h" goto :bphelp
if /I "%~1"=="--help" goto :bphelp
cd /d "%~dp0"
"%~dp0Baseport.exe" %*
exit /b %errorlevel%

:bphelp
cd /d "%~dp0"
"%~dp0Baseport.exe" help
exit /b %errorlevel%

:bptools
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0baseport-tools.ps1" %*
exit /b %errorlevel%
"@
    Set-Content -Path (Join-Path $dir 'baseport.cmd') -Value $shim -Encoding ASCII

    # The batch shim stays thin and every verb that needs real logic lives here, where it can be read.
    $toolsHeader = "`$repo = '$repo'`r`n`$dir = '$dir'`r`n`$installer = '$installer'`r`n"
    $toolsBody = @'
$ErrorActionPreference = 'Stop'
$exe  = Join-Path $dir 'Baseport.exe'
$url  = 'http://localhost:5000'
$verb = if ($args.Count -gt 0) { $args[0].ToLowerInvariant() } else { 'doctor' }
$rest = if ($args.Count -gt 1) { $args[1..($args.Count - 1)] } else { @() }
$rc   = 0

function Get-BaseportProcess { Get-Process Baseport -ErrorAction SilentlyContinue }
function Get-BaseportTask { Get-ScheduledTask -TaskName 'Baseport' -ErrorAction SilentlyContinue }
function Get-LatestLog {
    Get-ChildItem (Join-Path $dir 'log\baseport-*.log') -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 1
}
function Ok($m) { Write-Host "ok    $m" }
function Warn($m) { Write-Host "warn  $m" }
function Bad($m) { Write-Host "FAIL  $m"; $script:rc = 1 }

switch ($verb) {

'update' {
    $env:BASEPORT_REPO = $repo
    $env:BASEPORT_DIR = $dir
    Invoke-Expression (Invoke-WebRequest -UseBasicParsing $installer).Content
}

'logs' {
    $log = Get-LatestLog
    if (-not $log) { Write-Error "No log files in $dir\log yet."; exit 1 }
    $count = if ($rest.Count -gt 0) { [int]$rest[0] } else { 200 }
    Get-Content $log.FullName -Tail $count -Wait
}

'status' {
    $p = Get-BaseportProcess
    if ($p) { Write-Host ("Baseport is running as pid " + ($p.Id -join ', ') + ", on $url.") }
    else { Write-Host "Baseport is not running. Start it with: baseport" }
    $task = Get-BaseportTask
    if ($task) { Write-Host ("The Baseport scheduled task is " + $task.State + ".") }
}

'doctor' {
    if (Test-Path $exe) { Ok ("Baseport " + (& $exe version) + " in $dir") }
    else { Bad "$dir\Baseport.exe is missing. Reinstall: iwr $installer | iex" }

    $onPath = ([Environment]::GetEnvironmentVariable('Path', 'User') + ';' + $env:Path) -split ';' |
        Where-Object { $_ -and $_.TrimEnd('\') -eq $dir.TrimEnd('\') }
    if ($onPath) { Ok "$dir is on your PATH" }
    else { Warn "$dir is not on your PATH, so the baseport command only works from that folder." }

    $db = Join-Path $dir 'baseport.db'
    if (Test-Path $db) { Ok ("database $db, " + [math]::Round((Get-Item $db).Length / 1MB, 1) + " MB") }
    else { Warn "no database yet, the first start creates one and prints a one-time admin login." }

    if (Get-BaseportProcess) { Ok "a Baseport process is running" }
    else { Warn "no Baseport process is running." }

    try {
        Invoke-WebRequest -UseBasicParsing -TimeoutSec 3 $url | Out-Null
        Ok "answering on $url, console at $url/_/admin"
    } catch { Warn "nothing answered on $url." }

    $drive = Get-PSDrive -Name (Split-Path $dir -Qualifier).TrimEnd(':') -ErrorAction SilentlyContinue
    if ($drive) { Write-Host ("disk  " + [math]::Round($drive.Free / 1GB, 1) + " GB free on " + $drive.Name + ":") }
    exit $rc
}

'uninstall' {
    $purge = $rest -contains '--purge'
    if ($purge -and [Environment]::UserInteractive) {
        $answer = Read-Host "Delete $dir with its database, uploads and backups? This cannot be undone. [y/N]"
        if ($answer -notmatch '^(y|yes)$') { Write-Host "Cancelled, nothing was removed."; exit 1 }
    }

    Get-BaseportProcess | Stop-Process -Force -ErrorAction SilentlyContinue
    if (Get-BaseportTask) {
        try {
            Unregister-ScheduledTask -TaskName 'Baseport' -Confirm:$false
            Write-Host "Removed the Baseport scheduled task."
        } catch { Write-Host "Could not remove the Baseport scheduled task, do that as an administrator." }
    }

    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if ($userPath) {
        $kept = ($userPath -split ';' | Where-Object { $_ -and $_.TrimEnd('\') -ne $dir.TrimEnd('\') }) -join ';'
        if ($kept -ne $userPath) {
            [Environment]::SetEnvironmentVariable('Path', $kept, 'User')
            Write-Host "Removed $dir from your user PATH."
        }
    }

    $keep = @('baseport.db', 'baseport.db-shm', 'baseport.db-wal', 'baseport.key', 'uploads', 'backups', 'log', 'appsettings.json')
    $shimFiles = @('baseport.cmd', 'baseport-tools.ps1')
    if ($purge) {
        Get-ChildItem $dir -Force | Where-Object { $shimFiles -notcontains $_.Name } |
            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "Removed $dir and everything in it."
    } else {
        Get-ChildItem $dir -Force | Where-Object { $keep -notcontains $_.Name -and $shimFiles -notcontains $_.Name } |
            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "Removed the Baseport program files from $dir."
        Write-Host "Your data stayed: baseport.db, baseport.key, appsettings.json, uploads, backups, log."
        Write-Host "Delete that as well with: Remove-Item -Recurse -Force '$dir'"
    }

    # cmd.exe holds the batch file open for as long as it runs, so these two go a moment after this process exits.
    $tail = if ($purge) { "rd /s /q `"$dir`"" } else { ($shimFiles | ForEach-Object { "del /q `"" + (Join-Path $dir $_) + "`"" }) -join ' & ' }
    Start-Process cmd -ArgumentList '/c', "timeout /t 2 >nul & $tail" -WindowStyle Hidden
    Write-Host "Removed the baseport command."
}

default {
    Write-Error "$verb is not a baseport-tools command."
    exit 1
}

}
'@
    Set-Content -Path (Join-Path $dir 'baseport-tools.ps1') -Value ($toolsHeader + $toolsBody) -Encoding UTF8

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
Write-Host "  baseport status                      is it running, and where"
Write-Host "  baseport logs                        follow the log files"
Write-Host "  baseport doctor                      check this install"
Write-Host "  baseport update                      pull the latest release"
Write-Host "  baseport uninstall [--purge]         remove it, --purge deletes the data too"
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
