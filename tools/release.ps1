# One command from built package to published release. Every step that leaves this machine is a switch; without switches it only
# hashes the zip and tells you what it would do.
#   -Build              run package.ps1 -BuildServer -Zip first (about 8 minutes)
#   -Scan               upload MTGA-Forge.exe and the zip to VirusTotal (vt CLI, needs `vt init` once), wait, stop when anything flags malicious.
#                       NOTE: VirusTotal shares uploaded files with security vendors; the package has no personal data but it is public after this.
#   -Upload github|r2   host the zip. github: gh release create on -Repo owner/name (default Gerrard-Capashen/MTGA-Forge) with the token in -TokenFile; -SourceZip adds a second file (GPL source). r2: wrangler r2 object put into -Bucket, link = -PublicBase/<file>
#   -Url <link>         the zip is already hosted somewhere else (any https address): skip -Upload, just write latest.json (and deploy)
#   -Deploy             write site\public\latest.json (publish-latest.ps1) and run wrangler deploy for the site
# Example: powershell -NoProfile -File tools\release.ps1 -Scan -Upload github -Repo owner/mtga-forge -Notes 'First release' -Deploy
param(
    [string]$Zip = '',
    [switch]$Build,
    [switch]$Scan,
    [switch]$AllowDetections,
    [ValidateSet('none', 'github', 'r2')][string]$Upload = 'none',
    [string]$Repo = 'Gerrard-Capashen/MTGA-Forge',
    [string]$TokenFile = "$env:LOCALAPPDATA\MTGA-Forge-release\token.txt",
    [string]$SourceZip = '',
    [string]$Bucket = 'mtga-forge',
    [string]$PublicBase = '',
    [string]$Url = '',
    [string]$Notes = '',
    [switch]$Deploy
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..").Path
if (-not $Zip) { $Zip = "$root\dist\MTGA-Forge.zip" }
function Step($t) { Write-Host "== $t" }
function Native($exe, [string[]]$a) { $o = & $exe @a 2>&1; if ($LASTEXITCODE -ne 0) { throw "$exe $($a -join ' ') failed ($LASTEXITCODE): $o" }; $o }

if ($Build) {
    Step 'package'
    & powershell -NoProfile -File "$root\tools\package.ps1" -BuildServer -Zip
    if ($LASTEXITCODE -ne 0) { throw 'package failed' }
}
$Zip = (Resolve-Path $Zip).Path
$pkg = Join-Path (Split-Path $Zip -Parent) ([IO.Path]::GetFileNameWithoutExtension($Zip))
$dist = Get-Content "$pkg\dist.json" -Raw | ConvertFrom-Json
$sha = (Get-FileHash $Zip -Algorithm SHA256).Hash.ToLower()
$exe = Join-Path $pkg 'MTGA-Forge.exe'
$exeSha = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
Step "package $($dist.commit), built $($dist.builtAt)"
Write-Host "  zip  $Zip ($([int]((Get-Item $Zip).Length / 1MB)) MB)`n  sha256 $sha`n  exe  sha256 $exeSha"
if ((git -C $root status --porcelain 2>$null | Measure-Object).Count -gt 0) { Write-Host '  WARNING: the working tree has uncommitted changes; the package may not match its commit.' }

if ($Scan) {
    Step 'VirusTotal'
    if (-not (Get-Command vt -ErrorAction SilentlyContinue)) { throw 'vt CLI not found (https://github.com/VirusTotal/vt-cli/releases), then run: vt init' }
    $bad = 0
    foreach ($f in $exe, $Zip) {
        $line = (Native 'vt' @('scan', 'file', $f) | Select-Object -Last 1).ToString().Trim()
        $id = ($line -split '\s+')[-1]
        Write-Host "  uploaded $(Split-Path $f -Leaf), analysis $id"
        $stats = $null
        for ($i = 0; $i -lt 60 -and -not $stats; $i++) {
            Start-Sleep -Seconds 15
            $j = (Native 'vt' @('analysis', $id, '--format', 'json')) -join "`n" | ConvertFrom-Json
            $a = @($j)[0]; if ($a.attributes) { $a = $a.attributes }
            if ($a.status -eq 'completed') { $stats = $a.stats }
        }
        if (-not $stats) { throw "analysis of $f did not finish in 15 minutes: https://www.virustotal.com/gui/file-analysis/$id" }
        $h = if ($f -eq $Zip) { $sha } else { $exeSha }
        Write-Host ("  {0}: malicious {1}, suspicious {2}, harmless {3}, undetected {4}  https://www.virustotal.com/gui/file/{5}" -f (Split-Path $f -Leaf), $stats.malicious, $stats.suspicious, $stats.harmless, $stats.undetected, $h)
        $bad += [int]$stats.malicious
    }
    if ($bad -gt 0 -and -not $AllowDetections) { throw "VirusTotal flags $bad detection(s). Look at the links above; rerun with -AllowDetections once you judged them false positives." }
}

$url = $Url
if ($Url -and $Url -notmatch '^https://') { throw '-Url must be an https address' }
if ($Upload -eq 'github') {
    if (-not $Repo) { throw '-Repo owner/name is needed for -Upload github' }
    Step "GitHub release on $Repo"
    # Only this repo's token is used (never the gh login of this machine), and only if it belongs to the repo's owner.
    if (-not (Test-Path $TokenFile)) { throw "No token file at $TokenFile (one line: a fine-grained token of the account that owns $Repo, Contents: read and write)" }
    $oldToken = $env:GH_TOKEN
    $env:GH_TOKEN = (Get-Content $TokenFile -Raw).Trim()
    try {
        $login = ((Native 'gh' @('api', 'user', '--jq', '.login')) -join '').Trim()
        $owner = ($Repo -split '/')[0]
        if ($login -ne $owner) { throw "The token belongs to '$login', but $Repo belongs to '$owner'. Nothing was uploaded." }
        Write-Host "  releasing as $login"
        $tag = 'v' + ([DateTimeOffset]::Parse($dist.builtAt)).UtcDateTime.ToString('yyyy.MM.dd-HHmm')
        $asset = 'MTGA-Forge.zip'
        $tmpDir = Join-Path $env:TEMP ('mtgaf-release-' + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory $tmpDir | Out-Null
        Copy-Item $Zip (Join-Path $tmpDir $asset)
        $files = @((Join-Path $tmpDir $asset))
        if ($SourceZip) { $files += (Resolve-Path $SourceZip).Path }
        Native 'gh' (@('release', 'create', $tag) + $files + @('--repo', $Repo, '--title', "MTGA Forge $tag", '--notes', "sha256 of MTGA-Forge.zip: $sha`n$Notes")) | Out-Host
        Remove-Item $tmpDir -Recurse -Force
    } finally { $env:GH_TOKEN = $oldToken }
    $url = "https://github.com/$Repo/releases/download/$tag/$asset"
} elseif ($Upload -eq 'r2') {
    if (-not $PublicBase) { throw '-PublicBase https://<public address of the bucket> is needed for -Upload r2' }
    Step "R2 bucket $Bucket"
    $key = "MTGA-Forge-$($dist.commit).zip"
    Push-Location "$root\site"
    try { Native 'npx' @('wrangler', 'r2', 'object', 'put', "$Bucket/$key", '--file', $Zip, '--content-type', 'application/zip', '--remote') | Out-Host } finally { Pop-Location }
    $url = $PublicBase.TrimEnd('/') + '/' + $key
}

if ($url) {
    Step "latest.json -> $url"
    & powershell -NoProfile -File "$root\tools\publish-latest.ps1" -Zip $Zip -Url $url -Notes $Notes
    if ($Deploy) {
        Step 'deploy the site'
        Push-Location "$root\site"; try { Native 'npx' @('wrangler', 'deploy') | Out-Host } finally { Pop-Location }
    } else { Write-Host 'Not deployed. Run: cd site; npx wrangler deploy' }
} elseif ($Upload -eq 'none') {
    Write-Host "`nNothing published (no -Upload or -Url). With -Upload github|r2 the zip is hosted, latest.json is written and (with -Deploy) the site is updated."
}
