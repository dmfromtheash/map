## Task names, round 2 — settle the 21 groups of sets that still look identical in the catalog

Your round 1 (files in this folder: `collision_review.json`, `name_fixes.json`, `name_report.md`, `SOL_REPORT.md`) left
21 groups / 55 sets whose name + detail are still the same, because you had no pictures. Now you have them:
`in/pics2/<n>.<ext>` with `in/pics2.json` ([{file, key, rel}] — which set each picture belongs to). Open and LOOK at
the pictures (image viewing is allowed; read them with Python/Pillow, make contact sheets in tmp/ if it helps).

Do (offline):
1. For every still-colliding group: describe from the pictures what visibly differs between the sets (shape, material,
   colour, count, state, view) and propose a distinguishing `detail` (and `name`/`one` only if clearly wrong). Never
   invent what is not drawn. If two sets are truly the same pictures (same images, different sizes/packs), say
   «дубликат» and propose which key to keep — do not delete anything.
2. Deliver `name_fixes_r2.json` in the same format as `name_fixes.json` (old = the value after round-1 fixes), and
   `collision_r2.md` (per group: keys, what you saw, the fix, a contact-sheet file name).
3. Validate as in round 1 (keys exist, old values match, no new collisions, detail ≤ 120 chars, Russian, « · »).
