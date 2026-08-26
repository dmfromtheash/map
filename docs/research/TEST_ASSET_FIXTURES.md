# План собственных диагностических ассетов

Дата: **26.08.2026**. Статус: **PROPOSED / ACCEPTED SCOPE**.

Это не starter pack и не художественные материалы. Два **логических fixture** будут созданы
командой Map с нуля, детерминированно, без внешних изображений и без сходства с чужими наборами.
Первый fixture является набором из 12 PNG variants, второй — одним PNG brush tip. Для каждого
файла фиксируются generator/source, SHA-256, dimensions, alpha, author=`Map project`,
license=`CC0-1.0` или собственное разрешение владельца перед распространением.

## 1. `diagnostic-stamp-set`

- 12 PNG variants, каждый 512×512 RGBA с прозрачным фоном и общей узнаваемой, но
  асимметричной diagnostic silhouette;
- вариант `01` сохраняет четыре насыщенных цветовых сектора, нейтральную серую полосу,
  белую/чёрную метки и alpha 25/50/75/100%; остальные варианты детерминированно меняют marker,
  aspect/pivot-safe silhouette, цветовые сектора и alpha edge, оставаясь явно тестовыми;
- manifest задаёт stable set/variant IDs, порядок 01–12, cover `01`, tags/provenance и SHA;
- тестирует scale/aspect, H/V flip, hue/saturation/brightness/contrast, opacity, shadow, blur,
  blend, sublayer, replace asset, variant badge/expand/cycle/random, multi-SHA scatter и
  embedded/linked roundtrip.

## 2. `diagnostic-brush-tip.png`

- 256×256 RGBA, асимметричный radial tip;
- hard core, soft alpha rings, один цветной directional marker и пустой внутренний сектор;
- тестирует size, opacity, softness, spacing, scatter, angle, random flip, pressure/jitter и seam
  behavior непрерывного stroke.

## Gate

- генерация повторяема и имеет source/provenance;
- PNG signature/IHDR/dimensions/alpha и 13 уникальных SHA проверены автоматически;
- каталог импортирует stamp fixture как один set из 12 variants, а brush tip — как singleton set;
- импорт, thumbnail, SHA-identity, place/paint, undo/redo, save/reopen и export проходят;
- golden сцены покрывают нейтральные и крайние значения каждого эффекта;
- сами fixtures не попадают в release starter pack без отдельного решения владельца.
