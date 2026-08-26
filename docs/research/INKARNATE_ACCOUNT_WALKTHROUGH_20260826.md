# Account walkthrough Inkarnate: текст, линии, контуры, кисти, штампы и клавиатура

Дата: **26.08.2026**.

Статус: **FACT / PARTIAL / CLEAN-ROOM**.

## Граница

Владелец явно разрешил пройти его уже открытую тестовую карту в Inkarnate. Проверялось только
наблюдаемое пользовательское поведение через обычные controls и жесты. Не открывались DevTools,
network/storage, внутренние модели или исходный код страницы; не выгружались ассеты, названия
наборов, стили интерфейса или графика. Все добавленные объекты остались только на тестовой карте.

Снимки хранятся в игнорируемом `artifacts/inkarnate-mechanics-20260826/` как исследовательское
доказательство и не являются ресурсами продукта.

## FACT — клавиатура

### Глобально

- колесо — zoom; средняя кнопка или Space — pan;
- `K` или `?` — справка; `/` — правая панель;
- `Ctrl+Z` / `Ctrl+Shift+Z` — undo/redo; `G` — toggle snap;
- `Ctrl+Shift+Up/Down` — активный слой; `Shift+Up/Down` — значение свойства;
- `Tab` — предыдущий инструмент;
- `V` Select, `M` Mask, `B` Brush, `O` Stamp, `R` Shape, `L` Line, `T` Text, `N` Notes;
- удержание `Shift` увеличивает шаг.

### Select

- `Ctrl+A`, `` ` ``/`~`, `Esc`, `Delete`/`Backspace` — select all, restore selection,
  deselect, delete;
- `Ctrl+C`, `Ctrl+G`, `Ctrl+Shift+G`, `Ctrl+L` — copy, group, ungroup, lock;
- стрелки — move; `W/S` или `Shift+wheel` — size;
- `>/<` или `Ctrl+wheel` — rotate; `Ctrl+Alt+wheel` — fine rotate;
- `Ctrl+Up/Down` — sublayer; `Ctrl+Shift+Up/Down` — layer;
- `Shift+drag` ручки поворота — snap rotation; `Ctrl+click` — добавить/убрать из selection.

### Контекстные

- Stamp: `F` catalog, `W/S` size, `E/D` opacity, `Q/A` area,
  `Alt+Q/Alt+A` density, `Alt+Right/Left` или `Alt+wheel` — следующий/предыдущий asset set;
- Text: `W/S` font size, `]/[` font, `Q/A` curvature;
- Mask: `X` add/subtract, `W/S` size;
- Brush: `F` catalog, `W/S` size, `E/D` opacity, `Q/A` softness;
- Notes: `E` edit selected.

## FACT — текст

- font, bold, size, rotation, sublayer, fill;
- outline с отдельными цветом и толщиной; shadow с вариантом;
- curve, letter spacing, line height и left/center/right alignment;
- общие opacity, blend и blur;
- при curve glyphs раскладываются и поворачиваются по дуге; outline и shadow следуют каждому
  glyph;
- введённое spacing было ограничено UI допустимым максимумом;
- text на sublayer `3` оказался выше line на sublayer `0`.

`PARTIAL`: multiline заявлен публичным официальным обзором и спецификацией, но в этом проходе
повторно не подтверждён наблюдаемым результатом, поэтому не используется как новое доказательство.

## FACT — дороги и линии

- отдельный legacy Path отсутствует; дороги, границы, стены и маршруты выражаются Line;
- Free Draw создаёт редактируемый path и упрощает жест до ограниченного набора nodes;
- Pen строит straight/curved segments;
- Edit Nodes: выбрать/перетащить node, добавить node кликом по path, Cut Path;
- Edit Portals: неразрушаемые разрывы применимы к Wall/Room path, portal можно добавить и двигать;
- stroke имеет catalog/color/pattern, width, solid/dotted/dashed, sublayer, opacity, blend, blur,
  guides.

Наблюдаемая линия после свободного жеста имела около семи редактируемых узлов. Это требование к
результату simplification, а не основание копировать конкретный алгоритм или threshold.

## FACT — контуры и Shape

- Free Draw, Pen, Brush, Rectangle, Ellipse, Polygon;
- Add/Subtract/Preview и Merge Shapes;
- stroke и fill настраиваются отдельно; общие sublayer/opacity/blend/blur;
- Merge Add двух пересекающихся прямоугольников оставил один composite object;
- Subtract оставил тот же object и создал внутренний hole; contour корректно обошёл внешний и
  внутренний paths.

Для независимой реализации рассматривается собственная геометрия или отдельное решение о
пермиссивной библиотеке. Публичный Clipper2 подтверждает наличие C# clipping/offsetting/
triangulation под BSL-1.0, но зависимость не добавляется без отдельного решения.

## FACT — штамп как кисть и наложение

- Place: One / Many / Auto; Auto Select: On / Off / Auto;
- Many с drag размещает несколько независимых объектов вдоль жеста;
- Area задаёт радиус рассеивания, Density — частоту/количество;
- Q изменил Area с `0` до `5`, Alt+A — Density со `100` до `95`, то есть обычный шаг равен `5`;
- Randomize Angle: Off, 15°, 30°, 45°, 60°, 90°, 180°; Random Flip отдельный флаг;
- выбранный stamp: replace, scale, rotation, sublayer, оба flip, opacity, shadow, blend, blur;
- Color Tweaks: Hue, Saturation, Brightness, Contrast;
- одинаковый sublayer сохраняет список независимых объектов; больший sublayer рисуется выше.

## FACT — Brush, Mask/Land и Grid

- Brush: круглая кисть с size/opacity/softness, texture scale/rotation; edge brush меняет
  softness на roughness/smooth; grid brush использует текущую Grid;
- Rectangle/Ellipse/Polygon — отдельные brush shapes;
- advanced brush: pen dynamics, size range/jitter, opacity/jitter, softness, spacing, scatter;
- Land: Add/Subtract, size, roughness, smooth, Fill/Clear и тот же набор геометрических modes;
- Grid имеет четыре видимых geometry: Isometric Squares, Squares, Hexagons, Vertical Hexagons.

Критическая поправка: `Isometric Squares` — отдельная косоугольная ромбическая топология
примерно с линиями `±30°`, а не square, повернутая на `45°`. Для неё нужны собственный render,
snap и fixtures; это принято DEC-011.

## PARTIAL / не объявлено готовым

- multi-selection, group/ungroup и Ctrl+click не проходились на достаточном наборе объектов;
- наследование последних text settings и переключение licensed fonts не подтверждены в этом
  проходе;
- точная функция Auto placement, pen pressure curves и wall/room portal width требуют отдельного
  bounded walkthrough;
- закрытые алгоритмы sampling, smoothing, path boolean и scatter seed неизвестны и не нужны для
  clean-room паритета результата.

## Публичная сверка

- [официальный Line & Shape Tool Overview](https://www.youtube.com/watch?v=GzeGhwbpbqw);
- [официальный Stamp Tool Overview](https://www.youtube.com/watch?v=tfzd8Tisq3E);
- [Clipper2: polygon clipping/offsetting/triangulation для C#, C++ и Delphi](https://github.com/AngusJohnson/Clipper2);
- [Azgaar Fantasy Map Generator](https://github.com/Azgaar/Fantasy-Map-Generator) просмотрен только
  как публичный пример разделения world state, interactive editing и renderer. Его web-архитектура,
  код, UI и assets не переносятся в нативный Map.

## Следующие проверяемые срезы

1. Завершить input parity: multi-selection, group/ungroup, stamp set/area/density и font catalog.
2. Реализовать отдельную `IsometricSquares` topology с render/snap/golden fixtures.
3. Довести прямые inspectors Text/Line/Shape/Stamp/Brush по наблюдаемым значениям.
4. Создать два собственных диагностических ассета из `TEST_ASSET_FIXTURES.md` и прогнать ими
   transform, color tweaks, alpha, shadow, blur, blend, scatter и brush dynamics.
