# Stage 11 performance contract

## Status and scope

- `FACT`: the repository benchmark creates all fixtures deterministically at run time; no large
  binary fixture is committed.
- `FACT`: the default contract uses seed `110011`, exactly one warm-up iteration and five measured
  samples per steady-state metric. Raw samples and a machine/source fingerprint are written to
  JSON.
- `FACT`: Skia measurements use CPU raster `SKSurface`; they do not claim GPU or end-to-end UI
  input latency.
- `PARTIAL`: the renderer boundary profile proves bounded region rendering, not a user-facing
  export command. Product export remains the Stage 9D prerequisite.

Run the complete fixed contract from the repository root:

```powershell
dotnet run --project .\benchmarks\Map.Benchmarks\Map.Benchmarks.csproj -c Release --no-build -- `
  --seed 110011 --warmup 1 --samples 5 `
  --scratch .\artifacts\stage11-author\benchmark-scratch `
  --output .\artifacts\stage11-author\benchmark-final.json
```

`--profiles` accepts a comma-separated subset only for development after a related change. The
final author snapshot runs the default complete set once. A non-passing acceptance gate returns
exit code `2`.

## Fixed profiles and gates

| Profile | Fixed boundary | Acceptance |
|---|---|---|
| `objects-1k` | 65,536 square canvas, 1,920×1,080 viewport, fixed pan/scale | median render ≤ 33.3 ms |
| `objects-10k` | same viewport and deterministic layout | every measured render ≤ 33.3 ms |
| `objects-50k` | same viewport and deterministic layout | median render ≤ 33.3 ms |
| `large-map-layers` | 20 brush layers, 20 masks, zoom 0.005–64, cache capacity 64 | cache remains ≤ 64; no full-canvas allocation |
| `catalog-10k` | fresh metadata loads and a separate warmed query; no original decode | load maximum ≤ 2,000 ms; search P95 ≤ 100 ms |
| `undo-200` | fresh session, 200 transforms, undo all, redo all | exact geometry and history depths |
| `brush-mask-stress` | 25 one-tile brush plus 25 one-tile mask commands | exact round-trip and ≤ 30 MiB history payload |
| `save-open-10k` | distinct `.mapproj` files with 10,000 objects | first open ≤ 5 s; file < 500 MB; semantic round-trip |
| `tiled-renderer-boundary` | 8K, 16K and 20K regions, one 2,048-square surface at a time | 16 MiB maximum surface; cancellation before allocation |

The object budgets describe measured scene-render calls, not a synthetic FPS label. `33.3 ms`
corresponds to the Stage 11 minimum 30 FPS envelope; the 10k profile uses the stricter maximum
sample rule while 50k uses the specified median rule.

## Cold, warm and memory evidence

First spatial-index construction, first catalog load, first save and first open are recorded
separately in `evidence`. They are not inserted into warm sample arrays. Every measured action also
records current-thread allocations, managed heap, process working set, a labelled
`working set - managed heap` native estimate, and GC collection deltas. These are observation
counters, not a proof that every native allocation belongs to Skia.

The JSON fingerprint contains OS/process architecture, processor string and logical count,
available memory, .NET and Skia assembly versions, GC mode, stopwatch frequency, renderer kind,
Git commit and dirty state. The dirty author run is evidence for the implementation snapshot; the
fresh acceptance must repeat the contract from the exact clean implementation commit.

## Bounded renderer classification

The 8K/16K/20K profile renders selected regions through the same `SceneRenderer` used by the app.
It calculates the full plan (16, 64 and 100 tiles respectively), but holds one 2,048×2,048 RGBA
surface (16 MiB) at a time. A pre-cancelled token is observed before surface allocation.

- `FACT`: no full 8K/16K/20K bitmap is created by the profile.
- `FACT`: insufficient output space and locked release payloads are tested at the package boundary.
- `PARTIAL`: export target locking, interrupted product export and a user-facing export flow cannot
  be accepted before Stage 9D exists.
