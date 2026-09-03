# Stage 14 — размещение объектов, версии и рабочее пространство редактора

## Исполняемый prompt для Codex

Выполни этот этап полностью: изучи указанный аудит и текущий код, реализуй production-функции,
проверь их соразмерно риску, сделай локальные commits и отправь готовую ветку в существующий
`origin`. Не ограничивайся новым анализом, макетом, планом или ещё одним prompt. После минимальной
проверки состояния переходи к коду и доводи все обязательные пункты этого контракта до рабочего
пользовательского результата.

Работай преимущественно на русском. Используй Codex App и модель `gpt-5.6-sol`, если она доступна;
для воспроизводимых Git/build/test-команд используй локальный терминал. Не делегируй параллельное
редактирование другому агенту и не запускай вторую одновременную реализацию тех же файлов.

---

## 1. Практический результат для владельца

После Stage 14 редактор должен вести себя понятно без предварительного объяснения:

1. Нажатие «Добавить эллипс», «Добавить прямоугольник» или выбор stamp только **вооружает режим
   размещения**. Документ ещё не меняется.
2. Полупрозрачный точный образ будущего объекта следует за мышью по холсту. Пользователь видит
   размер, поворот, стиль, выбранный вариант ассета и итоговую snapped-позицию до клика.
3. Клик по холсту создаёт объект именно там, где был показан фантом. `Esc`, правая кнопка,
   переключение инструмента или закрытие отменяют незавершённое размещение без следа.
4. Доступны понятные режимы `Один`, `Серия`, `Россыпь` и предсказуемый `Автовыбор`.
5. Новые объекты и группы получают полезные автоматические русские названия; пользователь не
   видит GUID, enum или внутренние ключи и не обязан вручную подписывать каждый объект.
6. Непонятная вкладка «Точки» заменена пользовательской функцией **«Версии»**: одна кнопка создаёт
   версию с автоматическим названием, временем и preview; переименование остаётся необязательным.
7. После первого явного сохранения с выбором пути программа сама, в фоне и атомарно, сохраняет
   дальнейшие изменения в основной `.mapproj`. Recovery-автокопия остаётся отдельной страховкой.
8. Правая панель не перегружена семью несвязанными вкладками: постоянная работа сосредоточена в
   `Слои` и `Объекты`; библиотека открывается из Stamp, а версии, размер и экспорт находятся в
   понятных командах меню/диалогах.
9. Существующие возможности Map не теряются: Library, варианты, группы, object inspectors,
   эффекты, сетки, заметки, resize/region, export, undo/redo, save/reopen и оптимизации Stage 13
   продолжают работать офлайн.

Внешний вид остаётся собственным дизайном Map. Используй только наблюдаемую пространственную и
интеракционную логику Inkarnate, но не его код, ассеты, фирменные цвета, иконки, тексты, названия
пакетов, закрытые данные или точную trade dress.

---

## 2. Точная база, repository и Git-границы

### 2.1. Состояние на момент подготовки контракта

- Актуальный product-worktree:
  `C:\Users\Leshe\.codex\worktrees\stage10f-fonts-bindings\Map`.
- `C:\Projects\Map` — старый cleanup-checkout; не использовать его как базу функции.
- Текущая ветка: `stage/12d-migrations-file-diagnostics`.
- Точный подготовительный `BASE`:
  `68a8982c8ebaab145d05bddc7f47c26fc9fca063`
  (`feat(stage-12d): add safe project migrations and file diagnostics`).
- `d780efe2afd7b0123569f60bc85c3392de7d8370` и весь Stage 13A–13D являются предками `BASE`.
- На момент подготовки `BASE == origin/stage/12d-migrations-file-diagnostics`.
- Существующий private remote:
  `origin = https://github.com/dmfromtheash/map.git`.

До изменений снова выведи и зафиксируй:

```powershell
git rev-parse --show-toplevel
git branch --show-current
git rev-parse HEAD
git status --short --branch
git log --all --decorate --oneline --graph -n 80
git remote -v
```

Если `68a8982c...` больше не является последним принятым product HEAD, разрешено взять только более
новый **прямой потомок**, который текущий `docs/STATE_HANDOFF.md` явно называет принятым. Нельзя
самостоятельно откатываться, пересобирать историю или выбирать старую удобную ветку. Если новый
HEAD не является потомком `68a8982c...` либо source of truth противоречат друг другу — это реальный
stop-gate: ничего не переписывай, коротко сообщи конфликт владельцу.

### 2.2. Пользовательские изменения, которые нельзя присваивать

На момент подготовки worktree уже содержит изменения владельца:

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
?? docs/research/INKARNATE_CANVAS_PERFORMANCE_20260831.md
```

Сначала пересними актуальный список: он мог измениться. Не выполняй `reset`, `restore`, `stash`,
`clean`, `rebase`, `amend`, force-push или переписывание истории. Не удаляй и не включай перечисленные
файлы в свои commits. Не форматируй их механически.

Поскольку текущий `HEAD` уже равен правильной базе, предпочтительно создать новую ветку в этом же
worktree без переключения на иной commit:

```powershell
git switch -c codex/stage-14-object-placement-workspace-ux
```

Если такая ветка уже существует, не удаляй и не пересоздавай её: сначала проверь её HEAD/status и
используй только если она является продолжением нужного `BASE`. Новый worktree допустим только если
без него невозможно безопасно отделить реализацию от владельческих изменений; не создавай его
просто ради проверки.

### 2.3. Разрешённые remote-действия именно для этого этапа

Владелец в текущем диалоге отдельно разрешил commits и push новой реализации. Это узкое новое
разрешение перекрывает прежний общий запрет только в следующей части:

- разрешены локальные commits Stage 14;
- после зелёных обязательных ворот разрешён обычный push ветки
  `codex/stage-14-object-placement-workspace-ux` в существующий `origin`;
- запрещены force-push, изменение `main`, PR, merge, issue/comment/Project, tag, release,
  публикация, изменение GitHub auth/scopes и создание другого repository/remote;
- не использовать `3-puti`, `ensi-bot`, `nri-archive` или любой другой репозиторий;
- не выполнять `fetch`/`pull`, если для этой локальной линейной работы они не нужны.

Перед push ещё раз докажи, что коммиты содержат только Stage 14 и не забрали владельческие файлы.

---

## 3. Что обязательно прочитать до production-кода

Прочитай полностью короткие инструкции и только относящиеся разделы больших документов:

1. `AGENTS.md`.
2. `CODEX.md`.
3. `docs/STATE_HANDOFF.md`.
4. `docs/DECISIONS.md`: DEC-001, 003–005, 008–010, 012–017.
5. `docs/02_FUNCTIONAL_SPEC.md`: окно редактора, Stamp/Library, объекты/группы, history/autosave/
   checkpoints/recovery.
6. `docs/03_UX_UI_SYSTEM.md`.
7. `docs/04_TECH_ARCHITECTURE.md`: document/edit/render boundaries, Stage 13 invalidation и
   background save.
8. `docs/05_PROJECT_FILE_AND_DATA_MODEL.md`: atomic save, autosave/recovery и checkpoints.
9. `docs/06_DEVELOPMENT_ROADMAP.md`: реализованные Stages 10–13.
10. `docs/07_CODEX_RUNBOOK.md`.
11. `docs/08_QA_AND_ACCEPTANCE.md`.
12. `docs/GITHUB_PROJECTS.md` — только чтобы соблюдать неразрешённые границы.
13. `docs/research/INKARNATE_ACCOUNT_WALKTHROUGH_20260826.md`.
14. `docs/research/OBJECT_AND_GRID_PARITY.md`.
15. `docs/research/ASSET_CATALOG_AND_VARIANT_SETS.md`.

Не повторяй уже принятую Stage 13 performance-программу. Её код и инварианты нужно сохранить, а
не заново доказывать полным benchmark.

---

## 4. Аудит Inkarnate и правильные инструменты

### 4.1. Папка аудита

Основной визуальный источник этого этапа:

`C:\Users\Leshe\.codex\visualizations\2026\08\31\01a05986-f6f1-7d10-89ea-ede6ed57c70e\inkarnate-object-audit-2026-09-01`

Используй локальный AI-vision инструмент `view_image` и просмотри прежде всего:

- `03-inkarnate-stamp-tool.png` — контекст Stamp;
- `04-inkarnate-stamp-ghost.png`, `06-inkarnate-one-mode-ghost.png`,
  `22-inkarnate-final-ghost-wide.png` — точный фантом под курсором;
- `05-inkarnate-stamp-placement-modes.png` — режимы размещения;
- `07-inkarnate-after-one-click.png`, `08-inkarnate-many-mode-placed-plus-ghost.png` — различие
  единичного и продолжающегося размещения;
- `09-inkarnate-select-group.png`, `10-inkarnate-stamp-inspector-group.png` — объекты, группа,
  breadcrumb и inspector;
- `11-inkarnate-brush-tool.png`–`16-inkarnate-grid-settings.png` — progressive disclosure разных
  инструментов;
- `17-inkarnate-save-menu.png`, `18-inkarnate-editor-preferences.png`,
  `21-inkarnate-save-snapshot-dialog.png` — save/autosave/snapshot;
- `19-inkarnate-stamp-catalog.png`, `20-inkarnate-stamp-more-options.png` — большой каталог и
  расширенные свойства stamp.

Эти PNG являются исследовательскими скриншотами, а не production-ассетами. Не копируй и не
встраивай их в приложение или repository.

### 4.2. Наблюдаемые FACT из аудита

- Выбор stamp не меняет карту; полупрозрачный образ выбранного варианта следует за курсором.
- `One` завершает режим после одного объекта; `Many` оставляет новый фантом для продолжения;
  `Auto` работает через область/плотность.
- `Auto Select` имеет поведение Always/Off/Auto; практичный default — выбрать после одного и не
  вырывать пользователя из серии/россыпи.
- Панель редактора построена как tool rail + один контекст инструмента + canvas + Objects/Layers;
  редкие настройки скрыты глубже.
- Объекты подписаны по содержанию, а группы показывают понятное имя/количество и breadcrumb.
- Каталог открывается крупнее обычного inspector и содержит поиск, категории, фильтры, варианты,
  recent/favorite и import/upload surface.
- Snapshot в Inkarnate всё ещё просит имя. Требование владельца для Map лучше: обязательного поля
  имени быть не должно.
- Во время аудита Inkarnate показывал offline/disconnected; проверена локально видимая семантика
  UI, placement, undo и save controls, но не скорость cloud sync.

### 4.3. Если визуального аудита недостаточно

Только при реальной неоднозначности используй установленный Browser plugin и in-app Browser:

- skill:
  `C:\Users\Leshe\.codex\plugins\cache\openai-bundled\browser\26.825.51511\skills\control-in-app-browser\SKILL.md`;
- URL: `https://inkarnate.com/maps`;
- Browser plugin управляет встроенным браузером Codex, а не Brave;
- не используй полное управление ПК, `computer-use`, системный ввод или переключение чужих окон;
- не запрашивай, не читай и не сохраняй логин, пароль или 2FA;
- ничего не публикуй, не покупай, не меняй в аккаунте и не сохраняй пробную карту;
- после проверки отмени все пробные объекты и оставь карту без изменений.

Не повторяй весь walkthrough «для уверенности»: локального аудита достаточно для описанных ниже
решений.

### 4.4. Инструменты реализации

- `exec_command`/PowerShell — Git, поиск, Release build и адресные tests;
- `rg`/`rg --files` — поиск кода и тестов;
- `apply_patch` — ручные изменения файлов;
- `view_image` — визуальный разбор аудита и одного нового временного screenshot Map;
- Codex `open_in_codex` — по желанию открыть итоговые файлы/review в интерфейсе;
- существующий `tools/Map.Performance` — только как основа короткого own-process native smoke,
  без повторения Stage 13 benchmarks.

Не добавляй новый внешний пакет, сервис, runtime, browser dependency или account. Не используй
Python/Node для production UI. Не подключай внешний reference repository: для этого этапа он не
нужен.

---

## 5. Текущая реализация, которую надо развивать, а не дублировать

Перед изменением найди фактические участки через `rg`, потому что строки могут сместиться.

- `src/Map.App/Views/EditorView.cs`
  - `AddRectangleButton`/`AddEllipseButton` сейчас сразу вызывают `AddObject`;
  - `BuildSidePanel()` сейчас создаёт семь вкладок: Слои, Объекты, Библиотека, Заметки, Точки,
    Размер, Экспорт;
  - `BuildStampGestureBlock()` уже имеет One/Area, варианты, random angle/flip и вторичное
    размещение в центре viewport;
  - `BuildCheckpointsTab()` требует имя вручную;
  - `BuildStatusBar()` уже показывает save/autosave/layer/selection/grid/viewport;
  - `SyncContextArea()` уже сохраняет важный инвариант «ровно одна контекстная панель».
- `src/Map.Presentation/ViewModels/EditorViewModel.cs`
  - `AddObject()` сейчас создаёт примитив немедленно в центре canvas;
  - `PlaceCurrentVariantAt()` уже делает детерминированное One/Area placement одной undoable
    командой, разрешает конкретные SHA и выбирает последний объект;
  - `HandleEscape()` сейчас закрывает группу/selection, но не отменяет placement;
  - `CreateCheckpoint()`/`RestoreCheckpointAsync()`/`DeleteCheckpointAsync()` уже дают безопасную
    основу версий;
  - `TryAutosaveAsync()` сейчас пишет recovery snapshot, а не основной `.mapproj`;
  - `SaveAsync()` уже использует save gate, snapshot, atomic store и корректный saved revision.
- `src/Map.App/Controls/MapCanvasControl.cs`
  - pointer click уже размещает stamp;
  - pointer move имеет drag/stroke/vector previews, но не pending-placement preview;
  - `SceneRenderer.Render()` получает `ObjectOverrides` для drag preview.
- `src/Map.Rendering/SceneRenderer.cs`
  - `RenderOptions.ObjectOverrides` — существующий редакторский overlay-механизм;
  - export/thumbnail вызывают тот же renderer без editor chrome.
- `src/Map.Presentation/ViewModels/ObjectTreeRows.cs`
  - уже есть виртуализированные строки и group breadcrumb; улучшай представление, не строй второе
    дерево объектов.
- `src/Map.Core/Editing/AssetPlacement.cs`
  - уже есть детерминированные `PlanOne`/`PlanArea` и seeded randomization.
- `src/Map.Core/Documents/MapCheckpoint.cs` и
  `src/Map.Core/Editing/CheckpointCommands.cs`
  - уже есть bounded snapshots, add/delete/restore; требуется auto-name и rename, а не новая
    параллельная система истории.
- `src/Map.Persistence/MapProjectStore.cs`
  - существующее атомарное save/read-back и checkpoint storage остаются единственным файловым
    путём.
- `src/Map.App/MainWindow.cs`
  - текущий timer запускает recovery autosave; primary autosave надо координировать, а не запускать
    вторую неконтролируемую гонку.
- `src/Map.App/Views/Ui.cs`
  - текущие размеры rail/context/side равны 56/264/300; не меняй их вслепую и не сжимай canvas.

Сначала составь короткую матрицу `уже есть → переиспользовать / скрыто → открыть / отсутствует →
добавить`. Не переписывай рабочий editor с нуля и не создавай дублирующие состояния.

---

## 6. Инварианты Stage 14

1. Core не зависит от Avalonia/Skia/UI.
2. `MapDocument` остаётся источником истины; transient placement не входит в document.
3. Renderer не меняет document и не выбирает случайный вариант.
4. Один завершённый пользовательский жест создаёт одну undoable-команду.
5. Preview, drag, pointer move, отмена и выбор инструмента не меняют revision, dirty, undo stack,
   `.mapproj`, recovery, object count или asset references.
6. Save остаётся snapshot-based и атомарным. Ошибка/cancel не повреждает прежний файл.
7. Recovery snapshot и primary autosave — разные механизмы и разные статусы.
8. Editor preview никогда не попадает в thumbnail, save, export или checkpoint snapshot.
9. Pointer move не делает работу `O(all objects)`, не пересобирает Objects tree/spatial index и не
   создаёт UI controls по числу объектов.
10. Сохраняются Stage 13 exact/interactive/overview LOD, selection overlay, cache budgets,
    sparse canvas 131 072 и exact export.
11. Основные сценарии не требуют сети.
12. Все новые controls получают стабильные UIA `Name`/accessible name; русские пользовательские
    labels не содержат GUID, raw enum или internal blend keys.
13. Существующие UIA IDs и shortcuts сохраняются, если команда не удалена. Для перемещённой
    команды сохраняй ID и добавляй новый доступный путь, а не ломай automation без причины.
14. Не редактируй реальные `.mapproj`, Library, Recovery, Diagnostics или настройки владельца.
    Tests/smoke используют уникальный temporary profile и synthetic assets.

---

## 7. Stage 14A — единая transient placement session

### 7.1. Состояние

Введи одно presentation/editor state для незавершённого размещения примитива или stamp, например
`PendingPlacement`/`PlacementSession`. Название класса не предписано, но состояние обязано явно
хранить:

- вид объекта/asset set + конкретный variant;
- режим `One`, `Series` или `Scatter`;
- auto-select policy;
- будущий layer;
- world position после snap;
- размер, rotation, style/effects и текущую preview opacity;
- видимость cursor preview и причину невозможности commit;
- для Scatter — radius/density/seeded plan metadata без materialization всей карты.

Это presentation state, а не `MapDocument`, не serialized DTO и не edit command.

### 7.2. Эллипс и прямоугольник

- Нажатие `AddEllipseButton`/`AddRectangleButton` переводит кнопку в видимое selected-состояние и
  вооружает placement.
- Сразу после нажатия object count, revision, dirty, selection и undo не меняются.
- На pointer move виден будущий объект стандартного размера и текущего стиля.
- Клик создаёт его верхним/центральным anchor именно в previewed snapped-позиции; preview и commit
  используют одну геометрию, а не два похожих расчёта.
- Default size остаётся разумным относительно viewport/canvas и не зависит от полного 131K canvas
  так, чтобы объект оказался гигантским или невидимым.
- `Один`: после commit объект выбран, placement завершён, открывается его inspector.
- `Серия`: после commit tool остаётся активным и появляется новый ghost; inspector не перехватывает
  контекст после каждого клика.
- Vector Shape ellipse остаётся отдельным click-drag инструментом с live bounds. Подпиши пути
  понятно, например «Объект: эллипс» и «Фигура: эллипс», не мигрируй старые объекты и не объединяй
  разные доменные виды скрытно.

### 7.3. Stamp

- Выбор asset/variant вооружает Stamp и показывает реальное изображение выбранного варианта, а не
  абстрактный прямоугольник.
- `Один` создаёт один stamp и завершает placement.
- `Серия` создаёт один stamp на клик и сохраняет активный ghost для следующего клика.
- `Россыпь` переиспользует текущие Area/Density/seed/random variant/random angle/random flip и
  создаёт весь gesture одной командой.
- Для россыпи показывай область, количество и bounded preview/sample; не декодируй заново все
  assets и не создавай тысячи Avalonia controls на каждом pointer move.
- Seed/варианты/повороты, показанные preview, должны совпасть с успешным commit. Отказ не двигает
  gesture counter и не меняет следующий random result.
- `Random after placement` двигает current variant только после успешного commit.
- Команду «Разместить в центре вида» убери из главного пути. Если она реально полезна для
  keyboard/accessibility, оставь вторичным пунктом `…`, не как замену canvas placement.

### 7.4. Ghost rendering

Расширь общий render path аккуратно, например отдельным `RenderOptions.PreviewObjects`/
`PlacementPreview`; не обязан использовать это имя. Требования:

- ghost рисуется после сцены как transient editor overlay, но до/вместе с уместным selection
  chrome;
- реальная геометрия/asset resolver/style используются повторно;
- итоговая непрозрачность примерно 50–60%, плюс собственный accent/dashed outline там, где это
  помогает примитиву;
- preview не участвует в culling/index/history/document и не попадает в export/thumbnail/save;
- preview resources bounded и освобождаются при tool switch/detach/dispose;
- pointer leave скрывает ghost, но не обязан снимать armed tool;
- возвращение курсора восстанавливает ghost без мутации документа.

### 7.5. Отмена, ошибки и слой

Порядок `Esc`:

1. сначала отменить pending placement;
2. затем закрыть open group;
3. затем очистить selection.

Правая кнопка по холсту отменяет pending placement, но не открывает destructive action. Tool switch,
возврат на Dashboard и закрытие окна также очищают transient state.

Выбор слоя:

- сначала активный visible/unlocked Object layer;
- затем последний подходящий Object layer;
- если подходящего нет, не создавай объект молча в неверном слое: покажи короткую понятную причину
  и одну явную команду «Создать слой объектов и продолжить»;
- ghost может показываться как invalid, но commit запрещён;
- не показывай raw layer id.

---

## 8. Stage 14B — auto-select, автоматические имена и работа с объектами

### 8.1. Auto-select

Добавь понятный control `Автовыбор` с тремя политиками:

- `Всегда` — выбрать последний созданный объект;
- `Выкл.` — не менять selection;
- `Авто` (default) — выбрать после `Один`, не перехватывать работу после `Серия/Россыпь`.

Настройка относится к placement tool state и запоминается локально. Она не сериализуется в карту,
если проект уже не имеет принятого места для editor preferences.

### 8.2. Автоматические имена

Новые сущности получают осмысленные имена без обязательного ввода:

- stamp — display name asset/set с детерминированным различением повторов при необходимости;
- ellipse/rectangle — `Эллипс 1`, `Прямоугольник 1`, далее следующий свободный номер;
- line/shape — тип + следующий номер;
- text — нормализованный короткий фрагмент содержимого, а для пустого текста `Текст N`;
- group — сохранённое ручное имя, иначе `Группа · N объектов`/осмысленный auto-label;
- layer — понятное русское имя типа + номер.

Старые пользовательские названия не переписывай. Rename остаётся доступным. Генерация имён не
должна сканировать все 50 000 объектов на каждом pointer move; вычисляй только при commit и
переиспользуй revision index/ленивый name index или другой bounded подход. После save/reopen и
Undo/Redo имена стабильны, коллизии предсказуемо разрешаются.

### 8.3. Objects и groups

Сохрани существующую виртуализацию. Улучши уже имеющиеся строки вместо создания второго object
manager:

- содержательное имя, тип и понятные selected/hidden/locked состояния;
- быстрые hide/lock actions с tooltip и accessible name;
- явный breadcrumb `Сцена / <Группа>`;
- `Открыть группу`, `Выйти из группы`, `Разгруппировать` без знания внутренней модели;
- количество элементов группы;
- поиск и type filter продолжают работать без полной материализации controls;
- inspector выбранного stamp явно открывает `Заменить ассет` и существующие эффекты.

---

## 9. Stage 14C — «Версии» вместо «Точки»

Не создавай новую файловую систему версий. Развивай существующие bounded checkpoints.

### 9.1. Создание

- Пользовательская команда называется `Создать версию`.
- Одно нажатие сразу захватывает current in-memory document, preview и local time.
- Обязательного TextBox имени нет.
- Auto-name имеет стабильный формат вроде
  `Версия 01 · 01.09.2026 14:05`; номер выбирается детерминированно среди текущих версий.
- Если проект уже имеет путь, успешное создание версии попадает в очередь primary autosave.
- Если документ untitled, версия создаётся в памяти и не вызывает неожиданный file picker; путь
  выбирается только явным первым Save.
- Сохраняются лимиты 20 checkpoints / 64 MiB / preview 320×180 / без auto-eviction.

### 9.2. Управление

Покажи для версии preview, имя, local date/time и достаточную информацию о размере. Действия:

- `Восстановить` — существующая одна undoable transaction;
- `Переименовать` — optional, с тем же лимитом имени, отдельная undoable command;
- `Удалить` — с существующим подтверждением и без удаления других версий;
- save/reopen сохраняет порядок, названия, preview и snapshots;
- старые checkpoints с пользовательскими именами открываются без миграции и отображаются как
  версии с исходным именем.

Команда доступна из понятного пути верхнего меню/Save menu `Файл → Версии…`; она не занимает
постоянную правую вкладку. Можно оставить компактный shortcut/button, если он не перегружает top
bar.

---

## 10. Stage 14C — primary autosave после первого Save

Это сознательное новое решение владельца. Оно меняет только прежнюю operational-семантику
«autosave никогда не обновляет основной файл», но не отменяет recovery, atomic save и защиту
данных.

### 10.1. Поведение

- Новый документ не открывает picker автоматически.
- Первый явный `Save`/`Ctrl+S` выбирает `.mapproj` path и атомарно сохраняет документ.
- После этого изменения coalesce/debounce-ятся и автоматически сохраняются по тому же path в
  фоне.
- Выбери один разумный idle debounce в диапазоне примерно 3–5 секунд; не запускай save на каждом
  pointer event и не держи параллельные saves.
- `Ctrl+S` остаётся немедленным и имеет приоритет.
- `Save As` меняет current path только после успешной публикации.
- Autosave не стартует во время drag/stroke/vector gesture, pending placement, незавершённого
  property edit, export mutation gate или другого transient действия. После commit он догоняет
  последнюю revision.
- Если документ изменился во время фонового save, `MarkSaved(checkpoint)` не выдаёт новую revision
  за сохранённую; следующий coalesced save сохраняет остаток.
- UI остаётся responsive; serialization/thumbnail/I/O выполняются вне UI thread через уже
  существующий save gate/snapshot.

### 10.2. Безопасность

- Используй только существующий `MapProjectStore.Save` atomic temp/read-back/replace path.
- Не пиши напрямую поверх `.mapproj` и не ослабляй Stage 12D diagnostics/migration semantics.
- Ошибка оставляет прежний destination byte-for-byte, документ открыт и dirty, status показывает
  ошибку и понятное действие `Повторить`/`Сохранить как…`.
- Recovery autosave продолжает отдельно создавать safety snapshot. Его нельзя считать успешным
  primary save и наоборот.
- Primary и recovery operations координируются общим gate/coalescing, не interleave и не удаляют
  recovery раньше доказанного clean save.
- Закрытие окна ждёт/корректно отменяет только owned save operation и не блокирует бесконечно.

### 10.3. Видимое состояние

В top/status area различай простыми русскими словами:

- `Не сохранено`;
- `Сохраняется…`;
- `Сохранено HH:mm`;
- `Ошибка сохранения`;
- `Автосохранение выключено`, если пользователь его выключил;
- `Есть восстановление`, когда recovery действительно доступно.

Не смешивай это с техническими diagnostics. Настройка autosave должна быть доступна без raw
config-файла и сохраняться локально для приложения, а не внутри карты, если существующая модель
preferences это позволяет без новой зависимости.

### 10.4. Документирование решения

`docs/DECISIONS.md` сейчас не входит в известный owner-dirty список. Если он остаётся чистым,
добавь новую append-only запись DEC-018 о primary autosave after first explicit Save и отдельном
recovery. Не переписывай DEC-008 задним числом; явно укажи узкую superseded-часть.

Если `docs/DECISIONS.md` стал owner-dirty, не редактируй и не stage его: зафиксируй принятое
поведение в этом Stage 14 contract/result и в финальном отчёте.

---

## 11. Stage 14D — упрощение панелей и каталог

### 11.1. Информационная архитектура

Сохрани удачный фундамент Map: compact top bar, left tool rail, **ровно одна** context panel,
central canvas, right working panel и bottom hint/status. Не делай полный визуальный rewrite.

На обычной ширине редактора:

- справа постоянно доступны только `Слои` и `Объекты`;
- `Библиотека` открывается из Stamp context и может стать крупным overlay/dialog;
- `Заметки` открываются отдельным понятным инструментом слева с context panel;
- `Версии…` находятся в File/Save menu;
- `Размер и область…` находятся в menu карты/документа;
- `Экспорт…` остаётся заметной top/menu command и открывает существующий полноценный workflow;
- прежние семь permanent tabs больше не конкурируют за место и внимание.

Top bar оставь компактным: Back/название, Save/status, Undo/Redo, несколько частых действий и
один понятный menu для редких команд. Не превращай его в новую длинную полосу кнопок.

### 11.2. Слои

Вместо пяти непонятных icon-only Add Layer buttons сделай одну подписанную команду `Добавить слой`
с меню типов и русскими названиями. Сохрани быстрые hide/lock/select/reorder/rename/delete и
существующие UIA IDs там, где действие осталось тем же.

### 11.3. Сворачивание и ширина

- Добавь явные collapse/expand controls для context и right panel.
- Canvas получает освободившееся место; panel не перекрывает его случайно.
- При ширине около 1400×900 `Слои/Объекты` могут оставаться tabs, чтобы не съесть canvas.
- На очень широком окне допустим optional split/pin обоих разделов, только если это не усложняет
  state и реально использует свободное место.
- Минимальное окно остаётся usable: важные команды доступны через menu и keyboard, horizontal
  clipping не скрывает Save/Undo/выход.

### 11.4. Progressive disclosure

Для каждого инструмента показывай сверху 4–7 частых параметров. Shadow, blend, blur, clip mask,
цветовые коррекции, scatter randomization и тонкие параметры находятся в раскрываемом
`Дополнительно`, но не исчезают.

Не переизобретай уже реализованное:

- stamp: replace asset, scale/rotation/sublayer, flips, opacity, shadow, blend, blur,
  Hue/Saturation/Brightness/Contrast;
- scatter: area/density/random angle/random flip/variant;
- grid: geometry, rows/columns/cell size/offset, opacity/width/color/shadow/blend/export policy;
- group breadcrumb и inspector;
- favorites/recent/used-in-map/variants/search/group/filter в Library.

Сначала найди существующий control/command. Если функция есть в модели и renderer, но скрыта или
непонятна, открой её в UI. Если уже доступна и работает, только переставь/подпиши. Не создавай
второе поле с расходящимся значением.

### 11.5. Большой локальный каталог

Переиспользуя Library v2 backend, сделай нативный крупный каталог для выбора stamp:

- дерево локальных collections/categories;
- thumbnails и selected variant;
- search, sort, grouping и существующие filters;
- favorites, recents, used in map;
- variants с previous/next/current;
- import одного или набора через существующие безопасные команды;
- clear empty/error states;
- keyboard navigation и стабильные accessible names;
- virtualized/bounded thumbnail loading для 10 000 logical cards.

Не добавляй Marketplace, store, аккаунты, чужие pack names, публикацию или cloud. Каталог закрывается
с сохранением выбранного variant и возвращает пользователя в вооружённый Stamp placement с ghost.

---

## 12. Явно вне scope

- генератор мира/ландшафта;
- новые brush/land algorithms;
- Marketplace, тарифы, Community, feed, social, collaboration и cloud sync;
- копирование Inkarnate code/assets/icons/colors/text/branding;
- новый file schema только ради UI; существующие `.mapproj` должны открываться как раньше;
- автоматическая миграция/переименование старых объектов и checkpoints;
- редактирование или «заполнение объектами» реальной карты владельца;
- повторная проверка наполненности карты, которую владелец в текущем диалоге решил считать
  выполненной;
- повышение 131K public canvas или обещание 100K UI-fluidity;
- новый renderer, новый UI framework, web/Electron/Tauri/WebView;
- новые dependencies без доказанной невозможности реализовать на текущем стеке;
- full-suite, VM/чистая Windows, coverage, mutation, security scan, SBOM, pre-commit, release,
  installer, updater, package, DPI/accessibility matrix и отдельная независимая приёмка;
- постоянная папка screenshots/evidence.

Если в ходе реализации найден отдельный дефект вне этого scope, не чинить его попутно. Записать
одной строкой как `PROPOSED` в итоговом отчёте.

---

## 13. Обязательные адресные tests

Добавь только tests реального риска с общим маркером `Stage14`. Не прогоняй весь suite.

Минимально доказать:

### Placement/presentation

- arm ellipse/rectangle/stamp не меняет document/revision/dirty/history/selection;
- pointer preview и cancel не меняют document;
- commit совпадает с snapped preview и создаёт одну command;
- `One`, `Series`, `Scatter` и три Auto-select policy;
- Esc/right-click/tool-switch/leave behavior;
- invalid/locked/hidden layer не принимает commit;
- seeded scatter/random variant не reroll-ится между preview и commit;
- pointer preview не увеличивает object-list rebuild count на 50K synthetic scene.

### Core/naming/versions

- детерминированные новые имена и коллизии после Undo/Redo/reopen;
- auto version name/time/preview, rename Undo/Redo, restore/delete;
- старый named checkpoint остаётся совместим;
- limit не удаляет старые версии автоматически.

### Rendering

- ghost использует будущую geometry/asset и нужную opacity;
- ghost отсутствует в export/thumbnail/checkpoint/save scene;
- preview overlay не меняет exact output document и корректно dispose-ится.

### Persistence/autosave

- untitled document не вызывает picker сам;
- после первого Save coalesced primary autosave пишет последнюю committed revision;
- edit during save остаётся dirty и догоняется следующим save;
- primary и recovery не interleave;
- error/cancel сохраняет предыдущий destination byte-for-byte и recovery доступен;
- save/reopen сохраняет версии, имена и объекты.

Запускай только затронутые проекты/filters. Ожидаемый верхний budget:

```powershell
dotnet test .\tests\Map.Core.Tests\Map.Core.Tests.csproj -c Release --filter "FullyQualifiedName~Stage14"
dotnet test .\tests\Map.Rendering.Tests\Map.Rendering.Tests.csproj -c Release --filter "FullyQualifiedName~Stage14"
dotnet test .\tests\Map.Persistence.Tests\Map.Persistence.Tests.csproj -c Release --filter "FullyQualifiedName~Stage14"
dotnet test .\tests\Map.Presentation.Tests\Map.Presentation.Tests.csproj -c Release --filter "FullyQualifiedName~Stage14"
```

Если тесты разумно сосредоточены в меньшем числе проектов, не создавай искусственные tests в
четырёх местах только ради списка. Запусти каждый нужный filter один раз после финального изменения
его входов. Не повторяй зелёный прогон без нового кода.

---

## 14. Один Release build и один native smoke

После законченного production-кода один раз:

```powershell
dotnet build .\Map.slnx -c Release
```

Затем один короткий native smoke на текущем ПК. Переиспользуй минимальную инфраструктуру
`tools/Map.Performance`; не запускай `stage13a`–`stage13d` и не создавай большой новый harness.

Smoke обязан использовать настоящее own-process Avalonia/Win32 окно:

- `ShowActivated=false`;
- `ShowInTaskbar=false`;
- только собственный visual tree и synthetic temporary profile/map/assets;
- без `SendInput`, foreground activation, Brave/Chrome switching или управления другими окнами;
- все окна/Skia resources закрыты, temporary profile удалён.

Один связный сценарий:

1. Открыть synthetic map с подходящим Object layer и несколькими объектами.
2. Нажать `Добавить эллипс`: доказать отсутствие mutation, переместить pointer внутри собственного
   окна, увидеть ghost, сделать screenshot во временную/ignored папку.
3. Кликнуть: доказать точную позицию, auto-name, one command, selection и disappearance ghost.
4. Вооружить stamp `Серия`, сделать два commit и отменить `Esc`; доказать отсутствие лишнего
   объекта и object-list rebuild на pointer move.
5. Открыть `Версии…`, одной кнопкой создать auto-named version.
6. Сохранить synthetic `.mapproj` один раз, изменить объект, дождаться primary autosave, reopen и
   доказать последнюю committed revision и version.
7. Проверить новый правый layout, collapse/expand и открытие крупного каталога.
8. Закрыть без висящих save/preview ресурсов.

Открой новый screenshot через `view_image` и проверь глазами:

- ghost действительно виден и выглядит как будущий объект;
- выбранный placement mode понятен;
- canvas не зажат и не перекрыт;
- справа нет прежних семи конкурирующих tabs;
- основные русские labels читаемы и не содержат внутренних значений.

Screenshot/JSON не коммить; удалить или оставить только в ignored temporary folder. Это один smoke,
не screenshot matrix.

---

## 15. Порядок реализации и commits

Работай небольшими вертикальными срезами, но не останавливайся между ними за новым разрешением:

1. `Stage 14A`: transient placement + ghost + modes/cancel.
2. `Stage 14B`: auto-select + names + object/group ergonomics.
3. `Stage 14C`: versions + primary autosave/recovery separation.
4. `Stage 14D`: panel IA + add-layer menu + catalog/collapse/progressive disclosure.
5. Targeted tests, один Release build, один native smoke, diff review.

Разрешены до четырёх содержательных commits, если каждый срез компилируется и не оставляет
половинчатый пользовательский путь. Рекомендуемые сообщения:

```text
feat(stage-14a): add preview-first object placement
feat(stage-14b): automate object naming and selection
feat(stage-14c): add one-click versions and safe project autosave
feat(stage-14d): simplify the editor workspace and asset catalog
```

Можно объединить соседние commits, если реализация тесно связана. Не делай отдельные commits для
форматирования, tests или отчёта без необходимости. Не amend/rebase.

Перед каждым commit:

```powershell
git status --short
git diff --check
git diff --stat
git diff -- <только файлы Stage 14>
```

Stage только точные Stage 14 paths. Не используй `git add -A`/`git add .` в dirty worktree.

После всех ворот:

```powershell
git log --oneline --decorate --max-count=8
git diff --check <BASE>..HEAD
git status --short --branch
git push -u origin codex/stage-14-object-placement-workspace-ux
```

В PowerShell диапазон с переменными всегда заключай в кавычки: `"$base..$head"`.

Не отмечай Stage 14 `FACT`, пока обязательный пользовательский сценарий, tests, build, native smoke
и обычный push не завершены. Если push один не удался, локальный результат остаётся `FACT`, а
remote-публикация — `PARTIAL` с точной причиной; не расширяй auth/scopes сам.

---

## 16. Документация результата

Не редактируй перечисленные owner-dirty roadmap/handoff/runbook-файлы. Разрешено:

- добавить узкую DEC-018, только если `docs/DECISIONS.md` остаётся чистым;
- обновить чистые функциональную/data-model спецификации минимальными фактическими формулировками;
- в конце этого файла добавить краткий раздел `Результат выполнения` с commit hashes и реально
  пройденными gates, если файл включается в Stage 14 commit.

Не создавай отдельный prompt, review-plan, evidence tree, acceptance report или generated artifact.
Не переписывай исторические Stage 12/13 contracts.

---

## 17. Definition of Done

Stage 14 готов только если одновременно истинно:

- нажатие Add Ellipse/Rectangle и выбор stamp не добавляют объект до canvas commit;
- ghost точно следует курсору, показывает snapped result и не попадает в документ/export/save;
- One/Series/Scatter, Auto-select и отмена работают предсказуемо;
- commit является одной undoable command и не ломает random/variant semantics;
- auto-names/layer choice понятны и стабильны;
- «Точки» заменены на one-click «Версии» с optional rename и старой совместимостью;
- после первого Save primary autosave атомарно сохраняет main file, recovery остаётся отдельным;
- save/error/dirty/recovery status честны и UI не блокируется;
- справа остались понятные рабочие `Слои/Объекты`, остальные функции доступны через логичные пути;
- крупный local catalog переиспользует Library v2 и возвращает armed Stamp ghost;
- advanced controls не потеряны и не продублированы;
- 50K pointer preview не перестраивает object tree/spatial index и не обходит все объекты;
- старые `.mapproj`/checkpoints открываются без скрытой миграции;
- адресные Stage14 tests зелёные;
- один Release build зелёный;
- один own-process native smoke и AI-vision screenshot review зелёные;
- owner files/data не изменены и не попали в commits;
- ветка обычным push опубликована в существующий `origin`;
- PR/issues/Project/tag/release не создавались.

Если один пункт не доказан, используй `PARTIAL` или `BLOCKED`, а не заявляй весь этап готовым.

---

## 18. Stop-gates

Остановись и спроси владельца только если:

- актуальный accepted product HEAD не является потомком `68a8982c...`;
- реализация требует необратимого изменения реальной карты/Library/Recovery владельца;
- без нового file schema невозможно выполнить обязательное поведение;
- требуется новая dependency/account/network service или изменение GitHub auth/scopes;
- обнаружено, что primary autosave неизбежно нарушает atomic save/recovery contract;
- owner-dirty production-файл пересекается с необходимым изменением и безопасно отделить diff
  невозможно.

Обычная сложность кода, упавший test, необходимость исправить собственную реализацию или
неудачный первый UI layout stop-gate не являются. Исправляй в пределах этого этапа.

---

## 19. Финальный отчёт

Отчёт должен быть коротким, доказательным и преимущественно на русском:

1. точные branch, `BASE`, `HEAD`, commits и push-result;
2. `FACT`: что изменено для владельца;
3. `FACT`: какие Stage14 tests, Release build и native smoke реально выполнены;
4. `NOT CHECKED`: что сознательно не запускалось по правилам;
5. `PARTIAL`/`BLOCKED`: только реальные остатки с причиной;
6. подтверждение, что owner-dirty files и реальные данные владельца не попали в commits;
7. один следующий практический шаг.

Не выдавай наличие кода или зелёный build за доказательство визуального placement-сценария. Не
выдавай recovery snapshot за успешный primary save. Не называй локальный commit опубликованным,
если push не подтверждён.

Закончи строго обязательным разделом:

## ИТОГИ

- `Сделано:` простыми словами, что теперь умеет программа и что это даёт владельцу.
- `Что дальше:` один понятный следующий шаг либо «обязательных действий нет».
- `Реализация проекта: N%` — округлённая оценка готовности всего текущего принятого scope/roadmap
  с одной короткой оговоркой о том, что ещё не реализовано или не проверено.

Процент — оценка объёма продукта, а не доля зелёных tests. Не включай в готовую часть `PARTIAL`,
`PROPOSED` или `BLOCKED`.
