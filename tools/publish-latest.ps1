# Writes site\public\latest.json for a release: the build time and commit from the package's dist.json, plus the SHA-256 and size of the zip.
# The launcher (MTGA-Forge.exe) reads this file from the website and offers an update when it is newer than its own build.
# Usage: powershell -NoProfile -File tools\publish-latest.ps1 -Zip dist\MTGA-Forge.zip -Url https://<where the zip can be downloaded> [-Notes 'what changed']
# Then deploy the site: cd site; npx wrangler deploy
param(
    [Parameter(Mandatory)][string]$Zip,
    [Parameter(Mandatory)][string]$Url,
    [string]$Notes = '',
    [string]$Package = '',
    [string]$ScanFile = ''     # <zip>.scan.json from release.ps1 -Scan: VirusTotal results shown under the download button
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..").Path
$Zip = (Resolve-Path $Zip).Path
if (-not $Package) { $Package = Join-Path (Split-Path $Zip -Parent) ([IO.Path]::GetFileNameWithoutExtension($Zip)) }
$distJson = Join-Path $Package 'dist.json'
if (-not (Test-Path $distJson)) { throw "$distJson not found (pass -Package <the unzipped package folder>)" }
$dist = Get-Content $distJson -Raw | ConvertFrom-Json
$out = [ordered]@{
    schema   = 1
    released = ([DateTimeOffset]::Parse($dist.builtAt)).ToString('o')
    commit   = $dist.commit
    sha256   = (Get-FileHash $Zip -Algorithm SHA256).Hash.ToLower()
    size     = (Get-Item $Zip).Length
    url      = $Url
    notes    = $Notes
}
if ($ScanFile) {
    $sc = Get-Content $ScanFile -Raw | ConvertFrom-Json
    $out['scan'] = [ordered]@{
        at = $sc.at
        zipReport = $sc.zipReport; zipEngines = $sc.zipEngines; zipMalicious = $sc.zipMalicious; zipSuspicious = $sc.zipSuspicious
        exeReport = $sc.exeReport; exeEngines = $sc.exeEngines; exeMalicious = $sc.exeMalicious; exeSuspicious = $sc.exeSuspicious
    }
}
$target = "$root\site\public\latest.json"
[IO.File]::WriteAllText($target, (($out | ConvertTo-Json -Depth 4) + "`n"), (New-Object Text.UTF8Encoding $false))
Write-Host "wrote $target"
Get-Content $target
Write-Host "Deploy with: cd site; npx wrangler deploy"
