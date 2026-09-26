# Draws the Peergos Snap icon (original artwork, GPL-3.0-or-later with the project) and writes a multi-size .ico.
param([string]$Out = "$PSScriptRoot\..\src\PeergosSnap\Assets\PeergosSnap.ico")
Add-Type -AssemblyName System.Drawing
function Draw([int]$size) {
  $bmp = New-Object System.Drawing.Bitmap $size, $size
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'
  $s = $size / 32.0
  $body = [System.Drawing.Color]::FromArgb(43,138,110)
  $br = New-Object System.Drawing.SolidBrush $body
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $x=2*$s; $y=6*$s; $w=28*$s; $h=22*$s; $r=6*$s
  $p.AddArc($x,$y,$r,$r,180,90); $p.AddArc($x+$w-$r,$y,$r,$r,270,90); $p.AddArc($x+$w-$r,$y+$h-$r,$r,$r,0,90); $p.AddArc($x,$y+$h-$r,$r,$r,90,90); $p.CloseFigure()
  $g.FillPath($br,$p)
  $g.FillRectangle($br,10*$s,3*$s,12*$s,5*$s)
  $g.FillEllipse([System.Drawing.Brushes]::White,9*$s,10*$s,14*$s,14*$s)
  $g.FillEllipse($br,12.5*$s,13.5*$s,7*$s,7*$s)
  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms,[System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  return ,$ms.ToArray()
}
$sizes = 16,24,32,48,64,128,256
$images = foreach ($z in $sizes) { ,(Draw $z) }
$fs = [System.IO.File]::Create([System.IO.Path]::GetFullPath($Out))
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i=0; $i -lt $sizes.Count; $i++) {
  $z = $sizes[$i]; $d = $images[$i]
  $bw.Write([byte]($z % 256)); $bw.Write([byte]($z % 256)); $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$d.Length); $bw.Write([uint32]$offset)
  $offset += $d.Length
}
foreach ($d in $images) { $bw.Write($d) }
$bw.Close()
Write-Host "icon written: $Out"
