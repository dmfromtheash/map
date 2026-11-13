## Task seams2 — correct the seam verdicts and turn them into per-file marks

Round 2 (`in/seams_r2.json`, `in/seams_r2.md`) judged 82 texture files. An independent check (looked at 13 + a numeric
jump measure on all 82) found: overstated «шов явный» → really «слегка» for indices 29 `Dirt_9974153`, 72 `Blue
Ice_105448`, 76 `Deep Blue Ice_105446`, 77 `Deep Turquoise Ice_105447` (the «square blocks» are the picture's own pattern,
not the join); overstated «слегка» → really «не виден» for 73 `Blue Ice_105453`, 78 `Rock Garden_978656`; 66 `Wooden Floor
Ruined`: vertical seam yes, horizontal no. No missed seams were found. The proposed set `detail` texts are not usable
(10 over 120 chars, 12 contain English file names) — and a seam is a property of one FILE, not of the whole set.
Inputs: `in/tex2/<n>.<ext>` + `in/tex2.json`, `in/seams_r2.json`, `in/seams_r2.md`.
Do (offline, pictures read-only): re-look at ALL 82 with 2×2 tiles (also at 50 % so the pattern repeat is not mistaken for
a seam) and decide again, applying the corrections above unless you see clear evidence otherwise (then explain with a
crop). Deliver `seams_final.json` ([{sha256, originalPath, seam: "нет"|"слегка"|"явный"|"не заливка", axis, why}]) and a
short Russian per-file note text for «явный» only (≤60 chars, e.g. «на стыке виден шов по вертикали»), `seams_final.md`
with counts and a contact sheet per class. Validate: 82 entries, each sha once.
