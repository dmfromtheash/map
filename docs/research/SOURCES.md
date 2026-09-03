# Источники и проверка

Дата актуальной проверки: **31.08.2026**.

При конфликте источников приоритет: более свежая официальная документация/updates → официальный tutorial → наблюдаемое ручное поведение → вторичный обзор. Субтитры — вспомогательный источник и не доказывают неясную деталь сами по себе.

## Локальные источники владельца

| Источник | Использование |
|---|---|
| `C:\Users\Leshe\Downloads\AI Playbook v2.0.pdf` | процесс постановки, PRD, итерации, проверки, handoff; конкретные web/cloud советы отфильтрованы |
| `C:\Users\Leshe\Desktop\ИИ инструменты\AI-инструменты_из_Saved_Messages_06-08-2026.xlsx` | полный список инструментов, каждый пункт переоценён под Map |
| `C:\Projects\FreelancePortfolio\AGENTS.md` | общие правила малых проверяемых изменений |
| `C:\Projects\FreelancePortfolio\chatgpt\PROJECT_INSTRUCTIONS.md` | разделение ролей owner/lead/Opus |
| `C:\Projects\FreelancePortfolio\docs\project\07_OPUS5_RUNBOOK.md` | структура ограниченного задания и handoff |
| `C:\Projects\FreelancePortfolio\docs\project\05_DEVELOPMENT_ROADMAP.md` | stage gates; web stack не переносился |
| `C:\Projects\FreelancePortfolio\docs\DECISIONS.md` | append-only журнал решений |

## Inkarnate — первичные источники

| Источник | Что подтверждает |
|---|---|
| [Homepage](https://inkarnate.com/) | публичное позиционирование, типы карт, asset library, line/shape, clone, планы |
| [FAQ](https://inkarnate.com/faq) | World/Region, City/Village, Battlemap/Interior, Scene/Isometric; историческое online requirement |
| [Updates](https://inkarnate.com/updates/) | текущие изменения dashboard/editor/Windows app и даты; приоритет над старым video |
| [Terms](https://inkarnate.com/terms) | договорные ограничения исследования и права на materials/assets |
| [Privacy](https://inkarnate.com/privacy) | сетевой/account контекст конкурента; не переносится в offline product |
| [2.0 Editor Overview, 2:35:49](https://www.youtube.com/watch?v=QifZDEvGvBo) | полный UI/tool/layer/history/catalog walkthrough и Q&A |
| [Text Tool Overview](https://www.youtube.com/watch?v=ErwB0XeMJe4) | text properties и inheritance |
| [Line & Shape Tool Overview](https://www.youtube.com/watch?v=GzeGhwbpbqw) | nodes, portals, fill/stroke/boolean behavior |
| [Stamp Tool Overview](https://www.youtube.com/watch?v=tfzd8Tisq3E) | catalog, transform, random/scatter, shadows |
| [Layers Overview](https://www.youtube.com/watch?v=Rf5bVBKFnZ0) | layer types, object tree, masks/effects |
| [Brush Tool Overview](https://www.youtube.com/watch?v=IJl_R3E4tHo) | brush presets/dynamics/material/shape |
| [Land Tool Overview](https://www.youtube.com/watch?v=fo0v-4jSwcU) | add/subtract, shapes, move area, land style |

Фокусный повторный проход 26.08.2026 сопоставил актуальный публичный `Stamp Tool Overview`,
предоставленный владельцем снимок выбранного stamp и текущую реализацию Map. Результат:
`OBJECT_AND_GRID_PARITY.md`.

Bounded account walkthrough тестовой карты владельца 26.08.2026 проверил через видимые controls
keyboard profile, Text, Line/roads/portals, Shape boolean/contours, Brush/Mask/Land, stamp
Many/Area/Density/Auto, multi-selection/group, каталог и variant sets, а также четыре Grid
geometry. DevTools/network/storage и извлечение ассетов не использовались. Результаты:
`INKARNATE_ACCOUNT_WALKTHROUGH_20260826.md` и `ASSET_CATALOG_AND_VARIANT_SETS.md`.

Владелец 31.08.2026 отдельно разрешил bounded performance/size walkthrough своей тестовой карты.
Через обычный create/editor/export UI подтверждены editor choices 1K/2K/3K/4K, custom grid
`2..256` по каждой стороне и export choices до 16K; export не запускался, карта не изменялась.
Официальный [FAQ](https://inkarnate.com/faq) независимо подтверждает 4K editor для Creator/Studio,
до 8K export для Creator и до 16K export для Studio. Результат и границы утверждений:
`INKARNATE_CANVAS_PERFORMANCE_20260831.md`.

## Публичные open-source ориентиры clean-room прохода

| Источник | Что использовано / не использовано |
|---|---|
| [Clipper2](https://github.com/AngusJohnson/Clipper2) | Подтверждает публичную C# библиотеку clipping/offsetting/triangulation и BSL-1.0; пакет не добавлен, нужен отдельный dependency decision |
| [Azgaar Fantasy Map Generator](https://github.com/Azgaar/Fantasy-Map-Generator) | Только публичный архитектурный ориентир separation state/editors/renderers; web stack, код, UI и assets не переносятся |
| [Tiled — Editing Tilesets](https://github.com/mapeditor/tiled/blob/master/docs/manual/editing-tilesets.rst) | Публичное подтверждение image-collection sets, stable per-item metadata/alignment/probability; код, UI, formats и assets не переносятся |
| [Tiled — Tileset implementation](https://github.com/mapeditor/tiled/blob/master/src/libtiled/tileset.cpp) | Публичное подтверждение разделения remove-from-set и delete-content; используется только как data-model cross-check, dependency не добавлена |

### Performance/canvas ориентиры Stage 13

| Источник | Лицензия и допустимое использование |
|---|---|
| [Avalonia `VirtualizingStackPanel`](https://github.com/AvaloniaUI/Avalonia/blob/main/src/Avalonia.Controls/VirtualizingStackPanel.cs) и [tests](https://github.com/AvaloniaUI/Avalonia/blob/main/tests/Avalonia.Controls.UnitTests/VirtualizingStackPanelTests.cs) | MIT; встроенная в текущий UI stack виртуализация списка, сначала использовать её без новой production dependency |
| [SkiaSharp](https://github.com/mono/SkiaSharp) и [`SKPictureRecorder`](https://learn.microsoft.com/en-us/dotnet/api/skiasharp.skpicturerecorder) | MIT; существующая dependency, изучить bounded display-list/chunk cache и R-tree recording, не менять renderer без benchmark |
| [`SKCanvas.DrawAtlas`](https://learn.microsoft.com/en-us/dotnet/api/skiasharp.skcanvas.drawatlas) | API существующей SkiaSharp dependency; кандидат для одного batched draw повторяющихся stamps |
| [Mapsui](https://github.com/Mapsui/Mapsui) | MIT; reference probe для Avalonia/Skia viewport culling, vector/raster cache и benchmark patterns, не переносить библиотеку целиком |
| [NetTopologySuite](https://github.com/NetTopologySuite/NetTopologySuite) | BSD-3-Clause с JTS notices; isolated STRtree/quadtree comparison против собственного uniform grid, dependency только после отдельного измеренного решения |
| [BenchmarkDotNet](https://github.com/dotnet/BenchmarkDotNet) | MIT; dev-only runner с warmup, process isolation, memory diagnoser и JSON, не входит в production solution |
| [Godot MultiMesh](https://github.com/godotengine/godot-docs/blob/master/classes/class_multimesh.rst) и [2D batching architecture](https://github.com/godotengine/godot-docs/blob/master/engine_details/architecture/internal_rendering_architecture.rst) | MIT; concept-only cross-check batching/LOD для тысяч одинаковых объектов, Godot не становится dependency Map |

Pinned commits и sparse paths для изолированного research lab записаны в `external-sources.json`.
Код из внешнего проекта нельзя переносить только потому, что он найден LLM или поиском: сначала
проверяются upstream URL, точный commit, license/notice, применимость к текущей версии и собственный
адресный benchmark. Необнаруженный первичный источник означает `REJECTED` для копирования.

### Известная временная оговорка

Главный video опубликован 10.12.2025. Update от 12.01.2026 о retiring Path и более поздние изменения dashboard/Windows app считаются актуальнее. Неясные Q&A ведущего помечены как открытые, потому что в нескольких местах он сам исправлял ответ.

## Предлагаемый native stack — первичные источники

### Avalonia

- [Avalonia 12 release](https://avaloniaui.net/blog/avalonia-12/)
- [Avalonia 12.1 release](https://avaloniaui.net/blog/release-12-1)
- [Windows platform guide](https://docs.avaloniaui.net/docs/platform-specific-guides/windows)
- [Custom rendering](https://docs.avaloniaui.net/docs/graphics-animation/custom-rendering)
- [Licensing/free tools](https://docs.avaloniaui.net/tools)

### .NET

- [.NET 10 download](https://dotnet.microsoft.com/en-us/download)
- [Single-file deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)

### Сравнивавшиеся альтернативы

- [Qt Graphics View](https://doc.qt.io/qt-6/graphicsview.html)
- [Qt licensing](https://doc.qt.io/qt-6/licensing.html)
- [Qt LGPL obligations](https://www.qt.io/development/open-source-lgpl-obligations)
- [Godot 2D introduction](https://docs.godotengine.org/en/latest/tutorials/2d/introduction_to_2d.html)
- [Godot Windows/project export](https://docs.godotengine.org/en/stable/tutorials/export/exporting_projects.html)
