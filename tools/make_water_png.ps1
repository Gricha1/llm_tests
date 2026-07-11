Add-Type -AssemblyName System.Drawing
$src = 'c:\Grisha\unity_projects\forest_survival\Assets\UI\Icons\Custom\water.jpg'
$dst = 'c:\Grisha\unity_projects\forest_survival\Assets\UI\Icons\Custom\water_drop.png'
$img = [System.Drawing.Image]::FromFile($src)
$bmp = New-Object System.Drawing.Bitmap $img.Width, $img.Height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::FromArgb(0, 0, 0, 0))
$g.DrawImage($img, 0, 0, $img.Width, $img.Height)
$minX = $bmp.Width; $minY = $bmp.Height; $maxX = 0; $maxY = 0
for ($y = 0; $y -lt $bmp.Height; $y++) {
  for ($x = 0; $x -lt $bmp.Width; $x++) {
    $p = $bmp.GetPixel($x, $y)
    if ($p.R -gt 215 -and $p.G -gt 215 -and $p.B -gt 215) {
      $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(0, $p.R, $p.G, $p.B))
      continue
    }
    $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(255, $p.R, $p.G, $p.B))
    if ($x -lt $minX) { $minX = $x }
    if ($y -lt $minY) { $minY = $y }
    if ($x -gt $maxX) { $maxX = $x }
    if ($y -gt $maxY) { $maxY = $y }
  }
}
$w = [Math]::Max(1, $maxX - $minX + 1)
$h = [Math]::Max(1, $maxY - $minY + 1)
$crop = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$cg = [System.Drawing.Graphics]::FromImage($crop)
$cg.Clear([System.Drawing.Color]::FromArgb(0, 0, 0, 0))
$cg.DrawImage($bmp, (New-Object System.Drawing.Rectangle 0, 0, $w, $h), (New-Object System.Drawing.Rectangle $minX, $minY, $w, $h), [System.Drawing.GraphicsUnit]::Pixel)
$crop.Save($dst, [System.Drawing.Imaging.ImageFormat]::Png)
$cg.Dispose(); $crop.Dispose(); $g.Dispose(); $bmp.Dispose(); $img.Dispose()
Write-Output "saved $dst ${w}x${h}"
