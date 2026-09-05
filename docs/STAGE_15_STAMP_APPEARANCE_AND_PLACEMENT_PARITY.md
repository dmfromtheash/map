# Stage 15 — единая панель штампа, стиль до размещения и полный внешний вид

## Исполняемый prompt для Codex

Выполни Stage 15 как один законченный пользовательский срез. После короткой проверки состояния
переходи к production-коду: не заменяй реализацию новым аудитом, макетом, отчётом или следующим
prompt. Цель этапа — сделать уже вооружённый Stamp и уже поставленный stamp двумя состояниями одного
понятного инструмента, чтобы внешний вид настраивался ползунками до клика, точно отображался в
фантоме и без расхождений сохранялся в созданном объекте.

Работай преимущественно на русском. Используй текущий нативный стек Map и существующие компоненты;
не добавляй dependency, web UI, сервис, аккаунт или второй параллельный editor state.

---

## 1. Практический результат для владельца

После Stage 15 пользователь должен иметь один очевидный путь:

1. Открыть Stamp, выбрать локальный набор и вариант.
2. До размещения изменить ползунками масштаб, поворот, подслой, прозрачность, оттенок,
   насыщенность, яркость, контраст и размытие; вручную отразить объект и настроить тень/
   смешивание.
3. Сразу увидеть итог на полупрозрачном фантоме, включая выбранный вариант, цветокоррекцию,
   отражение, тень, blend и snapped-позицию.
4. Поставить объект, визуально совпадающий с фантомом. До клика документ, revision, dirty и history
   не меняются; успешный клик остаётся одной undoable-командой.
5. Выделить поставленный stamp и получить те же свойства с фактическими значениями, а не другой
   набор расходящихся controls.
6. Использовать `Один`, `Серия`, `Россыпь`, три политики автовыбора, область/плотность, случайный
   вариант, дискретный случайный угол и случайное отражение.
7. После save/reopen и export видеть тот же результат.

Это функциональная clean-room реализация. Не копируй исходный layout, тексты, иконки, цвета,
ассеты, торговый вид или закрытые данные Inkarnate.

---

## 2. База, рабочая папка и полномочия

### 2.1. Подготовительный снимок

- Рабочий product-worktree:
  `C:\Users\Leshe\.codex\worktrees\stage10f-fonts-bindings\Map`.
- `C:\Projects\Map` — старый cleanup-checkout; не использовать как базу функции.
- Подготовительная ветка: `codex/stage-14-object-placement-workspace-ux`.
- Подготовительный `BASE`:
  `fa8efdeaa995464a6f902bbfb64f004d783cb769`
  (`fix(stage-14): rotate pending objects before placement`).
- `BASE` находится в существующем `origin/codex/stage-14-object-placement-workspace-ux` и содержит
  основной Stage 14 commit `b62f059` плюс его исправления.

До любых изменений выполни:

```powershell
git rev-parse --show-toplevel
git branch --show-current
git rev-parse HEAD
git status --short --branch
git log --all --decorate --oneline --graph -n 50
git remote -v
```

Если текущий `HEAD` — более новый прямой потомок `fa8efde...`, сначала докажи по Git и
`docs/STATE_HANDOFF.md`, что это принятый product HEAD, и используй его. Если история разошлась или
источники истины противоречат друг другу, остановись до изменения файлов.

Для реализации допустима новая локальная ветка
`codex/stage-15-stamp-appearance-placement-parity` от подтверждённого product HEAD. Не выполнять
reset/restore/stash/clean/rebase/amend и не переписывать историю.

### 2.2. Существующие изменения владельца

На момент подготовки присутствовали:

```text
 M AGENTS.md
 M CODEX.md
 M docs/04_TECH_ARCHITECTURE.md
 M docs/06_DEVELOPMENT_ROADMAP.md
 M docs/07_CODEX_RUNBOOK.md
 M docs/08_QA_AND_ACCEPTANCE.md
 M docs/STATE_HANDOFF.md
 M docs/research/SOURCES.md
 M docs/research/external-sources.json
?? docs/STAGE_12D_MIGRATIONS_AND_FILE_DIAGNOSTICS.md
?? docs/STAGE_13_LARGE_CANVAS_PERFORMANCE.md
?? docs/STAGE_14_OBJECT_PLACEMENT_VERSIONS_AND_EDITOR_WORKSPACE.md
?? docs/research/INKARNATE_CANVAS_PERFORMANCE_20260831.md
```

Пересними список: он мог измениться. Не редактируй, не форматируй, не stage и не присваивай эти
файлы. Сам этот Stage 15 prompt также не является production-результатом.

### 2.3. Git/remote

Текущий контракт разрешает production-изменения и локальную проверку, но сам по себе не даёт
отдельного разрешения на commit, push, PR, issue, Project или tag. Если в сообщении,
которым владелец запускает prompt, нет нового явного разрешения, оставь проверенный diff в рабочем
дереве и отчитайся без remote-действий.

---

## 3. Что прочитать и визуально проверить

До кода полностью прочитай короткие инструкции и только относящиеся разделы больших документов:

1. `AGENTS.md`, `CODEX.md`, `docs/STATE_HANDOFF.md`.
2. `docs/DECISIONS.md`: DEC-001, DEC-003–005, DEC-009–010, DEC-013–016 и DEC-018.
3. `docs/02_FUNCTIONAL_SPEC.md`: Stamp/Asset, Layers/Object tree, Masks, Catalog и Settings.
4. `docs/03_UX_UI_SYSTEM.md`: progressive disclosure, tool state, accessibility и запрещённые
   паттерны.
5. `docs/04_TECH_ARCHITECTURE.md`: immutable edit/render, Stage 13 culling/cache/invalidation.
6. `docs/05_PROJECT_FILE_AND_DATA_MODEL.md`: scene, assets, save/reopen и compatibility.
7. `docs/07_CODEX_RUNBOOK.md`, `docs/08_QA_AND_ACCEPTANCE.md`.
8. `docs/research/OBJECT_AND_GRID_PARITY.md`,
   `docs/research/INKARNATE_ACCOUNT_WALKTHROUGH_20260826.md` и относящийся Stamp-раздел
   `docs/research/INKARNATE_RESEARCH.md`.
9. Исполненный `docs/STAGE_14_OBJECT_PLACEMENT_VERSIONS_AND_EDITOR_WORKSPACE.md` — только как
   контракт уже существующего placement/ghost/workspace; не повторять весь Stage 14.

Проверь существование и открой через `view_image`:

- приложенный владельцем снимок панели:
  `C:\Users\Leshe\AppData\Local\Temp\codex-clipboard-cac65ba0-3ba2-4a8b-b97d-2050de4c4361.png`;
- устойчивые локальные reference-снимки:
  `C:\Users\Leshe\.codex\visualizations\2026\08\31\01a05986-f6f1-7d10-89ea-ede6ed57c70e\inkarnate-object-audit-2026-09-01\03-inkarnate-stamp-tool.png`;
  `C:\Users\Leshe\.codex\visualizations\2026\08\31\01a05986-f6f1-7d10-89ea-ede6ed57c70e\inkarnate-object-audit-2026-09-01\19-inkarnate-stamp-catalog.png`;
  `C:\Users\Leshe\.codex\visualizations\2026\08\31\01a05986-f6f1-7d10-89ea-ede6ed57c70e\inkarnate-object-audit-2026-09-01\20-inkarnate-stamp-more-options.png`.

Если временный clipboard-файл уже удалён, это не stop-gate: используй три устойчивых reference и
явно отметь отсутствие временного вложения.

Для неоднозначной семантики Clip Mask проверь первичные публичные материалы:

- [официальный разбор clipping masks](https://www.youtube.com/watch?v=9e4kS12LH3M);
- [официально закрытый запрос о toggle при повторном размещении](https://feedback.inkarnate.com/p/add-the-clip-mask-toggle-to-stamp-options).

Если без живого редактора смысл всё ещё неоднозначен, сначала полностью прочитай установленный
Browser skill и используй только in-app Browser с уже открытой тестовой картой. Не считывай
credentials/2FA, не перехватывай autosave открытой в другом месте карты, ничего не покупай,
публикуй и не сохраняй. Отмени пробные изменения и закрой созданную вкладку. Не запускай новый
полный walkthrough ради остальных известных controls.

---

## 4. Текущая реализация: развивать, не дублировать

Перед редактированием найди актуальные места через `rg`; номера строк не являются контрактом.

| Область | Что уже есть на `fa8efde...` | Что отсутствует |
|---|---|---|
| Pending placement | `PlacementSession`, точный ghost, One/Series/Area, AutoSelect, W/S size, A/D rotation | Полный будущий style/opacity/sublayer/scale/flips/effects |
| Создание stamp | `BuildStampPlacement` и `StampObject` | Новые stamps получают почти нейтральный `StampStyle` вместо tool style |
| Выделенный stamp | Scale, exact W/H, flips, opacity, blur, H/S/B/C, blend, custom shadow | Те же controls до клика; shadow color/type; все нужные blend modes |
| Renderer | Общий viewport/export renderer, flip, object opacity, blur, black drop shadow, color matrix | Цвет тени, layer-shadow prepass, семь blend modes, доказанный clip behavior |
| Persistence | `StampStyleDto` roundtrip текущих эффектов | Backward-compatible поля новых эффектов и neutral defaults |
| Library | Sets/variants, recents, favorites, search/filter, большой каталог | Компактный current/recents/favorites surface в Stamp context |
| Scatter | Radius, density, deterministic seed, random angle bool, random flip | Выбор шага угла и точное сочетание base rotation с randomization |

Основные места:

- `src/Map.Presentation/ViewModels/PlacementSession.cs`;
- `src/Map.Presentation/ViewModels/EditorViewModel.cs`:
  `BuildStampPlacement`, `TryStampInputs`, `StampObject`, commit и текущие stamp setters;
- `src/Map.App/Views/EditorView.cs`:
  `BuildStampGestureBlock`, `BuildStampSection`, `AppearanceSlider`, `ShadowBlock`;
- `src/Map.App/Controls/MapCanvasControl.cs` — pending preview и shortcuts;
- `src/Map.Core/Documents/SceneObject.cs` и `AppearanceContract.cs`;
- `src/Map.Core/Editing/AssetPlacement.cs`;
- `src/Map.Rendering/SceneRenderer.cs`;
- `src/Map.Persistence/Container/Dtos.cs` и `MapProjectStore.cs`;
- `src/Map.App/Services/EditorPreferencesStore.cs`;
- существующие Stage10D/Stage14 tests в `tests/Map.*.Tests`.

Сначала запиши в рабочем сообщении короткую матрицу
`переиспользовать / расширить / не требуется`; не сохраняй её отдельным evidence-файлом.

---

## 5. Инварианты Stage 15

1. `MapDocument` остаётся единственным источником истины для поставленных объектов.
2. Tool settings и pending placement являются transient/editor-local state; до commit они не
   попадают в document, history, save, recovery, checkpoint, export или asset references.
3. Preview и commit используют один resolved style/geometry/seeded plan. Renderer не выбирает
   варианты, углы или flips случайно.
4. Изменение pending controls не создаёт Undo. Один жест ползунка выделенного объекта создаёт одну
   undoable-команду, а не запись на каждый tick.
5. Editor-local defaults сохраняются отдельно от `.mapproj`; каждый поставленный объект сохраняет
   собственные фактические значения.
6. Старые `.mapproj` открываются с нейтральными defaults и без изменения единственной копии.
7. Viewport, preview, thumbnail, save/reopen и export используют одну семантику эффектов; editor
   chrome/ghost не попадают в exact output.
8. Pointer move и slider preview не обходят все 50 000 объектов, не перестраивают Objects tree/
   spatial index и не сбрасывают все Stage 13 caches.
9. Core не зависит от Avalonia/Skia. Renderer не мутирует документ.
10. Все новые controls имеют видимое значение, единицу, keyboard path, focus и стабильное UIA
    `Name`; raw enum/GUID/internal blend key пользователю не показывается.
11. Реальные карты, Library, Recovery, Diagnostics и preferences владельца не используются в
    tests/smoke.
12. После одиночной установки stamp выбранный set/variant и ghost остаются активными; обычный
    `AutoSelect` не переводит фокус на созданный объект, а `Esc` завершает размещение.

---

## 6. Stage 15A — единое состояние инструмента Stamp

### 6.1. Editor-local settings

Введи одно типизированное состояние будущего stamp, например `StampToolSettings`. Имя не
предписано. Оно должно хранить:

- scale percent с нейтральным `100%`;
- base rotation в градусах;
- sublayer `-10..10`;
- manual horizontal/vertical flip;
- object opacity;
- полный `StampStyle` будущего объекта;
- mode и AutoSelect policy;
- area/density, random variant, random-angle step, random flip;
- выбранные set/variant только через существующую catalog identity.

Не держи вторую копию каждого числа одновременно в `EditorView`, `EditorViewModel` и
`PlacementSession`. Presentation/ViewModel владеет состоянием; UI только читает/вызывает команды,
а `PlacementSession.PreviewObjects` содержит resolved future result.

`EditorPreferencesStore` сохраняет последний применённый tool preset атомарно и bounded. Не писать
preferences на каждый slider tick: сохранять после завершения жеста/дискретного выбора. Повреждённый
или старый preferences-файл даёт neutral defaults, а не ломает запуск.

### 6.2. Два явных режима одной панели

Один общий builder/binding показывает:

- `Будущий штамп`, когда Stamp вооружён: controls меняют только tool state и ghost;
- `Выбранный штамп`, когда активен Select и выбран stamp: controls меняют документ через
  существующую edit/history-модель.

Не допускай скрытого редактирования выделенного объекта во время armed placement. Не создавай две
визуально одинаковые панели с разными реализациями. Общие transform rows для других типов объектов
сохрани; для stamp не показывай второй расходящийся rotation/sublayer/opacity control.

### 6.3. Частые controls

В верхней части Stamp context доступны без раскрытия:

1. current set/variant: thumbnail, имя, previous/next/current и команда открыть большой каталог;
2. компактные `Недавние`/`Избранные`, использующие текущий Library backend и cache;
3. `Масштаб` — slider `10..400%`, текущее число, reset `100%`;
4. `Поворот` — slider/numeric `0..359°`, reset `0°`; A/D меняют то же значение и тот же ghost;
5. `Подслой` — slider/numeric `-10..10`;
6. две явные кнопки отражения;
7. `Прозрачность` — slider `0..100%`.

Exact W/H и aspect lock выбранного stamp остаются доступны, но не подменяют scale. При изменении
scale используется сохранённая reference size; смена варианта до placement пересчитывает размер
от native aspect нового варианта и текущего scale без растягивания.

W/S и A/D не срабатывают, когда фокус находится в TextBox/NumberBox или пользователь вводит текст.
Используй существующие editable bindings, а не hard-coded физические клавиши.

### 6.4. Раскрываемый внешний вид

В `Внешний вид` доступны:

- Hue `-180..180°`, neutral `0°`;
- Saturation, Brightness, Contrast `0..200%`, neutral `100%`;
- отдельный reset каждого параметра и один reset цветокоррекции;
- Blend Mode;
- Blur `0..32 px`, где `0` честно означает выключено;
- Shadow;
- один reset всего внешнего вида, не меняющий asset, position, rotation, size или sublayer.

Ползунки pending state обновляют ghost без history. Ползунки selected state используют уже
принятую Stage10D семантику live preview + один commit на release/key/focus completion.

---

## 7. Stage 15B — preview и commit без расхождений

### 7.1. Resolution будущего объекта

Расширь `BuildStampPlacement`/`StampObject` так, чтобы каждый preview object и commit object получал:

- итоговые Width/Height после scale;
- base rotation плюс deterministic scatter rotation;
- sublayer;
- object opacity;
- manual flips плюс deterministic random flips;
- полную копию будущего `StampStyle` с конкретным asset SHA/reference size.

Commit не должен заново собирать стиль из defaults. Предпочтительно commit-ить уже resolved план
или повторно получать его из неизменяемого typed snapshot/seed с тестом структурного равенства.

Ghost использует фактические blend, blur, shadow и color adjustments, но дополнительно умножает
только preview opacity. Не записывай 50–60% ghost-opacity в созданный объект.

### 7.2. Режимы размещения

- `Один`: один commit, затем Select/inspector согласно AutoSelect.
- `Серия`: один объект на клик, tool style сохраняется, ghost остаётся; inspector не перехватывает
  контекст автоматически.
- `Россыпь`: area/density и весь resolved batch одной command; bounded preview остаётся.
- `Автовыбор`: `Всегда`, `Выкл.`, `Авто` с принятой Stage 14 семантикой.

Не добавляй новый непрерывный paint-mode под названием `Auto`, пока наблюдаемое поведение не
доказано и владелец его отдельно не запросил. Текущая `Россыпь` является Map-native функциональным
эквивалентом area/density placement.

### 7.3. Random angle и flips

Замени bool случайного угла на typed step:

```text
Выкл. / 15° / 30° / 45° / 60° / 90° / 180°
```

Для каждого scatter item выбирается seeded целое кратное step в пределах полного оборота и
добавляется к base rotation. `Выкл.` оставляет base rotation. Preview/commit/Undo/Redo/reopen не
reroll-ят результат.

Manual H/V являются базовыми. При включённом random flip каждая ось детерминированно может быть
инвертирована относительно базы; зафиксируй это правило тестом, не оставляй неявное XOR/overwrite
поведение.

---

## 8. Stage 15C — тени, blend modes и формат

### 8.1. Модель тени

Расширь `StampStyle` минимально:

- placement/type тени: `Object` или `Layer`; отсутствие по-прежнему определяется нулевой opacity;
- shadow color в `#AARRGGBB`, neutral/default `#FF000000`;
- существующие offset X/Y, blur и opacity сохраняются.

UI предоставляет четыре понятных состояния:

1. `Нет` — opacity 0;
2. `Стандартная` — Object + значения `ShadowPreset` + black;
3. `Настраиваемая` — Object + X/Y/blur/opacity/color;
4. `За слоем` — те же custom параметры, но shadow рисуется под всем содержимым содержащего
   ObjectLayer.

`Стандартная` и `Настраиваемая` могут выводиться из фактических значений; не сериализуй лишний
UI-only enum. Старый объект с ненулевой тенью читается как Object shadow; старый объект без тени
остаётся без неё.

Object shadow рисуется непосредственно перед собственным stamp в его z-order. Layer shadow
рисуется отдельным bounded prepass внутри ObjectLayer под содержимым этого слоя, сохраняя порядок
самих слоёв, layer opacity/blend/mask и culling. Preview одного stamp не должен заставлять
перерисовывать или материализовывать все 50 000 объектов.

Shadow color проходит общий валидатор цвета; неверный ввод не создаёт command и показывает
понятную ошибку. Итоговый alpha учитывает color alpha, shadow opacity, object opacity и layer
opacity ровно один раз.

### 8.2. Blend modes

Сохрани существующие `Normal`, `Multiply`, `Screen`, `Overlay`, `Darken`, `Lighten`, `SoftLight`,
`HardLight`, `Difference`, `Add` и добавь:

```text
ColorBurn, ColorDodge, Exclusion, Hue, Saturation, Color, Luminosity
```

`AppearanceContract` остаётся единым источником supported keys; persistence, inspector и renderer
не имеют отдельных списков. Пользователь видит понятные локализованные названия. Для каждого
нового key используется соответствующий Skia blend без silent fallback в Normal.

Не удаляй `Add`: он является существующим Map-значением и нужен для совместимости файлов.

### 8.3. Persistence

Добавь только backward-compatible optional DTO fields с neutral defaults. Не повышай schema version
только из-за аддитивных свойств `StampStyle`, если текущий v2-контракт допускает их. Если reader/
migration contract требует новый schema version, это stop-gate для согласования, а не повод скрытно
начинать Stage 12D-подобную миграцию.

Validation ограничивает числовые значения, enum, blend key и цвет до materialization. Save/reopen,
checkpoint, copy/duplicate/replace asset и region extraction сохраняют новый внешний вид. Replace
asset меняет asset identity/reference aspect, но не сбрасывает эффекты, opacity, rotation, sublayer
или flips.

---

## 9. Clip Mask — обязательный честный decision gate

`Clip Mask` не является обычным флагом прозрачности. Публичные материалы показывают связь с
маской конкретного ассета и foreground-текстурой; текущий Map имеет per-layer `LayerMask`, но не
принятую пару Inkarnate-style foreground/background и не asset clip channel. В
`docs/02_FUNCTIONAL_SPEC.md` точная семантика clipping также оставлена открытым вопросом.

До добавления control:

1. Доказательно опиши в рабочем сообщении, что именно делает toggle в текущем Inkarnate: входные
   данные, цель маски, порядок относительно stamp/layer/brush, сохранение и export.
2. Сопоставь это с `LayerMask`, ObjectLayer/BrushLayer и asset metadata Map.
3. Выбери только один исход:
   - `FACT`: семантика выражается существующей моделью без ложного поведения — реализуй typed
     property, preview/commit/save/export и доступный control;
   - `BLOCKED`: требуется новая foreground/background topology, asset mask payload или выбор
     владельца — не добавляй декоративный checkbox; заверши остальные подпункты и запроси у
     владельца одно конкретное решение с 2–3 Map-native вариантами и последствиями.

Без реализованного Clip Mask или явного решения владельца вынести его в следующий stage нельзя
называть **полный** паритет панели `FACT`; весь остальной Stage 15 может быть `PARTIAL` с точной
границей. Не использовать Reddit/форумы как единственное основание поведения.

---

## 10. Компоновка и обнаруживаемость

Сохрани Stage 14 foundation: tool rail, ровно одна context panel, canvas и справа Layers/Objects.
Не возвращай семь вкладок и не сжимай canvas.

Порядок Stamp context:

1. текущий asset/set и компактные Recent/Favorite;
2. частые transform/opacity controls;
3. Place/AutoSelect;
4. раскрываемый `Внешний вид`;
5. `Больше параметров` для scatter/randomization.

Допустима иная точная компоновка, если при 1416×939 всё остаётся прокручиваемым, labels читаемы и
canvas не перекрыт. Частые controls не прятать глубже двух раскрытий. Icon-only flip/reset имеют
tooltip и accessible name. Selected/pending state различаются заголовком и состоянием tool, а не
только цветом.

Большой каталог остаётся отдельным нативным overlay/dialog. Compact Recent/Favorite не создаёт
новый индекс, cache или копию asset metadata.

---

## 11. Явно вне scope

- новые Brush/Land algorithms и полный паритет их material/color panels;
- изменение Line/Shape/Text/Filter, кроме переиспользования общего безопасного control helper;
- Marketplace, store, чужие packs, account, collaboration и cloud sync;
- новый постоянный foreground/background subsystem без решения Clip Mask gate;
- копирование Inkarnate code/assets/icons/colors/text/layout/trade dress;
- новый renderer/UI framework или внешняя dependency;
- редактирование реальной карты/Library/preferences владельца;
- повтор Stage 13 benchmark, повышение canvas/object limits;
- full suite, coverage, mutation, security scan, SBOM, screenshot matrix и постоянная
  evidence-папка;
- исправления несвязанных дефектов.

Несвязанный дефект зафиксируй одной строкой `PROPOSED` в финальном отчёте и не расширяй diff.

---

## 12. Адресные tests

Добавь только tests изменённого риска с общим маркером `Stage15`. Не дублируй уже зелёные Stage10D/
Stage14 cases, если новый assertion можно добавить к существующему тесту.

### Presentation/tool state

- neutral defaults и bounded load/save editor preferences;
- pending scale/rotation/sublayer/flips/opacity/H/S/B/C/blur/blend/shadow меняют preview, но не
  document/revision/dirty/history/selection;
- W/S меняют размер, A/D и slider меняют один base rotation; input field suppresses shortcut;
- одиночная установка сохраняет активные set/variant и следующий ghost, не выбирая созданный stamp;
- preview object и committed object совпадают по geometry/sublayer/opacity/style;
- One/Series/Scatter сохраняют tool style; cancel не оставляет следа;
- selected slider gesture — одна history entry; pending gesture — ноль;
- random-angle step и manual/random flips детерминированы и не reroll-ятся;
- replacement сохраняет внешний вид и корректно принимает aspect нового asset.

### Core/persistence

- bounds/enums/color/blend validation;
- старый v2 stamp без новых fields получает black Object/default semantics и открывается;
- roundtrip всех новых полей, новых blend keys и optional Clip Mask, если реализован;
- Undo/Redo, checkpoint, duplicate/copy и region extraction сохраняют resolved style.

### Rendering

- H/S/B/C и blur дают тот же результат в normal scene, preview и export path;
- shadow color и Object/Layer ordering проверяются маленьким deterministic pixel/golden fixture;
- все новые blend keys имеют отличный от silent Normal ожидаемый результат;
- object/layer opacity и shadow alpha не применяются дважды;
- fast overview может упрощать дорогой эффект только по явной policy, exact/export остаются точными;
- Layer shadow cull/invalidation не превращает изменение одного stamp в обход всей 50K scene;
- Clip Mask получает отдельный render/save/export test только если gate закрыт `FACT`.

Запускай только проекты, где появились/изменились Stage15 tests, например:

```powershell
dotnet test .\tests\Map.Core.Tests\Map.Core.Tests.csproj -c Release --filter "FullyQualifiedName~Stage15"
dotnet test .\tests\Map.Rendering.Tests\Map.Rendering.Tests.csproj -c Release --filter "FullyQualifiedName~Stage15"
dotnet test .\tests\Map.Persistence.Tests\Map.Persistence.Tests.csproj -c Release --filter "FullyQualifiedName~Stage15"
dotnet test .\tests\Map.Presentation.Tests\Map.Presentation.Tests.csproj -c Release --filter "FullyQualifiedName~Stage15"
```

Это верхний список, не требование искусственно создать tests во всех четырёх проектах. Каждый
нужный filter запускается один раз после последнего изменения его входов.

---

## 13. Release build и один native smoke

После законченного production diff один раз:

```powershell
dotnet build .\Map.slnx -c Release
```

Затем один связный own-process native smoke на текущем ПК. Переиспользуй существующую минимальную
инфраструктуру `tools/Map.Performance`, но не запускай Stage13 benchmark profiles.

Smoke использует настоящее Avalonia/Win32 окно с `ShowActivated=false`, `ShowInTaskbar=false`,
уникальный temporary profile/map и synthetic transparent asset. Запрещены `SendInput`, foreground
activation и управление чужими окнами.

Сценарий:

1. Открыть synthetic карту с двумя Object layers, layer mask и несколькими фоновыми objects.
2. Открыть Stamp, выбрать synthetic set/variant; проверить compact recent/favorite и current
   thumbnail.
3. До клика изменить scale через W/S, rotation через A/D, sublayer, flips, opacity, Hue/Saturation/Brightness/
   Contrast, blur, blend и custom colored shadow. Доказать отсутствие document mutation.
4. Сделать временный screenshot ghost и открыть его через `view_image`: объект/эффекты видимы,
   controls и числа читаемы, canvas не перекрыт.
5. Commit `Один`; сравнить resolved preview/created object, Undo/Redo.
6. Выделить stamp и изменить один color slider: один gesture = одна history entry.
7. Проверить `Серия` и `Россыпь` с random angle step/flip; cancel не добавляет объект.
8. Save/reopen synthetic `.mapproj`, выполнить exact small export и сравнить внешний вид.
9. Проверить Object и Layer shadow; Clip Mask — только если его gate закрыт `FACT`.
10. Закрыть окно, dispose Skia/resources и удалить temporary profile/output.

Screenshot не коммить и не оставлять в tracked папке.

---

## 14. Порядок работы и проверка diff

Работай следующими вертикальными срезами без запроса нового разрешения между ними:

1. typed Stamp tool settings + общий pending/selected binding;
2. exact preview/commit + placement randomization;
3. shadows/blends/persistence;
4. Clip Mask gate и, если доказуемо, реализация;
5. адресные tests, один Release build, один native smoke, ручной diff review.

После каждого законченного среза проверяй только затронутый diff. Перед финальным отчётом:

```powershell
git status --short --branch
git diff --check
git diff --stat
git diff -- src tests
```

Не использовать `git add -A`/`git add .` в dirty worktree. Если commit отдельно разрешён, stage
только точные Stage 15 production/test paths и сначала докажи, что owner-dirty файлы не вошли.

---

## 15. Definition of Done

Stage 15 можно назвать `FACT` только когда одновременно доказано:

- будущий stamp имеет одну типизированную панель со всеми частыми и расширенными параметрами;
- pending controls не мутируют документ, а ghost показывает resolved future result;
- commit совпадает с ghost и создаёт одну command;
- selected stamp показывает те же фактические свойства без расходящихся дублей;
- W/S и scale control, A/D и rotation control синхронны;
- после одиночной установки тот же stamp остаётся на указателе до `Esc` или смены инструмента;
- One/Series/Scatter, AutoSelect, area/density, random variant/angle-step/flips предсказуемы;
- цветокоррекция, blur, новые blend modes и цветные Object/Layer shadows работают в
  preview/viewport/export;
- editor preferences bounded, а `.mapproj` roundtrip backward-compatible;
- Clip Mask реализован по доказанной семантике либо владелец явно принял его отдельный scope;
- Stage 13 culling/cache, old files, Undo/Redo, asset replacement и save/reopen не сломаны;
- нужные Stage15 filters, Release build и один native smoke зелёные;
- реальные данные и owner-dirty файлы не изменены;
- не было неразрешённых commit/remote действий.

Если Clip Mask остаётся без решения или любой обязательный пользовательский путь не доказан,
используй `PARTIAL`; не понижай критерий задним числом.

---

## 16. Stop-gates

Остановись и спроси владельца только если:

- подтверждённый product HEAD не является потомком `fa8efde...`;
- безопасная реализация требует нового schema version/migration;
- Clip Mask требует выбора новой foreground/background или asset-mask модели;
- необходима новая dependency, service, account, network write или GitHub permission;
- нужный production-файл пересекается с owner-dirty изменением и diff нельзя отделить;
- действие может изменить реальные данные владельца.

Упавший адресный test, сложность renderer, первый неудачный layout или необходимость исправить
свой код stop-gate не являются.

---

## 17. Финальный отчёт

Коротко и доказательно сообщи:

1. branch, `BASE`, `HEAD`, изменённые production/test paths и отсутствие/результат Git-действий;
2. `FACT`: что теперь может владелец до и после размещения stamp;
3. `FACT`: какие Stage15 filters, Release build и native smoke выполнены с результатом;
4. `NOT CHECKED`: что сознательно не запускалось по budget;
5. `PARTIAL`/`BLOCKED`: только реальные остатки, отдельно точный исход Clip Mask gate;
6. подтверждение сохранности owner-dirty файлов и реальных данных;
7. один следующий практический шаг.

Закончи обязательным разделом:

## ИТОГИ

- `Сделано:` простыми словами, что умеет программа и что это даёт владельцу.
- `Что дальше:` один следующий шаг или «обязательных действий нет».
- `Реализация проекта: N%` — округлённая оценка всего текущего принятого scope/roadmap с коротким
  основанием и без включения `PARTIAL`/`PROPOSED`/`BLOCKED` в готовую часть.
