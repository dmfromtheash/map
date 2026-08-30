[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string]$PublishedExe,
    [string]$EvidenceDirectory
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path -Parent $PSScriptRoot
}
$root = [System.IO.Path]::GetFullPath($RepositoryRoot)
$pngPath = Join-Path $root 'src/Map.App/Assets/AppIcon-Large.png'
$icoPath = Join-Path $root 'src/Map.App/Assets/AppIcon-Large.ico'
$projectPath = Join-Path $root 'src/Map.App/Map.App.csproj'
$expectedPng = '8BF8D23D901D005670DE90C34E08DBB0BFA39993C6363E8DE229C64352DED4E1'
$expectedIco = '7E396A60DFB2B6AF62807445D62FF5C7E19168EA494F923EB150039F3FDCD4AC'
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)

foreach ($path in @($pngPath, $icoPath, $projectPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Stage 11 icon input is missing: $path"
    }
}
$pngHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $pngPath).Hash
$icoHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $icoPath).Hash
if ($pngHash -ne $expectedPng -or $icoHash -ne $expectedIco) {
    throw "Stage 11 icon hashes do not match the owner-approved input. PNG=$pngHash; ICO=$icoHash"
}

$png = [System.IO.File]::ReadAllBytes($pngPath)
$pngSignature = [byte[]](137, 80, 78, 71, 13, 10, 26, 10)
$signatureMatches = $png.Length -ge 8
for ($index = 0; $signatureMatches -and $index -lt $pngSignature.Length; $index++) {
    $signatureMatches = $png[$index] -eq $pngSignature[$index]
}
if ($png.Length -lt 24 -or -not $signatureMatches) {
    throw 'AppIcon-Large.png is not a valid PNG header.'
}
function Read-BigEndianInt32 {
    param([byte[]]$Bytes, [int]$Offset)
    return [int](
        ([uint32]$Bytes[$Offset] -shl 24) -bor
        ([uint32]$Bytes[$Offset + 1] -shl 16) -bor
        ([uint32]$Bytes[$Offset + 2] -shl 8) -bor
        [uint32]$Bytes[$Offset + 3])
}
$pngWidth = Read-BigEndianInt32 -Bytes $png -Offset 16
$pngHeight = Read-BigEndianInt32 -Bytes $png -Offset 20
if ($pngWidth -ne 1254 -or $pngHeight -ne 1254) {
    throw "Unexpected source PNG dimensions: ${pngWidth}x${pngHeight}."
}

$stream = [System.IO.File]::OpenRead($icoPath)
$reader = [System.IO.BinaryReader]::new($stream)
try {
    $reserved = $reader.ReadUInt16()
    $type = $reader.ReadUInt16()
    $count = $reader.ReadUInt16()
    if ($reserved -ne 0 -or $type -ne 1 -or $count -eq 0) {
        throw 'AppIcon-Large.ico has an invalid ICONDIR header.'
    }
    $frames = @()
    for ($index = 0; $index -lt $count; $index++) {
        $widthByte = $reader.ReadByte()
        $heightByte = $reader.ReadByte()
        $colorCount = $reader.ReadByte()
        $entryReserved = $reader.ReadByte()
        $planes = $reader.ReadUInt16()
        $bitCount = $reader.ReadUInt16()
        $bytesInResource = $reader.ReadUInt32()
        $imageOffset = $reader.ReadUInt32()
        $width = if ($widthByte -eq 0) { 256 } else { [int]$widthByte }
        $height = if ($heightByte -eq 0) { 256 } else { [int]$heightByte }
        if ($entryReserved -ne 0 -or $width -ne $height -or $bytesInResource -eq 0 -or
            ([long]$imageOffset + [long]$bytesInResource) -gt $stream.Length) {
            throw "ICO frame $index has an invalid directory entry."
        }
        $frames += [ordered]@{
            index = $index
            width = $width
            height = $height
            colorCount = [int]$colorCount
            planes = [int]$planes
            bitCount = [int]$bitCount
            bytes = [long]$bytesInResource
            offset = [long]$imageOffset
        }
    }
}
finally {
    $reader.Dispose()
    $stream.Dispose()
}
$frameSizes = @($frames | ForEach-Object { [int]$_.width })
foreach ($requiredSize in @(16, 32, 48, 256)) {
    if ($frameSizes -notcontains $requiredSize) {
        throw "ICO does not contain the required ${requiredSize}px frame."
    }
}

[xml]$project = Get-Content -Raw -LiteralPath $projectPath
$iconProperty = @($project.Project.PropertyGroup.ApplicationIcon | Where-Object { $_ }) | Select-Object -First 1
$windowProperty = @($project.Project.PropertyGroup.AvaloniaIncludeApplicationIconAsWindowIcon | Where-Object { $_ }) | Select-Object -First 1
if ([string]$iconProperty -ne 'Assets\AppIcon-Large.ico' -or [string]$windowProperty -ne 'true') {
    throw 'Map.App.csproj does not bind the approved ICO to both the executable and Avalonia window.'
}

$embedded = $null
if (-not [string]::IsNullOrWhiteSpace($PublishedExe)) {
    $exeFull = [System.IO.Path]::GetFullPath($PublishedExe)
    if (-not (Test-Path -LiteralPath $exeFull -PathType Leaf)) {
        throw "Published executable does not exist: $exeFull"
    }
    Add-Type -AssemblyName System.Drawing
    $icon = [System.Drawing.Icon]::ExtractAssociatedIcon($exeFull)
    if ($null -eq $icon) {
        throw "Windows did not extract an embedded icon from: $exeFull"
    }
    try {
        $bitmap = $icon.ToBitmap()
        try {
            $embedded = [ordered]@{
                executable = $exeFull
                executableSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $exeFull).Hash
                extractedWidth = $bitmap.Width
                extractedHeight = $bitmap.Height
            }
            if (-not [string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
                $evidenceFull = [System.IO.Path]::GetFullPath($EvidenceDirectory)
                [System.IO.Directory]::CreateDirectory($evidenceFull) | Out-Null
                $extractedPath = Join-Path $evidenceFull 'embedded-associated-icon.png'
                $bitmap.Save($extractedPath, [System.Drawing.Imaging.ImageFormat]::Png)
                $embedded.extractedPng = $extractedPath
                $embedded.extractedPngSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $extractedPath).Hash
            }
        }
        finally {
            $bitmap.Dispose()
        }
    }
    finally {
        $icon.Dispose()
    }
}

$result = [ordered]@{
    result = 'PASS'
    source = [ordered]@{
        png = $pngPath
        pngSha256 = $pngHash
        pngWidth = $pngWidth
        pngHeight = $pngHeight
        ico = $icoPath
        icoSha256 = $icoHash
        frameCount = $frames.Count
        frames = @($frames)
    }
    project = [ordered]@{
        applicationIcon = [string]$iconProperty
        includeAsWindowIcon = [bool]::Parse([string]$windowProperty)
    }
    embedded = $embedded
}
$json = $result | ConvertTo-Json -Depth 8
if (-not [string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
    $evidenceFull = [System.IO.Path]::GetFullPath($EvidenceDirectory)
    [System.IO.Directory]::CreateDirectory($evidenceFull) | Out-Null
    [System.IO.File]::WriteAllText(
        (Join-Path $evidenceFull 'icon-report.json'),
        $json + [Environment]::NewLine,
        $utf8NoBom)
}
Write-Output $json
