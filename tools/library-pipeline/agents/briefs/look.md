## Task look — check every set's name, detail and tags against its actual pictures

Map shows each library set as a card: `name` (short common Russian noun for search: «Бочки»), `detail` (second line: what
exactly is drawn and how: «дубовые бочки с железными обручами · цветной рисунок · вид сверху»), `one` (singular), and
tags (`Раздел/…`, `Место/…`, `Сеттинг/…`, `Стиль/…`, `Вид/…`, material, season, state). Until now names and details came
from a vision pass shared by whole folders (1 590 sets reused one group description) and from file names — so many
cards may describe something that is not what the set's own pictures show.

Inputs: `in/sets.json` (your sets only; values are BEFORE the fixes in `in/fixes/` if present — treat sets.json as the
reference «old»), `in/thumbs/<n>.jpg` (≤384 px previews of 1–2 pictures per set; transparent parts on grey),
`in/thumbs.json` ([{thumb, key, rel}]), `in/look/*.jsonl` (the old shared vision pass, for context only).

Do (offline): LOOK at every thumbnail (open the images; make contact sheets of ~20 in tmp/ to go faster) and compare with
the set's card. Report only real mismatches:
- detail describes a different object/material/colour/count/view than drawn; name is not what is drawn; `one` wrong;
- `Вид/…` (top/iso/side) wrong; obvious setting mismatch (sci-fi object tagged fantasy etc.); a whole map tagged as object.
Never invent what is not visible; if a thumbnail is too small to judge, skip it (count skips). Do not rewrite good cards
for taste. Russian, dry, « · » separators, detail ≤ 120 chars.
Deliver `look_fixes.json`: [{"key","catalog","field":"name"|"detail"|"one","old","new","reason","thumb"}] and
`look_tag_fixes.json`: [{"key","catalog","remove":[],"add":[],"reason","thumb"}] (existing tag values only), and
`look_report.md`: sets checked / skipped / fixed per catalog, 30 examples with thumb paths. Validate: JSON parses, every key
exists, old matches sets.json, no two sets in a catalog end up with the same name+detail.
