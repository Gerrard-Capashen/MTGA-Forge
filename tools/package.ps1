# Builds the patch distribution (no Wizards files, no card art) from the dev tree.
# Usage: powershell -NoProfile -File tools\package.ps1 [-Out dist\MTGA-Forge] [-ManagedDir <client Managed folder>] [-BuildServer] [-Zip]
#   dist\MTGA-Forge\
#     MTGA-Forge.exe(+.config)                        the launcher window (bridge\Launcher): first-start guidance, friend code / offline
#     MTGA-Forge.cmd, README.txt, dist.json           command-line start (tools\launcher.ps1) / what it is
#     tools\                                          launcher.ps1 forge-patch.ps1 layout.ps1 verify.ps1 check-package.ps1
#     patcher\                                        Patcher.exe (.NET Framework 4.8, ships with Windows) + Mono.Cecil, ForgeBridge.dll (our client code)
#     runtime\                                        jlink JRE (java.exe), only the modules the server needs
#     server\lib\                                     forge-*.jar, forge-gui-mtga.jar and their dependencies
#     server\forge-gui\res\                           Forge rules data (card scripts, editions, formats, AI profiles, en-US texts); no images, music, adventure mode
# Then runs check-package.ps1 against the result (fails the build when the not-allowed list is violated).
# -ManagedDir: a patched or unpatched client's MTGA_Data\Managed holding Core.dll.orig, only used to compile ForgeBridge.dll.
param(
    [string]$Out = '',
    [string]$ManagedDir = '',
    [switch]$BuildServer,
    [switch]$Zip
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..").Path
if (-not $Out) { $Out = "$root\dist\MTGA-Forge" }
if (-not $ManagedDir) { $ManagedDir = "$root\client-fresh\MTGA_Data\Managed" }
if (-not (Test-Path "$ManagedDir\Core.dll.orig")) { throw "$ManagedDir\Core.dll.orig missing (compile reference for ForgeBridge)" }
function Run($exe, [string[]]$a, $what) {
    $ErrorActionPreference = 'Continue'
    & $exe @a 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)" }
}

if ($BuildServer) {
    Push-Location "$root\forge"
    try { Run "$root\tools\apache-maven-3.9.9\bin\mvn.cmd" @('-B', '-q', '-o', '-DskipTests', '-pl', 'forge-gui-mtga', '-am', 'compile') 'server build' } finally { Pop-Location }
}

Remove-Item $Out -Recurse -Force -ErrorAction SilentlyContinue
foreach ($d in 'tools', 'patcher', 'server\lib', 'server\forge-gui') { New-Item -ItemType Directory -Force "$Out\$d" | Out-Null }

# ---- patcher + client code
Write-Host '== patcher'
$tmp = "$root\dist\build"
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
Run 'dotnet' @('build', "$root\bridge\Patcher\Patcher.csproj", '-c', 'Release', '-o', "$tmp\patcher", '-nologo', '-v', 'q') 'Patcher build'
Run 'dotnet' @('build', "$root\bridge\Launcher\Launcher.csproj", '-c', 'Release', '-o', "$tmp\launcher", '-nologo', '-v', 'q') 'Launcher build'
Run 'dotnet' @('build', "$root\bridge\ForgeBridge\ForgeBridge.csproj", '-c', 'Release', '--no-incremental', '-o', "$tmp\bridge", "-p:ManagedDir=$ManagedDir", '-nologo', '-v', 'q') 'ForgeBridge build'
Copy-Item "$tmp\patcher\*" "$Out\patcher" -Exclude '*.pdb', 'Mono.Cecil.Mdb.dll', 'Mono.Cecil.Rocks.dll'
Copy-Item "$tmp\bridge\ForgeBridge.dll" "$Out\patcher"
Copy-Item "$tmp\launcher\MTGA-Forge.exe", "$tmp\launcher\MTGA-Forge.exe.config" $Out

# ---- server jars: our class folders become jars, dependencies are copied as they are
Write-Host '== server jars'
$jdk = Split-Path (Split-Path (Get-Command java).Source -Parent) -Parent
foreach ($tool in 'jar.exe', 'jdeps.exe', 'jlink.exe') { if (-not (Test-Path "$jdk\bin\$tool")) { throw "$tool not found in $jdk (a JDK 17+ is needed to package)" } }
$entries = (Get-Content "$root\tools\cp.txt" -Raw).Trim() -split ';' | Where-Object { $_ }
foreach ($e in @($entries) + "$root\forge\forge-gui-mtga\target\classes") {
    if (Test-Path $e -PathType Container) {
        $name = Split-Path (Split-Path (Split-Path $e -Parent) -Parent) -Leaf     # <module>\target\classes
        Run "$jdk\bin\jar.exe" @('--create', '--file', "$Out\server\lib\$name.jar", '-C', $e, '.') "jar $name"
    } else { Copy-Item $e "$Out\server\lib" }
}
$jars = @(Get-ChildItem "$Out\server\lib\*.jar")
Write-Host "   $($jars.Count) jars, $([int](($jars | Measure-Object Length -Sum).Sum / 1MB)) MB"

# ---- Forge rules data
Write-Host '== forge res'
$res = "$root\forge\forge-gui\res"
$skip = 'adventure', 'music', 'sound', 'skins', 'fonts', 'tutorial', 'quest\world', 'conquest', 'defaults', 'effects' | ForEach-Object { "$res\$_" }
& robocopy $res "$Out\server\forge-gui\res" /E /XD @skip "$res\languages" /NFL /NDL /NP /NJH /NJS | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy res failed ($LASTEXITCODE)" }
$global:LASTEXITCODE = 0
New-Item -ItemType Directory -Force "$Out\server\forge-gui\res\languages" | Out-Null
Copy-Item "$res\languages\en-US.properties" "$Out\server\forge-gui\res\languages"

# ---- runtime: the modules the server needs, nothing else
Write-Host '== runtime (jlink)'
$own = @(Get-ChildItem "$Out\server\lib\forge-*.jar" | ForEach-Object FullName)     # analysed with -R: the libraries are followed through the class path
$ErrorActionPreference = 'Continue'
$mods = (& "$jdk\bin\jdeps.exe" --multi-release 17 --ignore-missing-deps --print-module-deps -R -cp "$Out\server\lib\*" $own 2>$null) -join ''
$ErrorActionPreference = 'Stop'
if ($LASTEXITCODE -ne 0 -or -not $mods) { throw 'jdeps found no modules' }
$extra = 'java.desktop', 'java.logging', 'java.sql', 'java.naming', 'java.management', 'jdk.unsupported', 'jdk.crypto.ec', 'jdk.zipfs', 'jdk.localedata'
$all = (($mods.Trim() -split ',') + $extra | Sort-Object -Unique) -join ','
Write-Host "   $all"
Run "$jdk\bin\jlink.exe" @('--add-modules', $all, '--strip-debug', '--no-header-files', '--no-man-pages', '--compress', 'zip-6', '--output', "$Out\runtime") 'jlink'
Write-Host "   runtime $([int]((Get-ChildItem "$Out\runtime" -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)) MB"

# ---- scripts + docs
foreach ($f in 'launcher.ps1', 'forge-patch.ps1', 'layout.ps1', 'verify.ps1', 'check-package.ps1') { Copy-Item "$root\tools\$f" "$Out\tools" }
New-Item -ItemType Directory -Force "$Out\tools\decks" | Out-Null
Copy-Item "$root\tools\decks\modern-import-test.txt" "$Out\tools\decks"     # verify.ps1 fixture (fd-import)
Copy-Item "$root\tools\dist\MTGA-Forge.cmd" $Out
Copy-Item "$root\tools\dist\README.txt" $Out
Copy-Item "$root\forge\LICENSE" "$Out\LICENSE-Forge.txt"
$rev = (git -C $root rev-parse --short HEAD).Trim()
[ordered]@{ name = 'MTGA-Forge'; commit = $rev; builtAt = (Get-Date -Format o); compiledAgainst = (Get-Item "$ManagedDir\Core.dll.orig").Length } |
    ConvertTo-Json | Set-Content "$Out\dist.json" -Encoding utf8
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue

Write-Host '== check-package'
& powershell -NoProfile -File "$root\tools\check-package.ps1" -Package $Out
if ($LASTEXITCODE -ne 0) { throw 'check-package failed' }
$size = [int]((Get-ChildItem $Out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
Write-Host "PACKAGE OK: $Out ($size MB)"
if ($Zip) {
    $z = "$Out.zip"; Remove-Item $z -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path $Out -DestinationPath $z -CompressionLevel Optimal
    Write-Host "zip: $z ($([int]((Get-Item $z).Length / 1MB)) MB)"
}
