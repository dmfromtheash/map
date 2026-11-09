# Builds the downloadable Map release: self-contained win-x64 ReadyToRun publish + the installer
# «Установить Map.exe» (tools/release/setup) + read-me, zipped as artifacts/release/Map-<version>-win-x64.zip.
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
# «Установить Map.exe»: built with the .NET Framework compiler that ships with Windows (no packages).
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "csc.exe of .NET Framework 4 not found: $csc" }
& $csc /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 `
    "/out:$folder\Установить Map.exe" "/win32icon:$root\src\Map.App\Assets\AppIcon-Large.ico" `
    "/win32manifest:$PSScriptRoot\setup\setup.manifest" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll "$PSScriptRoot\setup\MapSetup.cs"
if ($LASTEXITCODE -ne 0) { throw "setup build failed: $LASTEXITCODE" }
Copy-Item "$PSScriptRoot\ЧИТАЙ-МЕНЯ.txt" $folder
$zip = "$out\Map-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($folder, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
Write-Output "Release zip: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"