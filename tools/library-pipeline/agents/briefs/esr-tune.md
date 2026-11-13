## Task esr-tune — make the Mac upscale as fast as possible WITHOUT changing a single visible pixel

Inputs in your folder: `in/mac_worker.py` (the live worker: one GPU stage per 60-file job — prepare_rgb, then
`realesrgan-x4plus-anime` and `realesrgan-x4plus` with `-s 4 -f png`, then a pool of 6 CPU processes runs
`finish_asset.finish` + `verify` + block check), `in/finish_asset.py` (the recipe; read-only), `in/sample/` —
180 original reference files (a random mix of the remaining work: tiny previews, objects, big maps) + `in/sample.json`.
Machine: Apple M3 (4 performance + 4 efficiency cores, 10-core GPU), 24 GB shared with the owner's apps.

Do:
1. Baseline: run the exact live recipe on the sample in your folder (copy the worker logic into your own script; do not
   import from ~/MapWork/upscale). Measure wall time per stage (prepare, GPU anime, GPU x4plus, CPU finish) and files/h.
   Keep these outputs as the reference.
2. Try to go faster: realesrgan `-j load:proc:save` (e.g. 2:2:2, 4:4:4), `-t` tile size (0=auto vs 128/256/512),
   running the two models as two concurrent processes, overlapping GPU of job k+1 with CPU of job k (already done), CPU
   pool size 4/6/8, job size 60/120/240, WebP `method` is NOT allowed to change (it changes bytes; must stay method=4
   lossless). Any other safe idea you find.
3. Each variant must give visibly identical output to the baseline: for every file, on pixels with alpha > 8,
   mean |diff| < 0.3 and 99.9th percentile ≤ 2 (0..255), alpha identical; report max values. Variants that differ are
   rejected even if faster. Watch memory pressure (`memory_pressure`, `vm_stat`) — the owner uses this Mac.
4. Deliver `RESULT.md`: table of variants (files/h, stage times, max diff, peak memory) and ONE recommended
   configuration, plus `mac_worker.recommended.py` (a full copy of the worker with only the recommended changes) and
   `mac_worker.diff` against `in/mac_worker.py`. Expect the live worker to be running at the same time: repeat
   measurements and compare variants under the same conditions.
