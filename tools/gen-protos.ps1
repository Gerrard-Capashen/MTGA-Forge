# Generates the MTGA protocol schemas (.proto) from YOUR OWN MTGA install. They are not kept in git (Wizards' definitions).
# Reads the protobuf descriptors embedded in the client's DLLs (tools\descdump) and turns them into .proto text (tools\desc2proto.py).
# Usage: powershell -NoProfile -File tools\gen-protos.ps1 [-ManagedDir <MTGA_Data\Managed>] [-IfMissing] [-Docs]
#   writes  forge\forge-gui-mtga\src\main\proto\*.proto   (the 7 the server build compiles)
#   -Docs   also docs\proto\ (per-message Front Door schemas + .desc files, handy for reading; gitignored)
#   -IfMissing  do nothing when the server's .proto files are already there (the build scripts call it this way)
# Needs: .NET SDK 8+, Python 3 with the protobuf package (pip install protobuf).
param(
    [string]$ManagedDir = '',
    [switch]$IfMissing,
    [switch]$Docs
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..").Path
$protoDir = "$root\forge\forge-gui-mtga\src\main\proto"
$want = 'GREMessages', 'GREMessagesCommon', 'GRETHHostMessages', 'MatchServiceMessages', 'ServiceMessagesCommon',
        'Wizards.Arena.Models.Network', 'Wizards.Arena.Protocol'
if ($IfMissing -and -not $Docs -and -not ($want | Where-Object { -not (Test-Path "$protoDir\$_.proto") })) { return }

if (-not $ManagedDir) {
    $ManagedDir = @("$root\client-fresh\MTGA_Data\Managed", "$root\client\MTGA_Data\Managed") |
        Where-Object { Test-Path "$_\Wizards.MDN.GreProtobuf.dll" } | Select-Object -First 1
}
if (-not $ManagedDir -or -not (Test-Path "$ManagedDir\Wizards.MDN.GreProtobuf.dll")) {
    throw "No MTGA client found. Pass -ManagedDir <your MTGA install>\MTGA_Data\Managed (see CONTRIBUTING.md)."
}
$ManagedDir = (Resolve-Path $ManagedDir).Path
function Run($exe, [string[]]$a, $what) {
    $ErrorActionPreference = 'Continue'
    & $exe @a 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)" }
}
& python -c "import google.protobuf" 2>$null
if ($LASTEXITCODE -ne 0) { throw 'Python package "protobuf" missing: pip install protobuf' }

Write-Host "== protocol schemas from $ManagedDir"
$tmp = "$root\tools\protogen-tmp"
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$tmp\gre", "$tmp\fd" | Out-Null
try {
    $dump = "$tmp\descdump"
    Run dotnet @('build', "$root\tools\descdump\descdump.csproj", '-c', 'Release', '-o', $dump, "-p:ManagedDir=$ManagedDir", '-nologo', '-v', 'q') 'descdump build'
    Run dotnet @("$dump\descdump.dll", "$ManagedDir\Wizards.MDN.GreProtobuf.dll", "$tmp\gre.desc") 'GRE descriptor dump'
    Run dotnet @("$dump\descdump.dll", "$ManagedDir\Wizards.Arena.Models.Protobuf.dll", "$tmp\fd.desc") 'Front Door descriptor dump'
    Run python @("$root\tools\desc2proto.py", "$tmp\gre.desc", "$tmp\gre") 'GRE .proto'
    Run python @("$root\tools\desc2proto.py", "$tmp\fd.desc", "$tmp\fd", 'com.wizards.mtga.fd', 'merge') 'Front Door .proto'

    New-Item -ItemType Directory -Force $protoDir | Out-Null
    Get-ChildItem $protoDir -Filter *.proto | Remove-Item -Force
    Copy-Item "$tmp\gre\*.proto", "$tmp\fd\*.proto" $protoDir
    foreach ($w in $want) { if (-not (Test-Path "$protoDir\$w.proto")) { throw "expected $w.proto was not generated" } }
    Write-Host "   $((Get-ChildItem $protoDir -Filter *.proto).Count) files -> forge\forge-gui-mtga\src\main\proto"

    if ($Docs) {
        $d = "$root\docs\proto"
        New-Item -ItemType Directory -Force "$d\fd" | Out-Null
        Copy-Item "$tmp\gre\*.proto", "$tmp\gre.desc", "$tmp\fd.desc" $d -Force
        Run python @("$root\tools\desc2proto.py", "$tmp\fd.desc", "$d\fd", 'com.wizards.mtga.fd') 'Front Door .proto (per message)'
        Write-Host '   reading copies -> docs\proto'
    }
} finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
