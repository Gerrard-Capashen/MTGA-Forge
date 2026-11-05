# Checks a patch distribution folder against the NOT-ALLOWED list (docs\DISTRIBUTION.md): nothing from Wizards, no card art.
# Usage: powershell -NoProfile -File tools\check-package.ps1 -Package dist\MTGA-Forge [-Install <MTGA install>] [-Zip <package.zip>]
# Exit code 0 = clean, 1 = violations. The install is only read (auto-detected when omitted; the hash comparison is skipped if none is found).
param(
    [Parameter(Mandatory)][string]$Package,
    [string]$Install = '',
    [string]$Zip = ''
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\layout.ps1"
$Package = (Resolve-Path $Package).Path
$bad = New-Object Collections.Generic.List[string]
function Fail($m) { $script:bad.Add($m); Write-Host "  VIOLATION: $m" -ForegroundColor Red }
function Warn($m) { Write-Host "  NOTE: $m" -ForegroundColor Yellow }
function Pass($m) { Write-Host "  ok: $m" }

# 1. Only the known top-level layout
$topAllowed = 'MTGA-Forge.exe', 'MTGA-Forge.exe.config', 'README.txt', 'dist.json', 'LICENSE-Forge.txt', 'components.json', 'tools', 'patcher', 'runtime', 'server'
foreach ($e in Get-ChildItem $Package -Force) { if ($topAllowed -notcontains $e.Name) { Fail "unexpected top-level entry: $($e.Name)" } }
foreach ($d in 'tools', 'patcher', 'runtime', 'server') { if (-not (Test-Path "$Package\$d")) { Fail "missing $d" } }
$files = @(Get-ChildItem $Package -Recurse -File -Force)
Pass "$($files.Count) files, $([int](($files | Measure-Object Length -Sum).Sum / 1MB)) MB"

# 2. Names and extensions that only exist in the game (or are produced from it by the patcher)
$gameNames = 'MTGA.exe', 'UnityPlayer.dll', 'UnityCrashHandler64.exe', 'Core.dll', 'SharedClientCore.dll', 'Assembly-CSharp.dll', 'globalgamemanagers',
    'app.info', 'StoreConfig.txt', 'boot.config', 'ScriptingAssemblies.json', 'AK.Wwise.Unity.API.dll', 'GfxPluginAK.dll'
$gameExt = '.assets', '.ress', '.bundle', '.unity3d', '.mtga', '.orig', '.bank', '.wem', '.wwu', '.sharedassets'
$gameDirs = 'MTGA_Data', 'Downloads', 'Managed', 'StreamingAssets', 'decompiled', 'Resources'
foreach ($f in $files) {
    $rel = $f.FullName.Substring($Package.Length + 1)
    if ($gameNames -contains $f.Name) { Fail "game file name: $rel" }
    if ($gameExt -contains $f.Extension.ToLower()) { Fail "game file type $($f.Extension): $rel" }
    if ($f.Name -match '^(level\d+|sharedassets\d+|resources\.assets|globalgamemanagers)') { Fail "Unity data file: $rel" }
}
foreach ($d in Get-ChildItem $Package -Recurse -Directory -Force) { if ($gameDirs -contains $d.Name) { Fail "game folder name: $($d.FullName.Substring($Package.Length + 1))" } }

# 3. No images, audio or video at all (the one exception, patcher\forge-logo.png, is the boot screen logo we put into the client): card art, symbols, UI art and sounds are all someone's artwork; the patcher builds everything else locally
$media = '.jpg', '.jpeg', '.png', '.gif', '.bmp', '.webp', '.tga', '.dds', '.psd', '.ico', '.svg', '.tif', '.tiff', '.ogg', '.mp3', '.wav', '.flac', '.mp4', '.webm', '.avi', '.mov'
foreach ($f in $files) { if ($media -contains $f.Extension.ToLower() -and $f.FullName -ne "$Package\patcher\forge-logo.png") { Fail "image/audio/video file: $($f.FullName.Substring($Package.Length + 1))" } }
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($j in Get-ChildItem "$Package\server\lib", "$Package\server\ext" -Filter *.jar -ErrorAction SilentlyContinue) {
    $za = [IO.Compression.ZipFile]::OpenRead($j.FullName)
    try {
        # a library's own icon (Jetty's favicon) is the only allowed media entry
        $m = @($za.Entries | Where-Object { $media -contains [IO.Path]::GetExtension($_.Name).ToLower() -and $_.FullName -ne 'org/eclipse/jetty/favicon.ico' })
        foreach ($e in $m | Select-Object -First 3) { Fail "media inside $($j.Name): $($e.FullName)" }
        if ($m.Count -gt 3) { Fail "... and $($m.Count - 3) more media files in $($j.Name)" }
        $w = @($za.Entries | Where-Object { $_.FullName -match '(?i)wizards|mtga_data' -and $_.FullName -notmatch '^forge/mtga/' })
        if ($w.Count -and $j.Name -eq 'forge-gui-mtga.jar') {
            # the wire protocol (.proto descriptors and their generated classes) our server must speak to the client: interoperability data, no game assets
            Warn "$($j.Name): $($w.Count) protocol definition entries named after Wizards ($(($w | Where-Object { $_.Name -like '*.proto' }).Count) .proto files): needs a conscious yes before publishing"
        } elseif ($w.Count) { Fail "jar entry naming Wizards outside forge/mtga: $($j.Name): $($w[0].FullName)" }
    } finally { $za.Dispose() }
}
if (-not $bad.Count) { Pass 'no image/audio/video files (also inside the jars), no game names, types or folders' }

# 4. Dlls: only the Patcher and our own ForgeBridge, no Wizards code inside it
$dllOk = 'Mono.Cecil.dll', 'Mono.Cecil.Pdb.dll', 'ForgeBridge.dll'
foreach ($f in Get-ChildItem "$Package\patcher" -File) {
    if ($f.Extension -in '.dll', '.exe' -and $f.Name -ne 'Patcher.exe' -and $dllOk -notcontains $f.Name) { Fail "unexpected binary in patcher\: $($f.Name)" }
}
$pe = "$Package\patcher\Patcher.exe"
if (Test-Path $pe) {
    $o = & $pe audit-bridge "$Package\patcher\ForgeBridge.dll" 2>&1
    if ($LASTEXITCODE -ne 0) { Fail "ForgeBridge.dll is not purely our code: $($o -join '; ')" } else { Pass "ForgeBridge.dll: $($o | Select-Object -Last 1)" }
}
foreach ($f in Get-ChildItem "$Package\runtime", "$Package\server" -Recurse -File -Include *.exe) {
    if ($f.FullName -notlike "$Package\runtime\bin\*") { Fail "unexpected exe: $($f.FullName.Substring($Package.Length + 1))" }
}

# 5. No byte-identical copy of anything in the user's install (whatever the name)
if (-not $Install) { $Install = Find-MtgaInstall }
if ($Install -and (Test-Path $Install)) {
    $bySize = @{}
    foreach ($g in Get-ChildItem $Install -Recurse -File -Force -ErrorAction SilentlyContinue) {
        if ($g.Length -lt 64) { continue }
        if (-not $bySize.ContainsKey($g.Length)) { $bySize[$g.Length] = New-Object Collections.Generic.List[string] }
        $bySize[$g.Length].Add($g.FullName)
    }
    $hit = 0; $hashed = 0
    foreach ($f in $files) {
        if (-not $bySize.ContainsKey($f.Length)) { continue }
        $h = (Get-FileHash $f.FullName -Algorithm SHA256).Hash; $hashed++
        foreach ($g in $bySize[$f.Length]) { if ((Get-FileHash $g -Algorithm SHA256).Hash -eq $h) { Fail "identical to an install file: $($f.FullName.Substring($Package.Length + 1)) = $g"; $hit++; break } }
    }
    Pass "compared with $Install ($($bySize.Count) distinct sizes; $hashed size matches hashed, $hit identical)"
} else { Write-Host '  skipped: no MTGA install found for the byte comparison (pass -Install)' }

# 6. Nothing machine- or user-specific (paths, e-mail), no tokens
# the folder this checkout lives in (where a build could leak its own paths from), any user profile path, any e-mail address
$here = [regex]::Escape((Resolve-Path "$PSScriptRoot\..").Path)
$personal = "C:\\Users\\[A-Za-z]|$here|[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}|steamapps\\common\\MTGA\\MTGA_Data"
# placeholder addresses on reserved domains (RFC 2606: the bridge's local@forgebridge.invalid) are not anyone's
function Personal($s) { ([regex]::Matches($s, $personal) | Where-Object { $_.Value -notmatch '\.(invalid|test|example|localhost)$|@example\.(com|org|net)$' } | Select-Object -First 1).Value }
$textExt = '.ps1', '.cmd', '.txt', '.json', '.properties', '.xml', '.config'
foreach ($f in $files | Where-Object { $textExt -contains $_.Extension.ToLower() -and $_.Length -lt 2MB -and $_.FullName -notlike "$Package\server\forge-gui\res\*" }) {
    $s = Get-Content $f.FullName -Raw -ErrorAction SilentlyContinue
    $hit = if ($s) { Personal $s }
    if ($hit) { Fail "machine/user specific text in $($f.FullName.Substring($Package.Length + 1)): $hit" }
}

# 6b. Same for our own binaries: no debug-symbol path or user folder compiled in (ASCII and UTF-16)
foreach ($f in $files | Where-Object { $_.Extension -in '.exe', '.dll' -and $_.Length -lt 4MB -and $_.Name -notlike 'Mono.Cecil*' -and $_.FullName -notlike "$Package\runtime\*" }) {   # runtime\: Microsoft's and OpenJDK's own binaries
    $b = [IO.File]::ReadAllBytes($f.FullName)
    foreach ($s in [Text.Encoding]::ASCII.GetString($b), [Text.Encoding]::Unicode.GetString($b), [Text.Encoding]::Unicode.GetString($b, 1, $b.Length - 2)) {
        $hit = if ($s -match '[A-Za-z]:\\[^\x00]{0,200}\.pdb') { $Matches[0] } else { Personal $s }
        if ($hit) { Fail "machine/user specific text compiled into $($f.FullName.Substring($Package.Length + 1)): $hit"; break }
    }
}

# 7. A zip of the package holds the same files
if ($Zip) {
    $z = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $Zip).Path)
    try { $n = @($z.Entries | Where-Object { $_.FullName -notmatch '[/\\]$' }).Count; if ($n -ne $files.Count) { Fail "zip has $n files, folder has $($files.Count)" } } finally { $z.Dispose() }
}

if ($bad.Count) { Write-Host "CHECK FAILED: $($bad.Count) violation(s)" -ForegroundColor Red; exit 1 }
Write-Host 'CHECK OK: package contains none of the not-allowed items'
