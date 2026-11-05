# Launch the client copy (+mock), then report window handle/rect/visibility after N seconds.
param([int]$Wait = 35)
$root = (Resolve-Path "$PSScriptRoot\..").Path
Get-Process MTGA -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$root\*" } | Stop-Process -Force
Add-Type @"
using System; using System.Runtime.InteropServices;
public class W { [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; } }
"@
$p = Start-Process "$root\client\MTGA.exe" -WorkingDirectory "$root\client" -ArgumentList '-screen-width 1280 -screen-height 720 -screen-fullscreen 0' -PassThru
Start-Sleep -Seconds $Wait
$p.Refresh()
"exited=$($p.HasExited) handle=$($p.MainWindowHandle) title='$($p.MainWindowTitle)' session=$($p.SessionId) mysession=$((Get-Process -Id $PID).SessionId)"
if ($p.MainWindowHandle -ne 0) {
    $r = New-Object W+RECT
    [W]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
    "rect L=$($r.L) T=$($r.T) R=$($r.R) B=$($r.B) visible=$([W]::IsWindowVisible($p.MainWindowHandle)) iconic=$([W]::IsIconic($p.MainWindowHandle))"
}
Stop-Process -Id $p.Id -Force
