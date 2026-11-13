# CPU edit latency

Extends the `edit-latency` investigation harness from review f7d4881. No window or renderer
is created. Only the owner's unchanged `owner-mountain-01.png` is imported; the other 99
asset keys carry metadata only. Deterministic seed 12345, 100 asset keys, ungrouped stamps.

Build in Release, then run each combination in a separate process, sequentially:

```sh
dotnet build tools/Map.EditLatency -c Release -p:RuntimeIdentifier=osx-arm64
DOTNET_TieredCompilation=0 dotnet tools/Map.EditLatency/bin/Release/net10.0/osx-arm64/Map.EditLatency.dll <output-directory> <count> <suite>
```

Counts: `100`, `100000`, `1000000`. Suites: `actions`, `indexes`, `fixes`.
The optional fourth argument is the repository root containing the fixture.
The output directory must be a disposable work directory: the harness creates catalogs
under its `tmp/` subdirectory. JSONL goes to stdout. No input files are modified.

`actions` measures real CanvasInputRouter select/place and Undo. `indexes` separates cold
derivatives, closed/open rows and follow passes on new revisions. `fixes` adds closed rows
with a nonempty selection, all three follow passes together, and the worst case of one
kind containing every object (including a new append and a closed row query).

Iteration 0 is cold; iterations 1–5 form the warm sample. Code is first exercised on a
different small document; the measured document retains cold caches. Use the median of
the five warm samples, and retain allocations and all raw timings. Revision lookup is
explicitly warmed before `fixes` selected-kind queries; `actions` retains cold lookup.
Checks cover counts, selected flags, same-instance no-op follows, selection target,
placement, Undo/Redo depths and preservation of the source snapshot. CPU measurements
do not establish frame-time maxima, Windows latency, native layout or image decode cost.

For before/after comparison, build this same harness against the baseline and changed
project sources. Keep SDK/runtime, flags and machine load the same. No new NuGet packages.
