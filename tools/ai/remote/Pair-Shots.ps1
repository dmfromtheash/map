param([string]$PathA, [string]$PathB, [string]$Out, [int]$Width = 1500, [string]$Crop = '')
# Side-by-side A|B. Without -Crop: both scaled to fit $Width. With -Crop "x,y,w,h": that region at 1:1 (or x2 if small).
Add-Type -AssemblyName System.Drawing
$ia = [Drawing.Image]::FromFile($PathA); $ib = [Drawing.Image]::FromFile($PathB)
if ($Crop) {
    $c = $Crop.Split(',') | % { [int]$_ }
    $src = New-Object Drawing.Rectangle $c[0], $c[1], $c[2], $c[3]
    $k = if ($c[2] -le 400) { 2 } else { 1 }
    $w = $c[2] * $k; $h = $c[3] * $k
    $bmp = New-Object Drawing.Bitmap (2 * $w + 10), $h
    $g = [Drawing.Graphics]::FromImage($bmp); $g.Clear([Drawing.Color]::White)
    $g.InterpolationMode = 'NearestNeighbor'; $g.PixelOffsetMode = 'Half'
    $g.DrawImage($ia, (New-Object Drawing.Rectangle 0, 0, $w, $h), $src, 'Pixel')
    $g.DrawImage($ib, (New-Object Drawing.Rectangle ($w + 10), 0, $w, $h), $src, 'Pixel')
} else {
    $w = [int](($Width - 10) / 2); $h = [int]($ia.Height * $w / $ia.Width)
    $bmp = New-Object Drawing.Bitmap $Width, $h
    $g = [Drawing.Graphics]::FromImage($bmp); $g.Clear([Drawing.Color]::White)
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.DrawImage($ia, 0, 0, $w, $h); $g.DrawImage($ib, $w + 10, 0, $w, $h)
}
$bmp.Save($Out, [Drawing.Imaging.ImageFormat]::Png)
"$($ia.Width)x$($ia.Height) -> $Out"
$g.Dispose(); $bmp.Dispose(); $ia.Dispose(); $ib.Dispose()

