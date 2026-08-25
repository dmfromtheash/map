# Stages 6–8 benchmark snapshot

Status: **FACT** for this recorded machine and fixture, not a universal hardware claim.

The Release benchmark uses a 65,536×65,536 logical canvas but renders only a 1,920×1,080
visible world region. The Stage 8 mixed fixture combines 50,000 base objects with 100 visible
text objects, one editable vector, one editable composition, a subdivided pointy-hex grid and one
exact color filter. Three iterations warm caches; 15 sorted samples provide median and P95.

| Profile | Median | P95 | Spatial/render bound |
|---|---:|---:|---:|
| 50,000-object baseline | 0.325 ms | 0.572 ms | 28 / 50,000 candidates |
| Stage 8 mixed scene | 26.782 ms | 27.470 ms | 130 candidates/drawn |

The first mixed run exposed per-cell `SKPath` allocation in the hex grid and repeated OS font
resolution for every text object. One shared grid path and a bounded 64-entry weak typeface cache
reduced the mixed median from 37.962 ms to 26.782 ms. The exact pass is below the existing
33.3 ms median gate on this fixture.

Filter processing and grids are clipped to `VisibleWorld`; explicit composition flatten allocates
only the composition bounds and caps its longest raster edge at 4,096 pixels. Blur/sharpen are not
implemented in Stage 8 because no separate bounded benchmark/budget was accepted for them.

Raw values are in [STAGES_06_08.json](STAGES_06_08.json). Reproduce once with:

```powershell
dotnet run --project benchmarks/Map.Benchmarks/Map.Benchmarks.csproj -c Release
```

The bounded native UIA driver is `scripts/drive-stages06-08.ps1`; evidence and its single editor
screenshot are written under ignored `artifacts/stages06-08/ui-smoke/`.
