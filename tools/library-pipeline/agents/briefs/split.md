## Task split — finish the split of the two giant sets so it can be applied

Round 2 (taxonomy agent) proposed `in/split_r2.json`: the clothing set (345 files) → 17 parts, «Modern Rooms 2» (435 files)
→ 19 parts. The file distribution is verified correct (every file exactly once, ≥5 per part) — keep it exactly.
An independent check found what must be rewritten before applying:
- part `detail`: English file names («87 файлов Hanger», «Colorable»), service notes («Индивидуальная обстановка не
  проверена»), no « · »; four «Планы комнат» parts are 125–131 chars;
- part `tags` were copied from the parent: shoes/watches/hats carry «рубашка», «Цвет/синий», «Место/Спальня»; walls and
  ready plans carry «пустая», «пол»;
- «Планы комнат» parts stay in the collection «Люки и решётки».
Inputs: `in/split_r2.json`, `in/sets.json` (current plan, all 9 094 sets — vocabulary of existing tags/collections),
`in/look/` not needed. Names of the parts can stay unless wrong.
Do (offline): for every part write a Russian `detail` (what is drawn and how, « · », ≤120 chars, no file names, no
service notes), the right tags (only values that already exist in sets.json; keep the parent's catalog-level tags like
`Тестовое`, `Набор/…`, `Стиль/…`, `Масштаб/…`; remove tags that do not fit the part), the right `collection` (existing
values only; if no fitting one exists, keep the parent's and list a proposal). Deliver `split_final.json` (same structure
as split_r2.json + per part name/one/detail/tags/collection) and `split_report.md`. Validate: files unchanged vs
split_r2.json, every tag value exists in sets.json, every part keeps a section and a place.
