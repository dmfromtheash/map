## Task previews — why tiny shop previews fail the check, and a recipe that passes without changing the drawing

8 825 of the 39 081 references are small shop previews (folder «_Магазин — мебель (превью)», typically 50–250 px,
thin line art: windows, walls, furniture). They are upscaled ×4. About a third fail `finish_asset.verify`
(colour error colourD ≥ 14 when the result is shrunk back to source size; alpha error alphaD < 12 is fine).

Inputs in your folder: `in/finish_asset.py` (recipe + verify; read-only), `in/failed/` — up to 80 failed sources (`<n><ext>`)
+ `in/failed.json` (rel + metrics of the rejected result; regenerate the outputs yourself with the recipe),
`in/passed/` — 30 passing previews for contrast + `in/passed.json`. Real-ESRGAN: `~/MapWork/esr/realesrgan-ncnn-vulkan -m ~/MapWork/esr/models`.

Do:
1. Diagnose: where does the colour error come from (thin dark lines thinned/brightened by the models, the grain step,
   the anime/x4plus blend, prepare_rgb fringe colour under alpha, JPEG ringing in sources)? Show per-component numbers
   (try: each model alone, blend weights, grain 0, factor 2 vs 4) on the failed set.
2. Propose the most conservative recipe change for small images (e.g. a size threshold below which a different
   weight/grain/factor/model is used) such that ≥ 95 % of the failed set passes verify AND the passing set still passes,
   and the drawing does not change (lines keep width/darkness; compare edge maps). Never lower verify thresholds.
3. Deliver `PREVIEWS.md` (findings with numbers + recommendation), `finish_small.py` (a copy of finish_asset.py with
   only the proposed change), contact sheets `sheet_*.jpg` (source ×4 nearest | current | proposed) for 12 examples.
