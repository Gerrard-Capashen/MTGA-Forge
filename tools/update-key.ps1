# The key that signs auto-updates. The launcher only installs an update whose update.json is signed with it.
# Usage: powershell -NoProfile -File tools\update-key.ps1 [-KeyFile <private key>]
#   First run: makes a new RSA-3072 key, saves the PRIVATE key to -KeyFile (outside the repo; back it up somewhere safe, e.g. a
#   password manager: without it no launcher already out there accepts a new update), and writes the PUBLIC key into
#   bridge\Launcher\UpdateKey.cs. Later runs only rewrite UpdateKey.cs from the existing key.
#   Never commit the private key. Anyone who has it can push code to every player.
param([string]$KeyFile = "$env:LOCALAPPDATA\MTGA-Forge-release\update-signing-key.xml")
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..").Path
if ($KeyFile.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw "The private key must live outside the repository: $KeyFile" }
$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider 3072
try {
    if (Test-Path $KeyFile) {
        $rsa.FromXmlString((Get-Content $KeyFile -Raw))
        Write-Host "Using the existing key $KeyFile"
    } else {
        New-Item -ItemType Directory -Force (Split-Path $KeyFile -Parent) | Out-Null
        [IO.File]::WriteAllText($KeyFile, $rsa.ToXmlString($true), (New-Object Text.UTF8Encoding $false))
        Write-Host "New signing key saved to $KeyFile"
        Write-Host 'BACK IT UP NOW (password manager or an offline drive). Losing it means players must download a new launcher by hand.'
    }
    $public = $rsa.ToXmlString($false)
} finally { $rsa.Dispose() }
$cs = @"
namespace MtgaForgeLauncher
{
    /// <summary>Public half of the update signing key (tools\update-key.ps1). update.json must carry a valid signature by its private half.</summary>
    static class UpdateKey
    {
        public const string PublicKeyXml = "$public";
    }
}
"@
[IO.File]::WriteAllText("$root\bridge\Launcher\UpdateKey.cs", $cs, (New-Object Text.UTF8Encoding $false))
Write-Host 'Public key written to bridge\Launcher\UpdateKey.cs'
