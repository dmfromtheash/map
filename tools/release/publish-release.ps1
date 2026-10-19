# Builds the downloadable Map release: self-contained win-x64 ReadyToRun publish + shortcut
# helper + read-me, zipped as artifacts/release/Map-<version>-win-x64.zip.
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/release/publish-release.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$version = ([xml](Get-Content "$root\Directory.Build.props")).Project.PropertyGroup.VersionPrefix
$out = "$root\artifacts\release"
$folder = "$out\Map"
if (Test-Path $folder) { Remove-Item $folder -Recurse -Force }
New-Item -ItemType Directory -Force $folder | Out-Null
dotnet publish "$root\src\Map.App\Map.App.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -o $folder -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "publish failed: $LASTEXITCODE" }
# Native Skia/HarfBuzz symbols (~100 MB) are not needed to run Map.
Remove-Item "$folder\libSkiaSharp.pdb", "$folder\libHarfBuzzSharp.pdb" -ErrorAction SilentlyContinue
Copy-Item "$PSScriptRoot\make-shortcut.ps1", "$PSScriptRoot\Создать ярлык на рабочем столе.cmd", "$PSScriptRoot\ЧИТАЙ-МЕНЯ.txt" $folder
$zip = "$out\Map-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($folder, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
Write-Output "Release zip: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"