# Stage 12C — полноценный export и отмена

Статус: `IMPLEMENTED` 2026-08-31. Файл сохранён как исторический исполняемый контракт этапа.

Этот файл — исполняемый контракт следующей отдельной задачи. Он разрешает реализацию Stage 12C и
один локальный commit только после прохождения указанных ниже ворот. Он не разрешает GitHub,
Stage 12D или любую другую работу вне этого контракта.

## Роль и результат

Ты — основной исполнитель Stage 12C проекта Map. Работай на `gpt-5.6-sol` с высоким reasoning.

Нужен один законченный пользовательский срез: владелец открывает заметную команду «Экспорт…»,
выбирает PNG/JPEG/WebP, всю карту или прямоугольный регион, размер, фон и сетку, видит честный
preview и оценку работы, запускает фоновый export, наблюдает progress, может отменить и получает
валидный файл либо пакет тайлов. Исходная карта и уже существующий destination не меняются при
cancel/ошибке.

Не ограничивайся отчётом или новым планом: после минимальной проверки состояния реализуй этот
срез, проверь его по указанным воротам и создай один локальный commit. GitHub не трогай.

## Обязательная стартовая проверка

Рабочая папка:

```text
C:\Users\Leshe\.codex\worktrees\stage10f-fonts-bindings\Map
```

Принятый production-результат Stage 12B:

```text
branch: stage/12b-resize-crop-region
commit: 5863099ce7716812b231deb8dcfe496655a9bd56
message: feat(stage-12b): add canvas resize and region extraction
```

Сначала:

1. Полностью прочитай `AGENTS.md`, `CODEX.md`, `docs/STATE_HANDOFF.md` и этот контракт.
2. Прочитай относящиеся разделы `docs/01_PRODUCT_VISION_AND_SCOPE.md`,
   `docs/02_FUNCTIONAL_SPEC.md`, `docs/03_UX_UI_SYSTEM.md`,
   `docs/04_TECH_ARCHITECTURE.md`, `docs/05_PROJECT_FILE_AND_DATA_MODEL.md`,
   `docs/06_DEVELOPMENT_ROADMAP.md`, `docs/08_QA_AND_ACCEPTANCE.md` и решения DEC-001, DEC-002,
   DEC-003, DEC-005, DEC-009, DEC-016, DEC-017.
3. Зафиксируй точные root, branch, `HEAD`, status и `git log --all`.
4. Текущий чистый `HEAD` должен быть потомком `5863099` и содержать только локальное принятие
   Stage 12B/этот контракт после production-commit. Если появились неизвестные production-правки
   или dirty owner-файлы, не присваивай и не сбрасывай их: остановись и сообщи точную границу.
5. Создай локальную ветку `stage/12c-full-export-cancel` строго от проверенного текущего
   принятого `HEAD`. Не используй `C:\Projects\Map` и не начинай от `main`, Stage 10F/11/12A.

Нельзя выполнять `reset`, `restore`, `stash`, rebase, amend или переписывать историю.

## Жёсткая граница GitHub

В этой задаче запрещены любые обращения и записи в GitHub:

- не делать fetch/pull/push;
- не читать и не менять issues, comments, PR и GitHub Project;
- не запускать `gh auth`, не менять scopes;
- не создавать tag и не отправлять ветку в remote.

Допустим только один локальный stage-commit после зелёных ворот.

## Что уже есть и должно быть переиспользовано

- `MapDocument` — immutable source of truth; `Session.Document` меняется заменой snapshot.
- `SceneRenderer` — единственный scene evaluator viewport/thumbnail/export.
- `RenderOptions.IsExport` уже исключает editor chrome и соблюдает `GridDefinition.IncludeInExport`.
- `ThumbnailRenderer` показывает правильный CPU offscreen-путь, но не является полноценным
  exporter и не должен разрастись в него.
- Stage 12B уже даёт валидируемый прямоугольный region и canvas до 65 536 px.
- Assets могут быть embedded, catalog или project-linked; missing asset уже отображается честным
  placeholder.
- Исторический spike доказывал 4K PNG, но spike-код не production-код: не восстанавливай его
  вслепую и не создавай второй renderer.

Перед изменением изучи текущие реализации `SceneRenderer`, `TileImageCache`,
`CatalogStampImageSource`, `EditorViewModel`, `EditorView`, file picker/prompts и текущие адресные
test-паттерны.

## Функциональный контракт

### 1. Форматы и destination

- Одиночный файл: PNG, JPEG/JPG или WebP.
- PNG и WebP поддерживают прозрачность.
- JPEG всегда непрозрачный; при выборе JPEG фон принудительно включён, а UI объясняет почему.
- Для JPEG/WebP есть качество `1..100`, default `90`; PNG не показывает ложную настройку качества.
- Расширение, выбранный encoder и фактический header должны совпадать.
- После успеха UI показывает полный локальный путь и действие «Открыть папку».
- Никаких upload, cloud, аккаунта или сети.

### 2. Область и размер

- Источник: вся фактическая граница canvas или прямоугольный region внутри canvas.
- Region задаётся точными `X/Y/Width/Height`; переиспользуй Stage 12B geometry/validation, не
  создавай расходящийся второй контракт прямоугольника.
- Нулевой, отрицательный, NaN/overflow или выходящий за canvas region не запускает export и даёт
  понятную локальную ошибку.
- Пользователь задаёт output width/height в пикселях; aspect ratio исходной области по умолчанию
  связан. Разрыв связи должен быть явным, а preview должен показывать полученное растяжение.
- Максимум одиночного изображения — 16 384 px по длинной стороне. Запрос больше автоматически не
  пытается выделить full surface и требует режим «Пакет тайлов» или меньший размер.

### 3. Фон, сетка и editor chrome

- «Прозрачный фон» означает не рисовать обязательный `BaseColorLayer`; это доступно только для
  PNG/WebP.
- При выключенной прозрачности используется фактический фон документа, без отдельного
  расходящегося цвета.
- «Сетка» — master switch. Если он выключен, сетка не экспортируется. Если включён, экспортируются
  только grid definitions с `IncludeInExport=true`, независимо от editor visibility.
- Selection, handles, marquee, mask-edit tint, resize/region boundary overlay, guides и другие
  editor-only элементы никогда не попадают ни в preview, ни в результат.
- Notes сейчас являются sidebar metadata и не имеют принятого canvas-вида. Не выдумывай marker,
  markdown-layout или новый визуальный язык в Stage 12C: notes остаются вне изображения, как
  требует их текущий default. Отдельный notes-export возможен только после нового UX-решения.

### 4. Preview и оценка

- Preview bounded: не больше 960×540 и не зависит памятью от полного output.
- Он использует тот же snapshot, region transform, background/grid flags, asset resolution и
  `SceneRenderer`, что и финальный export.
- Изменение настройки отменяет устаревший preview и не позволяет позднему результату перетереть
  более новый.
- До запуска видны: итоговые размеры, формат, single/tiled, число тайлов и честная верхняя оценка
  raw working memory. Не выдавай предполагаемый compressed file size за точный.
- Оценки используют checked 64-bit arithmetic; overflow становится понятной validation error.

### 5. Фоновая работа, progress и cancel

- На Start захватывается конкретный immutable `MapDocument` snapshot и все настройки операции.
- Рендер/encode/write выполняются не на UI thread с `CancellationToken` и monotonic progress.
- Progress различает хотя бы подготовку, rendering/tiles, encoding/write и завершение.
- UI остаётся отзывчивым; кнопка Cancel доступна во время операции и не означает успех до
  фактической остановки/cleanup.
- Редактирование после Start не меняет уже запущенный export. Завершение export не меняет current
  document, revision, dirty state, history, selection, viewport, path, checkpoints или autosave.
- Worker не использует disposable UI-owned Skia cache из другого потока. Сделай bounded
  operation-owned `TileImageCache` и безопасный snapshot/resolver ассетов либо докажи
  thread-safety существующего источника адресным тестом.

### 6. Одиночный файл и атомарность

- Результат сначала пишется в уникальный sibling temp-файл.
- Перед окончательной заменой он flush-ится и декодируется/read-back проверкой: формат, width и height.
- Только валидный завершённый temp атомарно заменяет выбранный destination.
- Cancel, render/encode/write error или read-back failure удаляет только temp и сохраняет прежний
  корректный destination byte-for-byte.
- Ошибка отвечает: что произошло, карта/старый файл сохранены ли, что сделать дальше.
- Allocation/encoder failure для большого single-image предлагает меньший размер или пакет тайлов.

### 7. Пакет тайлов для 16K+

- Результат больше 16 384 px по любой стороне создаётся только как явный пакет тайлов, не как один
  bitmap и не как скрытая попытка single-image.
- Пакет — отдельная выбранная папка. В ней после успеха находятся `manifest.json` версии `1` и
  детерминированные файлы `tile-y####-x####.<ext>`.
- Manifest содержит: schema version, исходный region, общий output width/height, tile width/height,
  columns/rows, format/quality, transparency/background/grid flags и относительные имена каждого
  тайла с его точным pixel rectangle.
- Рабочий tile edge — 2048 px; крайние tiles имеют фактический остаточный размер. Один render
  surface не превышает tile плюс вычисленный effect bleed.
- Тайлы пишутся последовательно или с явно bounded parallelism. Нельзя materialize все decoded
  tiles/encoded bytes одновременно.
- Для blur/shadow/filter границ используется вычисленный overlap/bleed и обрезка центральной
  части, чтобы между tiles не появлялись швы. Не маскируй seam только тестовой сценой без эффектов.
- Весь пакет сначала создаётся в уникальной sibling temp-папке; manifest пишется последним после
  read-back каждого tile. Success публикует готовую папку одним rename.
- Существующая destination-папка не удаляется и не перезаписывается: UI просит новое имя. Cancel
  или ошибка удаляет только temp-папку.
- Тестовый reassemble небольшого пакета должен совпасть с single render по размерам и пикселям в
  пределах зафиксированной encoder tolerance; отдельно проверь границу tile с blur/shadow.

## Архитектурная форма

Создай предусмотренный `docs/04_TECH_ARCHITECTURE.md` production-проект `src/Map.Export` и добавь
его в `Map.slnx`. Он зависит внутрь от `Map.Core` и `Map.Rendering`; Core не знает об export/UI.
Новые NuGet-пакеты не добавляй.

В `Map.Export` должны жить независимо тестируемые понятия, а не Avalonia controls:

- format/mode/request/validated plan;
- region/output geometry и checked estimate;
- bounded preview/final/tile render orchestration;
- progress/result/error contract;
- atomic single-file и tile-package publication.

Названия типов выбирай по существующему стилю; не создавай абстракции без реального второго
потребителя. При необходимости минимально расширь `RenderOptions` явными export policy flags,
сохранив старое поведение viewport/thumbnail/tests по умолчанию.

UI и lifecycle остаются в `Map.App`/`Map.Presentation`:

- заметная текстовая кнопка «Экспорт…» рядом с file actions;
- один связный export surface, предпочтительно существующая правая панель, а не россыпь модалок;
- controls имеют имена/accessible labels и доступны клавиатурой;
- preview, validation, progress, Cancel, success/error видны в одном месте;
- отдельные интерфейсы для export destination и открытия папки допускают адресные tests без
  настоящего системного диалога/Explorer.

Не хранить export preferences в `.mapproj` и не менять его schema. DPI metadata, новый renderer
для notes и глобальные default-export settings не входят в этот stage: текущий roadmap требует
pixel size, а не миграцию настроек.

## Порядок реализации

1. Зафиксируй короткий план и критерии; не создавай evidence/review-пакет.
2. Добавь чистый request/plan/estimate контракт и адресные tests на validation/overflow.
3. Добавь single-file CPU exporter через существующий `SceneRenderer`, форматы и атомарную запись.
4. Добавь tiled package, bounded memory, bleed, manifest, cancel/cleanup.
5. Подключи snapshot-safe background operation и preview к Presentation/App.
6. Добавь заметный пользовательский entry point, progress/cancel и понятные состояния.
7. Выполни ровно минимальные проверки ниже, обнови handoff/roadmap и создай один локальный commit.

Если реальный код требует иной порядок, меняй порядок, но не продуктовый результат и инварианты.

## Адресные acceptance-критерии

Один целевой test project/filter должен доказать как минимум:

- PNG действительно имеет alpha при прозрачном фоне;
- JPEG декодируется, непрозрачен и имеет выбранные размеры;
- WebP декодируется и соответствует выбранной alpha/background policy;
- whole-map и offset region имеют правильную transform/обрезку/размер;
- grid master switch и `IncludeInExport` работают независимо от editor visibility;
- preview и final идут через одну policy/scene path, editor chrome отсутствует;
- single-file temp/read-back/replace сохраняет старый destination при cancel и injected error;
- пакет 16K+ планируется тайлами без создания full-size surface;
- tile manifest полный и reassemble не имеет seam, включая эффект у границы;
- cancel между tiles прекращает работу, чистит temp и не публикует ложный success;
- export snapshot остаётся прежним при параллельной замене current document;
- source document/history/dirty/path/checkpoints не меняются ни при success, ни при failure.

Тест не должен реально выделять гигантский 16K/65K bitmap: используй small test tile edge/surface
factory/observer, чтобы доказать максимальный размер каждого allocation и число tile passes.

## Единственные обязательные ворота

После законченного production-изменения:

1. Один адресный test-project/filter `Stage12C` для изменённой рискованной логики.
2. Один `dotnet build .\Map.slnx -c Release` — ровно один раз после последнего изменения входов.
3. Один короткий native UI smoke в настоящем скрытом Avalonia/Win32 окне текущего ПК,
   `ShowActivated=false`, `ShowInTaskbar=false`, без `SendInput`, foreground activation и
   переключения чужих окон.

Smoke должен пройти пользовательскую вертикаль: открыть отдельную тестовую карту с vector,
raster/mask, asset и export-enabled grid; открыть «Экспорт…»; увидеть preview/estimate; сделать
маленький PNG region-export; decode/read-back; начать multi-tile operation и отменить; подтвердить
responsive UI, отсутствие итогового cancel-результата и неизменность карты.

Не запускать full suite, coverage, mutation, benchmark, security scan, DPI/accessibility matrix
или повтор зелёных ворот. Не создавать постоянные screenshots/artifacts.

## Документы, commit и итог

После зелёных ворот минимально обнови:

- `docs/STATE_HANDOFF.md` — точный Stage 12C result, фактически выполненные gates и тогдашний
  статус Stage 12D;
- `docs/06_DEVELOPMENT_ROADMAP.md` — Stage 12C реализован, без расширения Stage 12D;
- при необходимости `docs/00_START_HERE.md`/`CODEX.md` — только актуальный вход.

Не редактируй `docs/DECISIONS.md`, если не возникло настоящее новое архитектурное решение, которое
невозможно принять из уже действующих DEC. Не создавай отдельный review/evidence prompt.

Создай один локальный commit:

```text
feat(stage-12c): add bounded image export and cancellation
```

Не делай push. После commit заново проверь exact root/branch/HEAD/status и дай короткий русский
отчёт:

1. что работает для владельца;
2. что реально проверено;
3. что сознательно не проверялось;
4. exact local commit и чист ли worktree;
5. каков был тогдашний статус Stage 12D и что GitHub не тронут.

## Stop-gates

Остановись без разрушительных действий и сообщи `BLOCKED`, если:

- стартовый `HEAD` не содержит принятый `5863099` или есть неизвестные owner production changes;
- нужный encoder отсутствует в текущем Skia runtime и его нельзя честно реализовать без новой
  зависимости/изменения scope;
- единственный реалистичный дизайн 16K+ требует full-canvas allocation;
- невозможно гарантировать сохранность существующего destination при cancel/error;
- реализация требует schema migration `.mapproj`, Stage 12D, GitHub или новый внешний сервис.

Во всех остальных случаях сделай разумное минимальное допущение внутри этого контракта, явно
зафиксируй его в коде/test и доведи вертикальный срез до локального commit.
