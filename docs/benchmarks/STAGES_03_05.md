# Stages 3–5 benchmark snapshot

Status: **FACT** for the recorded machine and fixture; not a universal hardware claim.

## Method

- Release build, no debugger, .NET 10.0.10 on Windows, 16 logical processors.
- The benchmark performs warm-up iterations before collecting sorted samples.
- Large-scene rendering uses a 65,536×65,536 canvas, a 1,920×1,080 visible viewport and
  50,000 scene objects. Median and P95 include spatial-index query plus drawing.
- The asset fixture loads and searches metadata for 10,000 records. It counts decoded originals
  to prove that catalog opening does not eagerly decode source images.
- The land fixture renders four 256×256 one-channel coverage tiles both in interactive preview
  and exact idle mode. Coverage is cached; coast effects remain derived and do not alter it.

The raw result is in [STAGES_03_05.json](STAGES_03_05.json). Reproduce it with:

```powershell
dotnet run --project benchmarks/Map.Benchmarks/Map.Benchmarks.csproj -c Release
```

## Result

| Profile | Result | Gate | Status |
|---|---:|---:|---|
| 50,000-object render median | 0.748 ms | ≤ 33.3 ms | FACT |
| 50,000-object render P95 | 1.001 ms | recorded | FACT |
| Spatial candidates / total | 28 / 50,000 | bounded | FACT |
| 10,000-asset metadata load | 141.947 ms | ≤ 2,000 ms | FACT |
| Warm asset search P95 | 1.344 ms | ≤ 100 ms | FACT |
| Originals decoded during catalog load | 0 | 0 | FACT |
| Land fast-preview median | 0.656 ms | recorded | FACT |
| Land exact-idle median | 75.094 ms | recorded | FACT |

No numeric Stage 5 coast-rendering budget is accepted in the roadmap, so the two land values are
measurements rather than a newly invented gate. Exact idle rendering was reduced from about
1,920 ms during implementation by replacing full-surface morphology with bounded derived rings.

## Bounded native smoke

`scripts/drive-stages03-05.ps1` creates a 65,536×65,536 map in the real Release EXE and exercises
brush, per-layer mask, land brush/shape/style/move, layer blend and undo/redo through Windows UI
Automation. It also checks the canvas UIA peer, Stage 4 controls and absence of TCP listeners.
Evidence is written to the ignored `artifacts/stages03-05/ui-smoke/` directory.

Asset import/decode is **PARTIAL** in this native smoke: the controls are present, while actual
catalog import, SHA-256 deduplication, lazy decode and persistence are covered by automated tests
and the benchmark rather than by a second file-dialog pass.
