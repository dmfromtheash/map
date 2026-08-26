# План собственных диагностических ассетов

Дата: **26.08.2026**. Статус: **PROPOSED / ACCEPTED SCOPE**.

Это не starter pack и не художественные материалы. Два PNG будут созданы командой Map с нуля,
детерминированно, без внешних изображений и без сходства с чужими наборами. Для каждого файла
фиксируются генератор/исходник, SHA-256, dimensions, alpha, author=`Map project`, license=`CC0-1.0`
или собственное разрешение владельца перед распространением.

## 1. `diagnostic-stamp-quadrants.png`

- 512×512 RGBA, прозрачный фон, асимметричный silhouette;
- четыре насыщенных цветовых сектора, нейтральная серая полоса и белая/чёрная контрольные метки;
- отдельные участки alpha 25/50/75/100%, тонкий край и выступ, различимые при flip/rotation;
- тестирует scale/aspect, H/V flip, hue/saturation/brightness/contrast, opacity, shadow, blur,
  blend, sublayer, replace asset и embedded/linked roundtrip.

## 2. `diagnostic-brush-tip.png`

- 256×256 RGBA, асимметричный radial tip;
- hard core, soft alpha rings, один цветной directional marker и пустой внутренний сектор;
- тестирует size, opacity, softness, spacing, scatter, angle, random flip, pressure/jitter и seam
  behavior непрерывного stroke.

## Gate

- генерация повторяема и имеет source/provenance;
- PNG signature/IHDR/dimensions/alpha проверены автоматически;
- импорт, thumbnail, SHA-identity, place/paint, undo/redo, save/reopen и export проходят;
- golden сцены покрывают нейтральные и крайние значения каждого эффекта;
- сами fixtures не попадают в release starter pack без отдельного решения владельца.
