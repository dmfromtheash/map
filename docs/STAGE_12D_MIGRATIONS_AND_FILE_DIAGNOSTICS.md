# Stage 12D — безопасные миграции `.mapproj` и понятная диагностика файла

## Исполняемый prompt

Выполни **Stage 12D** в Map: введи доказанную последовательную миграцию старого `.mapproj`
на новую каноническую schema, никогда не перезаписывая исходник, и доведи путь открытия до
понятной локальной диагностики, где corruption, resource limit, unsupported version и ошибки
файловой системы не смешиваются.

Это один локальный вертикальный этап: bounded inspection → классификация → безопасное решение
пользователя → migration-copy → полная проверка копии → открытие результата → адресные тесты →
один скрытый native smoke → один локальный commit. Не заменяй реализацию новым prompt, отчётом,
исследованием или стеной проверок.

---

## 1. Практический результат для владельца

После этапа владелец должен уметь:

1. выбрать существующий `.mapproj` в Dashboard;
2. сразу открыть текущий корректный файл;
3. для поддерживаемого старого файла увидеть, что нужна миграция, куда будет записана новая
   копия и что исходник останется нетронутым;
4. отменить миграцию без каких-либо записей;
5. создать новую проверенную `.mapproj`-копию, открыть именно её и продолжить обычную работу;
6. отличить:
   - повреждённый/неполный контейнер;
   - файл, который превышает лимиты текущей сборки;
   - файл более новой неподдерживаемой schema;
   - не `.mapproj`/не тот формат;
   - отсутствующий, заблокированный или недоступный файл;
7. получить короткое объяснение: что произошло, изменён ли исходный файл и что безопасно сделать
   дальше;
8. не потерять единственную существующую копию ни при cancel, ни при I/O failure, ни при сбое
   между записью и публикацией migrated copy.

Stage 12D считается завершённым только при рабочем пользовательском пути. Одних новых exception,
DTO или unit tests без реального Dashboard/open/migrate UX недостаточно.

---

## 2. Точная рабочая база и границы authority

Работай только в:

`C:\Users\Leshe\.codex\worktrees\stage10f-fonts-bindings\Map`

Известная на момент подготовки prompt база:

- branch: `stage/13-large-canvas-performance`;
- HEAD: `d780efe2afd7b0123569f60bc85c3392de7d8370`;
- последний принятый продуктовый ряд: Stage 12C → Stage 13A → 13B → 13C → 13D → узкий
  test-fix Stage 13;
- следующий не начатый функциональный этап: Stage 12D.

Перед любыми изменениями заново докажи:

```powershell
git rev-parse --show-toplevel
git branch --show-current
git rev-parse HEAD
git status --short
git log --all --oneline --decorate -20
```

Правила базы:

- не используй `C:\Projects\Map`: это старый cleanup checkout, а не база новой функции;
- если HEAD всё ещё `d780efe...`, создай/используй локальную ветку
  `stage/12d-migrations-file-diagnostics` от него;
- если появился более новый **принятый локальный product HEAD**, сначала докажи его линейность и
  прочитай обновлённый handoff; не откатывай Stage 13;
- если открытая папка старше принятого product HEAD или история расходится — остановись и сообщи
  точные branch/HEAD, не переноси изменения наугад;
- не выполняй `reset`, `restore`, `stash`, `rebase`, `amend`, history rewrite;
- не удаляй и не присваивай себе пользовательские изменения;
- до начала запомни полный список modified/untracked файлов; после каждого крупного шага сверяй,
  что чужие файлы не изменились случайно.

На момент подготовки prompt пользовательскими изменениями являются как минимум:

- `AGENTS.md`;
- `CODEX.md`;
- `docs/04_TECH_ARCHITECTURE.md`;
- `docs/06_DEVELOPMENT_ROADMAP.md`;
- `docs/07_CODEX_RUNBOOK.md`;
- `docs/08_QA_AND_ACCEPTANCE.md`;
- `docs/STATE_HANDOFF.md`;
- `docs/research/SOURCES.md`;
- `docs/research/external-sources.json`;
- `docs/STAGE_13_LARGE_CANVAS_PERFORMANCE.md`;
- `docs/research/INKARNATE_CANVAS_PERFORMANCE_20260831.md`;
- этот prompt-файл.

Не stage и не commit эти файлы автоматически. Если Stage 12D требует содержательно обновить
существующий owner-dirty документ, сначала отдели его прежние изменения от своих; правь минимальным
patch и stage только собственные строки. При семантическом конфликте не угадывай решение владельца:
production-код сохрани, конфликт точно опиши.

### GitHub hard freeze

До нового отдельного разрешения владельца запрещены:

- `git fetch`, `pull`, `push`;
- PR, issue, comment, close/reopen;
- GitHub Projects;
- auth/scope/token changes;
- tag, release, публикация;
- любые repository network actions.

Наличие `origin` не является разрешением его трогать.

---

## 3. Что обязательно прочитать до кода

Прочитай файлы полностью, последовательно, без выборочного пересказа. Если файл изменился после
первого чтения, перечитай затронутый раздел перед решением.

### Инструкции и состояние

1. `AGENTS.md`
2. `CODEX.md`
3. `docs/00_START_HERE.md`
4. `docs/STATE_HANDOFF.md`
5. `docs/DECISIONS.md`
6. `docs/GITHUB_PROJECTS.md`

### Продукт, UX, архитектура, формат, roadmap и QA

7. `docs/01_PRODUCT_VISION_AND_SCOPE.md`
8. `docs/02_FUNCTIONAL_SPEC.md`
9. `docs/03_UX_UI_SYSTEM.md`
10. `docs/04_TECH_ARCHITECTURE.md`
11. `docs/05_PROJECT_FILE_AND_DATA_MODEL.md`
12. `docs/06_DEVELOPMENT_ROADMAP.md`
13. `docs/07_CODEX_RUNBOOK.md`
14. `docs/08_QA_AND_ACCEPTANCE.md`
15. `docs/09_ASSET_PIPELINE_AND_LICENSING.md`
16. `docs/10_ENVIRONMENT.md`

### Предыдущие вертикальные контракты

17. `docs/STAGE_12A_NAMED_CHECKPOINTS.md`
18. `docs/STAGE_12C_FULL_EXPORT.md`
19. `docs/STAGE_13_LARGE_CANVAS_PERFORMANCE.md`

### Обязательный кодовый контур

Прочитай целиком все небольшие файлы и все относящиеся методы крупных файлов:

- `src/Map.Persistence/Exceptions.cs`
- `src/Map.Persistence/MapProjectFormat.cs`
- `src/Map.Persistence/MapProjectReadBudget.cs`
- `src/Map.Persistence/MapProjectZipAdmission.cs`
- `src/Map.Persistence/MapProjectJsonBudget.cs`
- `src/Map.Persistence/Container/Dtos.cs`
- `src/Map.Persistence/OpenedProject.cs`
- `src/Map.Persistence/SaveMetadata.cs`
- `src/Map.Persistence/SaveFaultInjection.cs`
- `src/Map.Persistence/MapProjectStore.cs`
- `src/Map.Persistence/Recovery/AutosaveSnapshots.cs`
- `src/Map.Persistence/Recovery/RecoveryCandidates.cs`
- `src/Map.Persistence/Recovery/RecoveryJournal.cs`
- `src/Map.Platform.Windows/DiagnosticsLog.cs`
- `src/Map.Platform.Windows/RecentMapsStore.cs`
- `src/Map.Presentation/Services/Interfaces.cs`
- `src/Map.Presentation/ViewModels/DashboardViewModel.cs`
- open/save/autosave/status regions of `src/Map.Presentation/ViewModels/EditorViewModel.cs`
- `src/Map.App/Services/AppServices.cs`
- `src/Map.App/Services/PromptWindows.cs`
- `src/Map.App/Services/TestMode.cs`
- `src/Map.App/App.axaml.cs`
- `src/Map.App/MainWindow.cs`
- `src/Map.App/Views/DashboardView.cs`

### Обязательные тестовые и tooling patterns

- `tests/Map.Persistence.Tests/MapProjectRoundTripTests.cs`
- `tests/Map.Persistence.Tests/MapProjectFailureTests.cs`
- `tests/Map.Persistence.Tests/MapProjectReadBudgetTests.cs`
- `tests/Map.Persistence.Tests/RecoveryAndHardeningTests.cs`
- `tests/Map.Persistence.Tests/TiledLayerRoundTripTests.cs`
- `tests/Map.Persistence.Tests/ProcessKillGateTests.cs`
- `tests/Map.Persistence.Tests/Support/ProjectFixtures.cs`
- `tests/Map.Persistence.Tests/Support/JsonTestMutations.cs`
- `tests/Map.Presentation.Tests/DashboardAndWizardTests.cs`
- `tests/Map.Presentation.Tests/Support/Fakes.cs`
- `src/Map.Assets/AssetLibraryDocument.cs` — только как уже существующий migration/backup pattern,
  не как готовую архитектуру `.mapproj`;
- `tests/Map.Assets.Tests/AssetLibraryMigrationTests.cs`;
- `tools/Map.Performance/README.md`;
- `tools/Map.Performance/Program.cs`;
- `tools/Map.Performance/PerformanceContract.cs`;
- `tools/Map.Performance/NativeStage13ASmoke.cs`.

Проверь историю формата, а не только текущий код:

```powershell
git log --all --oneline -- src/Map.Persistence/MapProjectFormat.cs src/Map.Persistence/MapProjectStore.cs
git log --all -G "SchemaVersion|schemaVersion" --oneline -- src/Map.Persistence tests/Map.Persistence.Tests docs/05_PROJECT_FILE_AND_DATA_MODEL.md
git show 6faa5c9:src/Map.Persistence/MapProjectFormat.cs
git show 6faa5c9:src/Map.Persistence/Container/Dtos.cs
git show 1afdf35:src/Map.Persistence/Container/Dtos.cs
```

До реализации явно зафиксируй факты:

- реальной schema v0 в истории нет;
- начальный Stage 2 и текущий writer используют schema v1;
- ранний v1 не имел `layer.type`, base layer и многих поздних полей;
- текущий reader содержит implicit `legacyStage2` compatibility;
- текущий writer по-прежнему пишет schema v1;
- текущий open только принимает ровно текущую schema и бросает исключение для любой другой;
- текущая DTO-десериализация теряет неизвестные JSON fields при последующей записи;
- неизвестный object kind сейчас считается corruption, хотя принятый формат требует placeholder
  с raw payload;
- typed corruption/resource-limit/tile/unsupported exceptions уже существуют, но Dashboard
  смешивает часть категорий в одну строку;
- текущий diagnostics log пишет переданные строки и `Exception.ToString()`, поэтому абсолютные пути
  могут попасть в лог вопреки privacy-инварианту;
- сохранение уже имеет правильный temp → read-back → flush → replace/backup pipeline, который нужно
  переиспользовать, а не дублировать несовместимой записью.

Если любой из этих фактов больше не верен, сначала адаптируй план к текущему коду и объясни
изменение. Не продолжай по устаревшему предположению.

---

## 4. Принятые продуктовые и архитектурные инварианты

Не нарушай:

- Map остаётся личным нативным офлайн-приложением Windows x64;
- никакого WebView, сайта, Electron/Tauri, server/cloud dependency;
- `Map.Core` не зависит от UI, Avalonia, Skia и persistence DTO;
- документ остаётся единственным источником истины открытой карты;
- rendering/inspection/migration preview не меняют документ;
- редактирование остаётся undoable;
- обычный save остаётся атомарным;
- migration выполняется только в новую копию;
- source file не открывается на запись и не получает новый timestamp;
- current/future/corrupted/resource-limit файлы не переписываются при обычном Open;
- embedded assets, linked refs, raw tiles, notes, grids, filters, compositions, checkpoints,
  previews, stable IDs, order и timestamps не теряются;
- recovery остаётся вне `.mapproj`;
- превышение принятого envelope — resource-limit, а не corruption;
- diagnostics остаётся локальной, bounded и без telemetry;
- лог не содержит содержимое карты, raw JSON, названия объектов/заметок/ассетов и абсолютные пути;
- no new dependency без доказанной необходимости;
- не копировать код, assets, visual style или закрытые данные Inkarnate.

Используй встроенные .NET 10 APIs и текущие зависимости. Новая библиотека для ZIP, JSON,
migration framework, logging или dialogs не нужна.

---

## 5. Решение по версиям: только честная `v1 → v2`

### Почему нужен v2

В истории проекта нет отдельной schema v0, но под номером v1 накопились два доказанных профиля:

1. ранний Stage 2 v1: object layers без `type`, без явного base layer и без поздних полей;
2. текущий полный v1: все реализованные Stage 3–13 сущности, tiles/assets/checkpoints и новые
   свойства, по-прежнему под schemaVersion 1.

Не придумывай v0 и не создавай фальшивую цепочку ради теста. Stage 12D вводит **schema v2 как
каноническую границу текущего полного формата** и одну реальную последовательную миграцию
`v1 → v2`.

V2 должен отличаться не косметическим числом, а проверяемым контрактом:

- каждый layer имеет явный поддерживаемый `type`;
- обязательные collections текущей модели представлены явно и не зависят от случайных DTO
  defaults старого reader;
- обязательный BaseColor layer является частью canonical scene, а не неявно синтезируется при
  обычном v2 open;
- неизвестный object payload не уничтожается: он становится видимым `Placeholder` и хранит
  bounded raw JSON для будущего восстановления/диагностики;
- неизвестные JSON fields v1 сохраняются при миграции там, где их можно безопасно привязать к
  stable root/entity ID; известные v2 fields всегда имеют приоритет над stale extension data;
- v2 writer выдаёт deterministic canonical bytes для одинаковых snapshot+metadata;
- обычный v2 reader строг к нарушению v2 invariants и не маскирует повреждение defaults.

Обнови `MapProjectFormat.SchemaVersion` только вместе с полным v1 reader/migrator, v2 writer,
fixtures, UI и docs. Нельзя сначала начать писать v2, оставив владельцу путь открытия v1 только
через исключение.

### Поддерживаемый диапазон

- `v1` — поддерживается только через явную migration-copy;
- `v2` — текущая schema, открывается напрямую;
- `< 1`, нецелое/отсутствующее schema, неправильный `format` — malformed/not-a-Map-project,
  не «старая поддерживаемая версия»;
- `> 2` — unsupported newer version, без попытки downgrade/migration;
- следующий migration step не регистрируется, пока нет реальной следующей schema.

---

## 6. Bounded inspection до materialization

Добавь один явный read-only inspection/classification boundary в `Map.Persistence`.

Он должен:

1. открыть source только `FileAccess.Read` с подходящим share mode;
2. применить существующий `MapProjectZipAdmission` до `ZipArchive` materialization;
3. проверить canonical entry names, case-insensitive duplicates, entry/category/total budgets и
   compression ratio через существующие механизмы;
4. bounded-прочитать и parse только нужный manifest;
5. различить format/schema до полного DTO graph;
6. при current v2 продолжить полную текущую read/validate цепочку;
7. при v1 вернуть `MigrationRequired`, не открывать source на запись и не мигрировать молча;
8. при future schema вернуть `UnsupportedVersion`;
9. не считать `MapProjectResourceLimitException` corruption;
10. не извлекать ZIP entries на диск;
11. не обходить существующие JSON token/string/cardinality limits в migration path.

Не делай вторую, более слабую ZIP/JSON проверку. Вынеси общие части текущего `ReadAndValidate`
так, чтобы обычный open, inspection и migration использовали один набор admission/budget/checksum
правил.

### Typed result

Введи компактный тип результата, эквивалентный следующей семантике (точные имена можешь выбрать
под стиль проекта):

- `Ready`;
- `MigrationRequired` (`sourceSchema`, `targetSchema`);
- `UnsupportedVersion` (`sourceSchema`, `maxSupportedSchema`);
- `NotMapProject`;
- `Corrupted`;
- `ResourceLimit`;
- `NotFound`;
- `AccessDenied`;
- `IoFailure`.

Результат содержит:

- стабильный диагностический code, пригодный для unit tests и локального лога;
- безопасный короткий русский title/message;
- подтверждение `SourceWasModified = false` для всех failure/cancel путей;
- только безопасные структурные детали: schema numbers, canonical entry name, budget category,
  tile layer/coordinate, если они уже доступны из typed exception;
- внутренний exception для отладки только если он не выводится напрямую пользователю/в лог.

Не передавай UI сырые stack traces и тексты произвольных внутренних исключений.

---

## 7. Последовательный migration registry

Создай простой registry `vN → vN+1`, а не один hard-coded `if (version == 1)` внутри UI.

Требования:

- каждый step объявляет точные `SourceVersion` и `TargetVersion`;
- registry строит только непрерывную цепочку;
- gap, duplicate step, reverse step и target выше current schema являются programmer error и
  адресно тестируются;
- единственный production step этого этапа — `v1 → v2`;
- step не имеет доступа к UI и не принимает решения за пользователя;
- миграция deterministic для одних source bytes/options;
- source читается, но никогда не открывается на запись;
- миграция не использует текущий permissive v2 DTO parse как доказательство корректности v1;
- каждый шаг проверяет свой input и output invariants;
- после последнего шага результат проходит полный current-v2 `MapProjectStore.Open`/validation.

Не добавляй внешний migration package.

---

## 8. Контракт `v1 → v2`

### Что обязано сохраниться

Для каждого корректного v1 fixture после migration-copy докажи:

- `DocumentId`, title, created/modified timestamps и canvas;
- порядок, stable ID, type, visibility, lock, opacity, blend и mask каждого layer;
- все objects, их порядок, IDs, geometry, groups/sublayer, styles и editability;
- raster/mask tile coordinates, exact bytes и checksums;
- embedded/linked asset metadata, exact embedded bytes, thumbnail, provenance/license и hashes;
- notes/anchors/markdown;
- grids, filters, compositions и their nested objects/assets;
- named checkpoints: catalog, names, IDs, timestamps, previews и полный snapshot payload;
- основной preview bytes;
- unknown fields, где безопасное stable attachment доказуемо;
- unknown object raw payload через placeholder;
- deterministic result при повторной миграции одинакового source в два новых destinations.

### Ранний Stage 2 v1

Используй существующий реальный fixture pattern из
`TiledLayerRoundTripTests.Stage2_scene_without_layer_types...` и ранний committed DTO shape.

Migration должна:

- явно распознать исторический v1 shape;
- создать deterministic BaseColor layer тем же доказанным ID algorithm, который сейчас использует
  `LegacyBaseLayerId`;
- назначить старым untyped layers тип `Object` только по доказанному историческому контракту;
- заполнить отсутствующие поздние optional/default поля ровно текущими принятыми defaults;
- не применять legacy heuristic к обычному v2 open;
- сохранить stable object/layer IDs и порядок.

### Текущий полный v1

Создай/зафиксируй маленький deterministic fixture через текущий v1 writer до повышения версии либо
через test fixture builder, отражающий фактический wire format на base HEAD. Он должен покрывать не
только два rectangles, а минимум:

- BaseColor + Object + Brush/Land/Grid/Filter layers;
- mask/tile payload;
- stamp/embedded или linked asset;
- line/shape/text/composition;
- note;
- named checkpoint.

Не создавай гигантский binary fixture. Предпочти маленький source-controlled fixture builder и
точные field-by-field assertions. Если нужен бинарный `.mapproj` golden, он должен быть маленьким,
объяснимым и действительно представлять committed v1, а не generated evidence.

### Unknown fields и unknown object

Не ограничивайся тем, что `System.Text.Json` по умолчанию игнорирует unknown property: это как раз
теряет данные при записи.

Раздели два разных контракта, не создавая второй источник истины рядом с `MapDocument`:

1. **Произвольные unknown fields v1:** сохрани их в опубликованной migration-copy в том же JSON
   object либо в документированной v2 extension envelope со stable owner ID. Это гарантия
   `source v1 → migrated v2`; она не требует тащить произвольный persistence-sidecar через каждую
   следующую пользовательскую правку. Известные v2 fields всегда имеют приоритет. Если поле нельзя
   безопасно привязать, объясни конкретную причину и оставь source v1 нетронутым; молча удалить его
   во время migration нельзя.
2. **Неизвестный object kind:** это часть самой карты, поэтому его bounded opaque payload обязан
   стать persistence-agnostic данными placeholder в document model и переживать
   migration → open → normal save/autosave/checkpoint → reopen. `Map.Core` не должен зависеть от
   `JsonElement`, persistence DTO или UI; храни opaque UTF-8/строку в минимальном immutable виде с
   явным byte budget.

Дополнительные правила unknown object:

- создавай `SceneObjectKind.Placeholder` только если общий object shell (stable ID, bounds,
  transform, visibility/lock и базовый style) сам валиден;
- известный kind с повреждёнными обязательными полями остаётся corruption, а не превращается в
  placeholder;
- неизвестный layer type не становится object placeholder;
- сохраняй исходный kind и exact bounded raw JSON payload;
- renderer получает только безопасную placeholder geometry/style;
- UI показывает понятную причину placeholder, но не raw content;
- payload не исполняется и не интерпретируется.

---

## 9. Безопасная публикация migrated copy

Пользователь выбирает новый destination. Предложенное имя:

`<original stem> — migrated v2.mapproj`

Точный текст может быть естественным русским, но имя не должно выглядеть как исходник.

Алгоритм:

1. нормализовать source и destination;
2. запретить один и тот же path с учётом Windows case-insensitivity;
3. до записи убедиться, что destination не существует;
4. создать unique temp в каталоге destination;
5. записать v2 контейнер существующим canonical writer pipeline;
6. применить все per-entry/category/total budgets;
7. закрыть ZIP;
8. полный read-back через current-v2 reader;
9. проверить document identity и полную семантическую сохранность, а не только counts;
10. flush temp to disk;
11. опубликовать только `File.Move(temp, destination, overwrite: false)` или эквивалентным
    create-new атомарным шагом;
12. ещё раз открыть опубликованный destination;
13. только после успеха открыть копию в Editor и добавить destination в Recent;
14. source bytes, size, last-write timestamp и openability должны остаться прежними.

Не используй source как destination. Не вызывай `File.Replace` для исходника. Не заменяй уже
существующий destination и не создавай его `.bak`: пользователь должен выбрать другое имя.

При ошибке:

- исходник остаётся byte-for-byte прежним;
- существующий destination остаётся прежним;
- owned temp удаляется bounded best effort только после проверки, что это exact созданный этим
  вызовом temp;
- если temp нельзя удалить, UI/diagnostic сообщает безопасное имя temp без автоматической
  публикации;
- результат не добавляется в Recent и не считается открытым/сохранённым.

Cancel до или после выбора destination не создаёт ничего.

---

## 10. Dashboard/open UX

Сделай путь async и unit-testable через существующие presentation interfaces/fakes.

### Current v2

- открывается как сейчас;
- LastError очищается;
- Recent получает v2 path;
- никакого migration prompt.

### Legacy v1

Покажи отдельный явный prompt, не красную generic error:

- title: `Нужно обновить копию проекта`;
- что найден schema v1 и текущая schema v2;
- `Исходный файл не будет изменён`;
- primary action: `Создать копию и открыть`;
- safe default/cancel: `Отмена`;
- после primary action — save picker с отличимым suggested filename;
- после успешной миграции — короткий статус с именем новой копии и открытие Editor на destination;
- Editor считается clean и bound к migrated destination;
- исходный v1 не добавляется заново поверх migrated recent entry.

Если destination уже существует, пользователь получает понятное сообщение и может выбрать другое
имя; приложение не перезаписывает файл.

### Future version

Сообщение отвечает:

- файл создан более новой версией/schema;
- эта сборка поддерживает максимум v2;
- downgrade не выполнялся;
- файл не изменён;
- безопасное действие: открыть его более новой сборкой/оставить как есть.

Не предлагай migration.

### Resource limit

Сообщение отвечает:

- файл не объявлен повреждённым;
- какой класс лимита превышен: container/entry/assets/tiles/JSON/preview/semantic count;
- текущая сборка отказалась читать его безопасно;
- файл не изменён;
- не предлагай «починить» или мигрировать.

Не показывай внутренние bytes/ratios как единственное объяснение; добавь понятную формулировку.

### Corruption / wrong file

Различай минимум:

- `Это не файл проекта Map` для неправильного `format`/не-ZIP;
- `Проект повреждён или записан не полностью` для missing entry, checksum, malformed JSON,
  semantic inconsistency и corrupt tile;
- safe structural detail: entry name или layer/tile coordinate, если уже известны;
- `Исходный файл не изменён`;
- существующие recovery cards остаются доступны, но не утверждай, что конкретная recovery относится
  к файлу, пока identity не доказана.

Не предлагай автоматическое исправление corruption и не переписывай контейнер.

### File-system failures

Отдельно обработай:

- not found;
- access denied/read-only;
- sharing/locked или иной I/O failure.

Удаляй recent entry только при доказанном not-found. Недоступный/locked файл не выкидывай из
Recent как удалённый.

### UI details

- каждое новое control имеет стабильный `Name`, accessible name и видимый keyboard focus;
- важные действия имеют текст, не icon-only;
- Cancel является безопасным default;
- длинное сообщение wraps и не требует огромного окна;
- не создавай modal на current-v2 success;
- migration/inspection выполняются off UI thread, но результат применяется на UI thread;
- повторный click не запускает две миграции одного source в один destination;
- во время операции виден busy/status, но окно не зависает;
- ошибка/cancel возвращают Dashboard в рабочее состояние.

---

## 11. Локальная диагностика и privacy

Доведи существующий `DiagnosticsLog` и `IAppLog` только настолько, насколько нужно Stage 12D.

Требования:

- bounded rotation сохраняется;
- никакой telemetry/network;
- structured/stable event code для inspect/open/migrate success/cancel/failure;
- абсолютный source/destination/recovery path не пишется в обычный лог;
- title, note markdown, object/asset names, raw JSON, embedded bytes и hashes содержимого не пишутся;
- `Exception.ToString()` не пишется слепо, если может включить path/content;
- допустимы: exception type, sanitized category/code, schema numbers, canonical ZIP entry,
  resource category, tile coordinate;
- UI может показать выбранный пользователем path в текущем окне, но лог не получает его;
- migration success фиксирует только `v1→v2` и факт validated copy;
- cancel — нормальное INFO-событие, не ERROR;
- логирование никогда не ломает open/migration.

Не строи отдельную большую Diagnostics page, GPU/driver dashboard или export-report: это вне
Stage 12D. Здесь нужна диагностика именно открытия/миграции файла.

---

## 12. Recovery и save semantics

- Обычный current-v2 save/autosave продолжает использовать существующий atomic pipeline.
- Migrated copy открывается clean; autosave не создаётся до реальной правки.
- V1 source не становится target для autosave/manual save после открытия v2 copy.
- Recovery candidate по-прежнему открывается dirty и не перезаписывает target до явного save.
- Recovery scan не должен пытаться автоматически мигрировать старый/будущий/corrupt snapshot.
  Старый recovery-кандидат можно пропустить с sanitized diagnostic; нельзя уничтожать его на
  догадке.
- Успешная migration не очищает существующий recovery journal исходного документа автоматически:
  migrated copy и recovery — разные решения пользователя.
- Обычная `.bak` семантика save не меняется.

---

## 13. Обязательные адресные тесты

Добавь Stage 12D tests с именами/trait/filter, позволяющими запускать только этот этап.

### Persistence — минимум

1. current v2 save/open roundtrip;
2. current v2 deterministic canonical JSON;
3. early Stage 2 v1 inspection → `MigrationRequired`;
4. early v1 → v2 migration preserves stable IDs/order/geometry и deterministic BaseColor ID;
5. full current-v1 fixture → v2 сохраняет layers/objects/tiles/assets/notes/grids/filters/
   compositions/checkpoints и exact payload bytes;
6. migration source bytes и last-write timestamp неизменны;
7. destination same as source rejected before write;
8. existing destination untouched;
9. cancel/no destination creates no file/temp;
10. injected failure before publish leaves source/destination intact and no false success;
11. published copy passes full read-back and reopens;
12. registry rejects gaps/duplicates/reverse steps;
13. absent/zero/negative/non-integer schema is malformed, not migration-required;
14. future v3+ is unsupported and untouched;
15. wrong format/not-ZIP classified separately;
16. missing entry/checksum/malformed JSON/semantic mismatch classified corrupted;
17. corrupt tile keeps typed layer/coordinate detail;
18. each existing resource-limit family remains resource-limit and untouched;
19. unknown fields сохраняются в опубликованной v2 migration-copy в documented safe form;
20. unknown object становится видимым placeholder, а exact bounded raw payload переживает
    migration → open → normal save/autosave/checkpoint → reopen;
21. oversized unknown payload is rejected by resource budget, not allocated without bound;
22. migration preserves named checkpoint nested data and does not create recursive checkpoints;
23. recovery scan does not delete unsupported/corrupt legacy candidate.

Не ослабляй существующие hardening tests и budgets, чтобы новый happy path стал зелёным.

### Presentation — минимум

1. current v2 opens without prompt;
2. v1 shows migration choice and source-untouched text;
3. cancel leaves Dashboard usable and does not touch Recent;
4. successful migrated copy opens Editor clean on destination and touches only destination Recent;
5. same/existing destination produces actionable error without overwrite;
6. duplicate click cannot publish twice;
7. corrupted/resource-limit/unsupported/not-found/access-denied/I/O messages differ;
8. not-found removes Recent, access denied/locked does not;
9. logs contain stable codes but no absolute path/title/raw exception text;
10. recovery cards remain visible after unrelated open failure.

### Fault injection

Расширь существующий узкий `SaveFaultInjection` или введи migration-specific equivalent для
точных boundaries:

- after temp write, before read-back;
- after read-back/flush, before publish;
- destination appears between preflight and create-new publish.

Не создавай отдельный большой process-kill suite: source уже никогда не заменяется, а один
адресный fault test достаточно доказывает Stage 12D boundary. Существующий Stage 2 kill gate не
повторяй без изменения его входов.

---

## 14. Один native UI smoke без управления ПК владельца

Изменён пользовательский open/migrate flow, поэтому нужен один короткий smoke в настоящем
Avalonia/Win32 окне текущего ПК.

Используй существующий безопасный own-process pattern из `tools/Map.Performance`:

- добавь минимальный `native-stage12d` profile или равно малый reusable own-process harness;
- не добавляй новый automation dependency;
- не восстанавливай удалённые исторические UI drivers;
- окно настоящее, но:
  - `ShowActivated=false`;
  - `ShowInTaskbar=false`;
  - manual off-screen position;
  - отдельный STA thread/process;
  - только собственный visual tree;
  - никаких `SendInput`, WinAppDriver, foreground activation, global mouse/keyboard, clipboard,
    screen scraping или переключения чужих окон;
- test profile, recent, diagnostics, recovery и `.mapproj` paths находятся только в уникальном
  temporary root;
- не читать и не писать реальный `%LOCALAPPDATA%\Map` владельца;
- interaction вызывается прямыми own-process control commands/events или testable view-model
  methods;
- smoke имеет timeout и гарантированно закрывает своё окно.

Smoke одним запуском доказывает:

1. реальный Dashboard виден в hidden window;
2. ранний v1 вызывает видимый migration prompt с safe Cancel;
3. own-process primary action создаёт выбранную новую v2 copy;
4. source SHA-256 и timestamp не изменились;
5. destination проходит v2 open и Editor bound к destination;
6. corrupted fixture показывает corruption state;
7. resource-limit fixture показывает другой resource-limit state;
8. future v3 fixture показывает unsupported-version state;
9. UI остаётся responsive;
10. после shutdown нет живого окна/process и temporary profile не смешан с реальным.

Не сохраняй screenshots/evidence folder по умолчанию. Короткий JSON/text result допустим только в
ignored `tmp/` и должен быть удаляемым generated output.

---

## 15. Минимальный verification budget

После законченного production change выполни ровно относящиеся проверки, по одному разу на
неизменных входах:

```powershell
dotnet test .\tests\Map.Persistence.Tests\Map.Persistence.Tests.csproj -c Release --filter "FullyQualifiedName~Stage12D"
dotnet test .\tests\Map.Presentation.Tests\Map.Presentation.Tests.csproj -c Release --filter "FullyQualifiedName~Stage12D"
dotnet build .\Map.slnx -c Release
dotnet run --project .\tools\Map.Performance\Map.Performance.csproj -c Release -- --profile native-stage12d --output .\tmp\stage12d-native.json
```

Если точный runner CLI после минимальной реализации отличается, укажи фактическую команду, но не
расширяй scope.

Порядок:

1. адресный Persistence filter;
2. адресный Presentation filter;
3. один Release build после всех production edits;
4. один native smoke после build.

Если одна проверка красная, исправь причину и перезапусти только её после изменившего входы fix.
Не повторяй неизменённую зелёную проверку «для уверенности».

Не запускай без отдельного нового запроса:

- full suite;
- VM/clean Windows/другой ПК;
- packaging/installer/updater/portable/self-contained;
- benchmark/performance matrix Stage 13;
- coverage/mutation/security scan/SBOM;
- DPI/accessibility matrix;
- independent acceptance/review session;
- GitHub actions или remote checks.

---

## 16. Документация после реализации

Минимально и фактически обнови, если owner-dirty state позволяет отделить свои строки:

- `docs/05_PROJECT_FILE_AND_DATA_MODEL.md`:
  - current schema v2;
  - exact v1 support and v1→v2 contract;
  - canonical v2 invariants;
  - unknown field/object preservation rule;
  - copy-only publication algorithm;
- `docs/06_DEVELOPMENT_ROADMAP.md`:
  - Stage 12D FACT/PARTIAL только по реально пройденным gates;
- `docs/04_TECH_ARCHITECTURE.md`:
  - inspection/classification/migration boundary и sanitized diagnostics, если это ещё не отражено;
- `docs/08_QA_AND_ACCEPTANCE.md`:
  - только новый адресный gate, без расширения глобальной матрицы;
- `docs/STATE_HANDOFF.md`:
  - exact branch/HEAD;
  - что реализовано;
  - что реально проверено;
  - что не проверялось и почему;
  - все сохранённые ограничения;
  - следующее единственное действие: использовать v2/migration flow на реальной копии карты
    владельца; новый функциональный этап не начинать автоматически.

Не создавай review/evidence/prompt/process files сверх уже запрошенного контракта. Не переписывай
историю Stage 13 и не объявляй старые `PARTIAL/BLOCKED` доказательства внезапно закрытыми.

---

## 17. Commit discipline

После зелёных адресных gates и проверки diff создай один локальный commit:

`feat(stage-12d): add safe project migrations and file diagnostics`

Перед commit:

```powershell
git status --short
git diff --check
git diff --stat
git diff -- src tests tools docs
```

Stage только собственные Stage 12D файлы/hunks. Не включай:

- исходные owner changes;
- этот prompt-файл, если он остаётся пользовательским untracked artifact;
- generated `tmp/`, `bin/`, `obj/`, screenshots, logs;
- unrelated formatting;
- старые research/evidence artifacts.

После commit:

```powershell
git rev-parse HEAD
git show --stat --oneline --decorate HEAD
git status --short
```

Докажи, что оставшийся dirty state совпадает с сохранёнными пользовательскими файлами и не
является незакоммиченной частью Stage 12D. Push не выполняй.

---

## 18. Stop-gates

Остановись и сообщи точный `BLOCKED`, не маскируя его обходом, если:

- правильный worktree/HEAD нельзя доказать;
- более новый accepted product HEAD расходится с указанной базой;
- пользовательские изменения конфликтуют с обязательной schema/architecture правкой и их нельзя
  безопасно отделить;
- migration требует перезаписать source или существующий destination;
- v1 fixture не проходит строгую schema-specific validation без ослабления budgets;
- unknown payload нельзя сохранить bounded без нарушения Core/UI boundaries;
- UI smoke потребовал бы системного ввода, активации или управления ПК владельца;
- current v2 writer не удаётся сделать deterministic и fully validated;
- source changed хотя бы по bytes/timestamp на cancel/failure/success;
- зелёный test достигается удалением/ослаблением существующего hardening gate.

Не считать blocker:

- необходимость небольшого refactor `MapProjectStore` для общего reader pipeline;
- обновление fakes/interfaces вслед за typed open result;
- добавление маленьких deterministic fixtures;
- один повтор красной адресной проверки после реального fix.

---

## 19. Definition of Done

Stage 12D завершён только если одновременно:

- [ ] работа выполнена в правильном worktree от доказанного product HEAD;
- [ ] Stage 13 production functionality сохранена;
- [ ] schema v2 имеет документированный несовместимый canonical contract;
- [ ] есть только реальная production migration `v1→v2`, без выдуманной v0;
- [ ] ранний Stage 2 v1 и полный текущий v1 fixtures мигрируют;
- [ ] source никогда не перезаписывается и остаётся byte-for-byte/timestamp прежним;
- [ ] existing destination никогда не перезаписывается;
- [ ] migrated copy полностью read-back validated до открытия;
- [ ] semantic equality охватывает весь текущий документ и checkpoints;
- [ ] unknown fields безопасно сохраняются в migration-copy без второго source of truth;
- [ ] unknown object становится placeholder с bounded raw payload;
- [ ] corrupted/resource-limit/unsupported/not-map/file-system categories различаются;
- [ ] user-facing текст отвечает «что произошло / сохранены ли данные / что делать»;
- [ ] log sanitized и не содержит path/content/raw exception;
- [ ] recovery/save semantics не сломаны;
- [ ] два адресных Stage12D filters зелёные;
- [ ] один Release build зелёный;
- [ ] один own-process hidden native smoke зелёный без управления ПК;
- [ ] полный suite и запрещённые проверки не запускались;
- [ ] GitHub не тронут;
- [ ] создан один локальный commit только со Stage 12D changes;
- [ ] handoff честно отражает FACT/PARTIAL/BLOCKED.

---

## 20. Финальный отчёт

Ответь преимущественно по-русски, коротко и доказательно. Обязательная структура:

```markdown
## ИТОГИ

### Что сделано
- ...

### Что реально проверено
- команда / результат / количество passed, failed, skipped;
- Release build warnings/errors;
- native smoke: какое настоящее окно, какие состояния, подтверждение отсутствия системного ввода;
- SHA/timestamp source preservation;
- branch и commit.

### Что не проверялось
- полный suite, VM, packaging, DPI matrix и т. п. — не запускались по текущим правилам;
- оставшиеся PARTIAL/BLOCKED, если есть.

### Что дальше
- один конкретный безопасный шаг владельца, без автоматического начала нового этапа.

### Процент готовности проекта
- оценка общего продукта с кратким объяснением, что это scope estimate, а не измеренная метрика.
```

Не называй Stage 12D `DONE`, если хотя бы один пункт Definition of Done не доказан. В этом случае
используй `PARTIAL` или `BLOCKED`, сохрани рабочий checkpoint и перечисли ровно оставшееся.

---

## 21. Первичные технические ресурсы

Используй их только как справку к уже выбранным .NET APIs, не как разрешение расширять scope:

- Microsoft Learn —
  [`JsonExtensionDataAttribute`](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.serialization.jsonextensiondataattribute): unknown JSON properties могут собираться в
  словарь и снова сериализоваться; привязку, budgets и precedence известных полей всё равно нужно
  спроектировать и протестировать;
- Microsoft Learn —
  [`File.Replace`](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.replace): заменяет destination и создаёт backup; подходит обычному save,
  но **не** migration source и не уже существующему migration destination;
- Microsoft Learn —
  [`File.Copy`](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.copy) / `File.Move`:
  destination для migration публикуется как новый файл
  без overwrite;
- Microsoft Learn —
  [`ZipArchive`](https://learn.microsoft.com/en-us/dotnet/api/system.io.compression.ziparchive):
  работай streaming через entries, не извлекай архив в filesystem;
- Microsoft Learn —
  [ZIP/TAR best practices](https://learn.microsoft.com/en-us/dotnet/standard/io/zip-tar-best-practices): path traversal и zip-bomb protection уже реализованы
  существующими Map admission/budget rules и не должны ослабляться.

Никакой внешний репозиторий, sample code или новый NuGet package для выполнения Stage 12D не
нужен.
