# Auto-update parts of a package. The launcher (bridge\Launcher\Updater.cs) installs a release part by part, downloading only the
# parts whose content changed since the version it has.
#
# Parts ("components"): app (the launcher and top-level files), tools, patcher, server (server\lib, our jars), libs (server\ext,
# third-party jars), res (server\forge-gui), runtime.
# Each one has a content hash: SHA-256 over the sorted lines "<path relative to the part, / separated>`t<sha256 of the file>`n".
# The launcher computes the same hash over what it unpacked before it installs anything (Updater.TreeHash).
#
# Usage:
#   -StateOnly                      write <Package>\components.json (the installed parts' hashes; package.ps1 does this)
#   -Out <dir> -Tag <tag> -BaseUrl <https://github.com/<repo>/releases/download/<tag>> [-Previous <update.json>] [-Notes <text>]
#                                   zip every part that changed since -Previous into <Out>\<part>-<hash12>.zip (parts that did not change
#                                   keep the previous release's zip and link), write <Out>\update.json, sign it into <Out>\update.json.sig
#                                   with -KeyFile (tools\update-key.ps1), and list the files to upload in <Out>\assets.txt.
#   Old releases must stay on GitHub: a later update.json may still link their zips.
param(
    [string]$Package = '',
    [string]$Out = '',
    [string]$Tag = '',
    [string]$BaseUrl = '',
    [string]$Previous = '',
    [string]$Notes = '',
    [string]$KeyFile = "$env:LOCALAPPDATA\MTGA-Forge-release\update-signing-key.xml",
    [switch]$StateOnly
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..").Path
if (-not $Package) { $Package = "$root\dist\MTGA-Forge" }
$Package = (Resolve-Path $Package).Path
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$utf8 = New-Object Text.UTF8Encoding $false

# The parts and where they live in the package. Keep in step with nothing else: the launcher reads paths from update.json.
$parts = @(
    [ordered]@{ name = 'app'; path = ''; files = @('MTGA-Forge.exe', 'MTGA-Forge.exe.config', 'README.txt', 'LICENSE-Forge.txt') },
    [ordered]@{ name = 'tools'; path = 'tools' },
    [ordered]@{ name = 'patcher'; path = 'patcher' },
    [ordered]@{ name = 'server'; path = 'server/lib' },
    [ordered]@{ name = 'libs'; path = 'server/ext' },
    [ordered]@{ name = 'res'; path = 'server/forge-gui' },
    [ordered]@{ name = 'runtime'; path = 'runtime' }
)
# every top-level entry must belong to a part (or be written by the launcher itself), so nothing new is silently left out of updates
$covered = @('dist.json', 'components.json', 'server') + $parts[0].files + ($parts | Where-Object { $_.path -and $_.path -notmatch '/' } | ForEach-Object { $_.path })
foreach ($e in Get-ChildItem $Package -Force) { if ($covered -notcontains $e.Name) { throw "top-level entry '$($e.Name)' belongs to no update part (tools\update-manifest.ps1)" } }
foreach ($e in Get-ChildItem "$Package\server" -Force) { if ('lib', 'ext', 'forge-gui' -notcontains $e.Name) { throw "server\$($e.Name) belongs to no update part" } }

function PartFiles($p) {
    if (-not $p.path) { return @($p.files | ForEach-Object { [pscustomobject]@{ Rel = $_; Full = Join-Path $Package $_ } }) }
    $dir = Join-Path $Package ($p.path -replace '/', '\')
    if (-not (Test-Path $dir)) { throw "part $($p.name): $dir is missing" }
    @(Get-ChildItem $dir -Recurse -File -Force | ForEach-Object { [pscustomobject]@{ Rel = $_.FullName.Substring($dir.Length + 1).Replace('\', '/'); Full = $_.FullName } })
}

function TreeHash($files) {
    $lines = [string[]]@($files | ForEach-Object { "$($_.Rel)`t$((Get-FileHash -LiteralPath $_.Full -Algorithm SHA256).Hash.ToLower())`n" })
    [Array]::Sort($lines, [StringComparer]::Ordinal)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { -join ($sha.ComputeHash($utf8.GetBytes(-join $lines)) | ForEach-Object { $_.ToString('x2') }) } finally { $sha.Dispose() }
}

$dist = Get-Content "$Package\dist.json" -Raw | ConvertFrom-Json
$hashes = [ordered]@{}
$fileLists = @{}
foreach ($p in $parts) {
    $f = PartFiles $p
    foreach ($x in $f) { if (-not (Test-Path -LiteralPath $x.Full)) { throw "part $($p.name): $($x.Rel) is missing" } }
    $fileLists[$p.name] = $f
    $hashes[$p.name] = TreeHash $f
    Write-Host ("  {0,-8} {1}  {2} files, {3} MB" -f $p.name, $hashes[$p.name].Substring(0, 12), $f.Count, [math]::Round((($f | ForEach-Object { (Get-Item -LiteralPath $_.Full).Length }) | Measure-Object -Sum).Sum / 1MB, 1))
}
$state = [ordered]@{ schema = 1; commit = $dist.commit; builtAt = $dist.builtAt; components = $hashes }
[IO.File]::WriteAllText("$Package\components.json", ($state | ConvertTo-Json -Depth 5), $utf8)
if ($StateOnly) { Write-Host "components.json written"; return }

if (-not $Out -or -not $Tag -or -not $BaseUrl) { throw '-Out, -Tag and -BaseUrl are needed (or -StateOnly)' }
if ($BaseUrl -notmatch '^(https://|http://127\.0\.0\.1[:/])') { throw '-BaseUrl must be https (or http://127.0.0.1 for a local test)' }
if (-not (Test-Path $KeyFile)) { throw "No signing key at $KeyFile (tools\update-key.ps1 makes one)" }
New-Item -ItemType Directory -Force $Out | Out-Null
$prev = @{}
if ($Previous) {
    $pj = Get-Content $Previous -Raw | ConvertFrom-Json
    foreach ($c in $pj.components) { $prev[$c.name] = $c }
}
$assets = New-Object System.Collections.Generic.List[string]
$list = @()
foreach ($p in $parts) {
    $h = $hashes[$p.name]
    $c = [ordered]@{ name = $p.name; path = $p.path }
    if (-not $p.path) { $c.files = $p.files }
    $c.hash = $h
    $old = $prev[$p.name]
    if ($old -and $old.hash -eq $h -and $old.url) {
        $c.url = $old.url; $c.sha256 = $old.sha256; $c.size = [long]$old.size
        Write-Host "  $($p.name): unchanged, keeps $($old.url)"
    } else {
        $asset = "$($p.name)-$($h.Substring(0, 12)).zip"
        $zip = Join-Path $Out $asset
        Remove-Item $zip -Force -ErrorAction SilentlyContinue
        $stage = Join-Path $Out "stage-$($p.name)"
        Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
        if ($p.path) { $src = Join-Path $Package ($p.path -replace '/', '\') }
        else {
            New-Item -ItemType Directory -Force $stage | Out-Null
            foreach ($x in $fileLists[$p.name]) { Copy-Item -LiteralPath $x.Full (Join-Path $stage $x.Rel) }
            $src = $stage
        }
        [IO.Compression.ZipFile]::CreateFromDirectory($src, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
        Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
        $c.url = $BaseUrl.TrimEnd('/') + '/' + $asset
        $c.sha256 = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
        $c.size = (Get-Item $zip).Length
        $assets.Add($zip)
        Write-Host ("  {0}: new zip {1} ({2} MB)" -f $p.name, $asset, [math]::Round($c.size / 1MB, 1))
    }
    $list += $c
}
$manifest = [ordered]@{ schema = 1; version = $Tag; commit = $dist.commit; builtAt = $dist.builtAt; notes = $Notes; dist = $dist; components = $list }
$bytes = $utf8.GetBytes(($manifest | ConvertTo-Json -Depth 6))
[IO.File]::WriteAllBytes("$Out\update.json", $bytes)
$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider
try {
    $rsa.FromXmlString((Get-Content $KeyFile -Raw))
    $sig = $rsa.SignData($bytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
} finally { $rsa.Dispose() }
[IO.File]::WriteAllText("$Out\update.json.sig", [Convert]::ToBase64String($sig), $utf8)
$assets.Add("$Out\update.json"); $assets.Add("$Out\update.json.sig")
[IO.File]::WriteAllLines("$Out\assets.txt", $assets, $utf8)
$new = ($list | Where-Object { $assets -contains (Join-Path $Out (Split-Path $_.url -Leaf)) } | ForEach-Object { $_.size } | Measure-Object -Sum).Sum
Write-Host ("update.json signed: {0} parts, {1} new ({2} MB to upload)" -f $list.Count, ($assets.Count - 2), [math]::Round($new / 1MB, 1))
