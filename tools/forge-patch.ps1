# Builds a Forge-backed MTGA client in a NEW folder from a Steam install (read only), with no manual steps.
# Ordered, idempotent steps; every step records its result in <Target>\forge-patch\state.json together with a fingerprint of
# the source install (Steam buildid, main manifest name, Core.dll hash). A re-run skips finished steps; a different fingerprint
# (Steam updated) throws the results away and starts again at step 1.
#
# Usage: powershell -NoProfile -File tools\forge-patch.ps1 -Target D:\x\client-fresh [-Source <MTGA install>] [-From <step>] [-Only a,b]
#        [-Force] [-Firewall] [-SkipFirewall] [-Formats Standard,Pioneer,Modern] [-BuildServer]
# Steps: refresh  orig  identity  unityconnect  storeconfig  bridge  patch  carddb  manifest  firewall
#   refresh       robocopy /MIR the install into Target (purges everything we added, incl. stale .orig files)
#   orig          Core.dll.orig / AK.Wwise.Unity.API.dll.orig = the pristine DLLs of THIS build (ForgeBridge compiles against them)
#   identity      app.info + globalgamemanagers company/product -> "Forge Bridge Project"/"MTGF" (tools\isolate_identity.py)
#   unityconnect  Unity analytics/cloud services off in globalgamemanagers (tools\disable_unity_connect.py)
#   storeconfig   StreamingAssets\StoreConfig.txt provider -> forge-bridge-local (the Steam provider restarts the app via Steam)
#   bridge        dotnet build bridge\ForgeBridge against Target\MTGA_Data\Managed (ManagedDir parameter)
#   patch         bridge\Patcher: Core.dll + AK.Wwise.Unity.API.dll from the .orig files; throws if a hooked method moved
#   carddb        forge.mtga.CardDbBuilder from the target's own pristine DB (Target\forge-patch\carddb\pristine)
#   manifest      asserts the main manifest is plain JSON (the builder writes it decompressed) and lists the generated DB
#   firewall      outbound block rule for Target\MTGA.exe; needs elevation, runs ONLY with -Firewall (prints the command otherwise)
# Needs: robocopy, python (3.x), dotnet SDK, java 17+, tools\cp.txt, built forge-gui-mtga (-BuildServer compiles it).
param(
    [Parameter(Mandatory)][string]$Target,
    [string]$Source = '',
    [string]$From = '',
    [string[]]$Only = @(),
    [switch]$Force,
    [switch]$Firewall,
    [switch]$SkipFirewall,
    [switch]$BuildServer,
    [string]$Formats = 'Standard,Pioneer,Modern'
)
$ErrorActionPreference = 'Stop'
$Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$root = (Resolve-Path "$PSScriptRoot\..").Path
if (-not $Source) {
    $Source = 'D:\SteamLibrary\steamapps\common\MTGA'
    foreach ($c in 'C:\Program Files (x86)\Steam\steamapps\common\MTGA', 'C:\Program Files\Steam\steamapps\common\MTGA') { if (-not (Test-Path $Source) -and (Test-Path $c)) { $Source = $c } }
}
$Source = (Resolve-Path $Source).Path
New-Item -ItemType Directory -Force $Target | Out-Null
$Target = (Resolve-Path $Target).Path
if ($Target -eq $Source -or $Target.StartsWith($Source + '\') -or $Source.StartsWith($Target + '\')) { throw "target and source overlap: $Target / $Source" }
if ($Target -match 'steamapps') { throw "refusing to patch inside a Steam library: $Target" }

$managed = "$Target\MTGA_Data\Managed"
$ggm = "$Target\MTGA_Data\globalgamemanagers"
$downloads = "$Target\MTGA_Data\Downloads"
$pdir = "$Target\forge-patch"
$statePath = "$pdir\state.json"
$cardData = "$pdir\carddb"
$bridgeOut = "$pdir\bridge-out"
$guiDir = "$root\forge\forge-gui"

function Sha($f) { (Get-FileHash $f -Algorithm SHA256).Hash.ToLower() }

# -------------------------------------------------------------------------------------------------- source fingerprint
function Get-Fingerprint {
    $acf = Get-ChildItem (Join-Path (Split-Path $Source -Parent | Split-Path -Parent) '*.acf') -ErrorAction SilentlyContinue |
        Where-Object { (Get-Content $_.FullName -Raw) -match '"installdir"\s+"MTGA"' } | Select-Object -First 1
    $build = if ($acf) { [regex]::Match((Get-Content $acf.FullName -Raw), '"buildid"\s+"(\d+)"').Groups[1].Value } else { '' }
    $mf = @(Get-ChildItem "$Source\MTGA_Data\Downloads" -Filter 'Manifest_*.mtga' | Where-Object { $_.Name -match '^Manifest_[0-9a-f]{32}\.mtga$' })
    if ($mf.Count -ne 1) { throw "expected one main manifest in $Source\MTGA_Data\Downloads, found $($mf.Count)" }
    [ordered]@{ steamBuildId = $build; mainManifest = $mf[0].Name; coreSha256 = (Sha "$Source\MTGA_Data\Managed\Core.dll")
                exeSize = (Get-Item "$Source\MTGA.exe").Length }
}
$fp = Get-Fingerprint
$fpJson = $fp | ConvertTo-Json -Compress

# state: { fingerprint, steps: { name: { ok, at, detail } } }
$state = [ordered]@{ fingerprint = $fpJson; steps = [ordered]@{} }
if (Test-Path $statePath) {
    $old = Get-Content $statePath -Raw | ConvertFrom-Json
    if ($old.fingerprint -eq $fpJson) { foreach ($p in $old.steps.PSObject.Properties) { $state.steps[$p.Name] = $p.Value } }
    else { Write-Host "source install changed since the last run -> every step again`n  was: $($old.fingerprint)`n  now: $fpJson" }
}
function Save-State { New-Item -ItemType Directory -Force $pdir | Out-Null; $state | ConvertTo-Json -Depth 6 | Set-Content $statePath -Encoding utf8 }

# ------------------------------------------------------------------------------------------------------------ helpers
function Run($exe, [string[]]$a, $what) {
    $ErrorActionPreference = 'Continue'   # Windows PowerShell 5.1 turns native stderr lines into errors
    & $exe @a 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)" }
}
function Invoke-Py { Run 'python.exe' (@('-I') + $args) 'python' }
function Java-Cp { (Get-Content "$root\tools\cp.txt" -Raw).Trim() + ";$root\forge\forge-gui-mtga\target\classes" }

# ------------------------------------------------------------------------------------------------------------- steps
# Each step: a scriptblock returning a detail string. Reached only when state says not done (or -Force / -From / -Only).
$steps = [ordered]@{}

$steps['refresh'] = {
    # /MIR purges whatever the install lacks (our .orig files, generated DB, forge-patch is excluded); /XJ no junctions
    $log = "$pdir\robocopy.log"; New-Item -ItemType Directory -Force $pdir | Out-Null
    & robocopy $Source $Target /MIR /XD "$pdir" /XJ /MT:16 /R:2 /W:2 /NFL /NDL /NP /NJH "/LOG:$log" | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE), see $log" }
    $global:LASTEXITCODE = 0
    $n = (Get-ChildItem $Target -Recurse -File -Force | Where-Object { $_.FullName -notlike "$pdir\*" }).Count
    # a fresh copy invalidates everything built on the previous one
    foreach ($k in @($state.steps.Keys)) { if ($k -ne 'refresh') { $state.steps.Remove($k) } }
    "$n files, $($fp.mainManifest)"
}

$steps['orig'] = {
    foreach ($f in 'Core.dll', 'AK.Wwise.Unity.API.dll') {
        # the install is pristine right after refresh; the .orig must equal the SOURCE's DLL, never an older one
        $src = "$Source\MTGA_Data\Managed\$f"
        Copy-Item $src "$managed\$f" -Force
        Copy-Item $src "$managed\$f.orig" -Force
    }
    "Core.dll.orig $((Get-Item "$managed\Core.dll.orig").Length) B"
}

$steps['identity'] = {
    # Unity takes LocalLow\<line 1>\<line 2> from app.info; the original has no trailing newline
    [IO.File]::WriteAllBytes("$Target\MTGA_Data\app.info", [Text.Encoding]::ASCII.GetBytes("Forge Bridge Project`nMTGF"))
    Invoke-Py "$root\tools\isolate_identity.py" $ggm
}

$steps['unityconnect'] = { Invoke-Py "$root\tools\disable_unity_connect.py" $ggm }

$steps['storeconfig'] = {
    $f = "$Target\MTGA_Data\StreamingAssets\StoreConfig.txt"
    if (-not (Test-Path "$f.orig")) { Copy-Item $f "$f.orig" -Force }
    [IO.File]::WriteAllBytes($f, [Text.Encoding]::ASCII.GetBytes("{ `"provider`": `"forge-bridge-local`", `"id`": `"0`" }`r`n"))
    'provider forge-bridge-local'
}

$steps['bridge'] = {
    # compiled against Target's Core.dll.orig + the other Managed DLLs, so a changed API breaks HERE, not at run time
    Remove-Item $bridgeOut -Recurse -Force -ErrorAction SilentlyContinue
    Run 'dotnet' @('build', "$root\bridge\ForgeBridge\ForgeBridge.csproj", '-c', 'Release', '--no-incremental', '-o', $bridgeOut, "-p:ManagedDir=$managed", '-nologo', '-v', 'q') 'ForgeBridge build'
    Copy-Item "$bridgeOut\ForgeBridge.dll" $managed -Force
    "ForgeBridge.dll $((Get-Item "$managed\ForgeBridge.dll").Length) B"
}

$steps['patch'] = {
    Run 'dotnet' @('run', '--project', "$root\bridge\Patcher\Patcher.csproj", '-c', 'Release', '--', $managed) 'Patcher (a hooked method probably moved in this client version)'
    if ((Sha "$managed\Core.dll") -eq (Sha "$managed\Core.dll.orig")) { throw 'Core.dll is unchanged after patching' }
    "Core.dll $((Get-Item "$managed\Core.dll").Length) B"
}

$steps['carddb'] = {
    if ($BuildServer -or -not (Test-Path "$root\forge\forge-gui-mtga\target\classes\forge\mtga\CardDbBuilder.class")) {
        Push-Location "$root\forge"
        try { Run "$root\tools\apache-maven-3.9.9\bin\mvn.cmd" @('-B', '-q', '-o', '-DskipTests', '-pl', 'forge-gui-mtga', '-am', 'compile') 'forge-gui-mtga build' } finally { Pop-Location }
    }
    # pristine = the main manifest + card DB of the install this target was copied from (the fingerprint names its manifest)
    $pristine = "$cardData\pristine"
    Remove-Item $cardData -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force "$pristine\Raw" | Out-Null
    $mf = "$Source\MTGA_Data\Downloads\$($fp.mainManifest)"
    Copy-Item $mf $pristine
    $m = Read-Manifest $mf
    $db = @($m.Assets | Where-Object { $_.Name -like 'Raw_CardDatabase_*' })
    if ($db.Count -ne 1) { throw "manifest lists $($db.Count) card databases" }
    Copy-Item "$Source\MTGA_Data\Downloads\Raw\$($db[0].Name)" "$pristine\Raw"
    # the target's Downloads must hold nothing but the generated pair afterwards: the builder deletes the other main manifest / DB
    Run 'java' @('-cp', (Java-Cp), 'forge.mtga.CardDbBuilder', $guiDir, $Target, '--carddata', $cardData, '--formats', $Formats) 'CardDbBuilder'
    $st = Get-Content "$cardData\state.json" -Raw | ConvertFrom-Json
    "$($st.db) via $($st.manifest), $($st.cardsAdded) cards added"
}

function Read-Manifest($f) {
    $b = [IO.File]::ReadAllBytes($f)
    if ($b.Length -gt 2 -and $b[0] -eq 0x1f -and $b[1] -eq 0x8b) {
        $ms = New-Object IO.MemoryStream(, $b); $gz = New-Object IO.Compression.GZipStream($ms, [IO.Compression.CompressionMode]::Decompress)
        $out = New-Object IO.MemoryStream; $gz.CopyTo($out); $b = $out.ToArray()
    }
    [Text.Encoding]::UTF8.GetString($b) | ConvertFrom-Json
}

$steps['manifest'] = {
    $st = Get-Content "$cardData\state.json" -Raw | ConvertFrom-Json
    $f = "$downloads\$($st.manifest)"
    $b = [IO.File]::ReadAllBytes($f)
    if ($b[0] -eq 0x1f -and $b[1] -eq 0x8b) { throw "$($st.manifest) is still gzip" }
    $md5 = [BitConverter]::ToString([Security.Cryptography.MD5]::Create().ComputeHash($b)).Replace('-', '').ToLower()
    if ("Manifest_$md5.mtga" -ne $st.manifest) { throw "manifest name is not the MD5 of its bytes" }
    $m = Read-Manifest $f
    $dbEntry = @($m.Assets | Where-Object { $_.Name -like 'Raw_CardDatabase_*' })
    if ($dbEntry.Count -ne 1 -or $dbEntry[0].Name -ne $st.db) { throw 'manifest does not list the generated card DB' }
    if ((Get-Item "$downloads\Raw\$($st.db)").Length -ne $dbEntry[0].Length) { throw 'generated DB length differs from the manifest' }
    $others = @(Get-ChildItem $downloads -Filter 'Manifest_*.mtga' | Where-Object { $_.Name -match '^Manifest_[0-9a-f]{32}\.mtga$' -and $_.Name -ne $st.manifest })
    if ($others.Count) { throw "another main manifest remains: $($others.Name -join ', ')" }
    "plain JSON, lists $($st.db)"
}

$fwName = 'MTGF block'
$fwCmd = "New-NetFirewallRule -DisplayName '$fwName ($Target)' -Direction Outbound -Action Block -Profile Any -Program '$Target\MTGA.exe'"
$steps['firewall'] = {
    $rule = "$fwName ($Target)"
    if (Get-NetFirewallRule -DisplayName $rule -ErrorAction SilentlyContinue) { return 'rule exists' }
    $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    Write-Host "  will run (elevated): $fwCmd"
    if ($admin) { Invoke-Expression $fwCmd | Out-Null }
    else { Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile', '-Command', $fwCmd }
    if (-not (Get-NetFirewallRule -DisplayName $rule -ErrorAction SilentlyContinue)) { throw 'firewall rule was not created (elevation declined?)' }
    'rule created'
}

# ------------------------------------------------------------------------------------------------------------- driver
$names = @($steps.Keys)
$fromIdx = if ($From) { if ($names -notcontains $From) { throw "unknown step $From" }; $names.IndexOf($From) } else { -1 }
Write-Host "source $Source`ntarget $Target`nfingerprint $fpJson"
foreach ($n in $names) {
    $i = $names.IndexOf($n)
    $want = ($Only.Count -eq 0 -or $Only -contains $n) -and ($fromIdx -lt 0 -or $i -ge $fromIdx)
    if (-not $want) { continue }
    if ($n -eq 'firewall' -and -not $Firewall) {
        Write-Host "== firewall : SKIPPED (needs elevation). Re-run with -Firewall to run:`n   $fwCmd"
        continue
    }
    if ($n -eq 'firewall' -and $SkipFirewall) { continue }
    $done = $state.steps.Contains($n) -and $state.steps[$n].ok
    if ($done -and -not $Force -and $fromIdx -lt 0 -and $Only.Count -eq 0) { Write-Host "== $n : done ($($state.steps[$n].detail))"; continue }
    # re-running a step invalidates the steps after it (they were built on its output)
    foreach ($k in $names[($i + 1)..($names.Count - 1)]) { if ($k -ne 'firewall') { $state.steps.Remove($k) } }
    Write-Host "== $n"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try {
        $detail = & $steps[$n]
        $detail = (@($detail) | Select-Object -Last 1) -as [string]
    } catch {
        $state.steps[$n] = [ordered]@{ ok = $false; at = (Get-Date -Format o); detail = "$($_.Exception.Message)" }
        Save-State
        Write-Host "PATCH FAILED in step '$n': $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
    $state.steps[$n] = [ordered]@{ ok = $true; at = (Get-Date -Format o); secs = [int]$sw.Elapsed.TotalSeconds; detail = $detail }
    Save-State
    Write-Host "   ok ($([int]$sw.Elapsed.TotalSeconds) s) $detail"
}
Save-State
Write-Host "PATCH OK: $Target  (verify: powershell -NoProfile -File tools\verify.ps1 -Client `"$Target`" -DataDir <dir>)"
