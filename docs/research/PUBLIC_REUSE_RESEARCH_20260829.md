# Публичные проекты и повторное использование — 29.08.2026

## RESULT

`FACT`: создана изолированная лаборатория `C:\Projects\Map-ResearchLab` с 15 публичными
репозиториями, закреплёнными по exact commit. Ни один источник не подключён к production Map и ни
один чужой asset/brand/trade dress не скопирован. Исследование сокращает неопределённость Stage
10E/10F/11, но не заменяет собственную реализацию, тесты и native acceptance.

`BLOCKED`: у Codex нет интерфейса к частной «базе данных GPT», training corpus или скрытому
хранилищу чужого кода. Получить оттуда фрагменты или целые этапы невозможно и недопустимо выдавать
их за проверенный источник. Проверялись только текущий локальный репозиторий, публичные страницы,
официальные Git-репозитории и их лицензии.

## Что уже есть в Map

- Stages 1–8: нативное offline-ядро, tiled rendering, собственная domain/command architecture,
  assets, Land/Line/Shape/Text/Grid/Filters и Editor UX.
- Stage 9: 9A autosave/recovery доказан; 9B checkpoints, 9C resize/crop/region, 9D расширенный
  export/cancellation и 9E migrations/corrupt diagnostics остаются отдельными срезами.
- Stage 10B: Library v2, logical variant sets и deterministic scatter реализованы; остались
  keyboard/DPI/file-picker/clean-context UX gaps.
- Stage 10C: production multi-selection/groups принят; часть настоящего keyboard/pointer gate
  остаётся `PARTIAL`.
- Stage 10D: production/security self-acceptance существует; узкий deterministic reparse fix
  находится в commit `8de17de27546783bdd6247cb4df094d9df505711`. Bounded independent Gate D
  диапазона `2af5bd3495851f032897435fc957f5705a692bbe..8de17de27546783bdd6247cb4df094d9df505711`
  принят отдельно: `1/1` exact regression и `209/209` Map.Presentation.Tests.
- Stage 10E: отдельная `IsometricSquares`, property sheet, snap и golden fixtures — следующий
  функциональный срез.
- Stage 10F: licensed font catalog/navigation и редактируемые bindings.
- Stage 11: performance, packaging, clean-VM, installer/update/rollback и release candidate.

## Решения по публичным репозиториям

Все SHA также хранятся в `external-sources.json`; ссылка ведёт на официальный upstream.

| Проект | Exact commit | Лицензия | Полезное для Map | Решение |
|---|---|---|---|---|
| [Clipper2](https://github.com/AngusJohnson/Clipper2) | `f9c5eb6e14a59f6f5d65fbfb3564519a561cf4fd` | BSL-1.0 | C# boolean/offset/clip для настоящей polygon-геометрии | `DEFER`: prototype только если будущий stage потребует materialized union/difference; текущая non-destructive Shape composition не требует dependency. Upstream сам предупреждает о проблемах triangulation — эту часть не использовать |
| [NetTopologySuite](https://github.com/NetTopologySuite/NetTopologySuite) | `e064b57bc4968544603a0f4a1143b0cdf2207912` | BSD-3-Clause + notices JTS | STRtree, robust geometry, simplification | `NOT NOW`: слишком широкий GIS graph; у Map уже есть собственный spatial index и документная модель |
| [Mapsui](https://github.com/Mapsui/Mapsui) | `7af71dbf2730322416b46b8fb58524a57f5356be` | MIT | Avalonia/Skia viewport, widgets, map-control tests | `REFERENCE/PROBE`: сравнить viewport/grid performance; не заменять редактор готовым map control |
| [Avalonia.Samples](https://github.com/AvaloniaUI/Avalonia.Samples) | `27204ddbd36f7d99c3ba8f20a3b0cdb84ffe7775` | MIT | Headless XUnit, input/focus/control patterns | `ADMIT TEST IDEAS`: использовать официальные API/patterns для закрытия native/headless gaps, без vendoring sample tree |
| [SkiaSharp](https://github.com/mono/SkiaSharp) | `f74bd9adb82a77fd4a423db03c1077ea9d3be7e7` | MIT | уже используемый renderer; HarfBuzzSharp и text shaping | `ADMIT EXISTING` для Skia; HarfBuzz — только отдельный Stage 10F probe при доказанном shaping gap |
| [Tiled](https://github.com/mapeditor/tiled) | `457ecbb4da480b5c6cd153bd87e8c758c9453e13` | GPL-2.0 и mixed notices | grid/layer/undo/editor semantics | `REFERENCE ONLY`: лицензия и Qt/C++ architecture исключают перенос кода в Map |
| [Azgaar Fantasy Map Generator](https://github.com/Azgaar/Fantasy-Map-Generator) | `4eb71cfb89bebf987174623f9ccc0485bf328819` | MIT | генеративный pipeline, layers/export UX | `CONCEPT ONLY`: web/TypeScript architecture не переносится в native Map; допустимы только публично наблюдаемые общие идеи |
| [MapTool](https://github.com/RPTools/maptool) | `92370a3d359cce69cdadc8cb916cfcaa2952b618` | AGPL-3.0 | mature square/hex/isometric grid and snap cases | `ORACLE/REFERENCE ONLY`: для Stage 10E выписать свойства и контрпримеры своими словами; Java-код не копировать |
| [Pinta](https://github.com/PintaProject/Pinta) | `42aa68ac8d13e6190c575cabe19ba848ec043160` | исходный код MIT, assets/notices mixed | history items, canvas grid, brush UI | `REFERENCE ONLY`: GTK/mutable bitmap mismatch; отдельно проверять лицензию каждого asset, ничего не переносить автоматически |
| [PixiEditor](https://github.com/PixiEditor/PixiEditor) | `e6b2d3f6f8a4ab8740b000e61734f9d550ccc2bc` | LGPL-3.0 | action boundaries, updateable undo operations | `REFERENCE ONLY`: Map уже имеет `IEditCommand`; direct code reuse не оправдан |
| [LDtk](https://github.com/deepnight/ldtk) | `6d69bd1d6be92f01ac30778f6a934f0da8448b16` | MIT | level/layer/tileset data UX | `CONCEPT ONLY`: Electron/Haxe и tile-level editor отличаются от Map architecture |
| [Velopack](https://github.com/velopack/velopack) | `214f8be7384201826ba03d4bad8f0f646d727613` | MIT | C#/Avalonia installer/update, local source, self-hosting | `PROPOSED STAGE 11`: лучший updater-candidate; не добавлять до отдельного supply-chain/offline/downgrade/clean-VM решения |
| [Nerdbank.GitVersioning](https://github.com/dotnet/Nerdbank.GitVersioning) | `5083947fec3a668267a7c851e2ccab7ee6f42af3` | MIT | Git-derived .NET versions | `DEFER`: у проекта пока нет remote/tag release history; central MSBuild version достаточно и не добавляет build dependency |
| [MinVer](https://github.com/adamralph/minver) | `8bbc4fb0230bedfa6f7678749c7b5fe977d240eb` | Apache-2.0 | SemVer из Git tags | `DEFER`: повторно сравнить с NBGV при создании принятого tag/release flow |
| [SVG.NET](https://github.com/vvvv/SVG) | `e74a8b0c9a1c52e02a3fe1580bde52453fd96a94` | MS-PL | SVG parsing/rendering | `REJECT NOW`: System.Drawing-oriented graph и лицензия не дают преимущества над текущим Skia/Avalonia pipeline |

## Конкретные файлы/API для будущих probes

Это указатели для чтения внутри ResearchLab, не разрешение копировать реализацию:

| Задача | Public reference files | Что извлечь своими словами |
|---|---|---|
| polygon boolean/offset | `clipper2/CSharp/Clipper2Lib/Clipper.cs`, `Clipper.RectClip.cs` | API semantics `Intersect/Union/Difference/Xor/InflatePaths`, precision и degenerate cases |
| geometry/spatial baseline | `nettopologysuite/src/NetTopologySuite/Geometries/Geometry.cs`, `Index/Strtree/STRtree.cs`, `Simplify/DouglasPeuckerSimplifier.cs` | корректностный/производительный baseline для собственного probe, не новая default architecture |
| Avalonia viewport | `mapsui/Mapsui/Extensions/ViewportExtensions.cs`, `Mapsui/Limiting/ViewportLimiter.cs`, `Tests/Mapsui.Tests/Layers/GridLayerTests.cs` | boundary tests и performance comparisons с уже существующим viewport Map |
| Avalonia testing | `avalonia-samples/src/Avalonia.Samples/Testing/TestableApp.Headless.XUnit/TestAppBuilder.cs` и `CalculatorTests.cs` | официальный headless bootstrap и input/control assertions |
| text shaping | `skiasharp/tests/Tests/SkiaSharp/SKShaperTest.cs`, `SKShaperGlyphExtractionTests.cs`, `binding/HarfBuzzSharp/` | failing fixtures для русского/multiscript/curved text и glyph extraction |
| isometric/grid oracle | `maptool/src/main/java/net/rptools/maptool/model/IsometricGrid.java`, `SquareGrid.java`, `HexGrid*.java` и CSV facing fixtures | только свойства roundtrip/boundary/facing; AGPL-код/fixtures не переносить |
| brush/history UX | `pinta/Pinta.Tools/Tools/BaseBrushTool.cs`, `PaintBrushTool.cs`, `Pinta.Core/HistoryItems/` | gesture/history boundary ideas; не переносить GTK/mutable-bitmap implementation |
| document changes | `pixieditor/src/PixiEditor.ChangeableDocument/DocumentChangeTracker.cs` и `Changes/` | сравнить transaction/updateable-change semantics с `IEditCommand`; LGPL implementation не копировать |
| updater | `velopack/samples/CSharpAvalonia/`, `src/lib-csharp/VelopackApp.cs`, `UpdateManager.cs`, `Sources/SimpleFileSource.cs` | disposable Stage 11 install/update/offline/rollback harness |
| Git versioning | `nerdbank-gitversioning/version.json`, `minver/README.md` | сравнить tag-derived policies после появления remote/tag flow |

Потенциальные NuGet IDs — `Clipper2`, `NetTopologySuite`, `Mapsui.Avalonia12`,
`SkiaSharp.HarfBuzz`, `Velopack`, `Nerdbank.GitVersioning`, `MinVer`. **Ни один не добавлен этой
работой.** Допуск возможен только в соответствующем stage после exact-version/license/advisory,
API surface, publish-size, offline и removal-plan проверки. Для Stage 10E пакет не требуется.

## Что реально можно ускорить

### Stage 10E — Grid geometry

Новая package dependency не нужна. Правильный probe:

1. Из публичных Tiled/MapTool материалов выписать только математические свойства square,
   hex-flat, hex-pointy и isometric diamond: world↔cell roundtrip, tie-breaking на границе,
   negative coordinates, offset/rotation и snap stability.
2. Реализовать `IsometricSquares` в собственной domain geometry, независимо от UI и Skia.
3. Сравнивать с собственным brute-force oracle/golden fixtures; чужие тесты и Java/C++ код не
   переносить.
4. Закрыть property sheet, undo/redo, save/reopen, export/snap и настоящий native UI одним Stage
   10E prompt.

Clipper2/NTS/Mapsui Stage 10E не закрывают: это не polygon boolean и не GIS viewer.

### Stage 10F — Fonts

Сначала инвентаризировать Windows fonts и текущие Skia/Avalonia text APIs. Если русский,
multiscript или curved text показывают реальный shaping defect, отдельно испытать официальный
HarfBuzzSharp/SkiaSharp.HarfBuzz. Пакет нельзя добавлять только потому, что он найден.

### Stage 9B–9E

Внешние editor-репозитории не заменяют существующие `AtomicDocumentStore`, autosave journal и
schema contracts. Здесь полезнее текущие fault-injection patterns Map. Clipper2 уместен только в
будущем настоящем polygon crop/boolean, если acceptance потребует materialized geometry.

### Stage 11 — Packaging/update/rollback

Сейчас добавлена dependency-free основа: central app version, immutable local bundle, exact commit,
schema identity и SHA-256. По официальной документации Velopack поддерживает C# и локальный
file-system source; облачный Flow опционален. На Stage 11 нужен отдельный probe в лаборатории:
install → update → offline launch → downgrade/rollback → uninstall → file association на clean VM.
До него Velopack не входит в production graph.

## Изоляция лаборатории

Рекомендуемая и уже созданная структура:

```text
C:\Projects\Map-ResearchLab\
  README.md
  sources\                 # 15 shallow/filter/sparse Git checkouts по exact SHA
  probes\
    stage10e-grid\         # собственные oracle/fixtures, без копии чужого кода
    stage10f-fonts\        # shaping/font catalog experiment
    stage11-update\        # будущий isolated Velopack test app
    geometry-boolean\      # будущий Clipper2/NTS comparison, только при product need
```

Правила:

- папка находится рядом с Map, не внутри repo и не в `Map.slnx`;
- upstream source считается untrusted/read-only; ничего не собирать и не запускать автоматически;
- direct reuse допускается только после отдельной записи: exact files/API, license, purpose,
  alternative, security boundary и removal plan;
- GPL/AGPL/LGPL reference не копируется в proprietary/неопределённый production-код;
- assets с mixed/неясной лицензией запрещены;
- воспроизведение лаборатории выполняет `scripts/sync-map-research-lab.ps1`; существующие dirty или
  отличающиеся checkouts скрипт не reset'ит.

## Рекомендуемый admission-порядок

1. Сейчас: ни одной новой production dependency.
2. Stage 10E: reference-only grid oracle, собственная реализация.
3. Stage 10F: HarfBuzz probe только после failing shaping fixture.
4. Future geometry stage: Clipper2 comparison только после accepted requirement; NTS — baseline
   для сравнения, не default dependency.
5. Stage 11: Velopack isolated probe; MinVer/NBGV compare после появления tag policy.

## Точный следующий шаг

Сначала текущий research/release foundation candidate сохраняется отдельным локальным checkpoint и
передаётся свежей независимой review-задаче как точный `8de17de..checkpoint HEAD`. Следующий
функциональный prompt — **Stage 10E Grid geometry** — не начинать до этой проверки и отдельного
решения владельца. Исследовательскую лабораторию не подключать к production до конкретного Stage 10E
dependency-free oracle plan.
