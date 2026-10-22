# Does a patched client folder work? Runs the scripted recipes of PROGRESS.md against a fresh Java server each, checks them by
# log assertions + screenshots + a socket check on MTGA.exe, runs SelfPlay, and prints a pass/fail table (exit code 1 on any FAIL).
# Read only: it neither builds nor patches anything (use deploy.ps1 / the patcher first).
#
# Usage: powershell -NoProfile -File tools\verify.ps1 [-Client <folder>] [-Only nc,bolt,...] [-SkipSelfPlay] [-SkipClient]
#        [-Port 30003 -FdPort 30010 -ArtPort 30011] [-DataDir <dir>] [-ClassPath <file>] [-Out <dir>] [-BuildServer]
# Recipes (client): nc bolt block bf et nf fd-loop fd-newdeck fd-delete fd-import      SelfPlay: sp-default sp-Standard sp-Pioneer sp-Modern sp-pvp sp-peer sp-online sp-profile sp-queues
# Needs: java + built forge-gui-mtga (-BuildServer compiles it), tools\cp.txt, a card DB in <Client>\MTGA_Data\Downloads, the
# player's window (Unity renders only on an unlocked desktop), Scryfall access for the "art served" check.
# Evidence per recipe in <Out>\<recipe>\ (player.log, server.log, capture.log, net.txt, shots\). The data dir (decks) is shared with
# normal play: decks the recipes create are removed again at the end.
param(
    [string]$Client = '',
    [string[]]$Only = @(),
    [switch]$SkipSelfPlay,
    [switch]$SkipClient,
    [switch]$BuildServer,
    [int]$Port = 30003,
    [int]$FdPort = 30010,
    [int]$ArtPort = 30011,
    [string]$DataDir = '',
    [string]$ClassPath = '',
    [string]$Out = '',
    [int]$SelfPlayGames = 4
)
$ErrorActionPreference = 'Stop'
$Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ })   # -File passes a,b as one string
. "$PSScriptRoot\layout.ps1"      # $Root, $Dist, $JavaExe, $ServerCp, $GuiDir (dev tree or packaged distribution)
$root = $Root
if (-not $Client) { $Client = "$root\client" }
$Client = (Resolve-Path $Client).Path
if (-not $DataDir) { $DataDir = Join-Path (Split-Path $Client -Parent) 'data' }
if (-not $Out) { $Out = if ($Dist) { "$env:LOCALAPPDATA\MTGA-Forge\verify-out\$(Get-Date -Format 'yyyyMMdd-HHmmss')" } else { "$root\tools\verify-out\$(Get-Date -Format 'yyyyMMdd-HHmmss')" } }
New-Item -ItemType Directory -Force $Out | Out-Null
$guiDir = $GuiDir
$exe = Join-Path $Client 'MTGA.exe'
$cp = if ($ClassPath) { (Get-Content $ClassPath -Raw).Trim() + ";$root\forge\forge-gui-mtga\target\classes" } else { $ServerCp }

# Unity writes Player.log to LocalLow\<app.info line 1>\<line 2>
$appInfo = @(Get-Content "$Client\MTGA_Data\app.info")
$playerLog = "$env:USERPROFILE\AppData\LocalLow\$($appInfo[0])\$($appInfo[1])\Player.log"

foreach ($f in @($exe, "$Client\MTGA_Data\Managed\ForgeBridge.dll") + @($ClassPath | Where-Object { $_ })) {
    if (-not (Test-Path $f)) { throw "missing: $f" }
}
if ($BuildServer -and -not $Dist) {
    Push-Location "$root\forge"
    & "$root\tools\apache-maven-3.9.9\bin\mvn.cmd" -B -q -o -DskipTests -pl forge-gui-mtga -am compile
    if ($LASTEXITCODE -ne 0) { throw 'server build failed' }
    Pop-Location
}
if (-not $Dist -and -not (Test-Path "$root\forge\forge-gui-mtga\target\classes\forge\mtga\ForgeMtgaMain.class")) { throw 'forge-gui-mtga not built (use -BuildServer)' }

function Stop-Ours {
    Get-Process MTGA -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
    Get-CimInstance Win32_Process -Filter "Name='java.exe'" | Where-Object { $_.CommandLine -like '*forge.mtga.ForgeMtgaMain*' -and $_.CommandLine -like "*$Port*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
}
Stop-Ours
foreach ($pt in $Port, $FdPort, $ArtPort) {
    if (Get-NetTCPConnection -LocalPort $pt -State Listen -ErrorAction SilentlyContinue) { throw "port $pt is in use" }
}

$deckDir = Join-Path $DataDir 'decks'
$decksBefore = @(if (Test-Path $deckDir) { Get-ChildItem $deckDir -Filter *.json | ForEach-Object Name })

# ---------------------------------------------------------------------------------------------------------------- helpers
$script:results = New-Object System.Collections.ArrayList
$script:cur = $null
function Start-Entry($name) { $script:cur = [pscustomobject]@{ Name = $name; Checks = (New-Object System.Collections.ArrayList); Secs = 0 }; [void]$script:results.Add($script:cur) }
function Check($label, [bool]$ok, $detail = '') {
    [void]$script:cur.Checks.Add([pscustomobject]@{ Label = $label; Ok = $ok; Detail = "$detail" })
    Write-Host ("  [{0}] {1} {2}" -f $(if ($ok) { 'ok  ' } else { 'FAIL' }), $label, $detail)
}
function Test-Loopback($a) { return ($a -match '^127\.' -or $a -eq '::1' -or $a -eq '[::1]') }
function Test-Unspec($a) { return ($a -in '0.0.0.0', '::', '') }

# nothing but loopback: no connection to a non-loopback peer, no listener / UDP endpoint reachable from outside
function Sample-Sockets($pids, $seen) {
    $bad = @()
    foreach ($c in @(Get-NetTCPConnection -ErrorAction SilentlyContinue | Where-Object { $pids -contains $_.OwningProcess })) {
        $key = "tcp $($c.State) $($c.LocalAddress):$($c.LocalPort) -> $($c.RemoteAddress):$($c.RemotePort)"
        $evil = if ($c.State -eq 'Listen') { -not (Test-Loopback $c.LocalAddress) }
                elseif ($c.State -eq 'Bound') { -not (Test-Loopback $c.LocalAddress) -and -not (Test-Unspec $c.LocalAddress) }
                else { -not (Test-Loopback $c.RemoteAddress) -and -not (Test-Unspec $c.RemoteAddress) }
        if ($evil -and $seen.Add($key)) { $bad += $key }
    }
    foreach ($u in @(Get-NetUDPEndpoint -ErrorAction SilentlyContinue | Where-Object { $pids -contains $_.OwningProcess })) {
        $key = "udp $($u.LocalAddress):$($u.LocalPort)"
        if (-not (Test-Loopback $u.LocalAddress) -and $seen.Add($key)) { $bad += $key }
    }
    return $bad
}

# a real picture: big enough, neither black nor one flat colour
function Test-Shot($path) {
    if (-not (Test-Path $path)) { return 'missing' }
    if ((Get-Item $path).Length -lt 20000) { return 'tiny file' }
    Add-Type -AssemblyName System.Drawing
    $bmp = [System.Drawing.Bitmap]::FromFile($path)
    try {
        $vals = @()
        for ($x = 0; $x -lt 32; $x++) { for ($y = 0; $y -lt 18; $y++) {
            $c = $bmp.GetPixel([int](($x + 0.5) * $bmp.Width / 32), [int](($y + 0.5) * $bmp.Height / 18))
            $vals += (0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B)
        } }
        $mean = ($vals | Measure-Object -Average).Average
        $sd = [Math]::Sqrt((($vals | ForEach-Object { ($_ - $mean) * ($_ - $mean) }) | Measure-Object -Average).Average)
        if ($mean -lt 6) { return 'black' }
        if ($sd -lt 8) { return ('flat sd={0:N1}' -f $sd) }
        return ''
    } finally { $bmp.Dispose() }
}

function Wait-ServerUp($logFile, $proc) {
    for ($i = 0; $i -lt 120; $i++) {
        Start-Sleep -Milliseconds 1000
        if ($proc.HasExited) { return $false }
        if ((Test-Path $logFile) -and (Select-String -Path $logFile -Pattern 'Art server (listening|disabled)' -Quiet)) { return $true }
    }
    return $false
}

# ------------------------------------------------------------------------------------------------------- client recipes
# Clicks are the recipes of PROGRESS.md (times in seconds after launch). Server = capture (GRE traffic) + stdout ([mtga] lines).
function Pass-Clicks($from, $to, $step) { ($from..$to | Where-Object { ($_ - $from) % $step -eq 0 } | ForEach-Object { "$($_):promptbutton_primary" }) -join ',' }
$recipes = [ordered]@{
    'nc' = @{ Deck = 'newcards'; Rig = 'nc'; Seconds = 80; Shots = '44,60'; Clicks = '45:keep,52:hand0|dbl'
        Server = @('rig: Blade of the Bloodchief', 'art 9\d{6} served'); Capture = @('actionType: ActionType_Cast grpId: 9\d{6}')
        Note = 'generated card (CardDbBuilder) cast, art from the Scryfall server' }
    'bolt' = @{ Rig = 'bolt'; Seconds = 100; Shots = '62,80'; Clicks = '45:keep,50:fullcontrol,56:cdc #101|dbl,62:player #2,66:promptbutton_primary,71:cdc #125|primary,78:promptbutton_primary'
        Capture = @('SERVER -> PayCostsReq', 'CLIENT -> .*ActionType_Activate_Mana', 'ModifiedLife'); Note = 'Full Control, target, manual mana' }
    'block' = @{ Rig = 'block'; Seconds = 90; Shots = '72,80'; Clicks = '45:keep,52:bf0|primary,55:bf1|primary,58:promptbutton_primary'
        Capture = @('SERVER -> DeclareBlockersReq', 'CLIENT -> .*DeclareBlockersResp'); Note = 'blocking' }
    'bf' = @{ Rig = 'bf'; Seconds = 140; Shots = '105,125'; Clicks = ('45:keep,52:cdc #101|dbl,' + (Pass-Clicks 55 135 3))
        Server = @('game over'); Capture = @('CLIENT -> .*DeclareAttackersResp'); Note = 'attack to victory' }
    'et' = @{ Deck = 'eldrazi'; Rig = 'et'; Seconds = 90; Shots = '80'
        Clicks = '45:keep,52:promptbutton_primary,55:promptbutton_primary,58:promptbutton_primary,62:cdc #127|dbl,67:player #2,71:promptbutton_primary'
        Capture = @('SERVER -> GameStateMessage'); Note = 'Eldrazi Temple {C}{C}, multi-mana pool (needs a game of 3 passes; AI may win the roll)' }
    'nf' = @{ Deck = 'newfaces'; Rig = 'nf'; Seconds = 92; Shots = '51,88'
        Clicks = ('45:keep,' + (Pass-Clicks 54 82 4)); Capture = @('SERVER -> GameStateMessage'); Note = 'transform / flip / split cards' }
    'fd-loop' = @{ Menu = $true; FreshPrefs = $true; Seconds = 200; Shots = '125,172'
        Clicks = '78:text=play,84:folder_toggle=on,89:grid - valid decks/deckview_base(clone)/ui/hitbox@1,96:blade_findmatch/buttons/mainbutton,118:keep,128:settingsbutton_desktop_16x9(clone)/button,132:text=concede,148:exitmatchoverlaybutton,170:dump'
        Server = @('FD bot match', 'game over'); StartHooks = 2; Note = 'Bot Match from the home page: play, concede, home again' }
    'fd-newdeck' = @{ Menu = $true; FreshPrefs = $true; Seconds = 125; Shots = '100'
        Clicks = '78:nav_decks,84:folder_toggle=on,88:newdeckbutton,92:text=discard deck,97:pagesmetacardview@1,98:pagesmetacardview@2,102:deckbuilderwidget_desktop_16x9/bottomright/buttons/mainbutton,108:text=ok'
        Server = @('FD saved deck New Deck'); OptionalClicks = @('text=discard deck'); Note = 'deck builder: new deck, add cards, save' }
    'fd-delete' = @{ Menu = $true; FreshPrefs = $true; Seconds = 105; Shots = '90'; NeedsDeck = 'New Deck'
        Clicks = '78:nav_decks,84:folder_toggle=on,88:text~new deck@0,91:delete_mainbutton_round,94:text=ok'
        Server = @('FD deleted deck'); Note = 'deck builder: delete (needs fd-newdeck first)' }
    'fd-import' = @{ Menu = $true; FreshPrefs = $true; Seconds = 175; Shots = '95,115,150,165'; Clipboard = "$root\tools\decks\modern-import-test.txt"
        Clicks = '78:nav_decks,84:folder_toggle=on,88:import_mainbutton_round,93:text=ok,97:nav_home,103:text=play,108:folder_toggle=on,112:text~modern import test,117:blade_findmatch/buttons/mainbutton,140:keep'
        Server = @('FD: AI deck for format Modern'); Player = @('import format: Modern'); Note = 'Modern list import, play it' }
}

function Run-Recipe($name, $r) {
    Start-Entry $name
    $dir = "$Out\$name"; New-Item -ItemType Directory -Force "$dir\shots" | Out-Null
    Write-Host "== $name : $($r.Note)"
    if ($r.NeedsDeck) {
        $have = @(Get-ChildItem $deckDir -Filter *.json -ErrorAction SilentlyContinue | Where-Object { (Get-Content $_.FullName -Raw) -match [regex]::Escape($r.NeedsDeck) })
        if (-not $have) { Check "precondition: a deck '$($r.NeedsDeck)'" $false 'run fd-newdeck first'; return }
    }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Stop-Ours
    if ($r.FreshPrefs) { Remove-Item "$DataDir\preferences.json" -Force -ErrorAction SilentlyContinue }
    $srvLog = "$dir\server.log"; $capLog = "$dir\capture.log"
    $jargs = @("-Dmtga.fdport=$FdPort", "-Dmtga.artport=$ArtPort", "-Dmtga.data=`"$DataDir`"", "-Dmtga.capture=`"$capLog`"")
    if ($r.Rig) { $jargs += "-Dmtga.rig=$($r.Rig)" } else { $jargs += '-Dmtga.norig=1' }
    if ($r.Deck) { $jargs += "-Dmtga.deck=$($r.Deck)" } else { $jargs += '-Dmtga.nodeck=1' }
    $jargs += '-cp', "`"$cp`"", 'forge.mtga.ForgeMtgaMain', "`"$guiDir`"", "`"$Client`"", "$Port"
    $java = Start-Process $JavaExe -ArgumentList $jargs -WorkingDirectory $guiDir -RedirectStandardOutput $srvLog -RedirectStandardError "$srvLog.err" -PassThru -WindowStyle Hidden
    try {
        if (-not (Wait-ServerUp $srvLog $java)) { Check 'server starts' $false (Get-Content "$srvLog.err" -ErrorAction SilentlyContinue | Select-Object -First 3 | Out-String); return }
        if (Test-Path $playerLog) { Remove-Item $playerLog -Force }
        $env:FORGEBRIDGE_MODE = if ($r.Menu) { 'menu' } else { 'duel' }
        $env:FORGEBRIDGE_PORT = "$Port"; $env:FORGEBRIDGE_FDPORT = "$FdPort"; $env:FORGEBRIDGE_ARTPORT = "$ArtPort"; $env:FORGEBRIDGE_DATA = $DataDir
        $env:FORGEBRIDGE_CLICKS = $r.Clicks
        $env:FORGEBRIDGE_SHOTS = $r.Shots; $env:FORGEBRIDGE_SHOT_DIR = "$dir\shots"
        if ($r.Clipboard) { $env:FORGEBRIDGE_CLIPBOARD = $r.Clipboard }
        $p = Start-Process $exe -WorkingDirectory $Client -ArgumentList '-screen-width 1280 -screen-height 720 -screen-fullscreen 0' -PassThru
        Remove-Item Env:FORGEBRIDGE_DATA, Env:FORGEBRIDGE_MODE, Env:FORGEBRIDGE_PORT, Env:FORGEBRIDGE_FDPORT, Env:FORGEBRIDGE_ARTPORT, Env:FORGEBRIDGE_CLICKS, Env:FORGEBRIDGE_SHOTS, Env:FORGEBRIDGE_SHOT_DIR, Env:FORGEBRIDGE_CLIPBOARD -ErrorAction SilentlyContinue

        # run, sampling the client's sockets (MTGA.exe and its children) the whole time
        $seen = New-Object 'System.Collections.Generic.HashSet[string]'; $badNet = @(); $samples = 0
        $pids = @($p.Id); $nextKids = [DateTime]::MinValue
        $end = (Get-Date).AddSeconds($r.Seconds)
        while ((Get-Date) -lt $end -and -not $p.HasExited) {
            if ((Get-Date) -ge $nextKids) {
                $pids = @($p.Id) + @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($p.Id)" -ErrorAction SilentlyContinue | ForEach-Object ProcessId)
                $nextKids = (Get-Date).AddSeconds(5)
            }
            $badNet += Sample-Sockets $pids $seen; $samples++
            Start-Sleep -Milliseconds 250
        }
        $exitedEarly = $p.HasExited
        if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
        Start-Sleep -Seconds 1
        Set-Content "$dir\net.txt" (@("samples=$samples") + @($seen)) -Encoding utf8
        if (Test-Path $playerLog) { Copy-Item $playerLog "$dir\player.log" -Force }
    } finally {
        if (-not $java.HasExited) { Stop-Process -Id $java.Id -Force }
        Stop-Ours
    }
    $sw.Stop(); $script:cur.Secs = [int]$sw.Elapsed.TotalSeconds

    # --- assertions
    Check 'client survived the run' (-not $exitedEarly) $(if ($exitedEarly) { "exit code $($p.ExitCode) before $($r.Seconds) s" })
    Check 'network: loopback only' ($badNet.Count -eq 0) ("$samples samples" + $(if ($badNet) { ': ' + ($badNet -join '; ') }))
    $pl = if (Test-Path "$dir\player.log") { @(Get-Content "$dir\player.log") } else { @() }
    $srv = @(Get-Content $srvLog -ErrorAction SilentlyContinue) + @(Get-Content "$srvLog.err" -ErrorAction SilentlyContinue)
    $cap = @(Get-Content $capLog -ErrorAction SilentlyContinue)
    Check 'Player.log written' ($pl.Count -gt 0)
    Check 'Privacy line' ((($pl -match '\[ForgeBridge\] Privacy: Unity analytics off').Count) -gt 0)
    Check 'LocalStartup ran' ((($pl -match 'LocalStartup.Run entered').Count) -gt 0)
    # client exceptions: every "...Exception" line, minus the known NREs of the missing social manager (SocialUI.Init at load, SocialUI.HandleFriendsChanged
    # when the home page is shown again after a match); the stack is in the next lines
    $exc = @()
    for ($i = 0; $i -lt $pl.Count; $i++) {
        if ($pl[$i] -match '\w+Exception' -and $pl[$i] -notmatch '^\[ForgeBridge\] click ') {
            $ctx = ($pl[$i..([Math]::Min($i + 8, $pl.Count - 1))]) -join "`n"
            if ($ctx -notmatch 'at SocialUI\.(Init|HandleFriendsChanged) ') { $exc += $pl[$i].Substring(0, [Math]::Min(160, $pl[$i].Length)) }
        }
    }
    Check 'no client exceptions (except SocialUI.Init)' ($exc.Count -eq 0) (($exc | Select-Object -First 2) -join ' | ')
    $noArt = @($pl -match 'No card art found')
    Check 'no "No card art found"' ($noArt.Count -eq 0) "$($noArt.Count) lines"
    $miss = @($pl -match "click '.*': 0 candidates" | Where-Object { $o = $_; -not ($r.OptionalClicks | Where-Object { $o -like "*click '$_'*" }) })
    Check 'every scripted click found a target' ($miss.Count -eq 0) (($miss | Select-Object -First 2) -join ' | ')
    Check 'server: Match/Front/Art Door up' (($srv -match 'Match Door listening').Count -gt 0 -and ($srv -match 'Front Door listening').Count -gt 0 -and ($srv -match 'Art server listening').Count -gt 0)
    $srvEx = @($srv -match 'Exception' | Where-Object { $_ -notmatch 'client disconnected|Connection reset|ClosedChannel|forcibly closed|Broken pipe' })
    Check 'no server exceptions' ($srvEx.Count -eq 0) (($srvEx | Select-Object -First 1) -join '')
    foreach ($re in $r.Server) { Check "server log: $re" (($srv -match $re).Count -gt 0) }
    foreach ($re in $r.Capture) { Check "capture: $re" (($cap -match $re).Count -gt 0) }
    foreach ($re in $r.Player) { Check "Player.log: $re" (($pl -match $re).Count -gt 0) }
    if ($r.StartHooks) { $n = ($srv -match 'FD StartHook').Count; Check "FD StartHook x$($r.StartHooks)" ($n -ge $r.StartHooks) "seen $n" }
    foreach ($s in ($r.Shots -split ',')) {
        $f = "$dir\shots\shot_$(($s.Trim()) -replace '\.', '_').png"
        $why = Test-Shot $f
        Check "screenshot $s s" ($why -eq '') $why
    }
}

# ------------------------------------------------------------------------------------------------------------- SelfPlay
function Run-SelfPlay($name, $format, $prop = $null, $okLine = 'SELFPLAY OK') {
    Start-Entry $name
    $dir = "$Out\$name"; New-Item -ItemType Directory -Force $dir | Out-Null
    Write-Host "== $name : headless games with a fake client ($SelfPlayGames games)"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $jargs = @()
    if ($format) { $jargs += "-Dselfplay.format=$format" }
    if ($prop) { $jargs += $prop }
    $jargs += '-cp', "`"$cp`"", 'forge.mtga.SelfPlay', "`"$guiDir`"", "`"$Client`"", "$SelfPlayGames", '1'
    $pr = Start-Process $JavaExe -ArgumentList $jargs -WorkingDirectory $guiDir -RedirectStandardOutput "$dir\selfplay.log" -RedirectStandardError "$dir\selfplay.err" -PassThru -WindowStyle Hidden
    if (-not $pr.WaitForExit(900000)) { Stop-Process -Id $pr.Id -Force; Check 'finishes within 15 min' $false; return }
    $script:cur.Secs = [int]$sw.Elapsed.TotalSeconds
    $o = @(Get-Content "$dir\selfplay.log")
    Check $okLine (($o -match "^$okLine").Count -gt 0) (($o -match '^SELFPLAY' | Select-Object -First 1) -join '')
}

# The player profile without a game: store, cosmetics API, public view (forge.mtga.fd.ProfileCheck, reads the client's cosmetic catalogue)
function Run-ProfileCheck($name) {
    Start-Entry $name
    $dir = "$Out\$name"; New-Item -ItemType Directory -Force $dir | Out-Null
    Write-Host "== $name : profile store and cosmetics (headless)"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $pr = Start-Process $JavaExe -ArgumentList '-cp', "`"$cp`"", 'forge.mtga.fd.ProfileCheck', "`"$Client`"" -WorkingDirectory $guiDir -RedirectStandardOutput "$dir\profilecheck.log" -RedirectStandardError "$dir\profilecheck.err" -PassThru -WindowStyle Hidden
    if (-not $pr.WaitForExit(300000)) { Stop-Process -Id $pr.Id -Force; Check 'finishes within 5 min' $false; return }
    $script:cur.Secs = [int]$sw.Elapsed.TotalSeconds
    $o = @(Get-Content "$dir\profilecheck.log")
    Check 'PROFILE CHECK OK' (($o -match '^PROFILE CHECK OK').Count -gt 0) (($o -match '^PROFILE CHECK|  FAIL' | Select-Object -First 3) -join '; ')
}

function Run-QueueCheck($name) {
    Start-Entry $name
    $dir = "$Out\$name"; New-Item -ItemType Directory -Force $dir | Out-Null
    Write-Host "== $name : play queues and deck format enforcement (headless)"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $pr = Start-Process $JavaExe -ArgumentList '-cp', "`"$cp`"", 'forge.mtga.fd.QueueCheck', "`"$Client`"" -WorkingDirectory $guiDir -RedirectStandardOutput "$dir\queuecheck.log" -RedirectStandardError "$dir\queuecheck.err" -PassThru -WindowStyle Hidden
    if (-not $pr.WaitForExit(300000)) { Stop-Process -Id $pr.Id -Force; Check 'finishes within 5 min' $false; return }
    $script:cur.Secs = [int]$sw.Elapsed.TotalSeconds
    $o = @(Get-Content "$dir\queuecheck.log")
    Check 'QUEUE CHECK OK' (($o -match '^QUEUE CHECK OK').Count -gt 0) (($o -match '^QUEUE CHECK|  FAIL' | Select-Object -First 3) -join '; ')
}

# ----------------------------------------------------------------------------------------------------------------- run
function Wanted($n) { return ($Only.Count -eq 0 -or $Only -contains $n) }
try {
    if (-not $SkipClient) { foreach ($k in $recipes.Keys) { if (Wanted $k) { Run-Recipe $k $recipes[$k] } } }
    if (-not $SkipSelfPlay) {
        if (Wanted 'sp-default') { Run-SelfPlay 'sp-default' $null }
        foreach ($f in 'Standard', 'Pioneer', 'Modern') { if (Wanted "sp-$f") { Run-SelfPlay "sp-$f" $f } }
        if (Wanted 'sp-pvp') { Run-SelfPlay 'sp-pvp' $null '-Dselfplay.pvp=true' 'SELFPLAY PVP OK' }
        if (Wanted 'sp-peer') { Run-SelfPlay 'sp-peer' $null '-Dselfplay.peer=true' 'SELFPLAY PEER OK' }
        if (Wanted 'sp-online') { Run-SelfPlay 'sp-online' $null '-Dselfplay.online=wss://mm.mtga-forge.workers.dev' 'SELFPLAY ONLINE OK' }   # needs the internet (live coordinator)
        if (Wanted 'sp-profile') { Run-ProfileCheck 'sp-profile' }
        if (Wanted 'sp-queues') { Run-QueueCheck 'sp-queues' }
    }
} finally {
    Stop-Ours
    # decks the menu recipes created stay out of the user's data dir
    if (Test-Path $deckDir) {
        # (the starter decks "Forge: ..." are seeded by the first server start on a fresh data dir: they belong to the user, keep them)
        Get-ChildItem $deckDir -Filter *.json | Where-Object { $decksBefore -notcontains $_.Name -and (Get-Content $_.FullName -Raw | ConvertFrom-Json).Summary.Name -notlike 'Forge: *' } | Remove-Item -Force
    }
}

# ---------------------------------------------------------------------------------------------------------------- table
Write-Host ''
Write-Host "client: $Client   ports: $Port/$FdPort/$ArtPort   evidence: $Out"
$rows = foreach ($r in $script:results) {
    $bad = @($r.Checks | Where-Object { -not $_.Ok })
    [pscustomobject]@{ Recipe = $r.Name; Result = $(if ($bad.Count) { 'FAIL' } else { 'PASS' }); Checks = "$($r.Checks.Count - $bad.Count)/$($r.Checks.Count)"; Secs = $r.Secs
        Failed = (($bad | ForEach-Object { $_.Label + $(if ($_.Detail) { " ($($_.Detail.Trim()))" }) }) -join '; ') }
}
$rows | Format-Table Recipe, Result, Checks, Secs, Failed -AutoSize -Wrap | Out-String -Width 200 | Write-Host
$rows | ConvertTo-Json | Set-Content "$Out\summary.json" -Encoding utf8
$failed = @($rows | Where-Object { $_.Result -eq 'FAIL' }).Count
if (-not $rows) { Write-Host 'nothing ran (check -Only / -Skip*)'; exit 2 }
Write-Host $(if ($failed) { "VERIFY FAILED: $failed of $($rows.Count)" } else { "VERIFY OK: $($rows.Count) of $($rows.Count)" })
exit $(if ($failed) { 1 } else { 0 })
