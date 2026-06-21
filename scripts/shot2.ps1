Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win2 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
[Win2]::SetProcessDPIAware() | Out-Null
$targetPid = [int]$args[0]; $outPath = $args[1]
$found = [IntPtr]::Zero; $best = 0
$cb = [Win2+EnumWindowsProc]{
  param($h, $l)
  $wpid = 0; [Win2]::GetWindowThreadProcessId($h, [ref]$wpid) | Out-Null
  if ($wpid -eq $targetPid -and [Win2]::IsWindowVisible($h)) {
    $r = New-Object Win2+RECT; [Win2]::GetWindowRect($h, [ref]$r) | Out-Null
    $area = ($r.Right - $r.Left) * ($r.Bottom - $r.Top)
    if ($area -gt $best) { $best = $area; $script:found = $h }
  }; return $true
}
[Win2]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
if ($found -eq [IntPtr]::Zero) { Write-Output "NO_WINDOW"; exit 1 }
$r = New-Object Win2+RECT; [Win2]::GetWindowRect($found, [ref]$r) | Out-Null
$w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
# 主药丸顶部居中:窗口水平中心、顶部下 48 物理像素(=逻辑32,药丸竖直中心)
$cx = $r.Left + [int]($w / 2); $cy = $r.Top + 48
# 先把鼠标移到别处,再移上药丸 → 强制 MouseEnter 转换
[Win2]::SetCursorPos($r.Left + 5, $r.Top + 300) | Out-Null; Start-Sleep -Milliseconds 300
[Win2]::SetCursorPos($cx, $cy) | Out-Null; Start-Sleep -Milliseconds 1100
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
# 扫描非暗像素边界(找药丸/详情卡)
$minX=$w; $minY=$h; $maxX=0; $maxY=0; $cnt=0
for ($y=0; $y -lt $h; $y+=2) {
  for ($x=0; $x -lt $w; $x+=2) {
    $p = $bmp.GetPixel($x,$y)
    if (($p.R + $p.G + $p.B) -gt 120) {
      $cnt++
      if ($x -lt $minX){$minX=$x}; if ($x -gt $maxX){$maxX=$x}
      if ($y -lt $minY){$minY=$y}; if ($y -gt $maxY){$maxY=$y}
    }
  }
}
Write-Output "RECT $($r.Left) $($r.Top) $w $h"
Write-Output "NONDARK count=$cnt bbox=[$minX,$minY -> $maxX,$maxY]"
# 裁出非暗区域(加边距)放大保存
if ($cnt -gt 10) {
  $px=[Math]::Max(0,$minX-10); $py=[Math]::Max(0,$minY-10)
  $pw=[Math]::Min($w-$px, $maxX-$minX+20); $ph=[Math]::Min($h-$py, $maxY-$minY+20)
  $crop = $bmp.Clone((New-Object System.Drawing.Rectangle($px,$py,$pw,$ph)), $bmp.PixelFormat)
  $scale=2
  $big = New-Object System.Drawing.Bitmap(($pw*$scale), ($ph*$scale))
  $g2 = [System.Drawing.Graphics]::FromImage($big)
  $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
  $g2.DrawImage($crop, 0, 0, ($pw*$scale), ($ph*$scale))
  $big.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
  $g2.Dispose(); $big.Dispose(); $crop.Dispose()
  Write-Output "SAVED $outPath ($($pw*$scale)x$($ph*$scale))"
}
$g.Dispose(); $bmp.Dispose()
