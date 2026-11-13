# Pours every ready hundred of every catalog into the drive library (H:\MapLibrary) with the drive-library builder,
# no window; stops a catalog when a batch has no files or its gate fails. Live passes are separate (owner's window rules).
#   powershell -NoProfile -ExecutionPolicy Bypass -File pour_all.ps1 [-Catalogs a,b] [-Log H:\MapLibrary\pour.log]
param([string[]]$Catalogs = @(), [string]$Log = 'H:\MapLibrary\pour.log', [int]$MaxBatches = 40)
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
Set-Location C:\Projects\Map
[Diagnostics.Process]::GetCurrentProcess().PriorityClass = 'BelowNormal'
"start $(Get-Date -f s) commit $(git rev-parse --short HEAD)" | Out-File $Log -Append -Encoding utf8
$b = dotnet build tools\Map.Performance\Map.Performance.csproj -c Release --nologo -v q 2>&1
($b | Select-String 'Build succeeded| error ' | Select-Object -Last 2) | Out-File $Log -Append -Encoding utf8
# A failed build must not pour with the previous binary (13.11: a broken check change ran the old one).
if ($LASTEXITCODE -ne 0) { "BUILD FAILED - nothing poured $(Get-Date -f s)" | Out-File $Log -Append -Encoding utf8; exit 1 }
$Catalogs = @($Catalogs | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
if (-not $Catalogs) {
  # ConvertFrom-Json of Windows PowerShell 5.1 cannot read the large plan: list catalogs with Python.
  $Catalogs = @((py -3.13 -c "import json; print('\n'.join(sorted({s['catalog'] for s in json.load(open(r'H:\MapLibrary\plan\sets.json', encoding='utf-8'))})))") -split "`n" | Where-Object { $_ })
}
"catalogs: $($Catalogs -join ', ')" | Out-File $Log -Append -Encoding utf8
$out = Join-Path $env:TEMP 'map-pour'; New-Item -ItemType Directory -Force $out | Out-Null
foreach ($catalog in $Catalogs) {
  for ($i = 0; $i -lt $MaxBatches; $i++) {
    $report = Join-Path $out "$catalog.json"
    $t = Get-Date
    & .\tools\Map.Performance\bin\Release\net10.0\win-x64\Map.Performance.exe --profile drive-library --catalogs $catalog --object-count 100 --output $report *> (Join-Path $out "$catalog.log")
    $r = (Get-Content $report -Raw | ConvertFrom-Json)
    $row = @($r.Reports)[0]; if (-not $row) { $row = @($r)[0] }
    "$(Get-Date -f HH:mm:ss) $catalog batch $($row.Batch) files $($row.Files) gate $($row.GatePassed) errors $(@($row.Errors).Count) $([int]((Get-Date)-$t).TotalSeconds)s" | Out-File $Log -Append -Encoding utf8
    if (-not $row.Files -or -not $row.GatePassed) { break }
  }
}
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
"DONE $(Get-Date -f s)" | Out-File $Log -Append -Encoding utf8
