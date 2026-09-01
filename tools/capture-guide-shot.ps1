<#
capture-guide-shot.ps1 - grab a cropped screenshot of the running K2 for use
in an in-app guide (see K2.Core\Assets\Guides\README.md).

K2.App runs elevated, so a non-elevated shell can't drive its UI or read its
UI-Automation tree. This script therefore does NOT navigate K2 - put K2 on the
section you want first, then run it. It captures the window with PrintWindow
(works across integrity levels) and crops to a client-relative rectangle.

  # 1. capture the whole client area, eyeball the region:
  powershell -File tools\capture-guide-shot.ps1 -Name tmp-full -Full

  # 2. capture just a region (x,y,width,height in client pixels):
  powershell -File tools\capture-guide-shot.ps1 -Name everest-appearance -Rect "170,120,430,360"

Output goes straight to K2.Core\Assets\Guides\<Name>.png (picked up by the
*.png glob in K2.Core.csproj - no rebuild wiring needed, just rebuild K2).

If PrintWindow comes back black/false for this machine's K2, capture with the
Snipping Tool instead and drop <Name>.png in that folder by hand.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)] [string] $Name,
  [string] $Rect,          # "x,y,w,h" client-relative; omit with -Full
  [switch] $Full,
  [int] $Pad = 0
)

Add-Type -AssemblyName System.Drawing
$sig = @'
using System;
using System.Runtime.InteropServices;
public class Win {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
}
'@
Add-Type -TypeDefinition $sig

$proc = Get-Process K2.App -ErrorAction SilentlyContinue | Sort-Object StartTime | Select-Object -First 1
if (-not $proc) { throw "K2.App is not running." }
$hwnd = $proc.MainWindowHandle
if ($hwnd -eq 0) { throw "K2.App has no main window handle (still starting up?)." }

if ([Win]::IsIconic($hwnd)) { [void][Win]::ShowWindow($hwnd, 9) }  # SW_RESTORE
[void][Win]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 400

$cr = New-Object Win+RECT
[void][Win]::GetClientRect($hwnd, [ref]$cr)
$cw = $cr.R - $cr.L; $ch = $cr.B - $cr.T
if ($cw -le 0 -or $ch -le 0) { throw "Client rect is empty ($cw x $ch) - is the window visible?" }
Write-Host "client area: ${cw} x ${ch}"

$bmp = New-Object System.Drawing.Bitmap $cw, $ch
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [Win]::PrintWindow($hwnd, $hdc, 3)   # PW_CLIENTONLY | PW_RENDERFULLCONTENT
$g.ReleaseHdc($hdc); $g.Dispose()
Write-Host "PrintWindow => $ok"

# black-frame guard
$sample = 0
for ($y = 10; $y -lt $ch; $y += 80) { for ($x = 10; $x -lt $cw; $x += 80) {
  $p = $bmp.GetPixel($x, $y); if ($p.R -bor $p.G -bor $p.B) { $sample++ }
}}
if ($sample -eq 0) { Write-Warning "Captured frame looks entirely black - PrintWindow likely blocked for this elevated window. Use the Snipping Tool instead." }

$outDir = Join-Path $PSScriptRoot '..\K2.Core\Assets\Guides'
$outDir = [System.IO.Path]::GetFullPath($outDir)
$null = New-Item -ItemType Directory -Path $outDir -Force
$outFile = Join-Path $outDir "$Name.png"

if ($Full) {
  $bmp.Save($outFile, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  Write-Host "saved full client -> $outFile"
  return
}
if (-not $Rect) { throw "Pass -Rect 'x,y,w,h' or -Full." }

$n = $Rect.Split(',') | ForEach-Object { [int]$_.Trim() }
if ($n.Count -ne 4) { throw "-Rect must be 'x,y,w,h'." }
$x = [Math]::Max(0, $n[0] - $Pad)
$y = [Math]::Max(0, $n[1] - $Pad)
$w = [Math]::Min($cw - $x, $n[2] + 2 * $Pad)
$h = [Math]::Min($ch - $y, $n[3] + 2 * $Pad)

$crop = $bmp.Clone((New-Object System.Drawing.Rectangle $x, $y, $w, $h), $bmp.PixelFormat)
$crop.Save($outFile, [System.Drawing.Imaging.ImageFormat]::Png)
$crop.Dispose(); $bmp.Dispose()
Write-Host "saved ${w}x${h} crop at ${x},${y} -> $outFile"
