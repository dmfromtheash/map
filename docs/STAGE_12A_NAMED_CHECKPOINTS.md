# Stage 12A — именованные checkpoints

## Статус

`READY AFTER CONSOLIDATION`.

Stage 12A закрывает функцию, исторически называвшуюся 9B, но не откатывает продукт к Stage 9.
Новая ветка называется `stage/12a-named-checkpoints` и начинается от последнего product HEAD.

Исходный product HEAD на момент утверждения:

`2c0a698e15573badb22a54de7ff1687ab784a9b2`

## Подготовительный checkpoint

Текущая уборка находится в `C:\Projects\Map` на старом Stage 10D и не может быть базой функции.
Перед Stage 12A нужно в чистом worktree создать новую ветку от `2c0a698...` и перенести поверх неё
смысл уборки:

- сохранить весь production-код Stage 10E, 10F и полезный Stage 11;
- сохранить app icon и test-profile isolation;
- удалить только benchmarks, VM/packaging/release scripts, drivers, старые prompts/reviews,
  generated evidence и неиспользуемые dev-tools;
- оставить `Map.slnx` только с семью production-проектами;
- объединить version `0.2.0` с `win-x64` и `SelfContained=false` в `Directory.Build.props`;
- сохранить адресные test sources вне обычного solution;
- перенести актуальные правила, roadmap, handoff, DEC-016 и DEC-017.

После одной Release-сборки это фиксируется отдельным локальным cleanup commit. Только затем в той
же ветке начинается код Stage 12A.

## Пользовательский результат

В настоящем Map владелец может:

1. создать checkpoint текущей карты и дать ему название;
2. увидеть название, время и preview;
3. продолжить редактирование, не меняя сохранённый checkpoint;
4. сохранить, закрыть и открыть `.mapproj`;
5. восстановить checkpoint без скрытой потери текущего состояния;
6. отменить restore одной командой Undo и повторить Redo;
7. удалить ненужный checkpoint после подтверждения.

## In scope

- stable checkpoint ID, trimmed non-empty name, creation timestamp и preview;
- immutable document state на момент создания;
- bounded количество и общий объём с понятным отказом без автоматического удаления;
- хранение в предусмотренной `.mapproj` области `checkpoints/`;
- отсутствие рекурсивного вложения каталога checkpoints в каждый snapshot;
- atomic save через существующий persistence pipeline;
- save/reopen без изменения checkpoint;
- restore одной undoable-командой;
- сохранение текущих `documentId`, открытого пути и checkpoint catalog при restore;
- честные dirty/save/error states;
- компактный раздел «Контрольные точки» в существующем редакторе;
- create/list/restore/delete и подтверждения опасных действий.

Если backward-compatible optional entries недостаточно и требуется несовместимая смена
`schemaVersion`, работа останавливается с `BLOCKED`: Stage 12D нельзя начинать скрытно внутри 12A.

## Out of scope

- Stage 12B resize/crop/new-from-region;
- Stage 12C full export/progress/cancel;
- Stage 12D migrations и общая diagnostics-страница;
- cloud history, sync, аккаунт и публикация;
- сравнение checkpoints и document branching;
- автоматические checkpoints;
- новые dependencies;
- VM, installer, packaging, full-suite и большие evidence-папки.

## Acceptance

1. Checkpoint создаётся из текущего immutable document snapshot и появляется в UI.
2. Последующие правки не меняют checkpoint.
3. После save/close/reopen сохраняются metadata, preview и точное состояние checkpoint.
4. Restore является одной записью истории; Undo возвращает состояние до restore, Redo — обратно.
5. `documentId`, текущий путь и остальные checkpoints не теряются при restore.
6. Create/delete/restore не показывают ложный `Saved` и не выполняют скрытый manual save.
7. Ошибка чтения/записи оставляет документ и предыдущий корректный `.mapproj` без изменения.
8. Лимит checkpoint объясняется пользователю и не удаляет старые данные молча.
9. В настоящем Release-окне проходит сценарий «До эксперимента → правка → save/reopen → restore →
   Undo → Redo → delete».

## Проверка

- один `dotnet build .\Map.slnx -c Release` после cleanup checkpoint;
- один `dotnet build .\Map.slnx -c Release` после законченного Stage 12A;
- один небольшой интеграционный `FullyQualifiedName~Stage12A` filter в
  `Map.Persistence.Tests`, без full suite;
- один короткий smoke в настоящем окне текущего ПК;
- никаких VM, coverage, mutation, benchmark, packaging или повторов зелёного snapshot.

## Git и GitHub

- разрешённая ветка: `stage/12a-named-checkpoints`;
- cleanup и feature являются двумя разными локальными commits;
- push/PR/tag/release запрещены без нового прямого разрешения;
- GitHub status синхронизируется только с уже существующей точной карточкой и доступным scope;
- отсутствие remote/Project не блокирует локальный stage;
- схема: `docs/GITHUB_PROJECTS.md`.

## Следующий этап

После фактического завершения 12A единственный следующий срез — Stage 12B: resize/crop и новая
карта из региона.
