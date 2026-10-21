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
} else {
    $JavaExe = 'java'
    $ServerCp = (Get-Content "$Root\tools\cp.txt" -Raw).Trim() + ";$Root\forge\forge-gui-mtga\target\classes"
    $GuiDir = "$Root\forge\forge-gui"
    $PatcherExe = "$Root\bridge\patcher-out\Patcher.exe"
    $BridgeDll = ''      # built from bridge\ForgeBridge against the target's Managed folder
}

# MTGA install: Steam (registry + libraryfolders.vdf, MTGA = app 2141910), then the Wizards launcher's default folders
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
    $cands += 'C:\Program Files (x86)\Steam\steamapps\common\MTGA', 'C:\Program Files\Wizards of the Coast\MTGA', 'C:\Program Files (x86)\Wizards of the Coast\MTGA'
    foreach ($c in $cands) { if (Test-Path "$c\MTGA.exe") { return (Resolve-Path $c).Path } }
    return ''
}
