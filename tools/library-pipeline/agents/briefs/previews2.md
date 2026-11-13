## Task previews2 — why small shop previews fail, and candidate recipes for Claude's GPU measurement

Small shop previews («_Магазин — мебель (превью)», 50–250 px, thin line art) are upscaled ×4 by two Real-ESRGAN models
blended 60/40 (anime/x4plus) + grain restore (`in/finish_asset.py`, `in/mac_worker.py`). Many fail `verify` (colourD ≥ 14
after shrinking back). Owner rules: the drawing must stay exactly as drawn — only the resolution grows; verify thresholds
are NOT lowered; a previous idea «repeat pixels ×4 (nearest)» was REJECTED by the owner (staircase look up close).
In this sandbox there is no GPU — you cannot run Real-ESRGAN. Claude runs the GPU part.
Inputs: `in/failed.jsonl` (all rejected results so far: rel, src size, alphaD, colourD, blockMax, msg), `in/failed/<n>.<ext>`
+ `in/failed.json` (≤150 failing sources), `in/passed/<n>.<ext>` + `in/passed.json` (≤60 passing small previews of the
same folders), `in/PREVIEWS_r1.md` (round-1 findings).
Do (offline, CPU):
1. Statistics: failure rate by source size, folder, transparency share, line darkness/width, JPEG vs PNG, colour count.
   Which features predict failure? Is it the thin dark lines (models thin/brighten them), alpha fringes, or grain?
2. Design ≤ 6 candidate recipes that keep the model upscale (no nearest-neighbour repeat) and could pass verify: e.g.
   anime model only (weight 1.0), x4plus only, grain 0 for small images, ×2 model then ×2 Lanczos, model on a ×2
   pre-enlarged (Lanczos) source, edge-preserving blend (take the model result but restore line darkness from a Lanczos
   ×4 of the source where the source has dark thin lines). For each: exact steps, which images it applies to (size
   threshold), why it should keep the drawing.
3. Write `bench_small.py` for Claude to run OUTSIDE the sandbox with the GPU: inputs = the failed + passed folders here,
   it runs Real-ESRGAN (`~/MapWork/esr/realesrgan-ncnn-vulkan -m ~/MapWork/esr/models`, models realesrgan-x4plus-anime and
   realesrgan-x4plus, `-s 4`), applies each candidate, runs `finish_asset.verify` on each result, and writes
   `bench_small_results.json` + contact sheets «source ×4 nearest | current recipe | candidate» for 12 images. Make the
   CPU part of the script runnable now on the round-1 outputs if any exist, and test everything except the GPU calls.
Deliver `PREVIEWS2.md` (statistics with numbers, candidates, what Claude must run), `bench_small.py`, `SOL_REPORT.md`.
