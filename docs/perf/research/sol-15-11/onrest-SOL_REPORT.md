# onrest — DEC-111

Статус: PARTIAL. Устранены три оставшихся линейных пути: восстановление готового пакета изменений, сбор прямоугольников подписей, локальные проверки рек/дорог при расстановке. Dense overview оставлен с отдельной причиной ниже. Каталоги SceneObjectList не менялись.

База: `912fc437758539bf6ac19968c7e1505793b32277`, ветка `claude/onfix-d`. `pc/claude/on-guards` / `ca390da` прочитаны, не слиты. Paste/reorder/boolean/cut/composition не повторялись. Чужих незакоммиченных изменений в начале не было.

FACT: локальные коммиты `0b9e8ca` — `perf(core): reuse prepared snapshots in change package history`; `a4ca688` — `perf(core): bound placement queries to labels and nearby features`. Push и добавление remotes не выполнялись. [onrest.patch](onrest.patch) получен именно `git -C /Users/user/MapWork/agents/onrest/repo format-patch --stdout 912fc437758539bf6ac19968c7e1505793b32277..HEAD`; `git apply --check` на baseline прошёл. Текст хранится в Git LF, рабочие существующие файлы сохранили CRLF/BOM согласно `.gitattributes`; это проверено через `git cat-file --filters`. [validation.log](results/validation.log): чистый git status, diff --check, apply --check, список файлов и SHA-256 патча `7f1f2a427d7ec2a942433b6f7224b07199aad4152f4a41e2a8df49d1948748f8` (42 414 байт).

## Инвентаризация на стартовом коммите

FACT: поиск выполнен по всем `tests/**/*.cs`, включая `Skip`, `Known_`, `IsViolation`. В этой базе **нет** `UndoMemoryGuardTests.cs`, `IsViolation`-списков и названных в поручении Place10/Delete10/Path memory guards. `DocMemoryProbe` и `OverviewMemoryProbe` — условные измерители, без таких списков. Экологические skips `MAP_*_HUGE/AUDIT` — не дефекты алгоритма.

Пути и номера строк в этой таблице относятся к базе; старые номера внутри skip иногда уже не совпадали с исходником. Коэффициенты далее — время / выделенные managed bytes, карта 200 000 относительно 10 000.

| Случай | Причина skip и фактический линейный путь в базе | Решение |
|---|---|---|
| PasteObjectsCommand | `Commands.cs:328 filters every object on Undo`; фактически `Commands.cs:333` — фильтр в Revert | Не исправлялся: уже реализован `ca390da`, интеграция на Windows. |
| ReorderObjectsCommand | `Commands.cs:553,573 captures and rebuilds the entire layer for a two-object swap`; фактически `Commands.cs:550,556,557,577-579` — проверка/копии и полный ToDictionary в Order | Не исправлялся: `ca390da`, включая SplicePositions. |
| ReplaceObjectWithManyCommand | `VectorEditing.cs:22,35 copies and filters the whole layer`; фактически `VectorEditing.cs:20` ToList/FindIndex, `:33` фильтрует слой | Не исправлялся: `ca390da` (cut/replace). |
| ShapeBooleanCommand | `VectorEditing.cs:307,315 filters the whole layer`; фактически `VectorEditing.cs:308,315` — Apply/Revert фильтруют список слоя | Не исправлялся: `ca390da`. |
| ReplaceObjectsWithCompositionCommand | `Compositions.cs:163-168 scans/copies the whole layer` | Не исправлялся: `ca390da`. |
| ApplyChangePackageCommand | `ChangePackages.cs:222 -> WithLayers ... exceed MapDocument.MaxDeltaObjects`; `MapDocument.cs:554,559` собирает все entering и уходит в WholeDocumentRevision | Исправлен; skip снят. Команда использует неизменяемые Layers/Notes/Assets/Entities готового снимка, сохраняя текущие настройки/историю. |
| Label_clearance_bounds | `LabelPriority.cs:69-73 ... scans all objects even with three fixed labels`; `:72` SelectMany(l.Objects) | Исправлен; skip снят. Обход только текстовых chunks через существующий TextObjects, в прежнем порядке и с прежними BoundsOf. |
| Placement_rules_with_many_features | `PlacementAreaRuleCheck.cs:17-27,45-55,80-89 ... builds arrays and scans all rivers/roads for one local probe`; фактически `:23-25` — массивы, `:52,73,82` — переборы | Исправлен; skip снят. Подготовленный пространственный индекс, прежние точные предикаты. |
| Document_preview_replacement | `SceneObjectList.cs:90-97,466-480 ... copies O(N/256) chunk directories` | Вне задачи по прямому исключению владельца. Skip не трогался. На этой базе замер уже bounded из-за onfix-b AVL; старый текст skip устарел. |
| Dense_overview_delta | `SceneRenderer.cs:6540-6552 ... rebuilds every object in affected 8192-unit chunk`; фактически `:6541` ToDictionary по oldChunk.Objects, `:6549` полная сортировка/новый массив | Не исправлен: нужна смена представления OverviewChunk, его набора assets и зависимости variants/raster от полного draw order. Для гарантии пиксельной идентичности перекрывающихся полупрозрачных объектов это отдельная работа с контрольными изображениями. Перенос полного flatten в следующий кадр не считался бы устранением стоимости. Ни формат, ни диск для решения не нужны. |

FACT: остальные `Known_*` уже `[Fact]`, без Skip/Linear, в стартовом коммите: AddObject, AddObjects/legend, TransformObject, DeleteObjects/singleton, UpdateLayer/controlled-area, PasteComposition, RegionLabelFollow, DecorationRefresh, SetMapScale, AddObjects/append-limit, ReplaceObjects/overlay-limit. Их старые причины про SceneObjectList, legend, controlled area и лимиты индекса не являются оставшимися нарушениями этой базы. Они прошли базовый прогон. River/Road preview rendering также уже включены в базе (`962ca6a`).

## Что изменено

- `src/Map.Core/Editing/ChangePackages.cs`: Apply/Undo/Redo больше не создают новую identity/name root над всей картой при восстановлении уже готовых неизменяемых списков. Поведение полной подмены содержимого команды сохранено; это не переработка conflict merge.
- `src/Map.Core/Editing/LabelPriority.cs`: LabelBounds обходится каталогом текстов. Сохранены порядок слоёв/подписей, visibility, Text != null и исходные границы.
- `src/Map.Core/Documents/FeatureSpatialIndex.cs` (новый): persistent grid с id-наборами в ячейках, exact bounds broad phase, отдельный набор oversized. Boolean Any не создаёт список кандидатов. Пустой/одиночный индекс имеет прямую ветку.
- `Layers.cs`, `DocumentDerivedIndexes.cs`: индексы принадлежат содержимому feature lists и разделяются record-копиями. Готовятся вместе с остальными индексами документа. Новые поля не участвуют в equality/hash и сериализации.
- `RiverEditing.cs`, `RoadEditing.cs`: передаются только изменённые экземпляры; подготовленный индекс наследуется через persistent updates затронутых записей/ячеек. Старый снимок не мутирует.
- `PlacementAreaRuleCheck.cs`: локальный broad phase перед прежними RoadGeometry.TryClosest / RiverGeometry.TryClosest / InsideLake. Проверка суши и шесть проб основания объекта сохранены.
- `EditCostGuardTests.cs`, `HotPathCostGuardTests.cs`: включены три guards, добавлены связанные bounded-case проверки индексов. Проверка delta допускает возврат уже полностью подготовленного снимка; счётчики полных root builds/перебора объектов по-прежнему обязательны. Fixture с множеством features явно готовится вне таймера, как требует контракт prepared-map guard.
- `Editing/ChangePackageTests.cs`, `Editing/LabelPriorityTests.cs`, `Editing/PlacementFeatureIndexTests.cs` (новый): пять регрессионных тестов — повторная история и настройки, точный порядок/границы подписей, сравнение правил с прежним полным алгоритмом (включая озеро, hidden layers/features, отрицательные координаты, границы ячеек и большой margin), раздельные ветки истории, dense buckets/oversized.

## Измерения и проверки

Все численные утверждения ниже относятся только к Mac Release, `DOTNET_TieredCompilation=0`, `DOTNET_gcServer=0`. Даже нулевые managed allocations нужно **перепроверить на Windows**. GPU/60 FPS не измерялись.

FACT: коэффициенты — последняя строка соответствующего ключа в логах. У некоторых Known_* факт и bounded theory печатают один ключ дважды; не выбирался лучший результат. Непроходящие случаи измерены opt-in audit без ослабления порога включённых guards (оба коэффициента <=3).

| Случай из начального списка | Время: до → после | Allocated bytes: до → после | Статус |
|---|---:|---:|---|
| PasteObjectsCommand | 54.322 → 67.682 | 17.589 → 17.593 | Не менялся, ждёт ca390da |
| ReorderObjectsCommand | 9.892 → 74.223 | 18.380 → 18.262 | Не менялся, ждёт ca390da; audit без no-GC, время чувствительно к GC |
| ReplaceObjectWithManyCommand | 80.822 → 58.912 | 18.372 → 18.383 | Не менялся, ca390da |
| ShapeBooleanCommand | 74.889 → 72.445 | 18.461 → 18.459 | Не менялся, ca390da |
| ReplaceObjectsWithCompositionCommand | 79.647 → 37.712 | 17.755 → 17.749 | Не менялся, ca390da |
| ApplyChangePackageCommand | **95.182 → 1.093** | **17.852 → 1.000** | Исправлен, guard проходит |
| label-bounds | **13.155 → 1.000** | **17.130 → 1.000** | Исправлен, guard проходит |
| placement-features | **12.396 → 1.077** | **19.745 → 1.000** | Исправлен, guard проходит |
| preview-replace | 1.173 → 1.020 | 1.065 → 1.076 | Исключён владельцем, код/skip не менялись |
| overview-delta-dense | 28.252 → 31.473 | 21.056 → 21.056 | Не исправлен, причина выше |

Абсолютные медианы включённых новых guards, малая / большая карта:

| Случай | До, мкс | После, мкс | До, байт | После, байт |
|---|---:|---:|---:|---:|
| ApplyChangePackageCommand, Execute/Undo/Redo | 3468.7 / 330157.7 | 11.8 / 12.9 | 7 917 800 / 141 351 192 | 14 320 / 14 320 |
| LabelBounds | 223.5 / 2940.2 | 0.7 / 0.7 | 5 152 / 88 256 | 1 024 / 1 024 |
| PlacementAreaRuleCheck: constructor + 2 Allows | 820.8 / 10175.0 | 1.3 / 1.4 | 81 088 / 1 601 088 | 576 / 576 |

Контроль уже исправленных в базе путей: legend — время 1.119→1.144, bytes 1.097→1.120; controlled-area — 1.034→1.027 / 1.006→1.007; append-limit — 1.215→1.231 / 1.166→1.185; overlay-limit — 1.095→1.079 / 1.148→1.143. River/Road local geometry по bytes 1.000→1.000; render preview обеих features — 1.000→1.000 по времени и bytes.

PARTIAL: малый случай placement-stamps (одна река/дорога) стал 0.174→0.282 мкс на большой stamp-карте, allocations 120→40 байт. Новая broad phase имеет фиксированную цену; абсолютного ускорения для карты с единственной feature не заявляю.

FACT: дополнительная **удерживаемая managed память истории** пакета после Execute/Undo/Redo и Prepare: baseline 677 176 / 13 973 136 байт, итог 7 736 / 8 104 байт (10 000 / 200 000 объектов); коэффициент **20.634 → 1.048**. Подготовленные корни готовых снимков переиспользуются. Это один парный heap probe с прогревом, не статистический memory benchmark; для нового индекса features отдельный heap-ratio probe не проводился. Источники: [history-baseline.log](results/history-baseline.log), [history-current.log](results/history-current.log).

FACT: Release `Map.slnx` — 0 ошибок, 18 предупреждений; Core.Tests — 0 ошибок/предупреждений; Rendering.Tests — 0 ошибок, 1 предупреждение. [solution-build.log](results/solution-build.log), [core-build.log](results/core-build.log), [rendering-build.log](results/rendering-build.log), [runner-build.log](results/runner-build.log). Базовые сборки: [baseline-core-build.log](results/baseline-core-build.log), [baseline-rendering-build.log](results/baseline-rendering-build.log).

| Прогон | База passed / failed / skipped | Итог passed / failed / skipped |
|---|---:|---:|
| Core guards, audit=1 | 204 / 0 / 11 | 209 / 0 / 8 |
| Rendering guards, audit=1 | 6 / 0 / 2 | 6 / 0 / 2 |
| Полная Core DLL | 881 / 4 / 13 | 891 / 4 / 10 |
| Полная Rendering DLL | 571 / 1 / 3 | 571 / 1 / 3 |

Логи: [baseline-core-guards.log](results/baseline-core-guards.log), [core-guards.log](results/core-guards.log), [baseline-rendering-guards.log](results/baseline-rendering-guards.log), [rendering-guards.log](results/rendering-guards.log), [baseline-core-all.log](results/baseline-core-all.log), [core-all.log](results/core-all.log). В полной Core DLL ровно те же четыре macOS-сбоя именованных семафоров `WorkingMemoryBudgetTests`: `Small_edit_preview_and_save_validation_work_below_the_normal_OS_reserve`, `Rejected_heavy_operation_releases_the_cross_window_slot`, `The_heavy_slot_is_granted_while_the_thread_pool_is_busy`, `Separate_window_budgets_share_the_heavy_slot_and_cancelled_waiters_do_not_strand_it`. Новых ошибок Core нет. Пять новых регрессионных тестов прошли в whole DLL; вспомогательные прогоны [feature-index-tests.log](results/feature-index-tests.log), [feature-guards.log](results/feature-guards.log) выполнены до последней оптимизации одиночной feature, окончательный результат — whole DLL.

FACT: полная Rendering DLL — ровно тот же baseline-сбой `Stage10EGridRenderingTests.Four_proposed_golden_fixtures_match_without_overwriting_expected`, expected 0 / actual 7145 отличающихся пикселей. Не подавлялся и не выдаётся за зелёную визуальную проверку. [baseline-rendering-all.log](results/baseline-rendering-all.log), [rendering-all.log](results/rendering-all.log). Точное сравнение имён **и сообщений** ошибок обеих DLL: [failure-comparison.log](results/failure-comparison.log), `NEW_FAILURES 0` для обеих. Whole Rendering: 600.474 с на базе, 564.584 с в итоге; это длительность набора, не performance-guard и не доказательство ускорения renderer.

Команды выполнялись из `/Users/user/MapWork/agents/onrest`. Перед каждой shell-командой выполнялись `source ~/MapWork/env.sh; source ./run-env.sh`. Все пути собственные, NuGet только из offline cache. Начальная база создана точной командой:

```sh
git -C /Users/user/MapWork/agents/onrest/repo worktree add /Users/user/MapWork/agents/onrest/tmp/baseline 912fc437758539bf6ac19968c7e1505793b32277
```

Точные команды сборки текущей версии (baseline Core/Rendering — те же команды с `repo/` → `tmp/baseline/`):

```sh
dotnet build /Users/user/MapWork/agents/onrest/repo/Map.slnx -c Release -p:DirectoryBuildPropsPath=/Users/user/MapWork/agents/onrest/mac.props -p:NuGetAudit=false -p:RunAnalyzers=false --configfile /Users/user/MapWork/agents/onrest/NuGet.Config -m:1 -nr:false -p:UseSharedCompilation=false
dotnet build /Users/user/MapWork/agents/onrest/repo/tests/Map.Core.Tests -c Release -p:RuntimeIdentifier=osx-arm64 -p:NuGetAudit=false -p:RunAnalyzers=false --configfile /Users/user/MapWork/agents/onrest/NuGet.Config -m:1 -nr:false -p:UseSharedCompilation=false
dotnet build /Users/user/MapWork/agents/onrest/repo/tests/Map.Rendering.Tests -c Release -p:RuntimeIdentifier=osx-arm64 -p:NuGetAudit=false -p:RunAnalyzers=false --configfile /Users/user/MapWork/agents/onrest/NuGet.Config -m:1 -nr:false -p:UseSharedCompilation=false
dotnet build /Users/user/MapWork/agents/onrest/test-runner/TestRunner.csproj -c Release --configfile /Users/user/MapWork/agents/onrest/NuGet.Config -m:1 -nr:false -p:UseSharedCompilation=false
```

Точные команды измерений/проверок (вывод каждой команды — в соответствующий `results/*.log`; baseline заменяет `repo/` на `tmp/baseline/`):

```sh
MAP_ON_GUARDS_AUDIT=1 MAP_RENDER_GUARDS_AUDIT=1 dotnet /Users/user/MapWork/agents/onrest/test-runner/bin/Release/net10.0/TestRunner.dll /Users/user/MapWork/agents/onrest/repo/tests/Map.Core.Tests/bin/Release/net10.0/osx-arm64/Map.Core.Tests.dll EditCostGuardTests HotPathCostGuardTests
MAP_RENDER_GUARDS_AUDIT=1 dotnet /Users/user/MapWork/agents/onrest/test-runner/bin/Release/net10.0/TestRunner.dll /Users/user/MapWork/agents/onrest/repo/tests/Map.Rendering.Tests/bin/Release/net10.0/osx-arm64/Map.Rendering.Tests.dll RenderPathCostGuardTests
MAP_TEST_PROGRESS=1 dotnet /Users/user/MapWork/agents/onrest/test-runner/bin/Release/net10.0/TestRunner.dll /Users/user/MapWork/agents/onrest/repo/tests/Map.Core.Tests/bin/Release/net10.0/osx-arm64/Map.Core.Tests.dll
MAP_TEST_PROGRESS=1 dotnet /Users/user/MapWork/agents/onrest/test-runner/bin/Release/net10.0/TestRunner.dll /Users/user/MapWork/agents/onrest/repo/tests/Map.Rendering.Tests/bin/Release/net10.0/osx-arm64/Map.Rendering.Tests.dll
```

У baseline Core whole DLL `MAP_TEST_PROGRESS` не выставлялся (на выбор тестов не влияет). Первый whole Rendering был остановлен без результата после отсутствия видимого прогресса; итоговый baseline выполнен заново с `MAP_TEST_PROGRESS=1`, без изменения исходников/ожиданий/фильтров. В DLL есть тяжёлые штатные benchmark facts с 1 млн объектов, они не исключались.

Для retained-memory probe сохранён точный исходник XML + C# в [results/history-probe-source.log](results/history-probe-source.log): XML до `</Project>` — HistoryProbe.csproj, остальное — Program.cs. Он ссылается прямо на публичную Map.Core.dll, без пакетов и без изменения baseline. Прогрев 1000, затем 10 000/200 000 прямоугольников; подготовлены before/after, измерен живой heap после полного GC до и после одного Execute/Undo/Redo с Prepare каждого результата. Before/after/session удерживаются GC.KeepAlive, проверены координата, undo=1 и redo=0. Это дополнительная удерживаемая managed память готовой истории, а не размер всего документа/RSS.

Обе сборки probe прошли: [history-baseline-build.log](results/history-baseline-build.log), [history-current-build.log](results/history-current-build.log).

```sh
dotnet build /Users/user/MapWork/agents/onrest/tmp/history-probe/HistoryProbe.csproj -c Release -p:MapCoreAssembly=/Users/user/MapWork/agents/onrest/tmp/baseline/src/Map.Core/bin/Release/net10.0/osx-arm64/Map.Core.dll -p:DirectoryBuildPropsPath=/Users/user/MapWork/agents/onrest/mac.props -p:NuGetAudit=false -p:RunAnalyzers=false --configfile /Users/user/MapWork/agents/onrest/NuGet.Config -m:1 -nr:false -p:UseSharedCompilation=false -o /Users/user/MapWork/agents/onrest/tmp/history-baseline
dotnet build /Users/user/MapWork/agents/onrest/tmp/history-probe/HistoryProbe.csproj -c Release -p:MapCoreAssembly=/Users/user/MapWork/agents/onrest/repo/src/Map.Core/bin/Release/net10.0/osx-arm64/Map.Core.dll -p:DirectoryBuildPropsPath=/Users/user/MapWork/agents/onrest/mac.props -p:NuGetAudit=false -p:RunAnalyzers=false --configfile /Users/user/MapWork/agents/onrest/NuGet.Config -m:1 -nr:false -p:UseSharedCompilation=false -o /Users/user/MapWork/agents/onrest/tmp/history-current
dotnet /Users/user/MapWork/agents/onrest/tmp/history-baseline/HistoryProbe.dll
dotnet /Users/user/MapWork/agents/onrest/tmp/history-current/HistoryProbe.dll
```

## Границы и риски

- PARTIAL: холодное построение feature index требует обхода features и вычисления их геометрии, дополнительной памяти порядка числа features. Подготовка вынесена в существующий Prepare; произвольная замена Rivers/Roads через with создаёт новый холодный индекс. Известные RiverCommand/RoadCommand получают локальный delta.
- PARTIAL: локальные запросы зависят от числа кандидатов в пересекаемых ячейках и oversized features. Массовое наложение или огромный RoadMargin закономерно может затронуть все features; обещания O(1) для такого запроса нет. Изменение индекса — persistent paths порядка O(k log N), без копирования всех N записей.
- FACT: полные линейные проходы `ChangePackage.Create/ChangePackageMerge.Check/Apply` (`ChangePackages.cs:25-29,146-160,178-201`) при расчёте/слиянии пакета остались. Исправлен guard команды применения уже рассчитанного снимка. Полная переработка merge с межслойной маршрутизацией/конфликтами требует отдельного набора guards и изменений протокола delta; не подменялась быстрым случаем только для тестового пакета.
- FACT: сами операции RiverCommand/RoadCommand на больших **feature lists** всё ещё фильтруют/копируют ImmutableArray (`RiverEditing.cs:544-554`, `RoadEditing.cs:416-429`); данная задача устраняет оставшийся skip локальной проверки правил и локальное обновление её производного индекса. Линейность всего редактирования таких списков не объявляется исправленной.
- NOT CHECKED: Windows, UI, GPU, худший кадр 60 FPS, визуальная приёмка, открытие новых файлов старой Windows-сборкой. Файлы persistence, схема, UI/XAML, видимые тексты и renderer не менялись. Формат сохранения не менялся по diff; сквозной прогон сохранения не выполнялся.
- NOT CHECKED: анализаторы (отключены по заданию), миллионный opt-in вариант.

## Завершение

FACT: собственные `bin/obj` (включая runner), тестовые artifacts и временные probe удалены; baseline снят командой `git worktree remove`. Проверка среды отклонила первоначальную команду с `rm -rf`; очистка выполнена адресно с проверкой абсолютных путей внутри onrest. Последний вызов Git заново создал временный `xcrun_db`; он убран после проверки git status. Итоговая проверка пустого tmp и отсутствия bin/obj/baseline — в конце [cleanup.log](results/cleanup.log). Чистый рабочий каталог репозитория, два локальных коммита; общий NuGet cache и чужие папки/процессы не трогались.

## ИТОГИ

1. Сделано: три локальных пути перестали зависеть от всех посторонних объектов; подготовлен патч с тестами. Dense overview и делегированные случаи перечислены отдельно.
2. Что дальше: проверить патч на Windows и отдельно переработать dense OverviewChunk с проверкой пикселей.
3. Реализация проекта: не пересчитывалась; задача ограничена DEC-111.
