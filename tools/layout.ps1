# Dot-sourced by forge-patch.ps1, launcher.ps1, verify.ps1: where the pieces live, in the dev tree and in the packaged distribution.
#   dev  (repo checkout): java + dotnet on PATH, server classes in forge\*\target\classes (tools\cp.txt), Forge res in forge\forge-gui,
#                         Patcher and ForgeBridge built from bridge\ on demand
#   dist (dist.json next to tools\): everything below the package root, nothing on PATH is needed (Windows PowerShell 5.1 + robocopy only)
#                         runtime\ (jlink JRE), server\lib\*.jar, server\forge-gui\res, patcher\Patcher.exe, patcher\ForgeBridge.dll
$Root = (Resolve-Path "$PSScriptRoot\..").Path
$Dist = Test-Path "$Root\dist.json"
if ($Dist) {
    $JavaExe = "$Root\runtime\bin\java.exe"
    $ServerCp = "$Root\server\lib\*"
    $GuiDir = "$Root\server\forge-gui"
    $PatcherExe = "$Root\patcher\Patcher.exe"
    $BridgeDll = "$Root\patcher\ForgeBridge.dll"
    $BootLogo = "$Root\patcher\forge-logo.png"
} else {
    $JavaExe = 'java'
    $ServerCp = (Get-Content "$Root\tools\cp.txt" -Raw).Trim() + ";$Root\forge\forge-gui-mtga\target\classes"
    $GuiDir = "$Root\forge\forge-gui"
    $PatcherExe = "$Root\bridge\patcher-out\Patcher.exe"
    $BridgeDll = ''      # built from bridge\ForgeBridge against the target's Managed folder
    $BootLogo = "$Root\tools\bootlogo\forge-logo.png"
}

# MTGA install: Steam (registry + libraryfolders.vdf, MTGA = app 2141910), then the standalone version (its registry key, then
# the Wizards installer's default folders)
function Find-MtgaInstall {
    $cands = @()
    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if (-not $steam) { $steam = (Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam' -ErrorAction SilentlyContinue).InstallPath }
    if ($steam) {
        $libs = @($steam)
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) { $libs += [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"') | ForEach-Object { $_.Groups[1].Value -replace '\\\\', '\' } }
        foreach ($l in $libs) { $cands += Join-Path $l 'steamapps\common\MTGA' }
    }
    # the standalone version (Wizards installer) records its folder, which the player may have changed
    foreach ($k in 'HKLM:\SOFTWARE\Wizards of the Coast\MTGArena', 'HKLM:\SOFTWARE\WOW6432Node\Wizards of the Coast\MTGArena') {
        $p = (Get-ItemProperty $k -ErrorAction SilentlyContinue).Path
        if ($p) { $cands += $p.TrimEnd('\') }
    }
    $cands += 'C:\Program Files (x86)\Steam\steamapps\common\MTGA', 'C:\Program Files\Wizards of the Coast\MTGA', 'C:\Program Files (x86)\Wizards of the Coast\MTGA'
    foreach ($c in $cands) { if (Test-Path "$c\MTGA.exe") { return (Resolve-Path $c).Path } }
    return ''
}

# The server keeps the client's player preferences inside the player's profile (data\profile.json): empty them
# (scripted menu runs start from the same play-blade tab). The profile id, name and cosmetics stay.
function Clear-ProfilePrefs([string]$DataDir) {
    Remove-Item (Join-Path $DataDir 'preferences.json') -Force -ErrorAction SilentlyContinue
    $f = Join-Path $DataDir 'profile.json'
    if (-not (Test-Path $f)) { return }
    $j = Get-Content $f -Raw | ConvertFrom-Json
    $j.preferences = New-Object psobject
    [IO.File]::WriteAllText($f, ($j | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding $false))
}
