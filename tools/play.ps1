# Solo play session with full capture. No scripted clicks: you play, the server logs everything.
# Usage: powershell -NoProfile -File tools\play.ps1 [-Minutes 30] [-NoBuild] [-AiDeck "MTGA Chaos and Mayhem"|random|path.dck]
#        powershell -NoProfile -File tools\play.ps1 -Duel [-Deck playtest]     (old mode: straight into a duel, fixed decks)
# Default (menu mode): the client boots into its normal home page through the local Front Door; build decks in the
# deck builder (every card owned x4, decks saved under data\decks) and press Play -> Bot Match to play vs the Forge AI.
# Capture file: tools\capture\session-<timestamp>.log (Front Door commands "FD ...", client/server GRE messages,
# UNHANDLED markers, client exceptions appended at the end)
param([string]$Deck = '', [switch]$Duel, [string]$AiDeck = '', [string]$Rig = '', [int]$Minutes = 30, [switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$root = 'D:\Development\MTGA-Forge'
$m = "$root\client\MTGA_Data\Managed"
$log = "$env:USERPROFILE\AppData\LocalLow\Forge Bridge Project\MTGF\Player.log"
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$cap = "$root\tools\capture\session-$stamp.log"
New-Item -ItemType Directory -Force "$root\tools\capture" | Out-Null
if ($Deck) { $Duel = $true }
if ($Duel -and -not $Deck) { $Deck = 'playtest' }

Get-Process MTGA -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$root\*" } | Stop-Process -Force
Get-CimInstance Win32_Process -Filter "Name='java.exe'" | Where-Object { $_.CommandLine -like '*ForgeMtgaMain*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }

if (-not $NoBuild) {
    Push-Location "$root\forge"
    & "$root\tools\apache-maven-3.9.9\bin\mvn.cmd" -B -q -o -DskipTests -pl forge-gui-mtga -am compile
    Pop-Location
    dotnet build "$root\bridge\ForgeBridge\ForgeBridge.csproj" -c Release -o "$root\bridge\out" | Out-Null
    Copy-Item "$root\bridge\out\ForgeBridge.dll" $m -Force
    dotnet run --project "$root\bridge\Patcher\Patcher.csproj" -c Release -- $m | Select-Object -Last 1
}

$cp = (Get-Content "$root\tools\cp.txt" -Raw).Trim() + ";$root\forge\forge-gui-mtga\target\classes"
$guiDir = "$root\forge\forge-gui"
$serverLog = "$root\tools\capture\server-$stamp.log"
$javaArgs = @("-Dmtga.capture=`"$cap`"")
if ($Duel) { $javaArgs += "-Dmtga.deck=$Deck" }
if ($Rig) { $javaArgs += "-Dmtga.rig=$Rig" }
if ($AiDeck) { $javaArgs += "-Dmtga.aideck=`"$AiDeck`"" }
$javaArgs += '-cp', "`"$cp`"", 'forge.mtga.ForgeMtgaMain', "`"$guiDir`"", "`"$root\client`"", '30003'
$java = Start-Process java -ArgumentList $javaArgs -WorkingDirectory $guiDir -RedirectStandardOutput $serverLog -RedirectStandardError "$serverLog.err" -PassThru -WindowStyle Hidden
Write-Host "Server starting (pid $($java.Id)); waiting for it to listen..."
$ok = $false
for ($i = 0; $i -lt 90; $i++) {
    Start-Sleep -Seconds 1
    if ((Test-Path $serverLog) -and (Select-String -Path $serverLog -Pattern 'Front Door listening' -Quiet)) { $ok = $true; break }
}
if (-not $ok) { Write-Host 'Server did not start; see' $serverLog; Stop-Process -Id $java.Id -Force; exit 1 }

$env:FORGEBRIDGE_MODE = if ($Duel) { 'duel' } else { 'menu' }
Remove-Item Env:FORGEBRIDGE_CLICKS, Env:FORGEBRIDGE_SHOTS -ErrorAction SilentlyContinue
if (Test-Path $log) { Remove-Item $log -Force }
$p = Start-Process "$root\client\MTGA.exe" -WorkingDirectory "$root\client" -ArgumentList '-screen-width 1280 -screen-height 720 -screen-fullscreen 0' -PassThru
Write-Host "Client pid $($p.Id) ($env:FORGEBRIDGE_MODE mode). Play! Capture: $cap  (session ends when you close the game or after $Minutes min)"
$end = (Get-Date).AddMinutes($Minutes)
while ((Get-Date) -lt $end -and -not $p.HasExited) { Start-Sleep -Seconds 2 }
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }

# append client-side exceptions to the capture
if (Test-Path $log) {
    Add-Content $cap "`n== CLIENT EXCEPTIONS (Player.log) =="
    Select-String -Path $log -Pattern 'Exception|NullReference|Error' |
        Where-Object { $_.Line -notmatch 'Curl error 7|BIKE|ConnectFailure|Backtrace|backtrace|Http|promise error' } |
        ForEach-Object { Add-Content $cap $_.Line.Substring(0, [Math]::Min(300, $_.Line.Length)) }
}
Stop-Process -Id $java.Id -Force -ErrorAction SilentlyContinue
Write-Host "Done. Capture saved: $cap"
