## Task textures, round 2 — look at the 82 «not seamless» textures

Round 1 (`not_seamless.json`, `textures_report.md`, `SOL_REPORT.md` in this folder) flagged 82 files by metrics only.
Now you have the pictures: `in/tex2/<n>.<ext>` with `in/tex2.json` ([{file, sha256, originalPath, verdict}]).

Do (offline, pictures are read-only):
1. For each file make a 2×2 tiled preview (the picture repeated as a fill would repeat it, at a size that shows the joins)
   in tmp/ and LOOK at it. Decide: «шов не виден» / «шов виден слегка» / «шов явный» / «это не заливка (наложение,
   полоса, пятно)». Note where the seam is (vertical / horizontal / both) and what makes it (light gradient, cut object,
   colour jump).
2. Deliver `seams_r2.json` ([{sha256, originalPath, verdict_metric, verdict_visual, axis, why}]), contact sheets
   `seams_sheet_*.jpg` (12 previews per sheet, labelled with the index), and `seams_r2.md` with counts and which sets
   should say «с видимым швом» in their detail (propose the detail text; do not change any picture).
3. Validate: every sha256 in tex2.json is covered exactly once; JSON parses.
