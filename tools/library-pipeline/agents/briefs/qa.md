## Task qa — independent quality audit of finished upscales (PC and Mac nodes)

Owner rule: the upscaled picture must be the same drawing, only bigger. Inputs: `in/pairs/<n>/src.<ext>` (original) +
`in/pairs/<n>/up.webp` (finished ×2/×4 result), `in/pairs.json` ([{n, rel, node: pc|mac, factor}]), `in/finish_asset.py`
(the recipe and its `verify`; read-only), `in/audit_blocks.py` (8×8 block check).
Do (offline, CPU only):
1. For every pair: run `verify` and the block check; also your own checks: shrink `up` back to source size and compare
   (per-channel mean/p99.9 difference, alpha difference, hue shift), edge maps (lines kept, not thickened/thinned),
   halos around dark lines, flat areas that gained texture, text/numbers that changed.
2. LOOK at the worst 30 and 30 random pairs side by side (source scaled ×factor nearest | result; crops at 100 %), make
   `qa_sheet_*.jpg`. Classify each finding: none / slight (only visible at 400 %) / visible / broken.
3. Compare the PC and Mac nodes: same distribution or not.
Deliver `qa.json` (per pair metrics + class), `qa_report.md` (counts per node, the worst examples with sheet names, any
pattern by folder/size/transparency). Do not change any picture.
