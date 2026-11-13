## Task taxonomy, round 2 — close what round 1 left PARTIAL / NOT CHECKED

Your round-1 results are in this folder (`taxonomy_report.md`, `audit_summary.json`, `split_suggestions.json`,
`SOL_REPORT.md`). Inputs unchanged: `in/sets.json`, `in/look/*.jsonl`, `in/TAXONOMY.md`.

Do (offline):
1. The 39 617 distinct paths in sets.json vs 39 081 pictures + 537 textures in the project description: explain the
   difference exactly (count per catalog/kind; are textures counted in both? duplicates across sets?). List any path
   that appears in two sets.
2. The 53 groups of ready plans/rooms (whole maps or room tiles): for each, decide from name/detail/look/files whether it
   is a whole map (`use` map-like, the owner uses it as a battle map) or a placeable object/tile, and propose the tag/
   `use`/collection fix with the vocabulary that already exists (no new prefixes; if a needed value does not exist, list
   it as a proposal only, not as a fix).
3. The 15 wagon/train groups without a place: propose a place from the existing `Место/…` values or list as proposal.
4. The two giant sets (345 and 435 files): check `split_suggestions.json` against the file names once more and give the
   final split (each part ≥ 5 files, a Russian name and detail per part).
5. Deliver `tag_fixes_r2.json` (same format as `tag_fixes.json`), `split_r2.json`, `taxonomy_r2.md`. Validate as before.
