# Decompiles the MTGA client assemblies that MTGA Forge hooks into, from YOUR OWN install, into docs\decompiled\ (gitignored).
# This is a local reading aid for interoperability work (finding the methods the patcher hooks, the protocol, the UI flow).
# The output is Wizards of the Coast's code: never commit it, paste it into issues or PRs, or share it.
# Usage: powershell -NoProfile -File tools\decompile.ps1 [-ManagedDir <MTGA_Data\Managed>] [-Assemblies Core,SharedClientCore] [-Force]
# Needs: .NET SDK and ilspycmd (dotnet tool install -g ilspycmd).
param(
    [string]$ManagedDir = '',
    [string[]]$Assemblies = @('Core', 'SharedClientCore', 'Wizards.Arena.Models', 'Wizards.Arena.Models.Protobuf', 'Wizards.MDN.GreProtobuf',
        'Wizards.Arena.Enums', 'Wizards.Mtga.Interfaces', 'Wizards.Arena.Promises', 'Wizards.Arena.TcpConnection',
        'Wizards.Arena.MessageSerialization'),
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..").Path
$out = "$root\docs\decompiled"
if (-not (Get-Command ilspycmd -ErrorAction SilentlyContinue)) { throw 'ilspycmd not found: dotnet tool install -g ilspycmd' }
if (-not $ManagedDir) {
    $ManagedDir = @("$root\client-fresh\MTGA_Data\Managed", "$root\client\MTGA_Data\Managed") |
        Where-Object { Test-Path "$_\Core.dll" } | Select-Object -First 1
}
if (-not $ManagedDir -or -not (Test-Path "$ManagedDir\Core.dll")) {
    throw "No MTGA client found. Pass -ManagedDir <your MTGA install>\MTGA_Data\Managed (see CONTRIBUTING.md)."
}
$env:DOTNET_ROLL_FORWARD = 'Major'   # ilspycmd may target an older runtime than the one installed

foreach ($a in $Assemblies) {
    # A patched client keeps the original next to the patched file: decompile the original.
    $dll = @("$ManagedDir\$a.dll.orig", "$ManagedDir\$a.dll") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $dll) { Write-Warning "$a.dll not in $ManagedDir, skipped"; continue }
    $dir = "$out\$a"
    if ((Test-Path $dir) -and -not $Force) { Write-Host "== $a (already there; -Force to redo)"; continue }
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $dir | Out-Null
    Write-Host "== $a"
    # ilspycmd wants a .dll name; copy a .orig under its real name first
    $src = $dll
    if ($dll.EndsWith('.orig')) {
        $tmp = Join-Path ([IO.Path]::GetTempPath()) "mtgaforge-decompile"
        New-Item -ItemType Directory -Force $tmp | Out-Null
        $src = "$tmp\$a.dll"
        Copy-Item $dll $src -Force
    }
    $ErrorActionPreference = 'Continue'   # ilspycmd writes notices ("please update") to stderr
    & ilspycmd -p -o $dir -r $ManagedDir $src 2>&1 | ForEach-Object { "$_" } | Where-Object { $_ -notmatch 'latest version of the tool' } | Out-Host
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($code -ne 0) { throw "ilspycmd failed on $a" }
}
Write-Host "Done: $out (gitignored, do not share)"
