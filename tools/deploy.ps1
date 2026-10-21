# Build ForgeBridge, patch the client copy, optionally launch (with the mock Match Door) and report the logs.
# Usage: powershell -File tools\deploy.ps1 [-Run] [-Mock] [-Forge] [-Duel] [-Seconds 40] [-Shot N] [-Clicks "t:needle,..."]
# Client mode: menus through the local Front Door by default; straight into a duel with -Duel (implied by MTGA_RIG / MTGA_DECK,
# i.e. the scripted regression recipes).
# -FreshPrefs deletes data\preferences.json (server-side player prefs such as the last play-blade tab) so scripted
# menu clicks always start from the same UI state.
param([switch]$Run, [switch]$Mock, [switch]$Forge, [switch]$Duel, [switch]$FreshPrefs, [int]$Seconds = 40, [int]$Shot = 0, [string]$Shots = "", [string]$Clicks = "")
$ErrorActionPreference = 'Stop'
$root = 'D:\Development\MTGA-Forge'
$m = "$root\client\MTGA_Data\Managed"
$log = "$env:USERPROFILE\AppData\LocalLow\Forge Bridge Project\MTGF\Player.log"
$mockLog = "$root\tools\mock\mock.log"

Get-Process MTGA -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$root\*" } | Stop-Process -Force

dotnet build "$root\bridge\ForgeBridge\ForgeBridge.csproj" -c Release -o "$root\bridge\out" 2>&1 |
    Select-String 'error CS|Build succeeded' | ForEach-Object { ($_.Line -replace '\[D:.*', '') }
if ($LASTEXITCODE -ne 0) { throw 'ForgeBridge build failed' }
Copy-Item "$root\bridge\out\ForgeBridge.dll" $m -Force
dotnet run --project "$root\bridge\Patcher\Patcher.csproj" -c Release -- $m 2>&1 | Select-Object -Last 3

if ($Run) {
    if (Test-Path $log) { Remove-Item $log -Force }
    $mockProc = $null
    if ($Mock) {
        Get-CimInstance Win32_Process -Filter "Name='python.exe'" |
            Where-Object { $_.CommandLine -like '*mock_matchdoor*' } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
        Remove-Item $mockLog -Force -ErrorAction SilentlyContinue
        $mockProc = Start-Process python -ArgumentList '-I', "$root\tools\mock\mock_matchdoor.py" -PassThru -WindowStyle Hidden
        Start-Sleep -Seconds 2
    }
    $javaProc = $null
    $javaLog = "$root\tools\forge-server.log"
    if ($Forge) {
        Get-CimInstance Win32_Process -Filter "Name='java.exe'" |
            Where-Object { $_.CommandLine -like '*ForgeMtgaMain*' } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
        $cp = (Get-Content "$root\tools\cp.txt" -Raw).Trim() + ";$root\forge\forge-gui-mtga\target\classes"
        $guiDir = "$root\forge\forge-gui"
        $rigArg = if ($env:MTGA_RIG) { "-Dmtga.rig=$env:MTGA_RIG" } else { '-Dmtga.norig=1' }
        $deckArg = if ($env:MTGA_DECK) { "-Dmtga.deck=$env:MTGA_DECK" } else { '-Dmtga.nodeck=1' }
        $capArg = "-Dmtga.capture=`"$root\tools\capture\deploy.log`""
        if ($FreshPrefs) { Remove-Item "$root\data\preferences.json" -Force -ErrorAction SilentlyContinue }
        Remove-Item $javaLog, "$root\tools\capture\deploy.log" -Force -ErrorAction SilentlyContinue
        $javaProc = Start-Process java -ArgumentList $rigArg, $deckArg, $capArg, '-cp', "`"$cp`"", 'forge.mtga.ForgeMtgaMain', "`"$guiDir`"", "`"$root\client`"", '30003' `
            -WorkingDirectory $guiDir -RedirectStandardOutput $javaLog -RedirectStandardError "$javaLog.err" -PassThru -WindowStyle Hidden
        for ($i = 0; $i -lt 90; $i++) {
            Start-Sleep -Seconds 1
            if ((Test-Path $javaLog) -and (Select-String -Path $javaLog -Pattern 'Front Door listening|fdport=0' -Quiet)) { break }
        }
    }
    $env:FORGEBRIDGE_MODE = if ($Duel -or $env:MTGA_RIG -or $env:MTGA_DECK) { 'duel' } else { 'menu' }
    "client mode: $env:FORGEBRIDGE_MODE"
    if ($Clicks) { $env:FORGEBRIDGE_CLICKS = $Clicks }
    if ($Shot -gt 0 -or $Shots) {
        $env:FORGEBRIDGE_SHOTS = if ($Shots) { $Shots } else { "$Shot,$($Shot + 15)" }
        $env:FORGEBRIDGE_SHOT_DIR = "$root\tools\shots"
        Remove-Item "$root\tools\shots\*" -Force -ErrorAction SilentlyContinue
    }
    $p = Start-Process "$root\client\MTGA.exe" -WorkingDirectory "$root\client" -ArgumentList '-screen-width 1280 -screen-height 720 -screen-fullscreen 0' -PassThru
    "launched pid $($p.Id)"
    $end = (Get-Date).AddSeconds($Seconds)
    $shotAt = $null   # desktop screenshots are useless on a locked screen; in-game shots via FORGEBRIDGE_SHOTS
    while ((Get-Date) -lt $end -and -not $p.HasExited) {
        Start-Sleep -Seconds 2
        if ($shotAt -and (Get-Date) -ge $shotAt) {
            Add-Type -AssemblyName System.Windows.Forms, System.Drawing
            $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
            $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
            $bmp.Save("$root\tools\shot.png", [System.Drawing.Imaging.ImageFormat]::Png)
            $g.Dispose(); $bmp.Dispose(); $shotAt = $null
            "screenshot saved: $root\tools\shot.png ($($b.Width)x$($b.Height))"
        }
    }
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force; 'killed after timeout' } else { "exited code $($p.ExitCode)" }
    Start-Sleep -Seconds 1
    if (Test-Path $log) {
        Select-String -Path $log -Pattern 'ForgeBridge|Exception|NullReference|Error|error CS' |
            Where-Object { $_.Line -notmatch 'Curl error 7|BIKE|ConnectFailure|Backtrace|backtrace' } |
            Select-Object -First 70 | ForEach-Object { $_.Line.Substring(0, [Math]::Min(300, $_.Line.Length)) }
    } else { 'no Player.log' }
    if ($javaProc) {
        '--- forge server log (tail) ---'
        Get-Content $javaLog -ErrorAction SilentlyContinue | Where-Object { $_ -match '\[mtga\]|Exception|at forge' } | Select-Object -First 60
        Get-Content "$javaLog.err" -ErrorAction SilentlyContinue | Select-Object -First 20
        Stop-Process -Id $javaProc.Id -Force
    }
    if ($mockProc) {
        '--- mock match door log ---'
        if (Test-Path $mockLog) { Get-Content $mockLog | Select-Object -First 80 }
        Stop-Process -Id $mockProc.Id -Force
    }
}
