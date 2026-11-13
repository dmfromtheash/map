param([string]$RunId)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$root='D:\MapChecks\runs'
$dir=Join-Path $root $RunId
$files=@(Get-ChildItem -LiteralPath $dir -File | Where-Object {$_.Extension -in @('.jpg','.png') -and $_.Name -notlike 'contact-*'} | Sort-Object Name)
for($offset=0;$offset -lt $files.Count;$offset+=12) {
 $part=@($files | Select-Object -Skip $offset -First 12)
 $sheet=[Drawing.Bitmap]::new(1600,([int][math]::Ceiling($part.Count/4)*280))
 $g=[Drawing.Graphics]::FromImage($sheet);$g.Clear([Drawing.Color]::FromArgb(35,35,35))
 $font=[Drawing.Font]::new('Segoe UI',10)
 for($i=0;$i -lt $part.Count;$i++) {
  $src=[Drawing.Image]::FromFile($part[$i].FullName)
  $ratio=[math]::Min(394/$src.Width,248/$src.Height)
  $w=[int]($src.Width*$ratio);$h=[int]($src.Height*$ratio)
  $x=($i%4)*400;$y=[int][math]::Floor($i/4)*280
  $g.DrawImage($src,$x,$y,$w,$h)
  $g.DrawString($part[$i].BaseName.Replace('report.',''),$font,[Drawing.Brushes]::White,($x+2),($y+250))
  $src.Dispose()
 }
 $path=Join-Path $dir ('contact-'+[int]($offset/12)+'.jpg')
 $sheet.Save($path,[Drawing.Imaging.ImageFormat]::Jpeg)
 $font.Dispose();$g.Dispose();$sheet.Dispose()
 Write-Output $path
}
