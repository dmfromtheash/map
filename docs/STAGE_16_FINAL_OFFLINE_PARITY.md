# Stage 16 — финальная функциональная полнота нативного офлайн-редактора Map

Выполни Stage 16 как последний номерной функциональный этап Map. Это один общий продуктовый этап,
но не один монолитный diff: проведи его последовательно через четыре законченных среза `16A–16D`,
после каждого оставляя рабочий продукт и проверяя только затронутую логику. Не начинай новый
необязательный `P2`-этап.

## 1. Результат этапа

После Stage 16 владелец должен уметь без скрытых demo-команд и без сети пройти полный принятый
путь первой стабильной версии:

1. найти, отсортировать, создать, клонировать, безопасно удалить или восстановить локальную карту;
2. начать с собственного локального шаблона или минимального оригинального starter pack Map;
3. импортировать проверенный пользовательский ассет, включая ограниченный безопасный SVG;
4. полноценно рисовать Brush и Land, редактировать маски, слои и базовые Filters;
5. создавать и напрямую редактировать Line/Shape/Text, узлы, portals и boolean-операции;
6. вести форматированные локальные Notes;
7. сохранять, переоткрывать, клонировать, переносить композиции и экспортировать результат;
8. настроить реально действующие локальные параметры приложения и открыть офлайн-справку.

`FACT` для Stage 16 означает **100% принятого нативного офлайн-scope P0/P1**, а не копию всего
сервиса Inkarnate. Любой обязательный пункт ниже, оставшийся `PARTIAL` или `BLOCKED`, запрещает
объявлять весь этап и проект завершёнными.

## 2. Неподвижная граница продукта

В Stage 16 входят только собственные clean-room реализации публично наблюдаемых рабочих сценариев
и принятые локальные возможности Map.

Не входят и не считаются недостающими функциями проекта:

- аккаунты, подписки, Marketplace, Community и социальные функции;
- совместное редактирование, облачное хранение/синхронизация и обязательная сеть;
- копирование кода, API, ассетов, текстов, иконок, layout pixel-for-pixel, брендинга или trade dress
  Inkarnate;
- опциональный Line clip-to-Brush с пока неясной семантикой, folder layers, rotate Land selection,
  стек нескольких масок, vignette/blur/sharpen Filters и прочие пункты `P2`;
- полноценный SVG-редактор: SVG только безопасно импортируется и растрируется в локальный asset;
- большой художественный pack. Starter pack содержит только минимальные оригинальные нейтральные
  материалы Map; дальнейшее наполнение — отдельная контентная работа владельца.

Не возвращай исключённые функции в scope под видом «паритета». Не резервируй под них пустые панели.

## 3. Точная исходная точка

Работай только здесь:

`C:\Users\Leshe\.codex\worktrees\stage10f-fonts-bindings\Map`

На момент подготовки этого задания последний принятый product snapshot:

- branch: `codex/stage-15-stamp-appearance-placement-parity`;
- `HEAD`: `835c94491230e793f9526dfd40dda215ff9c3200`;
- remote `origin/codex/stage-15-stamp-appearance-placement-parity` указывает на тот же commit;
- Stage 12D commit `68a8982c8ebaab145d05bddc7f47c26fc9fca063`, Stage 13, Stage 14 и Stage 15 уже являются
  предками этого `HEAD`.

Перед изменениями заново докажи `git rev-parse --show-toplevel`, branch, `HEAD`, remotes, status и
`git log --all --decorate --oneline`. Если появился более новый принятый product commit, не стартуй
со старого снимка: сначала установи точную линейную границу без reset/rebase/history rewrite.

Создай новую ветку `codex/stage-16-final-offline-parity` от актуального принятого product `HEAD`.
Не используй `C:\Projects\Map` как базу нового функционального этапа.

## 4. Существующие изменения владельца

На момент подготовки prompt в worktree были изменения владельца:

- `AGENTS.md`;
- `CODEX.md`;
- `docs/04_TECH_ARCHITECTURE.md`;
- `docs/06_DEVELOPMENT_ROADMAP.md`;
- `docs/07_CODEX_RUNBOOK.md`;
- `docs/08_QA_AND_ACCEPTANCE.md`;
- `docs/STATE_HANDOFF.md`;
- `docs/research/SOURCES.md`;
- `docs/research/external-sources.json`;
- untracked Stage 12D/13/14/15 prompts и актуальный performance audit.

Считай их owner-owned, даже если status к моменту запуска изменится. Не используй
`reset`, `restore`, `stash`, `clean`, `rebase`, `amend`, `git add .` или `git add -A`. Перед каждым
commit перечисляй точные paths и проверяй staged diff.

Документы `CODEX.md`, roadmap и handoff разрешено минимально привести к итоговой правде только в
`16D`: сохрани все существующие owner hunks, не переписывай файлы целиком и не включай несвязанные
правки в commit. `AGENTS.md` не изменяй. Если owner hunks нельзя безопасно отделить, production-код
не откатывай; оставь документы вне commit и честно сообщи точный конфликт.

Сам этот prompt является запрошенным артефактом планирования, а не доказательством реализации.

## 5. Что обязательно прочитать до кода

Прочитай актуальные версии:

- `AGENTS.md`, `CODEX.md`, `docs/STATE_HANDOFF.md`;
- `docs/01_PRODUCT_VISION_AND_SCOPE.md`;
- `docs/02_FUNCTIONAL_SPEC.md`, особенно разделы 1, 4–5, 7–16, 20–22;
- `docs/03_UX_UI_SYSTEM.md`, `docs/04_TECH_ARCHITECTURE.md`;
- `docs/06_DEVELOPMENT_ROADMAP.md`, `docs/08_QA_AND_ACCEPTANCE.md`;
- `docs/09_ASSET_PIPELINE_AND_LICENSING.md`, `docs/DECISIONS.md`;
- `docs/research/INKARNATE_RESEARCH.md`;
- `docs/research/INKARNATE_ACCOUNT_WALKTHROUGH_20260826.md`;
- `docs/research/OBJECT_AND_GRID_PARITY.md`;
- `docs/research/ASSET_CATALOG_AND_VARIANT_SETS.md`;
- `docs/research/INKARNATE_CANVAS_PERFORMANCE_20260831.md`;
- `docs/research/SOURCES.md` и `docs/research/external-sources.json`;
- Stage 14/15 prompts только для уже принятой placement/workspace/stamp границы.

Затем адресно прочитай текущие production-классы и tests соответствующего среза. Не доверяй
старому status-тексту, если текущие Git ancestry, source и tests доказывают более новую реальность.

## 6. Сохранить уже реализованное

Не переписывай и не обходи существующие решения:

- нативные .NET 10/Avalonia 12.1/SkiaSharp, без WebView и сервера;
- `MapDocument` как единственный источник истины;
- reversible `IEditCommand`, одна gesture — одна history transaction;
- sparse raster tiles, spatial index, viewport/chunk rendering и лимиты Stage 13;
- atomic save/recovery, typed inspection и copy-only migration Stage 12D;
- transient placement/ghost, Versions, primary autosave и workspace Stage 14;
- полный Stamp appearance, random/area placement и настоящий Clip Mask Stage 15;
- Library v2 с sets/variants/search/filters/recent/favorites/virtualization;
- multi-selection/groups, четыре Grid geometry, object inspectors, shortcuts;
- существующие модели, persistence и rendering для Land, Brush, Line, Shape, Text, Notes, masks,
  Filters и compositions.

Сначала подключай уже существующую модель к честному пользовательскому управлению. Не создавай
второй engine или параллельное состояние ради нового UI.

## 7. Stage 16A — локальная библиотека, ассеты, композиции, настройки и справка

### 7.1 Dashboard и карты

Доведи локальную библиотеку карт до принятого поведения:

- поиск по имени/типу и metadata-only sort по имени, типу, дате и размеру без открытия/изменения
  каждого `.mapproj`;
- независимый clone существующего проекта с новым `documentId`, новым именем и атомарной
  записью в новый path;
- local templates: создание карты из встроенного или пользовательского template всегда создаёт
  независимый документ и не меняет источник;
- удаление только после явного подтверждения; на текущем Windows предпочесть Корзину. Tests и
  smoke работают только на синтетических temporary files, никогда на реальных картах владельца;
- «Импорт backup» сначала вызывает существующий bounded `Inspect`, показывает происхождение и
  состояние ассетов, затем открывает текущую схему или предлагает copy-only migration;
- понятные empty/error/recovery states и неизменность Recent при отмене/ошибке;
- локальная справка по первой карте, инструментам, recovery и shortcuts; отсутствие сети не ломает
  экран и не скрывает локальную инструкцию.

Не сканируй весь диск. Library roots должны быть явными, bounded и настраиваемыми.

### 7.2 Asset Manager и безопасный SVG

Закрой оставшиеся P1-контракты каталога:

- missing-asset flow: найти исходник, заменить с проверкой SHA/формата, оставить placeholder или
  удалить только после осознанного подтверждения;
- import/export локального composition package с manifest, hashes и provenance; clipboard между
  двумя открытыми картами сохраняется как быстрый путь, но не заменяет файл package;
- минимальный оригинальный starter pack Map: только собственные нейтральные geometric stamps,
  brush tips/texture и manifest с явной provenance/license; никаких материалов Inkarnate;
- ограниченный SVG profile без новой dependency: bounded XML admission, запрет `script`, animation,
  `foreignObject`, HTML, CSS/network imports, external/data/file URLs, fonts, filters и references;
  разрешить только явно поддержанные статические primitives/path, solid fill/stroke и bounded local
  transforms, затем растрировать существующим SkiaSharp в PNG asset. Ошибка отклоняет import без
  частичного catalog record;
- соблюдать текущие encoded/pixel/decode/cache budgets, SHA-256 identity и неизменность original.

Не добавляй `svg-dotnet`: текущая запись research помечает его `reject-now`. Если существующих
.NET XML и Skia API объективно недостаточно для узкого профиля, остановись перед новой dependency
и запроси отдельное решение владельца; не подменяй gate молчаливым пакетом.

### 7.3 Настройки

Сделай один локальный settings store вне `.mapproj`. Каждый control должен реально менять
поведение, переживать restart и иметь Reset:

- Dark/Light/System theme, русский интерфейс как обязательный; переключатель языка показывать
  только для реально существующих локализаций, не делать фиктивный dropdown;
- UI scale в безопасном диапазоне без изменения document coordinates;
- уже существующие configurable shortcuts с конфликтами/reset;
- autosave interval и recovery budget с безопасными нижним/верхним пределами;
- canvas performance mode и локальная diagnostics page: renderer/GPU-or-CPU fallback, cache/undo и
  document stats без телеметрии и без содержимого/абсолютных путей;
- Library/cache roots через проверяемый локальный выбор; смена пути не удаляет старые данные;
- default export settings;
- pen/tablet diagnostics с видимыми pressure samples; мышь остаётся полноценным fallback.

Не сохраняй tool/session state в документ, если оно не влияет на воспроизводимый результат карты.

### 7.4 Acceptance 16A

Адресные tests должны доказать: metadata search/sort не пишет файлы; clone/template получают новые
ID; delete использует только подтверждённый synthetic target; backup inspection/migration не меняет
source; composition package roundtrip сохраняет provenance; разрешённый SVG даёт ожидаемый raster,
а active/external/oversized SVG отклоняется; settings roundtrip и Reset реально влияют на services.

## 8. Stage 16B — Brush, Land, layers, masks и Filters

### 8.1 Brush

Сохрани существующий tile engine и `BrushSettings`, но убери разрыв между моделью и панелью:

- material modes: Texture, solid color, Eraser;
- каталог texture materials с recent/favorite и preview;
- Hue/Saturation/Brightness/Contrast и texture scale/rotation/offset/opacity;
- round hard/soft presets, edge-block, grid-block, rectangle, ellipse и polygon gestures;
- size, opacity, softness, spacing, scatter, angle, оба random flip, size/opacity jitter;
- отдельные switches pressure→size/opacity/softness и общий Pen dynamics;
- живой ghost/preview тем же evaluator, который commit-ит stroke;
- сохранение/rename/delete собственного local brush preset без копирования чужого материала.

Random seed фиксируется до command; renderer/reopen/undo не перебрасывают случайность. Один stroke
остаётся одной undo-транзакцией. Tool settings не загрязняют document до commit.

### 8.2 Land

Доведи существующие shapes/tile coverage/edge renderer до прямого управления:

- Add/Subtract, size, exact edge roughness и smoothing;
- circle, rectangle, ellipse, polygon, grid-block, bounded fill и явный Clear с подтверждением/undo;
- реальный interactive Move Area: пользователь задаёт/видит область и delta; убрать hard-coded
  центр, размер и «вправо на 128»;
- grid-aware snap и preview;
- точные controls Land style: outline/stroke, inner/outer shadow, ripple/coast, их цвета, offsets,
  blur/opacity/width; preset только заполняет controls и не заменяет их;
- несколько Land layers остаются независимыми.

Move/shape preview не меняет документ; commit создаёт одну reversible transaction и инвалидирует
только затронутые sparse tiles/chunks.

### 8.3 Layers и masks

Закрой общий P0/P1 layer contract:

- exact opacity и все уже поддержанные blend modes вместо цикла из трёх значений;
- Object layer: layer shadow, clipping mode и sublayer visibility/lock с ясным текущим состоянием;
- add/copy/disable/re-enable/delete mask для Object, Brush, Grid и совместимых Filter layers;
- mask brush Hide/Reveal, size, opacity, softness, shapes и pressure;
- copy mask проверяет совместимость и не меняет source;
- красный overlay существует только в edit mode и не попадает в save preview/export;
- явный flatten с предупреждением и Undo; до подтверждения vector source остаётся редактируемым.

Не добавляй folder layers или multi-mask stack: это `P2`.

### 8.4 Filters

Подключи к UI точные текущие значения базовых Filters:

- brightness, contrast, saturation, hue, temperature;
- texture asset, opacity, scale и reset;
- layer opacity/blend, mask, reorder/duplicate;
- быстрый preview может использовать LOD, но save/reopen/export дают точный одинаковый результат.

Не добавляй vignette/blur/sharpen без отдельного measured `P2`-решения.

### 8.5 Persistence boundary

Сначала различи tool/settings-only state и document state. Если для обязательных document values
нужны новые поля, Stage 16 явно разрешает canonical schema v3 и одну последовательную copy-only
миграцию `v2→v3`, сохраняя действующий `v1→v2→v3` registry, unknown placeholders и future-version
refusal. Не повышай schema ради UI-only данных и не переписывай source in place.

Адресные tests 16B покрывают preview=commit, Undo/Redo, save/reopen, mask owner isolation,
overlay exclusion from export, filter exact values и неизменность Stage 13 sparse budgets.

## 9. Stage 16C — Line, Shape, Text и Notes без demo-команд

### 9.1 Общая vector interaction model

Сохрани текущие vector models/rendering/persistence. Замени hard-coded действия реальным
pointer/keyboard interaction state:

- Line: Free Draw и Pen/Node mode;
- Shape: Free Draw, Pen, Brush, Rectangle, Ellipse и Polygon;
- до commit показывается ghost; preview и commit используют одну geometry;
- hit-test выделяет конкретный node/handle/portal, а не всегда первый элемент;
- add/move/delete/cut node, straight/curved node и handles работают прямым drag/numeric control;
- guides и snap используют текущую Grid engine;
- portal имеет selectable position и width, двигается drag/numeric control и удаляется без
  разрушения исходного path;
- cancel/tool switch не оставляют object/history mutation;
- одно законченное gesture — одна command.

Удали из пользовательского пути операции вида «сдвинуть первый узел на фиксированную дельту»,
«добавить portal в 0.5/48», «сдвинуть portal на +0.1» и boolean над двумя последними objects.

### 9.2 Line и Shape appearance/boolean

- сделай прямыми controls все уже существующие Line stroke values: color/asset, width, opacity,
  blend, blur, sublayer, Hue/Saturation для asset, offset, caps и shadow;
- Shape fill: color/texture, scale/rotation/offset/opacity; stroke использует тот же Line engine;
- overall Shape opacity/blend/blur — фактические значения с Reset;
- boolean Add/Subtract/Merge работает над явным совместимым выбором, показывает preview/result и
  не выбирает объекты по позиции в слое;
- используй существующую собственную geometry реализацию, если адресные degeneracy tests зелёные.

Clip-to-nearest-Brush для Line/Shape не реализуй: спецификация оставляет его `P2` до ясной
семантики. Существующие layer masks и Stamp Clip Mask не называй этим поведением.

### 9.3 Text

Сохрани полный текущий inspector и добавь честное tool state до размещения:

- новый Text размещается по click/drag, получает ghost и может наследовать последний выбранный
  Text только при явно включённой синхронизации;
- multiline editor/resize, font, bold/weight, size, fill/outline/shadow, rotate/scale/flips,
  sublayer, curve, spacing, line height, alignment, opacity/blend/blur остаются точными и undoable;
- missing font даёт fallback/warning, но не ломает open/export и не меняет сохранённое family name.

### 9.4 Notes

Сохрани безопасный локальный Markdown и добавь user-facing rich-text workflow:

- formatting actions для headings, lists, bold/italic и links работают над selection;
- edit/raw Markdown и rendered preview используют один разрешённый subset;
- raw HTML, script и внешняя загрузка не выполняются;
- list/search/go-to/edit/delete и world/object anchors сохраняются;
- Notes по умолчанию не попадают в image export.

Адресные tests 16C покрывают реальные выбранные node/portal, cancel без mutation, boolean над явным
selection, texture fill controls, Text inheritance gate, Markdown rendering/sanitization и
save/reopen/export parity.

## 10. Stage 16D — первый пользовательский путь и правдивая документация

### 10.1 Интеграция

Пройди один связный сценарий только на synthetic temporary profile:

1. dashboard search/sort → template/clone → open;
2. import собственного raster и безопасного SVG;
3. Brush material/preset → Land shape/style/move;
4. layer opacity/blend → mask edit/copy/disable → Filter exact values;
5. Line/Shape placement → node/portal/boolean → Text → formatted Note;
6. composition package transfer;
7. Save → reopen → Undo/Redo там, где это семантически возможно → export preview/result;
8. settings restart roundtrip и local help.

Проверь, что first-use controls называются по действию и текущему значению, клавиатурный путь не
скрыт, а на 1416×939 и 960×640 остаются canvas, tool rail, одна context panel и Layers/Objects.
Не копируй визуальный стиль Inkarnate.

### 10.2 Документы

После production acceptance минимально синхронизируй факты:

- `CODEX.md`: актуальная branch/HEAD и следующий action;
- `docs/06_DEVELOPMENT_ROADMAP.md`: Stage 12D–16 без противоречащих старых статусов;
- `docs/STATE_HANDOFF.md`: точный BASE..HEAD, проверки, остаток `P2` и исключённый online scope;
- `docs/02_FUNCTIONAL_SPEC.md` только если реализация потребовала уточнить уже принятое поведение;
- `docs/DECISIONS.md` только append-only, если появился реальный новый schema/dependency decision.

Не создавай evidence/process-дерево и не сохраняй generated screenshots/logs в Git.

## 11. Reference repositories и зависимости

Новая production dependency для Stage 16 заранее не нужна.

Приоритет использования:

1. текущие production Avalonia 12.1 и SkiaSharp 3.119.4;
2. собственные Core/Persistence/Rendering contracts Map;
3. exact-pinned read-only research checkouts из
   `C:\Projects\Map-ResearchLab\sources` и `docs/research/external-sources.json` только как
   clean-room cross-check.

Допустимые роли:

- `Avalonia`/`Avalonia.Samples`: native control, virtualization, input и test patterns;
- `SkiaSharp`: существующий render/path/raster API;
- `Clipper2` exact pin: только отдельный prototype, если targeted tests докажут, что собственный
  boolean engine не может закрыть обязательные случаи. До отдельного dependency/license решения
  пакет в product не добавлять;
- `Tiled`, `Azgaar`, `MapTool`, `Pinta`, `PixiEditor`, `LDtk`, `Mapsui`, `NetTopologySuite`, Godot:
  reference-only/concept-only; не переносить code, assets, UI, formats или branding;
- `svg-dotnet`: `REJECTED` для текущего product.

Отсутствующие локальные checkouts необязательных reference repositories не являются blocker.
Не fetch/update внешние repos без конкретной необходимости и отдельной проверки exact commit/license.

## 12. Архитектурные и пользовательские инварианты

- Core не зависит от Avalonia, Skia, filesystem UI или persistence DTO.
- Renderer никогда не меняет document.
- Tool preview/session state не сериализуется как сцена.
- Все document edits undoable; save/recovery/export не создают скрытых edits.
- Save и migration атомарны; source backup/import file не изменяется.
- Raster остаётся sparse; нельзя выделять bitmap размером со весь 131K canvas.
- UI не выполняет долгий decode/scan/save синхронно на UI thread.
- Нет сети, телеметрии, аккаунта, secret или нового service.
- Ошибка отвечает: что произошло, сохранены ли данные, что делать дальше.
- Реальные owner maps/assets/settings не используются для tests, smoke, delete или migration.
- Не ослабляй admission/cache/JSON/ZIP budgets ради прохождения fixture.

## 13. Проверка без стены тестов

Не запускай полный suite, security scan, coverage, mutation, benchmark или повторную
независимую приёмку.

Обязательный finite budget:

1. после каждого изменённого среза — один раз только относящиеся к нему filters/test-projects
   (`FullyQualifiedName~Stage16A`, `~Stage16B`, `~Stage16C` или более узкий эквивалент);
2. после всех production changes — один `dotnet build .\Map.slnx -c Release`;
3. один короткий combined native smoke в настоящем own-process окне на текущем ПК;
4. один `git diff --check` по точному Stage 16 diff;
5. ручная проверка staged paths и сохранения owner-dirty hunks.

Native smoke:

- `ShowActivated=false`, `ShowInTaskbar=false`;
- только собственное временное окно/visual tree Map;
- без `SendInput`, foreground activation, системной мыши/клавиатуры и переключения чужих окон;
- без real owner data;
- generated PNG/JSON/logs только во временной/ignored папке и не коммитятся.

Не повторяй зелёную проверку без изменения входных данных. Если один gate падает, исправь причину и
повтори только его; три одинаковых инфраструктурных сбоя означают смену метода или честный
`PARTIAL/BLOCKED`, а не бесконечный retry.

## 14. Коммиты и push

Commit/push разрешены только если сообщение владельца, которым запускается этот prompt, прямо их
разрешает. Тогда предпочти четыре небольших commits:

1. `feat(stage-16a): finish local project and asset workflows`;
2. `feat(stage-16b): complete raster layer and filter controls`;
3. `feat(stage-16c): complete vector text and notes workflows`;
4. `docs(stage-16): record final offline product scope`.

Если безопасная schema v3 требует отдельного законченного commit, помести его перед потребителями и
укажи точную причину. Не amend/rebase/squash. Push только Stage 16 branch после доказательства, что
каждый commit не захватил owner files. PR, tag и GitHub Project не создавать.

Если запуск не разрешает commit/push, оставь проверенный working tree и точный список paths.

## 15. Stop-gates

Остановись до спорного действия и запроси владельца, если требуется:

- новая production dependency;
- изменение внешнего формата кроме явно разрешённой schema v3 и `v2→v3` copy-only migration;
- удаление/перемещение реальных данных владельца;
- перенос чужого code/asset/style;
- network/account/cloud/service;
- расширение в `P2`;
- действие над owner-dirty файлом, которое невозможно отделить без потери его изменений.

Обычная реализация перечисленного Stage 16, новые адресные tests, synthetic fixtures, один Release
build и один own-process smoke дополнительного согласования не требуют.

## 16. Условия `FACT`

Stage 16 и принятый offline project можно назвать `FACT` только когда одновременно доказано:

- 16A, 16B, 16C и 16D не имеют обязательных `PARTIAL/BLOCKED` пунктов;
- dashboard/project/assets/settings flows работают на synthetic data;
- Brush/Land/layers/masks/Filters имеют реальные controls, preview=commit, Undo и reopen/export;
- Line/Shape nodes/portals/boolean управляются явным selection, без hard-coded demo semantics;
- Text/Notes закрывают принятый P1 workflow;
- safe SVG/profile, composition package и missing-asset flow проверены;
- targeted tests, один Release build и один native smoke зелёные;
- точный Stage 16 diff не потерял Stage 12D–15 и не захватил owner changes;
- docs не называют исключённые online/P2 функции дефектами завершённой первой стабильной версии;
- commit/push status сообщён фактически, без предположений.

Если любой обязательный пункт не доказан, итоговый статус — `PARTIAL` с ровно оставшимся
пользовательским срезом. Не выставляй 100% из-за зелёной сборки или большого числа tests.

## 17. Финальный отчёт

Кратко и простым русским языком укажи:

1. исходный и итоговый branch/HEAD, commits и remote status;
2. что сделано в 16A–16D;
3. какие targeted tests, Release build и native smoke реально выполнены;
4. что не выполнялось по правилам;
5. какие `P2` и online-возможности сознательно не входят в завершённый offline scope;
6. были ли schema/dependency stop-gates;
7. сохранились ли owner-dirty paths.

Заверши обязательным разделом:

```text
## ИТОГИ

Сделано: ...
Что дальше: ...
Реализация проекта: N%
```

`100%` допустимо только для принятого нативного offline P0/P1 scope при всех условиях `FACT` выше.
Это не означает и не должно означать реализацию онлайн-сервиса, Marketplace, Community, чужих
ассетов или необязательных `P2`-расширений.
