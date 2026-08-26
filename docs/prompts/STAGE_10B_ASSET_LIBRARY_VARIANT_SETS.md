# Stage 10B — локальная Library v2, наборы вариантов и stamp scatter

## Контекст

Работа выполняется в `C:\Projects\Map` как следующий owner-approved крупный срез после
проверенного Stage 10 input parity (`95d2fe8`). Перед любым кодом полностью прочитай:

1. `AGENTS.md` и `CLAUDE.md`;
2. `docs/00_START_HERE.md` и `docs/STATE_HANDOFF.md`;
3. этот файл;
4. `docs/DECISIONS.md`, особенно DEC-003, DEC-008, DEC-009, DEC-010, DEC-012, DEC-013, DEC-014;
5. `docs/02_FUNCTIONAL_SPEC.md` разделы 6 и 15;
6. `docs/05_PROJECT_FILE_AND_DATA_MODEL.md` раздел «Ассеты»;
7. `docs/06_DEVELOPMENT_ROADMAP.md` Stage 10;
8. `docs/09_ASSET_PIPELINE_AND_LICENSING.md`;
9. `docs/research/ASSET_CATALOG_AND_VARIANT_SETS.md` и
   `docs/research/TEST_ASSET_FIXTURES.md`;
10. `docs/11_CLAUDE_VERIFICATION_PROTOCOL.md`.

Затем верни короткий план с границами и gates. Не начинай изменения, пока не проверишь, что
worktree чистый. Запиши `BASE = git rev-parse HEAD`, затем создай/переключись на
`stage/10-asset-library-sets` от этого BASE. Если branch уже существует или состояние отличается,
остановись и опиши факт; не reset/rebase/amend.

Фактическая база:

- нативный offline Windows app: .NET 10 + Avalonia 12.1.1 + SkiaSharp;
- `AssetCatalog` уже имеет SHA-256 identity/dedupe, PNG/JPEG/WebP validation, linked/stored mode,
  provenance, thumbnail, favorite, metadata-only search 10 000 records и атомарный `catalog.json`;
- `MapAssetReference` и `StampStyle.AssetSha256` уже сохраняют конкретный content hash;
- `PlaceAssetObjectsCommand` сейчас принимает только один asset reference;
- UI каталога сейчас плоский список; Area/Density/set navigation отсутствуют;
- проверенный baseline suite: 199/199, но ты обязан получить собственный свежий baseline до кода;
- полный repository format historically может находить unrelated legacy formatting. Не меняй
  посторонние файлы: проверяй формат всех изменённых C# файлов и отдельно фиксируй baseline.

Clean-room: реализуем только наблюдаемую функциональную семантику и собственную информационную
архитектуру. Нельзя копировать чужие assets, category/tag names, trade dress, icons, тексты,
внутренние модели или код. Никаких DevTools/network extraction и обязательной сети.

## Единственная цель

Пользователь может в полностью локальной собственной Library импортировать одиночный ассет или
набор из нескольких/десятков вариантов, найти и раскрыть логическую карточку набора, выбрать или
переключить вариант и разместить один либо много stamps с deterministic randomization так, чтобы
undo/redo и save/reopen воспроизводили те же SHA, ObjectId, позиции и transforms.

Это один крупный вертикальный срез: catalog storage → presentation state → native UI → edit
command → persistence/render verification. Не объявляй весь Stage 10 готовым.

## In scope

### 1. Library schema v2 и безопасная миграция

- Ввести явную версию локального catalog document.
- Сохранить `AssetRecord` как content metadata по SHA-256 либо эквивалентно разделить content и
  variant без дублирования bytes.
- Добавить собственные модели:
  - `AssetCollection`: stable ID, optional parent ID, name, order;
  - `AssetVariant`: stable ID, content SHA, display label, optional anchor/default scale;
  - `AssetSet`: stable ID, display name, ordered variant IDs, cover variant ID, collection ID,
    tags, set-level favorite;
  - bounded recent и last-selected-variant per set.
- Одиночный импорт автоматически создаёт singleton set.
- Multi-file import создаёт один set, позволяет задать собственное имя, reorder variants и выбрать
  cover. Удаление связи variant из set не удаляет content/original.
- Старый JSON-массив `AssetRecord` мигрирует в singleton sets, сохраняя SHA, file metadata,
  stored/linked paths, pack, tags, favorite и provenance.
- Миграция записывается через unique temp + validation + atomic replace + bounded backup. При
  invalid/damaged index старая единственная копия не перезаписывается; UI показывает диагностику.
- Stable singleton IDs выводятся детерминированно из SHA либо сохраняются один раз; filename не
  является identity.

### 2. Query model и производительность

- Query поддерживает: text, collection/pack, tag, untagged, favorite, used-in-current-map,
  asset type и imported date range.
- Sort: name A–Z/Z–A и imported newest/oldest.
- Group: collection/pack или tag.
- Результат возвращает логические `AssetSet`, badge количества и cover metadata; originals не
  декодируются при load/search/filter/sort.
- Thumbnail decode остаётся lazy и bounded; UI показывает только видимые карточки через настоящую
  Avalonia virtualization, не создаёт 10 000 тяжёлых controls/bitmaps.
- Сохранить существующий gate: load < 2 s и warm query P95 ≤ 100 ms на 10 000 logical sets на
  reference machine. Добавить измерение allocated thumbnails/visible item count, чтобы тест не
  был зелёным при скрытом full-grid materialization.

### 3. Собственная native Library UI

- Перестроить текущий `Assets` tab в собственную desktop Library:
  - collection tree с counts;
  - breadcrumbs;
  - search;
  - sort/group/filter controls;
  - virtualized thumbnail grid;
  - Recent/Favorites;
  - Import one / Import set;
  - set tile с badge «N вариантов», expand/collapse или собственная detail область;
  - выбранный variant: `N / total`, previous/next, cover/current distinction.
- Не копировать чужую компоновку один-в-один. Использовать существующие Map tokens, controls,
  русскую терминологию и icon system.
- Все icon-only controls получают UIA `Name`, role, expanded/selected/checked state, keyboard
  focus и tooltip. Tree/grid имеют предсказуемый Tab/arrow flow; focus сохраняется после filter.
- Явные состояния: empty, no-results, loading thumbnail, missing, hash mismatch, unsupported,
  damaged catalog. Никакого ложного success.
- Favorites относится к set. Recent bounded минимум 100 set/variant selections и не включает
  private data в telemetry — telemetry вообще нет.

### 4. Set-aware Stamp tool и клавиатура

- Tool state различает selected set и current variant.
- Manual navigation:
  - `Alt+Right`/`Alt+Left` — next/previous variant;
  - `Alt+wheel` — cycle variants;
  - `F` в Stamp tool — фокус/открытие Library;
  - `Q/A` — Area radius up/down;
  - `Alt+Q/Alt+A` — Density up/down.
- Сочетание считается реализованным только при реальной функции и UI feedback. Не ломай
  существующие shortcuts DEC-012 и text/brush/mask context.
- Toggle `Случайный вариант после размещения` использует seeded PRNG. После успешного One
  placement выбирается следующий current variant; cancel/error не меняют его.
- `One` размещает current variant. `Many`/Area может выбрать отдельный variant для каждого stamp,
  когда random toggle включён.
- Собственная измеримая семантика:
  - Area = radius в world units; при active grid дополнительно показать эквивалент в cells;
  - Density = stamps на 10 000 квадратных world units;
  - count = `clamp(round(density * PI * radius^2 / 10000), 1, 5000)`;
  - точки равномерно sampling внутри круга; angle/flip randomization применяются тем же seed.
- Один pointer gesture = одна undo transaction. До создания command разрешить полный immutable
  placement plan: ObjectId, variant SHA, asset reference, position, size, rotation и flips.
- Расширить/заменить `PlaceAssetObjectsCommand`, чтобы одна command безопасно принимала несколько
  разных asset references. Undo удаляет только действительно введённые и больше нигде не
  используемые references; redo возвращает тот же план без reroll.
- Renderer не знает о set и PRNG, не меняет document и рисует конкретный `AssetSha256`.
- При выборе существующего stamp Library пытается найти containing set по SHA. Если set/catalog
  отсутствует, объект всё равно полностью работает и экспортируется как раньше.

### 5. Два собственных logical diagnostic fixtures

- Добавить test-only source generator и tracked outputs под `tests/fixtures/assets/`, не включать
  их в publish/starter pack.
- `diagnostic-stamp-set`: 12 уникальных 512×512 RGBA PNG variants по контракту
  `TEST_ASSET_FIXTURES.md`, manifest с stable IDs/order/cover/provenance/SHA.
- `diagnostic-brush-tip.png`: один 256×256 RGBA singleton fixture.
- Это намеренно технические контрольные изображения, не production art. Никаких скачанных или
  похожих на чужие деревьев/гор/наборов.
- Generator должен повторно выдавать byte-identical outputs на той же locked SkiaSharp версии;
  тест сверяет все SHA, PNG signature/IHDR/dimensions/alpha и отсутствие publish inclusion.
- Разрешено добавить маленький `tools/Map.DiagnosticAssets` console project, используя только уже
  принятую/зафиксированную в solution SkiaSharp dependency; новую библиотеку не добавлять.

### Разрешённые production/test paths

Меняй только необходимое внутри этого закрытого списка:

- `src/Map.Assets/**`;
- `src/Map.Core/Editing/Commands.cs` и при необходимости новый узкий asset-placement файл в
  `src/Map.Core/Editing/`;
- `src/Map.Presentation/ViewModels/EditorViewModel.cs`;
- `src/Map.Presentation/ViewModels/EditorShortcutSettings.cs`;
- `src/Map.Presentation/Services/CatalogStampImageSource.cs` только если нужен bounded cache/API;
- `src/Map.App/Views/EditorView.cs`;
- `src/Map.App/Controls/MapCanvasControl.cs` и `src/Map.App/MainWindow.cs` только для pointer/key
  routing этого среза;
- соответствующие `tests/Map.Assets.Tests/**`, `tests/Map.Core.Tests/**`,
  `tests/Map.Persistence.Tests/**`, `tests/Map.Presentation.Tests/**`;
- `tests/fixtures/assets/**`;
- optional `tools/Map.DiagnosticAssets/**` и минимальные solution/project references для него;
- новый `scripts/drive-stage10b-asset-library.ps1`;
- `docs/STATE_HANDOFF.md` и Stage 10 status в `docs/06_DEVELOPMENT_ROADMAP.md` только после
  фактически пройденных gates.

Если нужен другой production path, новая dependency или изменение `.mapproj` schema — остановись
и запроси решение; не расширяй allowlist скрытно.

## Out of scope

- multi-selection/Ctrl+click, именованный group container, group inspector и group breadcrumbs;
- полный Stamp/Text/Line/Shape/Brush inspector и новые effects;
- `IsometricSquares`, grid property sheet и snap topology;
- font catalog/cycling и configurable bindings;
- Stage 9B checkpoints, resize/crop/export/migrations project format;
- raster brush engine/pen pressure, line portal width и shape boolean changes;
- production starter pack, production artwork, marketplace, accounts, publishing, sync, network;
- delete originals/content, mass destructive relink, SVG import;
- refactor всего `EditorView`, новая архитектура UI или новая visual system;
- push, PR, release, installer, amend, rebase или переписывание истории.

## Инварианты

1. `Map.Core` не зависит от Avalonia, Skia или `Map.Assets`.
2. UI не является источником истины документа; renderer не изменяет документ.
3. Каталог локальный и не нужен для запуска, save/reopen/render/export уже размещённых stamps.
4. Content identity — SHA-256; same filename никогда не заменяет другой content тихо.
5. Все edit gestures проходят через commands и дают детерминированный undo/redo.
6. Сохранение catalog index атомарное; migration не перезаписывает единственную старую копию.
7. IDs collections/sets/variants и document/assets/objects стабильны.
8. Не загружать все originals/thumbnails; caches bounded, large map/full-canvas allocation не
   возвращаются.
9. Private imports, screenshots и test map content не входят в Git/release fixtures.
10. Не снижать существующие проверки/limits и не «лечить» тест удалением assertion.

## Acceptance criteria

1. Fresh baseline до кода записан; после кода все baseline tests остаются зелёными.
2. Старый плоский catalog fixture мигрирует в v2 singleton sets без потери каждого поля и с
   читаемым backup; повторное открытие idempotent.
3. Kill/fault test на миграции/SaveIndex оставляет хотя бы одну валидную старую или новую копию;
   damaged index не заменяется пустым catalog.
4. Single и 12-file import создают соответственно singleton и ordered set; reimport одинаковых
   bytes не дублирует content.
5. Search/sort/group/all filters/favorite/recent работают на logical sets и сохраняются после
   restart каталога.
6. 10 000-set profile проходит load < 2 s, warm query P95 ≤ 100 ms без decode originals и без
   materialization всех thumbnails/controls.
7. Native UI показывает tree/breadcrumb/grid, badge 12, expand variants, current `N/12`,
   previous/next, Recent и Favorites; UIA names/states/focus доказаны.
8. `Alt+Right/Left`, `Alt+wheel`, `F`, `Q/A`, `Alt+Q/Alt+A` выполняют реальные контекстные команды
   и не конфликтуют с существующим input profile.
9. One placement выбранного variant создаёт один stamp с точным SHA. Random-after-place меняет
   current variant только после успешной команды.
10. Один Many/Area gesture с минимум тремя разными SHA создаёт одну history entry; undo удаляет
    все, redo возвращает byte-for-byte эквивалентный placement plan без reroll.
11. Save/reopen `.mapproj` без локального catalog рендерит и экспортирует те же variants; никаких
    изменений project schema ради set membership.
12. Replace selected stamp variant сохраняет transform/style и меняет только asset/reference +
    понятное имя одной undoable command.
13. Diagnostic generator и 13 PNG outputs проходят SHA/dimensions/alpha/manifest tests и не
    попадают в `Map.App` publish output.
14. Настоящее Release-окно проходит happy path и failure path missing/hash-mismatch/damaged-index
    без crash или ложного success.
15. Worktree после одного commit чистый; diff содержит только разрешённые paths; Stage 10 остаётся
    `PARTIAL`, если любой обязательный gate не доказан.

## Required verification

### До кода

```powershell
git status --short --branch
git rev-parse HEAD
powershell -ExecutionPolicy Bypass -File .\scripts\doctor.ps1
dotnet restore .\Map.slnx --force-evaluate
dotnet build .\Map.slnx -c Release --no-restore
dotnet test .\Map.slnx -c Release --no-build --blame-hang --blame-hang-timeout 2m
```

Зафиксируй baseline test count, warnings и известные NuGet/package findings до изменений.

### Автоматически после реализации

```powershell
dotnet restore .\Map.slnx --force-evaluate
dotnet build .\Map.slnx -c Release --no-restore
dotnet test .\Map.slnx -c Release --no-build --blame-hang --blame-hang-timeout 2m
dotnet list .\Map.slnx package --vulnerable --include-transitive
dotnet list .\Map.slnx package --deprecated --include-transitive
python -m pre_commit run --all-files
```

Дополнительно:

- targeted Assets migration/query/performance tests;
- Core multi-reference command deterministic undo/redo tests;
- Persistence save/reopen without local catalog;
- Presentation shortcut/state tests;
- generator reproducibility + publish-exclusion test;
- `dotnet format ... --verify-no-changes --include` для каждого изменённого `.cs` path. Если full
  repository format по-прежнему падает на baseline unrelated files, приложи точный before/after,
  не форматируй их самовольно.

### Настоящее окно Windows

Создай `scripts/drive-stage10b-asset-library.ps1`, который запускает именно свежий Release
`Map.App.exe` с изолированным temp catalog/profile и завершает только запущенный им процесс.
Проверить минимум:

1. импорт diagnostic singleton и 12-variant set;
2. tree/breadcrumb/search/filter/favorite/recent;
3. expand set, variant `1/12` → `2/12` keyboard/mouse, random toggle;
4. One placement и Many/Area placement минимум с тремя SHA;
5. undo/redo, save, close, reopen при временно недоступном local catalog;
6. missing/hash-mismatch и damaged catalog copy без потери исходного индекса;
7. UIA Name/role/state/focus для icon buttons, tree, set tile, badge, variant controls;
8. 100%, 150% и 200% DPI либо честный `PARTIAL`, если среда не позволяет переключение.

Сохранить screenshots/logs/temporary `.mapproj` в игнорируемом
`artifacts/stage10b-asset-library/`. Screenshot не заменяет проверку UIA, файла и SHA. Не включай
private user assets.

### Git gate

Перед commit:

```powershell
git status --short
git diff --check
git diff --name-status $BASE..HEAD
git diff --stat
git ls-files --stage
```

Для ещё не закоммиченного среза используй также `git diff --name-status` и `git diff --cached`.
Установленный pre-commit hook обязателен. После свежего полного self-check создай **один** commit:

```text
feat(stage-10): add local asset sets and deterministic placement
```

Не push. После commit сообщи `BASE`, `HEAD`, полный A/M/D/R список и остановись. Не исправляй свой
commit через amend/rebase; возможные дефекты Codex даст отдельным prompt и новым commit.

## Required report

```markdown
## RESULT
Одно предложение о реально работающем пользовательском результате.

## CHANGED
- каждый path — назначение; отдельно schema/migration, UI, commands, fixtures, tests

## VERIFIED
- baseline и final test counts
- build/package/pre-commit/format
- migration/fault/performance numbers
- SHA и placement determinism evidence

## MANUAL UI CHECK
- шаги в настоящем Release-окне, UIA и screenshots

## PARTIAL / BLOCKED
- каждый непройденный criterion и почему; без слова «готово» при пробеле

## RISKS
- migration, cache/virtualization, deterministic PRNG, DPI и linked-file risks

## SCOPE CHECK
- BASE/HEAD, branch, clean worktree, A/M/D/R список, подтверждение out-of-scope

## NEXT
- один следующий отдельный срез: 10C multi-selection/groups; не реализовывать его сейчас
```
