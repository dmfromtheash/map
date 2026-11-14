# SOL_REPORT — splice / DEC-111

Сеанс на Mac, 2026-11-14. Старт: `b6f12b18d2b5140e79294f7fc4d26336f743944b`, ветка `claude/onfix-c`.
Результат закоммичен локально: `f1c361a` (реализация и unit tests), `6a8be28` (включение guards).
Патч: [splice.patch](splice.patch), создан через `git format-patch --stdout b6f12b18d2b5140e79294f7fc4d26336f743944b..HEAD`.
Push и добавления remotes не выполнялись.

## Изменения — FACT

- `src/Map.Core/Documents/SceneObjectList.ChunkDirectory.cs`: постоянное AVL-дерево чанков с числом объектов/чанков в поддеревьях, текстовыми сводками, split/join и постоянным индексом родительских связей. Индекс использует уникальные номера узлов и сжатое двоичное дерево с пределом глубины 64; номера не сериализуются. Промежуточные узлы и неактивные связи удаляются перед публикацией версии.
- `src/Map.Core/Documents/SceneObjectList.cs`: структурные операции больше не копируют весь каталог, starts array и chunk-position dictionary. Перестраиваются затронутые группы чанков и максимум по одному малому соседу с каждой стороны. Старые поддеревья и identity runs разделяются между снимками; поиск позиции id учитывает текущие размеры поддеревьев. Нет пересчёта абсолютных позиций всех текстовых чанков.
- Замены чанков без текста сохраняют дерево рангов и меняют постоянное значение листа; активный лист имеет одну связь со стабильным листом рангов, без цепочки preview-версий. Изменения текста обновляют только соответствующие пути и сводки.
- `tests/Map.Core.Tests/SceneObjectListSpliceTests.cs`: 7 новых тестовых случаев. Случайные insert/remove/move/reorder против `List<SceneObject>`, порядок и индексный/id lookup, перечисление, старые снимки, текстовые маски/счётчики, смены Kind, Undo/Redo смешанной истории, границы и исключения, баланс AVL и отсутствие устаревших связей.
- `tests/Map.Core.Tests/UndoMemoryGuardTests.cs`: включены ReorderTwoObjects, Place10, Path на 10k/200k и ReorderTwoObjects, Place10, Delete10 на 1M. `IsViolation` больше не исключает случаи; `IsHugeViolation` оставляет только Path. Причина Path на 1M теперь относится к снимкам документа.
- `tests/Map.Core.Tests/EditCostGuardTests.cs`: семь случаев с причиной SceneObjectList перенесены из Known-skips в обычные bounded cases, включая проверку готовых индексов на Execute/Undo/Redo. Остальные причины skip и лимиты guards не изменены.

Стоимость отдельного диапазона каталога определяется путями AVL и затронутыми чанками; для g разрозненных групп верхняя оценка — O(g log C + changed), где C — число чанков. Обхода/копии всех C записей при малой структурной правке нет. Строгая оценка O(log C + changed) для произвольно растущего числа разрозненных групп отдельно не доказана — PARTIAL.

Публичный API, порядок объектов и сериализуемые модели не менялись. Map.Core не получил UI/Skia-зависимостей. Проверка порядка и идентичности — unit tests; формат сохранения дополнительно проверен существующими SchemaV4ObjectChunkTests.

## Проверки — FACT

Числа: passed / failed / skipped.

| Проверка | Baseline | После | Журнал |
| --- | --- | --- | --- |
| Сборка Core.Tests Release | 0 ошибок | 0 ошибок | [baseline-build.log](results/baseline-build.log), [core-build.log](results/core-build.log) |
| UndoMemoryGuardTests, AUDIT=1, HUGE=1 | 52 / 0 / 7 | 58 / 0 / 1 | [baseline-undo.log](results/baseline-undo.log), [undo.log](results/undo.log) |
| EditCostGuardTests, AUDIT=1 | 172 / 0 / 13 | 186 / 0 / 6 | [baseline-edit.log](results/baseline-edit.log), [edit.log](results/edit.log) |
| Весь Map.Core.Tests | 897 / 4 / 20 | 921 / 4 / 10 | [baseline-core-all.log](results/baseline-core-all.log), [core-all.log](results/core-all.log) |
| Новые splice + CoreHotPathEquivalence | — | 11 / 0 / 0 | [splice-tests.log](results/splice-tests.log) |
| SchemaV4ObjectChunkTests | не запускался | 7 / 0 / 0 | [persistence-build.log](results/persistence-build.log), [persistence-chunks.log](results/persistence-chunks.log) |
| Map.slnx Release | не запускался | успешно, 0 ошибок, 18 warnings | [solution-build.log](results/solution-build.log) |
| In-process xUnit runner build | успешно | тот же runner | [runner-build.log](results/runner-build.log) |

Все 18 предупреждений solution относятся к незатронутым файлам Rendering/Presentation/App. Анализаторы отключены.

Списки четырёх ошибок Core совпадают: [failure-comparison.log](results/failure-comparison.log).
Это PlatformNotSupportedException в ожиданиях нескольких handles с именованным примитивом на macOS, методы WorkingMemoryBudgetTests:

- Small_edit_preview_and_save_validation_work_below_the_normal_OS_reserve
- Rejected_heavy_operation_releases_the_cross_window_slot
- The_heavy_slot_is_granted_while_the_thread_pool_is_busy
- Separate_window_budgets_share_the_heavy_slot_and_cancelled_waiters_do_not_strand_it

Сохранённые skips по legend refresh, controlled-area, ChangePackages и пределам DocumentRevisionIndex остались вне задачи.
Path 1M остался skipped по причине удержания before/after документов; audit после изменения дал 97376 → 116888 B, ratio 1.200, без enforcement.

## Undo: одна запись, 10k → 200k

Медиана удерживаемой добавленной управляемой памяти, исходный снимок закреплён якорем. Пять samples после прогревов; это GC delta, не allocated bytes и не RSS. Лимит ratio ≤ 3 без byte floor. До — исходный commit, после — финальный код. Все 25 случаев после изменения enforce=True; максимальный ratio 2.080 (Delete10).

| Случай | 10k: до → после, B | 200k: до → после, B | Ratio: до → после |
| --- | --- | --- | --- |
| AddLayer10 | 38112 → 38016 | 37848 → 37752 | 0.993 → 0.993 |
| Area | 448864 → 483336 | 599064 → 515016 | 1.335 → 1.066 |
| CheckpointAdd | 3744 → 3744 | 3744 → 3744 | 1.000 → 1.000 |
| CheckpointDelete | 3280 → 3280 | 3280 → 3280 | 1.000 → 1.000 |
| CheckpointRename | 3840 → 3840 | 3840 → 3840 | 1.000 → 1.000 |
| CheckpointRestore | 4184 → 4184 | 4184 → 4184 | 1.000 → 1.000 |
| Delete10 | 87640 → 223416 | 163288 → 464736 | 1.863 → 2.080 |
| DeleteLayer10 | 21472 → 21632 | 21472 → 21632 | 1.000 → 1.000 |
| DuplicateLayer10 | 36328 → 36312 | 36592 → 36576 | 1.007 → 1.007 |
| Fog | 2328 → 2328 | 2328 → 2328 | 1.000 → 1.000 |
| Labels | 2328 → 2328 | 2328 → 2328 | 1.000 → 1.000 |
| LayerBlend | 3808 → 3808 | 3808 → 3808 | 1.000 → 1.000 |
| LayerLocked | 3808 → 3808 | 3808 → 3808 | 1.000 → 1.000 |
| LayerOpacity | 3808 → 3808 | 3808 → 3808 | 1.000 → 1.000 |
| LayerVisible | 3808 → 3808 | 3808 → 3808 | 1.000 → 1.000 |
| Lighting | 2984 → 2984 | 2984 → 2984 | 1.000 → 1.000 |
| MapScale | 2648 → 2648 | 2648 → 2648 | 1.000 → 1.000 |
| MapStyle | 6792 → 6792 | 6792 → 6792 | 1.000 → 1.000 |
| Move10 | 101792 → 108320 | 101792 → 109608 | 1.000 → 1.012 |
| Path | 87976 → 98320 | 157024 → 110200 | 1.785 → 1.121 |
| Place10 | 45848 → 54040 | 119648 → 69408 | 2.610 → 1.284 |
| RenameLayer | 3808 → 3808 | 3808 → 3808 | 1.000 → 1.000 |
| ReorderLayer | 8096 → 8096 | 8096 → 8096 | 1.000 → 1.000 |
| ReorderTwoObjects | 36192 → 46832 | 111760 → 57000 | 3.088 → 1.217 |
| Style10 | 101792 → 108264 | 101792 → 109664 | 1.000 → 1.013 |

Источники и полные samples: [baseline-undo.log](results/baseline-undo.log), [undo.log](results/undo.log).

## Undo: 1M

У каждого 1M-сравнения собственные 10k samples. Максимальный enforced ratio после изменения 2.548 (Delete10); все 24 enforced случая прошли. Path измерен audit-режимом, skip сохранён.

| Случай | 10k: до → после, B | 1M: до → после, B | Ratio: до → после | После |
| --- | --- | --- | --- | --- |
| AddLayer10 | 38112 → 37960 | 37848 → 38016 | 0.993 → 1.001 | guard |
| Area | 448704 → 482120 | 955168 → 535216 | 2.129 → 1.110 | guard |
| CheckpointAdd | 11968 → 3744 | 3744 → 3744 | 0.313 → 1.000 | guard |
| CheckpointDelete | 3280 → 3280 | 3280 → 3280 | 1.000 → 1.000 | guard |
| CheckpointRename | 3840 → 3840 | 3840 → 3840 | 1.000 → 1.000 | guard |
| CheckpointRestore | 4184 → 4184 | 4184 → 4184 | 1.000 → 1.000 | guard |
| Delete10 | 87640 → 223360 | 341400 → 569064 | 3.895 → 2.548 | guard |
| DeleteLayer10 | 21472 → 21584 | 21472 → 21632 | 1.000 → 1.002 | guard |
| DuplicateLayer10 | 36328 → 36312 | 36592 → 36576 | 1.007 → 1.007 | guard |
| Fog | 2328 → 2328 | 2328 → 2328 | 1.000 → 1.000 | guard |
| Labels | 2328 → 10552 | 2328 → 2328 | 1.000 → 0.221 | guard |
| LayerBlend | 3808 → 3808 | 3808 → 3808 | 1.000 → 1.000 | guard |
| LayerLocked | 3808 → 12032 | 3808 → 3808 | 1.000 → 0.316 | guard |
| LayerOpacity | 3808 → 3808 | 3808 → 3808 | 1.000 → 1.000 | guard |
| LayerVisible | 3808 → 3808 | 3808 → 3808 | 1.000 → 1.000 | guard |
| Lighting | 2984 → 2984 | 2984 → 2984 | 1.000 → 1.000 | guard |
| MapScale | 2648 → 2648 | 2648 → 2648 | 1.000 → 1.000 | guard |
| MapStyle | 6792 → 6792 | 6792 → 6792 | 1.000 → 1.000 | guard |
| Move10 | 101792 → 108280 | 101792 → 110280 | 1.000 → 1.018 | guard |
| Path | 87976 → 97376 | 335136 → 116888 | 3.809 → 1.200 | audit; skip сохранён |
| Place10 | 45848 → 54152 | 297760 → 79904 | 6.495 → 1.476 | guard |
| RenameLayer | 3808 → 3808 | 3808 → 3808 | 1.000 → 1.000 | guard |
| ReorderLayer | 8096 → 8096 | 8096 → 8096 | 1.000 → 1.000 | guard |
| ReorderTwoObjects | 36192 → 46496 | 289872 → 68184 | 8.009 → 1.466 | guard |
| Style10 | 101792 → 108544 | 101792 → 110576 | 1.000 → 1.019 | guard |

## История из 200 команд на 200k

Во всех случаях проверены 200 Execute, Undo и Redo. Показаны signed added GC deltas, не размер графа объектов истории. Освобождение/компактизация общих weak-index caches может давать отрицательные значения; их нельзя трактовать как отрицательный размер Undo или как самостоятельное доказательство экономии. Net deltas приведены в исходных логах.

| Случай / owners | 200 записей: до → после, B | На запись: до → после, B |
| --- | --- | --- |
| Area/True | 31287672 → 20722448 | 156438.36 → 103612.24 |
| Delete10/True | 6383960 → -15311320 | 31919.80 → -76556.60 |
| Move10/False | -1749200 → 16147664 | -8746.00 → 80738.32 |
| Move10/True | 3382280 → 5269040 | 16911.40 → 26345.20 |
| Path/True | 16252296 → 12224160 | 81261.48 → 61120.80 |
| Place10/True | 6271968 → 10072736 | 31359.84 → 50363.68 |
| Style10/False | -1749664 → 16179032 | -8748.32 → 80895.16 |
| Style10/True | 3373896 → 5240592 | 16869.48 → 26202.96 |

## EditCostGuard: снятые SceneObjectList skips

Измеряются создание команды, Execute, Undo и Redo. Медиана 7 samples, лимиты отношения времени и allocated bytes ≤ 3. Для всех bounded измерений максимумы после изменения: время 1.565, bytes 1.954 (оба DeleteObjectsCommand). Positive control с полной копией обнаружен тестом; его ratio не включён в эти максимумы.

| Случай | 10k B: до → после | 200k B: до → после | Ratio B: до → после | 10k мкс: до → после | 200k мкс: до → после | Ratio времени: до → после |
| --- | --- | --- | --- | --- | --- | --- |
| AddObjectCommand | 49776 → 77048 | 169448 → 109576 | 3.404 → 1.422 | 29.600 → 34.000 | 62.200 → 40.800 | 2.101 → 1.200 |
| DecorationRefreshCommand | 108384 → 130240 | 109728 → 165704 | 1.012 → 1.272 | 65.100 → 61.500 | 65.800 → 68.900 | 1.011 → 1.120 |
| DeleteObjectsCommand/singleton | 91280 → 122104 | 209048 → 144560 | 2.290 → 1.184 | 148.100 → 151.700 | 183.500 → 190.900 | 1.239 → 1.258 |
| PasteCompositionCommand | 52008 → 78608 | 171840 → 110320 | 3.304 → 1.403 | 32.500 → 35.200 | 65.900 → 41.300 | 2.028 → 1.173 |
| RegionLabelFollowCommand | 155872 → 193952 | 158496 → 256440 | 1.017 → 1.322 | 95.400 → 100.000 | 96.400 → 119.400 | 1.010 → 1.194 |
| SetMapScaleCommand | 45408 → 60544 | 46560 → 86752 | 1.025 → 1.433 | 27.500 → 29.100 | 28.000 → 34.500 | 1.018 → 1.186 |
| TransformObjectCommand | 44424 → 44880 | 44296 → 44816 | 0.997 → 0.999 | 29.200 → 25.800 | 29.800 → 25.600 | 1.021 → 0.992 |

Источники: [baseline-edit.log](results/baseline-edit.log), [edit.log](results/edit.log). Промежуточный вариант выявил увеличение стоимости point-replace; перед финальным прогоном добавлено разделение дерева рангов для чанков без текста. Его отдельные [edit-initial.log](results/edit-initial.log) и [undo-initial.log](results/undo-initial.log) сохранены для прозрачности; Undo-прогон был прерван и не считается полной проверкой. После изменения кода выполнен один полный финальный 1M-прогон.

## Точные команды

Все команды выполнялись из `/Users/user/MapWork/agents/splice`, перед shell-вызовом:

```sh
source ~/MapWork/env.sh
source ./run-env.sh
```

Baseline и сборки:

```sh
git -C /Users/user/MapWork/agents/splice/repo worktree add /Users/user/MapWork/agents/splice/tmp/baseline b6f12b18d2b5140e79294f7fc4d26336f743944b
dotnet build /Users/user/MapWork/agents/splice/tmp/baseline/tests/Map.Core.Tests -c Release -p:RuntimeIdentifier=osx-arm64 -p:NuGetAudit=false -p:RunAnalyzers=false --configfile /Users/user/MapWork/agents/splice/NuGet.Config -m:1 -nr:false -p:UseSharedCompilation=false > /Users/user/MapWork/agents/splice/results/baseline-build.log 2>&1
dotnet build /Users/user/MapWork/agents/splice/test-runner/TestRunner.csproj -c Release --configfile /Users/user/MapWork/agents/splice/NuGet.Config -m:1 -nr:false -p:UseSharedCompilation=false > /Users/user/MapWork/agents/splice/results/runner-build.log 2>&1
dotnet build /Users/user/MapWork/agents/splice/repo/tests/Map.Core.Tests -c Release -p:RuntimeIdentifier=osx-arm64 -p:NuGetAudit=false -p:RunAnalyzers=false --configfile /Users/user/MapWork/agents/splice/NuGet.Config -m:1 -nr:false -p:UseSharedCompilation=false > /Users/user/MapWork/agents/splice/results/core-build.log 2>&1
dotnet build /Users/user/MapWork/agents/splice/repo/Map.slnx -c Release -p:DirectoryBuildPropsPath=/Users/user/MapWork/agents/splice/mac.props -p:NuGetAudit=false -p:RunAnalyzers=false --configfile /Users/user/MapWork/agents/splice/NuGet.Config -m:1 -nr:false -p:UseSharedCompilation=false > /Users/user/MapWork/agents/splice/results/solution-build.log 2>&1
dotnet build /Users/user/MapWork/agents/splice/repo/tests/Map.Persistence.Tests -c Release -p:RuntimeIdentifier=osx-arm64 -p:NuGetAudit=false -p:RunAnalyzers=false --configfile /Users/user/MapWork/agents/splice/NuGet.Config -m:1 -nr:false -p:UseSharedCompilation=false > /Users/user/MapWork/agents/splice/results/persistence-build.log 2>&1
```

Замеры baseline:

```sh
MAP_UNDO_MEM_AUDIT=1 MAP_UNDO_MEM_HUGE=1 dotnet /Users/user/MapWork/agents/splice/test-runner/bin/Release/net10.0/TestRunner.dll /Users/user/MapWork/agents/splice/tmp/baseline/tests/Map.Core.Tests/bin/Release/net10.0/osx-arm64/Map.Core.Tests.dll UndoMemoryGuardTests > /Users/user/MapWork/agents/splice/results/baseline-undo.log 2>&1
MAP_ON_GUARDS_AUDIT=1 dotnet /Users/user/MapWork/agents/splice/test-runner/bin/Release/net10.0/TestRunner.dll /Users/user/MapWork/agents/splice/tmp/baseline/tests/Map.Core.Tests/bin/Release/net10.0/osx-arm64/Map.Core.Tests.dll EditCostGuardTests > /Users/user/MapWork/agents/splice/results/baseline-edit.log 2>&1
dotnet /Users/user/MapWork/agents/splice/test-runner/bin/Release/net10.0/TestRunner.dll /Users/user/MapWork/agents/splice/tmp/baseline/tests/Map.Core.Tests/bin/Release/net10.0/osx-arm64/Map.Core.Tests.dll > /Users/user/MapWork/agents/splice/results/baseline-core-all.log 2>&1
```

Итоговые проверки:

```sh
dotnet /Users/user/MapWork/agents/splice/test-runner/bin/Release/net10.0/TestRunner.dll /Users/user/MapWork/agents/splice/repo/tests/Map.Core.Tests/bin/Release/net10.0/osx-arm64/Map.Core.Tests.dll SceneObjectListSpliceTests CoreHotPathEquivalenceTests > /Users/user/MapWork/agents/splice/results/splice-tests.log 2>&1
MAP_ON_GUARDS_AUDIT=1 dotnet /Users/user/MapWork/agents/splice/test-runner/bin/Release/net10.0/TestRunner.dll /Users/user/MapWork/agents/splice/repo/tests/Map.Core.Tests/bin/Release/net10.0/osx-arm64/Map.Core.Tests.dll EditCostGuardTests > /Users/user/MapWork/agents/splice/results/edit.log 2>&1
MAP_UNDO_MEM_AUDIT=1 MAP_UNDO_MEM_HUGE=1 dotnet /Users/user/MapWork/agents/splice/test-runner/bin/Release/net10.0/TestRunner.dll /Users/user/MapWork/agents/splice/repo/tests/Map.Core.Tests/bin/Release/net10.0/osx-arm64/Map.Core.Tests.dll UndoMemoryGuardTests > /Users/user/MapWork/agents/splice/results/undo.log 2>&1
dotnet /Users/user/MapWork/agents/splice/test-runner/bin/Release/net10.0/TestRunner.dll /Users/user/MapWork/agents/splice/repo/tests/Map.Core.Tests/bin/Release/net10.0/osx-arm64/Map.Core.Tests.dll > /Users/user/MapWork/agents/splice/results/core-all.log 2>&1
dotnet /Users/user/MapWork/agents/splice/test-runner/bin/Release/net10.0/TestRunner.dll /Users/user/MapWork/agents/splice/repo/tests/Map.Persistence.Tests/bin/Release/net10.0/osx-arm64/Map.Persistence.Tests.dll SchemaV4ObjectChunkTests > /Users/user/MapWork/agents/splice/results/persistence-chunks.log 2>&1
git -C /Users/user/MapWork/agents/splice/repo format-patch --stdout b6f12b18d2b5140e79294f7fc4d26336f743944b..HEAD > /Users/user/MapWork/agents/splice/splice.patch
git -C /Users/user/MapWork/agents/splice/tmp/baseline apply --check --verbose /Users/user/MapWork/agents/splice/splice.patch > /Users/user/MapWork/agents/splice/results/patch-check.log 2>&1
```

Применимость патча к стартовому дереву проверена: [patch-check.log](results/patch-check.log). `git diff --check` чистый.

## Ограничения и риски

- PARTIAL: подтверждено масштабирование фиксированных правок по guards; универсальное O(log C + changed) для растущего количества разрозненных групп не доказано. Каталог имеет больше начальных метаданных, чем массив; отдельный benchmark стоимости загрузки не проводился.
- FACT: абсолютная память не везде стала меньше. Delete10 на 200k: 163288 → 464736 B; на 1M: 341400 → 569064 B. Истории Move10/Style10/Place10 также могут удерживать больше памяти. Это цена постоянных путей/индексов; таблицы не скрывают эти случаи.
- NOT CHECKED: Windows x64, GPU, UI, ≥60 FPS в худших кадрах и pixel-identical рендер. Изменений UI/XAML/видимых текстов нет; порядок и значения объектов проверены тестами.
- NOT CHECKED: открытие конкретных новых файлов старым Windows-билдом. Схема и код сериализации не менялись; существующие 7 тестов чанкового формата прошли.
- NOT CHECKED: анализаторы. Сборки выполнялись с `RunAnalyzers=false`, `NuGetAudit=false`, offline cache.
- `run-env.sh` устанавливает `DOTNET_TieredCompilation=0` и workstation GC. Все точные байтовые результаты относятся к этому Mac-профилю: **to be rechecked on Windows**. Утверждений об универсальной нулевой аллокации нет.

## Очистка — FACT

Временный baseline worktree удалён через `git worktree remove`; удалены свои bin/obj, временные артефакты и содержимое tmp. Исходники, локальные коммиты, patch, этот отчёт, конфигурация/runner для воспроизведения и перечисленные results/*.log сохранены.

## ИТОГИ

1. Сделано: постоянный каталог чанков и локальные сплайсы; все SceneObjectList-reason guards включены и проходят, кроме намеренно оставленной причины document snapshots у Path 1M. Patch из двух локальных коммитов готов.
2. Что дальше: проверить patch и абсолютную стоимость операций/память на Windows перед приёмкой владельцем.
3. Реализация проекта: не пересчитывалась — задача касается одного хранилища Core, общий прогресс проекта не оценивался.
