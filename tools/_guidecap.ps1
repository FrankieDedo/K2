# Dot-source helper for capturing guide screenshots from a NON-elevated K2.
#
# K2.App has requireAdministrator in its manifest; a non-elevated shell can't
# drive its UI. Launch a non-elevated copy first:
#   $env:__COMPAT_LAYER='RUNASINVOKER'
#   Start-Process 'K2.App\bin\x86\Debug\net8.0-windows10.0.19041.0\K2.App.exe'
# (and temporarily set "StartMinimizedToTray": false in
#  %LOCALAPPDATA%\K2\app_settings.json so the window actually shows - restore
#  it afterwards).
#
# Then:
#   . .\tools\_guidecap.ps1
#   Sel TabEverest ; Sel RbSecAppearance
#   Cap everest-appearance PnlSecAppearance -Union RbSecKeyMapping -Pad 16
#   Cap dp-rotation-before "330,188,530,322"          # or a client-rect crop
#   Tree PnlSecAppearance                             # list AutomationIds+rects
#
# Notes: DisplayPad/MacroPad on-canvas keys are NOT UIA Buttons - to reach the
# action picker, click by screen coordinate. Output goes straight to
# K2.Core\Assets\Guides\<name>.png (picked up by the *.png glob).

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
$script:sig = @'
using System; using System.Runtime.InteropServices;
public class K2Cap {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref PT p);
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L,T,Rr,B; }
  [StructLayout(LayoutKind.Sequential)] public struct PT { public int X,Y; }
}
'@
if (-not ('K2Cap' -as [type])) { Add-Type -TypeDefinition $script:sig }

$script:OutDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\K2.Core\Assets\Guides'))

function Hwnd { (Get-Process K2.App | Sort-Object StartTime | Select-Object -First 1).MainWindowHandle }
function Root { [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr](Hwnd)) }

function Find([string]$aid) {
  $c = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty), $aid
  (Root).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function FindName([string]$name) {
  $c = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), $name
  (Root).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

function Sel($aidOrElem) {
  $e = if ($aidOrElem -is [string]) { Find $aidOrElem } else { $aidOrElem }
  if (-not $e) { Write-Warning "not found: $aidOrElem"; return }
  foreach ($patId in @([System.Windows.Automation.SelectionItemPattern]::Pattern, [System.Windows.Automation.InvokePattern]::Pattern)) {
    $p = $null
    if ($e.TryGetCurrentPattern($patId, [ref]$p)) {
      if ($patId -eq [System.Windows.Automation.SelectionItemPattern]::Pattern) { $p.Select() } else { $p.Invoke() }
      Start-Sleep -Milliseconds 650; return
    }
  }
  Write-Warning "no Select/Invoke pattern on $aidOrElem"
}

function Tree($aid) {
  $base = if ($aid) { Find $aid } else { Root }
  if (-not $base) { Write-Warning "not found: $aid"; return }
  $all = $base.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
  foreach ($e in $all) {
    $a = $e.Current.AutomationId; if (-not $a) { continue }
    $r = $e.Current.BoundingRectangle
    $ct = ($e.Current.ControlType.ProgrammaticName -replace 'ControlType\.','')
    "{0,-28} {1,-11} off={2,-6} [{3,6:0},{4,5:0} {5,5:0}x{6,4:0}] '{7}'" -f $a,$ct,$e.Current.IsOffscreen,$r.X,$r.Y,$r.Width,$r.Height,$e.Current.Name
  }
}

function Grab {
  $h = [IntPtr](Hwnd)
  $cr = New-Object K2Cap+R; [void][K2Cap]::GetClientRect($h, [ref]$cr)
  $o = New-Object K2Cap+PT; [void][K2Cap]::ClientToScreen($h, [ref]$o)
  $bmp = New-Object System.Drawing.Bitmap $cr.Rr, $cr.B
  $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
  $ok = [K2Cap]::PrintWindow($h, $hdc, 3); $g.ReleaseHdc($hdc); $g.Dispose()
  [pscustomobject]@{ Bmp=$bmp; Ox=$o.X; Oy=$o.Y; Ok=$ok; W=$cr.Rr; H=$cr.B }
}

# Cap <file> <aid|"x,y,w,h">  [-Pad n] [-Grow "l,t,r,b"]  [-Union aid2,aid3]
function Cap {
  param([string]$File, [string]$Target, [int]$Pad = 8, [string]$Grow, [string[]]$Union)
  $s = Grab
  if (-not $s.Ok) { Write-Warning "PrintWindow failed" }
  if ($Target -match '^\s*-?\d+\s*,') {
    $n = $Target.Split(',') | ForEach-Object { [int]$_.Trim() }
    $x=$n[0]; $y=$n[1]; $w=$n[2]; $hh=$n[3]
  } else {
    $e = Find $Target; if (-not $e) { Write-Warning "not found: $Target"; $s.Bmp.Dispose(); return }
    $r = $e.Current.BoundingRectangle
    $x = [int]$r.X - $s.Ox; $y = [int]$r.Y - $s.Oy; $w = [int]$r.Width; $hh = [int]$r.Height
    foreach ($u in ($Union | Where-Object { $_ })) {
      $eu = Find $u; if ($eu) { $ru = $eu.Current.BoundingRectangle
        $ux = [int]$ru.X - $s.Ox; $uy = [int]$ru.Y - $s.Oy
        $nx = [Math]::Min($x,$ux); $ny = [Math]::Min($y,$uy)
        $w = [Math]::Max($x+$w, $ux+[int]$ru.Width) - $nx
        $hh = [Math]::Max($y+$hh, $uy+[int]$ru.Height) - $ny
        $x = $nx; $y = $ny }
    }
  }
  $gl=0;$gt=0;$gr=0;$gb=0
  if ($Grow) { $q = $Grow.Split(',') | ForEach-Object {[int]$_.Trim()}; $gl=$q[0];$gt=$q[1];$gr=$q[2];$gb=$q[3] }
  $x = [Math]::Max(0, $x - $Pad - $gl); $y = [Math]::Max(0, $y - $Pad - $gt)
  $w = [Math]::Min($s.W - $x, $w + 2*$Pad + $gl + $gr)
  $hh = [Math]::Min($s.H - $y, $hh + 2*$Pad + $gt + $gb)
  $crop = $s.Bmp.Clone((New-Object System.Drawing.Rectangle $x,$y,$w,$hh), $s.Bmp.PixelFormat)
  $null = New-Item -ItemType Directory -Path $script:OutDir -Force
  $out = Join-Path $script:OutDir "$File.png"
  $crop.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
  $crop.Dispose(); $s.Bmp.Dispose()
  "saved ${w}x${hh} @ ${x},${y} -> $out"
}

"guidecap loaded. HWND=$(Hwnd)  OutDir=$script:OutDir"
