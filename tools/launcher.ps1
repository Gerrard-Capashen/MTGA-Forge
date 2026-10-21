# One start button: (re)patches the client when MTGA was updated, starts the Forge server, waits for its ports, starts the patched
# MTGA.exe and stops the server when the client exits.
# Usage: MTGA-Forge.cmd [-Source <MTGA install>] [-Folder <folder>] [-PatchOnly] [-Repatch] [-Firewall] [-ClientArgs '-screen-width 1280 ...']
#   -Folder      where the patched client (client\), your decks and settings (data\) and the logs (logs\) live; default %LOCALAPPDATA%\MTGA-Forge
#                (the patched client is a full copy of the game, about 14 GB: pick a drive with room; once chosen it is remembered)
#   -Source      the MTGA install to patch from (found through Steam by default; remembered once given)
#   -PatchOnly   patch if needed, do not start anything      -Repatch  patch again even when everything looks current
#   -Firewall    also create the outbound-block firewall rule for the patched MTGA.exe (asks for administrator rights once)
# Exit code: 0 = the client ran and was closed, 1 = problem (message printed), 2 = another instance is running.
param(
    [string]$Source = '',
    [string]$Folder = '',
    [switch]$PatchOnly,
    [switch]$Repatch,
    [switch]$Firewall,
    [string]$ClientArgs = ''
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\layout.ps1"
$ports = 30003, 30010, 30011        # Match Door, Front Door, Art server (the client's defaults; the server's defaults)

function Stop-WithMessage($msg, $code = 1) {
    Write-Host "`n$msg" -ForegroundColor Red
    if ($Host.Name -eq 'ConsoleHost' -and -not [Console]::IsInputRedirected) { Write-Host 'Press Enter to close.'; [void][Console]::ReadLine() }
    exit $code
}

# ------------------------------------------------------------------------------------------------ settings
$defaultHome = "$env:LOCALAPPDATA\MTGA-Forge"
$cfgPath = "$defaultHome\config.json"
$cfg = [ordered]@{}
if (Test-Path $cfgPath) { try { (Get-Content $cfgPath -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $cfg[$_.Name] = $_.Value } } catch { } }
if ($Folder) { $cfg['home'] = (New-Item -ItemType Directory -Force $Folder).FullName }
if ($Source) { $cfg['source'] = (Resolve-Path $Source).Path }
$homeDir = if ($cfg['home']) { $cfg['home'] } else { $defaultHome }
New-Item -ItemType Directory -Force $homeDir, "$homeDir\logs", "$homeDir\data" | Out-Null
if ($Folder -or $Source) { New-Item -ItemType Directory -Force $defaultHome | Out-Null; $cfg | ConvertTo-Json | Set-Content $cfgPath -Encoding utf8 }
$client = "$homeDir\client"
$dataDir = "$homeDir\data"
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$serverLog = "$homeDir\logs\server-$stamp.log"
Get-ChildItem "$homeDir\logs\server-*.log*" | Sort-Object LastWriteTime -Descending | Select-Object -Skip 20 | Remove-Item -Force -ErrorAction SilentlyContinue

# ------------------------------------------------------------------------------------------------ one instance per home
$mutex = [Threading.Mutex]::new($false, 'Global\MTGA-Forge-' + ($homeDir -replace '[^A-Za-z0-9]', '_'))
if (-not $mutex.WaitOne(0)) { Stop-WithMessage 'MTGA-Forge is already running.' 2 }

$patchArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "$PSScriptRoot\forge-patch.ps1", '-Target', $client)
if ($cfg['source']) { $patchArgs += '-Source', $cfg['source'] }
$server = $null
$clientProc = $null
try {
    # -------------------------------------------------------------------------------------------- patch when needed
    $check = & powershell @patchArgs -Check
    $code = $LASTEXITCODE
    Write-Host ($check -join "`n")
    $haveClient = (Test-Path "$client\MTGA.exe") -and (Test-Path "$client\forge-patch\state.json") -and
        (Get-Content "$client\forge-patch\state.json" -Raw | ConvertFrom-Json).steps.manifest.ok
    switch ($code) {
        0 { if ($Repatch) { $code = 3 } }
        3 { }
        4 { if ($haveClient) { Write-Host 'No MTGA install found: starting the client as it is (it cannot be updated).' -ForegroundColor Yellow }
            else { Stop-WithMessage "MTGA is not installed (or not where Steam says). Install it with Steam, or run MTGA-Forge.cmd -Source <folder that holds MTGA.exe>." } }
        5 { Stop-WithMessage 'Steam is still updating MTGA. Close this window, wait for the update to finish, then start again.' }
        default { Stop-WithMessage "Could not check the install (exit code $code)." }
    }
    if ($code -eq 3) {
        # a refresh copies the whole game: check the disk first
        $src = if ($cfg['source']) { $cfg['source'] } else { Find-MtgaInstall }
        $need = (Get-ChildItem $src -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum + 2GB
        $drive = Get-PSDrive -Name ($homeDir.Substring(0, 1)) -ErrorAction SilentlyContinue
        if ($drive -and $drive.Free -lt $need) { Stop-WithMessage ("Not enough free space on $($drive.Name): ($([int]($drive.Free / 1GB)) GB free, about $([int]($need / 1GB)) GB needed for the patched copy). Free some space or use -Folder <other folder>.") }
        Write-Host "`nPatching the game for Forge (first start and after every MTGA update; takes a few minutes)..." -ForegroundColor Cyan
        $pa = $patchArgs + ($(if ($Firewall) { '-Firewall' } else { '-SkipFirewall' }))
        & powershell @pa
        if ($LASTEXITCODE -ne 0) { Stop-WithMessage "Patching failed (see above). Nothing was started; the original game is untouched. After an MTGA update this can mean the patch needs a new version of MTGA-Forge." }
    } elseif ($Firewall) { & powershell @patchArgs -Only firewall -Firewall }
    if ($PatchOnly) { Write-Host 'Patched. Nothing started (-PatchOnly).'; exit 0 }

    # -------------------------------------------------------------------------------------------- server
    # a server left over from a crashed session of this package
    Get-CimInstance Win32_Process -Filter "Name='java.exe'" | Where-Object { $_.CommandLine -like '*forge.mtga.ForgeMtgaMain*' -and $_.ExecutablePath -eq (Resolve-Path $JavaExe -ErrorAction SilentlyContinue).Path } |
        ForEach-Object { Write-Host "stopping a leftover server (pid $($_.ProcessId))"; Stop-Process -Id $_.ProcessId -Force }
    $busy = @(Get-NetTCPConnection -State Listen -LocalPort $ports -ErrorAction SilentlyContinue)
    if ($busy) { Stop-WithMessage "Port $(($busy.LocalPort | Sort-Object -Unique) -join ', ') is used by another program ($((Get-Process -Id $busy[0].OwningProcess -ErrorAction SilentlyContinue).ProcessName)). Close it and start again." }

    $jargs = @("-Dmtga.data=`"$dataDir`"", '-cp', "`"$ServerCp`"", 'forge.mtga.ForgeMtgaMain', "`"$GuiDir`"", "`"$client`"", "$($ports[0])")
    Write-Host "Starting the Forge server (log: $serverLog)"
    $server = Start-Process $JavaExe -ArgumentList $jargs -WorkingDirectory $GuiDir -RedirectStandardOutput $serverLog -RedirectStandardError "$serverLog.err" -PassThru -WindowStyle Hidden
    $deadline = (Get-Date).AddSeconds(180)
    while ($true) {
        if ($server.HasExited) { Stop-WithMessage "The server stopped at start-up (exit code $($server.ExitCode)):`n$((Get-Content "$serverLog.err" -ErrorAction SilentlyContinue | Select-Object -First 8) -join "`n")`nLog: $serverLog" }
        $up = @(Get-NetTCPConnection -State Listen -LocalPort $ports -OwningProcess $server.Id -ErrorAction SilentlyContinue | Select-Object -ExpandProperty LocalPort -Unique)
        if ($up.Count -eq $ports.Count) { break }
        if ((Get-Date) -gt $deadline) { Stop-WithMessage "The server did not open its ports within 3 minutes (open: $($up -join ', ')). Log: $serverLog" }
        Start-Sleep -Milliseconds 500
    }
    Write-Host "Server ready on ports $($ports -join ', ')."

    # -------------------------------------------------------------------------------------------- client
    Get-ChildItem Env: | Where-Object { $_.Name -like 'FORGEBRIDGE_*' } | ForEach-Object { Remove-Item "Env:$($_.Name)" }
    $sp = @{ FilePath = "$client\MTGA.exe"; WorkingDirectory = $client; PassThru = $true }
    if ($ClientArgs) { $sp.ArgumentList = $ClientArgs }
    $clientProc = Start-Process @sp
    Write-Host "MTGA started (pid $($clientProc.Id)). Close the game window to stop the server."
    while (-not $clientProc.HasExited) {
        if ($server.HasExited) {
            Write-Host "The Forge server stopped unexpectedly (exit code $($server.ExitCode), log: $serverLog); closing the game." -ForegroundColor Red
            Stop-Process -Id $clientProc.Id -Force -ErrorAction SilentlyContinue
            exit 1
        }
        Start-Sleep -Seconds 1
    }
    Write-Host 'MTGA closed.'
} finally {
    if ($server -and -not $server.HasExited) {
        Write-Host 'Stopping the Forge server.'
        & taskkill /PID $server.Id /T /F 2>&1 | Out-Null
    }
    $mutex.ReleaseMutex()
}
