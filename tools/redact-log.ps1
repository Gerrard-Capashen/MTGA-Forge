# Dot-sourced by launcher.ps1. Protect-LogFile blanks personal data in a log file in place, so a Player.log attached to a bug
# report does not carry it: the Windows user name (also inside C:\Users\<name>\ paths), the computer name, the profile name and id
# (learned from the "LocalProfile: NAME (GUID)" line the game writes at start), non-loopback IP addresses (P2P peers) and the
# install folder when it is outside the user profile. Safe to run twice; a file in use (the game still open) is left alone.
function Protect-LogFile {
    param([string]$Path, [string[]]$Folders = @())
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $enc = New-Object System.Text.UTF8Encoding($false)
    $octet = '(25[0-5]|2[0-4]\d|1?\d?\d)'
    $rules = New-Object System.Collections.Generic.List[object]
    function Add-Rule($pattern, $with, $options) { $rules.Add(@([regex]::new($pattern, $options), $with)) }
    $ci = [Text.RegularExpressions.RegexOptions]::IgnoreCase
    $none = [Text.RegularExpressions.RegexOptions]::None

    # what this log says about its own player
    $name = $null; $id = $null
    try {
        $r = New-Object IO.StreamReader($Path, $enc)
        try {
            for ($i = 0; $i -lt 600 -and ($line = $r.ReadLine()) -ne $null; $i++) {
                if ($line -match 'LocalProfile: (.+?) \(([0-9a-fA-F-]{36})\)') { $name = $Matches[1]; $id = $Matches[2]; break }
            }
        } finally { $r.Dispose() }
    } catch { return }

    foreach ($f in $Folders) {
        if (-not $f) { continue }
        $t = $f.TrimEnd('\', '/')
        Add-Rule (([regex]::Escape($t) -replace '\\\\', '[\\/]+') -replace '/', '[\\/]+') '<MTGA-Forge>' $ci
    }
    Add-Rule '([A-Za-z]:[\\/]+Users[\\/]+)[^\\/"\s]+' '$1<user>' $ci
    if ($id) { Add-Rule ([regex]::Escape($id)) '<account-id>' $ci }
    if ($name -and $name.Length -ge 3) { Add-Rule ([regex]::Escape($name)) '<player>' $none }
    if ($env:USERNAME -and $env:USERNAME.Length -ge 4) { Add-Rule ('\b' + [regex]::Escape($env:USERNAME) + '\b') '<user>' $ci }
    if ($env:COMPUTERNAME -and $env:COMPUTERNAME.Length -ge 4) { Add-Rule ('\b' + [regex]::Escape($env:COMPUTERNAME) + '\b') '<pc>' $ci }
    Add-Rule "(?<![\d.])(?!127\.|0\.0\.0\.0(?![\d.]))($octet\.){3}$octet(?![\d.])" '<ip>' $none

    $tmp = "$Path.redacting"
    try {
        $in = New-Object IO.StreamReader($Path, $enc)
        try {
            $out = New-Object IO.StreamWriter($tmp, $false, $enc)
            try {
                while (($line = $in.ReadLine()) -ne $null) {
                    foreach ($rule in $rules) { $line = $rule[0].Replace($line, $rule[1]) }
                    $out.WriteLine($line)
                }
            } finally { $out.Dispose() }
        } finally { $in.Dispose() }
        Move-Item -LiteralPath $tmp -Destination $Path -Force
    } catch {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    }
}

# the Player.log (and the previous run's) of the client in $ClientDir, written by Unity to LocalLow\<app.info line 1>\<line 2>
function Protect-PlayerLogs {
    param([string]$ClientDir, [string[]]$Folders = @())
    $info = "$ClientDir\MTGA_Data\app.info"
    if (-not (Test-Path -LiteralPath $info)) { return }
    $lines = @(Get-Content -LiteralPath $info)
    if ($lines.Count -lt 2) { return }
    $dir = "$env:USERPROFILE\AppData\LocalLow\$($lines[0].Trim())\$($lines[1].Trim())"
    foreach ($n in 'Player.log', 'Player-prev.log') { Protect-LogFile -Path "$dir\$n" -Folders $Folders }
}
