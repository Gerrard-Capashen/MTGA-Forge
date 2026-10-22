# One start button: (re)patches the client when MTGA was updated, starts the Forge server, waits for its ports, starts the patched
# MTGA.exe and stops the server when the client exits.
# Usage: MTGA-Forge.cmd [-Source <MTGA install>] [-Folder <folder>] [-PatchOnly] [-Repatch] [-Firewall] [-ClientArgs '-screen-width 1280 ...'] [-Instance 2 -Name Bob] [-P2P host | -P2P join -Peer <ip>]
#   -Folder      where the patched client (client\), your decks and settings (data\) and the logs (logs\) live; default %LOCALAPPDATA%\MTGA-Forge
#                (the patched client is a full copy of the game, about 14 GB: pick a drive with room; once chosen it is remembered)
#   -Source      the MTGA install to patch from (found through Steam by default; remembered once given)
#   -PatchOnly   patch if needed, do not start anything      -Repatch  patch again even when everything looks current
#   -Instance N  a second (third, ...) client on the same machine: ports +100*N (Match Door 30003+100N, Front Door 30010+100N, art 30011+100N), its own Unity
#                identity (product MTG<N>: own PlayerPrefs and Player.log) and, without -ClientArgs, a 1280x720 window. Use a different -Folder per instance
#                (each has its own 14 GB patched client, decks and logs). N = 0..9, default 0 = the normal single client.
#   -Name        player name shown in the game (default 'Forge Player', with -Instance 'Forge Player N')
#   -P2P host|join  play a friend instead of the bot: pick the bot-match queue entry with a deck. host: waits for ONE guest on the peer port
#                (30012 + 100*Instance; prints the addresses to give your friend, e.g. your Tailscale/ZeroTier IP); join: connects to -Peer.
#                Both need the same MTGA build and MTGA-Forge version. Start the host's game first, then the guest's.
#   -Peer        host address for -P2P join: <ip> or <ip>:<port>        -PeerKey  optional shared secret both sides must give
#   -NoPause     do not wait for Enter after an error (the launcher window uses it)
#   -Firewall    also create the outbound-block firewall rule for the patched MTGA.exe (asks for administrator rights once)
# Exit code: 0 = the client ran and was closed, 1 = problem (message printed), 2 = another instance is running.
param(
    [string]$Source = '',
    [string]$Folder = '',
    [switch]$PatchOnly,
    [switch]$Repatch,
    [switch]$Firewall,
    [ValidateRange(0, 9)][int]$Instance = 0,
    [string]$Name = '',
    [ValidateSet('', 'host', 'join')][string]$P2P = '',
    [string]$Peer = '',
    [string]$PeerKey = '',
    [string]$ClientArgs = '',
    [switch]$NoPause
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\layout.ps1"
$ports = 30003, 30010, 30011        # Match Door, Front Door, Art server (the client's defaults; the server's defaults)
$ports = @($ports | ForEach-Object { $_ + 100 * $Instance })
$peerPort = 30012 + 100 * $Instance
if ($P2P -eq 'join' -and -not $Peer) { Write-Host 'P2P join needs -Peer <host ip>[:port]' -ForegroundColor Red; exit 1 }
$product = if ($Instance -eq 0) { 'MTGF' } else { "MTG$Instance" }     # Unity identity (4 characters): PlayerPrefs registry key and LocalLow\Player.log
if (-not $Name) { $Name = if ($Instance -eq 0) { 'Forge Player' } else { "Forge Player $Instance" } }

function Stop-WithMessage($msg, $code = 1) {
    Write-Host "`n$msg" -ForegroundColor Red
    if (-not $NoPause -and $Host.Name -eq 'ConsoleHost' -and -not [Console]::IsInputRedirected) { Write-Host 'Press Enter to close.'; [void][Console]::ReadLine() }
    exit $code
}

# ------------------------------------------------------------------------------------------------ settings
$defaultHome = "$env:LOCALAPPDATA\MTGA-Forge"
$cfgPath = "$defaultHome\config.json"
$cfg = [ordered]@{}
if (Test-Path $cfgPath) { try { (Get-Content $cfgPath -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $cfg[$_.Name] = $_.Value } } catch { } }
# extra instances (-Instance N) never read or write the remembered folder: several launchers start at once and share this file
if ($Instance -gt 0) { $cfg.Remove('home'); if ($Folder) { $cfg['home'] = (New-Item -ItemType Directory -Force $Folder).FullName } else { $cfg['home'] = "$defaultHome-$Instance" } }
elseif ($Folder) { $cfg['home'] = (New-Item -ItemType Directory -Force $Folder).FullName }
if ($Source) { $cfg['source'] = (Resolve-Path $Source).Path }
$homeDir = if ($cfg['home']) { $cfg['home'] } else { $defaultHome }
New-Item -ItemType Directory -Force $homeDir, "$homeDir\logs", "$homeDir\data" | Out-Null
if ($Instance -eq 0 -and ($Folder -or $Source)) { New-Item -ItemType Directory -Force $defaultHome | Out-Null; $cfg | ConvertTo-Json | Set-Content $cfgPath -Encoding utf8 }
$client = "$homeDir\client"
$dataDir = "$homeDir\data"
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$serverLog = "$homeDir\logs\server-$stamp.log"
Get-ChildItem "$homeDir\logs\server-*.log*" | Sort-Object LastWriteTime -Descending | Select-Object -Skip 20 | Remove-Item -Force -ErrorAction SilentlyContinue

# ------------------------------------------------------------------------------------------------ one instance per home
$mutex = [Threading.Mutex]::new($false, 'Global\MTGA-Forge-' + ($homeDir -replace '[^A-Za-z0-9]', '_'))
if (-not $mutex.WaitOne(0)) { Stop-WithMessage 'MTGA-Forge is already running.' 2 }

$patchArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "$PSScriptRoot\forge-patch.ps1", '-Target', $client, '-Product', $product)
if ($cfg['source']) { $patchArgs += '-Source', $cfg['source'] }
$server = $null
$clientProc = $null
try {
    # -------------------------------------------------------------------------------------------- patch when needed
    $ErrorActionPreference = 'Continue'   # Windows PowerShell 5.1 turns a native program's stderr lines into terminating errors when output is redirected
    $check = & powershell @patchArgs -Check
    $ErrorActionPreference = 'Stop'
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
        $ErrorActionPreference = 'Continue'
        & powershell @pa
        $paExit = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
        if ($paExit -ne 0) { Stop-WithMessage "Patching failed (see above). Nothing was started; the original game is untouched. After an MTGA update this can mean the patch needs a new version of MTGA-Forge." }
    } elseif ($Firewall) { & powershell @patchArgs -Only firewall -Firewall }
    if ($PatchOnly) { Write-Host 'Patched. Nothing started (-PatchOnly).'; exit 0 }

    # -------------------------------------------------------------------------------------------- server
    # a server left over from a crashed session of this package
    Get-CimInstance Win32_Process -Filter "Name='java.exe'" | Where-Object { $_.CommandLine -like '*forge.mtga.ForgeMtgaMain*' -and $_.CommandLine -like "*$dataDir*" -and $_.ExecutablePath -eq (Resolve-Path $JavaExe -ErrorAction SilentlyContinue).Path } |
        ForEach-Object { Write-Host "stopping a leftover server (pid $($_.ProcessId))"; Stop-Process -Id $_.ProcessId -Force }
    $busy = @(Get-NetTCPConnection -State Listen -LocalPort $ports -ErrorAction SilentlyContinue)
    if ($busy) { Stop-WithMessage "Port $(($busy.LocalPort | Sort-Object -Unique) -join ', ') is used by another program ($((Get-Process -Id $busy[0].OwningProcess -ErrorAction SilentlyContinue).ProcessName)). Close it and start again." }

    $p2pArgs = @()
    if ($P2P) {
        $p2pArgs += "-Dmtga.p2p=$P2P", "-Dmtga.peerport=$peerPort"
        if ($Peer) { $p2pArgs += "-Dmtga.peer=$Peer" }
        if ($PeerKey) { $p2pArgs += "-Dmtga.peerkey=`"$PeerKey`"" }
        if ($P2P -eq 'host') { $busyPeer = @(Get-NetTCPConnection -State Listen -LocalPort $peerPort -ErrorAction SilentlyContinue); if ($busyPeer) { Stop-WithMessage "Peer port $peerPort is used by another program. Close it and start again." } }
    }
    $jargs = @("-Dmtga.data=`"$dataDir`"", "-Dmtga.name=`"$Name`"", "-Dmtga.fdport=$($ports[1])", "-Dmtga.artport=$($ports[2])") + $p2pArgs + @('-cp', "`"$ServerCp`"", 'forge.mtga.ForgeMtgaMain', "`"$GuiDir`"", "`"$client`"", "$($ports[0])")
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
    Write-Host "[$Name, instance $Instance] Server ready on ports $($ports -join ', ')."
    if ($P2P -eq 'host') {
        Write-Host "P2P host: waiting for ONE guest on port $peerPort. Give your friend one of these addresses ('-P2P join -Peer <address>'):" -ForegroundColor Cyan
        Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } | ForEach-Object {
            $kind = if ($_.IPAddress -match '^100\.(6[4-9]|[7-9]\d|1[01]\d|12[0-7])\.') { 'Tailscale' } elseif ($_.InterfaceAlias -like '*ZeroTier*') { 'ZeroTier' } else { $_.InterfaceAlias }
            Write-Host ("   {0}:{1}   ({2})" -f $_.IPAddress, $peerPort, $kind)
        }
        Write-Host "In the game: play your deck from the Bot Match queue; it waits until your friend joins." -ForegroundColor Cyan
    } elseif ($P2P -eq 'join') {
        Write-Host "P2P guest: play your deck from the Bot Match queue AFTER the host started theirs; it connects to $Peer." -ForegroundColor Cyan
    }

    # -------------------------------------------------------------------------------------------- client
    Get-ChildItem Env: | Where-Object { $_.Name -like 'FORGEBRIDGE_*' } | ForEach-Object { Remove-Item "Env:$($_.Name)" }
    $env:FORGEBRIDGE_FDPORT = "$($ports[1])"; $env:FORGEBRIDGE_ARTPORT = "$($ports[2])"; $env:FORGEBRIDGE_NAME = $Name
    if ($Instance -gt 0 -and -not $ClientArgs) { $ClientArgs = '-screen-fullscreen 0 -screen-width 1280 -screen-height 720' }
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
