# 00. Протокол этапа Map — обязательные чек-листы и отчёт

Статус: `FACT`, принят владельцем 2026-10-19 (DEC-051). Приоритет: сразу после прямого решения
владельца в текущем поручении и наравне с `AGENTS.md`; при расхождении с другими документами
действует этот протокол, а расхождение записывается в handoff. Протокол обязателен для Codex,
Claude Code и любого другого исполнителя. Он собран из `AGENTS.md`, `CODEX.md`, `CLAUDE.md`,
`docs/00…10`, `docs/ai/*`, `.claude/skills/*`, `tools/ai/*`, `tools/Map.Performance`,
`docs/STATE_HANDOFF.md` (практика 2026-09…10) и заметок владельца; источники названы в скобках.

**Как применять.** Этап — любое поручение владельца, после которого меняется код, документы,
инструменты или артефакты. Исполнитель проходит чек-листы A–G по порядку. Каждый пункт в итоговом
отчёте получает статус и доказательство (раздел G). Пункт нельзя пропустить молча: либо «выполнен»
с доказательством, либо «не нужен» с причиной, либо «не выполнен» с причиной и риском.

---

## A. Старт этапа

| № | Пункт | Как выполнить | Доказательство в отчёте |
|---|---|---|---|
| A1 | Поручение владельца есть и понято; этап не из отложенных (Stages 21/22, DEFERRED/WAITING), номер этапа дал владелец, а не имя ветки или старый промпт (`AGENTS §Непрерывность`, `CODEX`) | Процитировать поручение одной строкой | Строка поручения |
| A2 | Рабочая папка — продуктовая копия `C:\Users\Leshe\.codex\worktrees\stage10f-fonts-bindings\Map`; `C:\Projects\Map` не трогать (`CLAUDE.md`, DEC-020) | `powershell -NoProfile -ExecutionPolicy Bypass -File tools/ai/Get-MapContext.ps1 -AsJson` | root, branch, HEAD, dirty paths |
| A3 | Git-снимок зафиксирован: branch, HEAD, `git status`, `git log --all`; HEAD не старше «последнего продуктового HEAD» из верха handoff; чужие незакоммиченные изменения прочитаны и не трогаются; reset/restore/stash запрещены (`AGENTS §Git`) | команды выше | HEAD, число dirty-файлов |
| A4 | Прочитаны в этом порядке: `AGENTS.md` → `CODEX.md`/`CLAUDE.md` → этот протокол → две верхние секции `docs/STATE_HANDOFF.md` → относящиеся DEC → раздел `02_FUNCTIONAL_SPEC`/`06_ROADMAP` по теме → (UI) `03_UX_UI_SYSTEM`, `DESIGN_SYSTEM` → (арт/ассеты) `09_ASSET_PIPELINE` → (формат файла) `05_PROJECT_FILE` → (GitHub) `GITHUB_PROJECTS` + DEC-050 (`00_START_HERE`) | чтение | Список прочитанных файлов |
| A5 | Референсы, снимки, листы владельца открыты как изображения, а не по именам файлов (`STAGE_20_FABLE_PROMPT`) | Read картинок | Перечень открытых файлов |
| A6 | Сформулированы один практический результат, границы «входит / не входит», проверяемое условие «готово» и перечень проверок из раздела D (`AGENTS §Как работать`) | 3–6 строк в начале работы | Эти строки в отчёте |
| A7 | Стоп-условия раздела H проверены: если этап требует нового пакета, инструмента, схемы файла, изменения дефолтов UI, арта, внешних действий — есть прямое решение владельца или вопрос задан до работы | сверка с H | «Стоп-условий нет» или ссылка на решение |
| A8 | Ранее принятые правила по теме из памяти/handoff учтены: отвергнутые подходы не повторяются (раздел I) | сверка | «Отвергнутых подходов не затронуто» или список |

## B. Проектирование

| № | Пункт | Как выполнить | Доказательство |
|---|---|---|---|
| B1 | Прослежена одна операция пользователя: ввод → ViewModel → undoable-команда → документ → renderer → save/reopen/export; найдено недостающее звено (`map-local-feature`) | чтение кода | Цепочка файлов |
| B2 | Инварианты сохранены: Core не зависит от UI/Skia; документ — источник истины; rendering не меняет документ; редактирование undoable; сохранение атомарное; всё работает без сети (`AGENTS`) | проверка плана | «Инварианты не нарушены» |
| B3 | Для связанных конструкций (реки, дороги, области, гексы) определено: что хранится, что вычисляется, что затронет локальная правка, как Undo/Redo и reopen восстанавливают смысл (`map-linked-geometry`) | заметка | Заметка |
| B4 | Новая схема файла — только при реальной нужде; старые карты и версии читаются; schema-номер и `docs/05` обновляются (DEC-019, `map-linked-geometry`) | решение | Номер схемы или «схема не менялась» |
| B5 | Новая зависимость или инструмент — только при доказанной нехватке существующего кода, по решению владельца; версия и лицензия зафиксированы; геометрических движков не больше одного на задачу (`docs/10`, DEC-016/050/051) | решение | Пакет, версия, лицензия или «нет» |
| B6 | UI: категории карт не смешиваются (DEC-047); дефолты не меняются неявно (DEC «одно размещение»); имена контролов для смоуков (`…Button`, `…Slider`, `Tag="Reveal:…"`) сохраняются; подписи по-русски, «ассет», не «штамп»; QuickPropertyBar геометрия защищена | проверка плана | Список новых/изменённых имён контролов |
| B7 | Арт: только собственные материалы владельца или его явно разрешённые листы; ничего из Inkarnate/игр; provenance и лицензия в `docs/09` (`AGENTS §Продукт`) | проверка | «Чужих материалов нет» |
| B8 | Крупная UI-функция: сначала короткий сценарий и макет (Artifact с вариантами), затем выбор владельца, затем код; макеты, иконки, звуки не внедрять до выбора (`03_UX`, память 2026-10-17) | макет | Ссылка на макет и выбор владельца |

## C. Реализация

| № | Пункт | Как выполнить | Доказательство |
|---|---|---|---|
| C1 | Только запрошенное, небольшими вертикальными срезами, до конца поручения; production-код прошлых этапов не удаляется (`AGENTS`) | — | `git diff --stat` |
| C2 | Правки файлов сохраняют кодировку: UTF-8 BOM и CRLF как в HEAD; скрипты `tools/ai/*.ps1` — с BOM (PowerShell 5.1); Python-правки — через запись во временный файл и замену; `sed -i` в Git Bash ломает CRLF (`MAP_REVIEW…`, память) | `git diff` без ложных изменений строк | «Кодировка/переводы строк сохранены» |
| C3 | Изолированный профиль для любого запуска Map: `MAP_UI_TEST_MODE=1`, `MAP_UI_TEST_PROFILE_ROOT`, `MAP_UI_TEST_RECOVERY_ROOT`, `MAP_UI_TEST_SHORTCUT_SETTINGS_PATH` (абсолютные пути); профиль владельца `%LOCALAPPDATA%\Map`, его карты, `recent.json` и окна не трогаются; live-скрипты не запускать при открытом Map владельца | — | Путь профиля |
| C4 | Ничего не копируется из Inkarnate и других программ: только наблюдаемое поведение, read-only CDP, страница владельца не навигируется (`AGENTS`, память) | — | «Копирования нет» |
| C5 | Новая функция получает имя контрола и попадает в смоук-профиль `tools/Map.Performance` (новый или ближайший существующий) и/или в `tools/Map.LiveCheck` | код | Имя профиля |
| C6 | Снимки — только окна Map (`mapwin.ps1 -Action shot`, PrintWindow), никогда весь рабочий стол; windows-mcp — только после `App switch` на Map | — | Пути снимков |
| C7 | При нехватке контекста сессии точное состояние и следующий шаг записываются в handoff до продолжения (`STAGE_20`) | — | — |

## D. Проверка — по одному разу, соразмерно изменению

Обязательный минимум зависит от типа изменения (`AGENTS §Минимальная проверка`); строки суммируются.

| Тип изменения | Обязательно | Команда / способ | Что записать |
|---|---|---|---|
| Любой production-код или сборочная конфигурация | D1 Release-сборка один раз | `dotnet build .\Map.slnx -c Release -m:1 -nr:false -p:UseSharedCompilation=false` (Map владельца закрыт; свои тестовые `Map.App` убиты) | «0 ошибок, N предупреждений» (анализаторы SDK — предупреждения, не ошибки) |
| Конкретная логика с риском регрессии | D2 адресные тесты по затронутым проектам | `dotnet test tests\<Проект>\<Проект>.csproj -c Release --filter "FullyQualifiedName~<Класс или область>"` | `N/N` по каждому фильтру; полный suite запрещён без запроса |
| Движок/рендер/Core широкого действия | D3 весь затронутый тестовый проект и сверка с перечнем давних падений (раздел J) | `dotnet test tests\<Проект>\<Проект>.csproj -c Release` | «падают только известные: …» |
| UI или пользовательский сценарий | D4 один native-смоук в собственном скрытом окне **и** один живой проход в настоящем окне | `dotnet build tools/Map.Performance -c Release` (не в slnx!) → `dotnet tools/Map.Performance/bin/Release/net10.0/win-x64/Map.Performance.dll --profile native-<x> --output artifacts/<тема>-<дата>/<x>.json`; живой проход: `dotnet run --project tools/Map.LiveCheck -c Release -- --out artifacts/live-check-<дата>` или Release-exe в изолированном профиле + `mapinput.ps1`/`mapwin.ps1` | `N/N, gatePassed=true`; снимки просмотрены глазами; найденные расхождения |
| Скорость/память | D5 A/B медианами на шумном ПК | `native-paint-perf` с `MAP_PAINT_W=2560 MAP_PAINT_H=1369` (+ `MAP_NEW_ART_PERF=1 MAP_NEW_ART_OBJECTS=50000`, `MAP_HEX_STRESS=1`, `MAP_CLIFF_PERF`), базовый коммит в копии bin, 3–5 чередующихся пар; при GPU-вопросах `tools/ai/live/renderdoc-capture.ps1 -Map <.mapproj> -Frames 10`; CPU/память — `dotnet-trace`, `dotnet-counters`, `dotnet-gcdump`, `MAP_FRAME_TRACE` + `analyze_frames.py` | Медианы до/после, пути отчётов |
| Сохранение/формат файла | D6 save → reopen, старые схемы, именованные версии | тесты Persistence по фильтру + ручной save/reopen в смоуке | «v4…v13 открываются» |
| Экспорт | D7 все режимы вывода, которых коснулась правка | единый файл, пакет тайлов, MBTiles, dd2vtt, hex JSON | Размеры и пути результатов |
| Арт/оформление | D8 визуальное сравнение | `map-visual-compare`: настоящий холст рядом с образцом, 1× и 2×, расхождения названы | Снимки и список расхождений; приёмка владельца не заявляется |
| Инструменты, скрипты, CI, релиз | D9 инструмент реально запущен и дал результат | запуск скрипта/профиля/workflow (`gh run watch <id>`) | Код выхода, путь результата, ссылка на run |
| Любой этап | D10 Git-граница: функции прошлых этапов на месте; ничего из поручения не осталось «готово без кнопки» | `git log --all`, прогон сценария | «Граница цела» |
| Любой этап | D11 Зелёная проверка не повторяется без изменения входных данных; каждая проверка — на тех же DLL, что и приложение (SHA-256 `Map.*.dll` между `src/Map.App/bin` и инструментом при сомнении) | — | — |

## E. Документы

| № | Пункт | Куда | Формат |
|---|---|---|---|
| E1 | Новая **верхняя** секция `docs/STATE_HANDOFF.md`: «## Актуально на <дата> (<время суток>) — <тема>» → `**FACT.** По поручению владельца (…)` → что сделано (файлы, классы, имена контролов) → `**Проверено (FACT):**` с числами → `**NOT CHECKED:**` (всегда включая «живой просмотр владельцем», если его не было) → `**Следующий шаг (PROPOSED):**` → хеши коммитов | handoff | как в секциях 2026-10-18/19 |
| E2 | Новое решение владельца → `DEC-NNN` сверху `docs/DECISIONS.md`: контекст, решение, реализация, границы, что superseded | DECISIONS | как DEC-049/050 |
| E3 | Заметное пользовательское изменение → `CHANGELOG.md` | CHANGELOG | `### Changed/Added` |
| E4 | Затронутые `02`/`03`/`05`/`06`/`09`/`10`/`DESIGN_SYSTEM`/`STAGE_*`/`tools/Map.Performance/README.md` обновлены по факту; противоречия не исправляются молча — записываются в handoff «реестр расхождений» | docs | список файлов |
| E5 | Новый профиль смоука, скрипт или инструмент описан рядом с кодом (заголовок файла: назначение, команда, переменные) | код | — |
| E6 | Память исполнителя (Claude: `memory/`) получает только не выводимое из репозитория: решения владельца, отвергнутые подходы, подводные камни | память | — |

## F. Git и публикация

Действующие разрешения владельца (раздел K) позволяют commit+push после каждого проверенного этапа.
Без такого разрешения в текущем поручении пункты F2–F5 не выполняются (`AGENTS §Git`).

| № | Пункт | Как |
|---|---|---|
| F1 | `git status` показывает только свои изменения; чужие не включаются | `git status --short` |
| F2 | Коммит с понятным сообщением (conventional: `feat(...)`, `fix(...)`, `docs(...)`, `perf(...)`), тело по-русски или по-английски без BOM; подпись `Co-Authored-By` исполнителя | `git commit` |
| F3 | Показать владельцу `git show --stat HEAD` | — |
| F4 | Push **одной командой на действие** (классификатор блокирует связки): `git push origin codex/stage-18-editable-areas`; затем `git push origin codex/stage-18-editable-areas:main` (fast-forward, `main` = последняя версия) | — |
| F5 | CI: дождаться `build` (`gh run list --limit 3`, `gh run watch <id> --exit-status`); красная сборка или новые падения в воротах Core/Assets — чинить в этом же этапе | — |
| F6 | Если менялся код и владелец смотрит через ярлык: `powershell -NoProfile -ExecutionPolicy Bypass -File tools/ai/publish-local-r2r.ps1` при закрытом Map | — |
| F7 | Новая версия для скачивания: поднять `VersionPrefix` в `Directory.Build.props`, коммит, push, тег `vX.Y.Z` → `release.yml` собирает zip; при сбое `tools/release/publish-release.ps1` + `gh release upload`. `RegionalAtlas` в zip не входит | — |
| F8 | Хеши коммитов и ссылка на релиз записаны в handoff (E1) | — |

## G. Отчёт по каждому пункту (обязательный формат)

Отчёт этапа состоит из трёх частей и завершает каждое поручение (`AGENTS §Статусы`).

**G1. Таблица чек-листов.** Для каждого пункта A1…F8 одна строка:

```
| Пункт | Статус | Доказательство / причина |
|---|---|---|
| A1 | ✅ выполнен | «Поручение: …» |
| A5 | — не нужен | референсов в поручении нет |
| D4 | ⛔ не выполнен | живой проход не сделан: окно владельца занято; риск: … |
```

Допустимые статусы: `✅ выполнен`, `— не нужен (причина)`, `⛔ не выполнен (причина, риск)`,
`⏳ ждёт владельца (что именно)`. Пустых строк и пропущенных пунктов быть не должно.

**G2. Статусы результата** строго по `AGENTS.md`: `FACT` / `PARTIAL` / `PROPOSED` / `BLOCKED` /
`NOT CHECKED` / `NOT CLAIMED` / `HISTORICAL` / `ASSUMPTION` / `REJECTED`. Зелёные тесты ≠ работающая
кнопка ≠ художественная приёмка владельца; техническая готовность, непроверенный остаток и
отложенный художественный контент разделяются.

**G3. `## ИТОГИ`** простым языком: `Сделано:` что готово и что это даёт владельцу; `Что дальше:`
один шаг или «обязательных действий нет»; `Реализация проекта: N%` с основанием и неопределённостью
или «не пересчитывалась/не подтверждена» с причиной. Число ради формата не придумывать.

## H. Стоп-условия: остановиться и спросить владельца

- Нужен новый пакет NuGet, глобальный инструмент, программа на ПК, аккаунт, секрет (DEC-016, docs/10).
- Меняется схема файла `.mapproj`, формат экспорта или совместимость старых карт (DEC-019, docs/05).
- Меняется дефолт поведения, раскладка клавиш, геометрия QuickPropertyBar, палитра, иконки, звук.
- Нужен арт, текстура, шрифт, которых нет среди материалов владельца; любая генерация арта.
- Внешние действия сверх разрешённых в разделе K: новый репозиторий, Project, issue, права токена,
  удаление релизов, изменение видимости, действия в чужих окнах/страницах.
- Действие необратимо для данных владельца: удаление файлов вне `artifacts/` и `bin/obj`, правка
  `%LOCALAPPDATA%\Map`, правка `C:\Projects\Map`, любой `reset/restore/stash`.
- Задача из отложенного этапа (21/22) или без явного поручения (например «заодно начать 23»).
- Проверка требует запуска при открытом окне Map владельца или снимка его экрана.

## I. Отвергнутые подходы — не повторять без нового решения владельца

Из `docs/DECISIONS.md` (REJECTED) и заметок 2026-09…10: area-first размещение по умолчанию;
генерированный ИИ арт и текстуры; копирование библиотеки Inkarnate или «близкая перерисовка»;
векторная трассировка живописи владельца; движущиеся фоны (EmberField); звонкие тональные звуки;
колёсико мыши на ползунках; установка иконок/макетов до выбора владельца (откат b899401);
lazy-expander, PrepareMethod warm-up и idle pre-mount панелей (без выигрыша); GPU clip-path при
протяжке гексов; dilate/erode через stroke для филлетов дорог; синхронная тяжёлая работа областей
на UI-потоке; оптимизация по скрытому окну 1440×900; снимки всего рабочего стола; live-скрипты при
открытом Map владельца; generic `:is(Button)` правила в конце `App.axaml`; запись файлов через
`open(p,'wb').write(expr)` без временного файла; повторный аудит 2026-09-22 и переоткрытие Stage 19/20;
REA по файлам игр и чтение React-props Inkarnate.

## J. Давние падения, которые не считаются регрессией (сверять с handoff, список переписывать)

- Unit-тесты (2026-10-19): Persistence 15 (`Stage18EditableAreaRoundTripTests.V6_roundtrip…` ×4,
  `ContainerHardeningTests.Null_elements…` ×5, `An_unknown_object_kind…` ×3,
  `Stage13DLargeCanvasRoundTripTests…`, `Stage17EditablePathRoundTripTests.V4_opens…`,
  `Stage10VariantSetRoundTripTests…`); Presentation 5 (`Stage10CMultiSelectionViewModelTests.Select_all…`,
  `Stage10CObjectTreeRowTests.Type_filter…`, `Stage10FFontsAndSettingsTests.Default_registry…`,
  `Stage10EGridViewModelTests.Stage10EPortalSnapReviewFix…`, `EditorIntegrityInputRoutingTests.A_press…`);
  Rendering 6 (`Stage19IllustratedWaterTests.A_lake_outflow…` ×2, `Stage19WaterJoinRenderingTests…`,
  `Stage19EstuaryScenarioTests.A_tributary_and_a_lake_outflow…` (строка 184, та же пробка озера; падает и с
  `MAP_ROBUST_PATHOPS=0`), `Stage14PlacementPreviewRenderingTests…`, `RenderImageCacheDiagnostics.Where_the_cached_frame_differs`).
  В CI (`build.yml`) эти три проекта идут информационно; ворота — Core и Assets.
- Смоуки: `native-stage19`, `native-stage20` («StartRiver/RoadButton is unavailable»),
  `native-land-materials`, `native-land-cliffs`, `native-history-memory`, `native-object-workflow`,
  `native-editor-integrity` (2 проверки), `native-brush-stress`, `native-placement-workflow` (память),
  `native-stamp-family`/`native-mountain-foundation`/`native-editor-defaults`/`native-regional*`
  (требуют удалённое наполнение или `RegionalAtlas`); `native-stage18` (`FutureStampScaleSlider`
  больше нет в панели, 12/12 проверок до него зелёные) и `native-stage17`
  (`reopened-path-appearance-controls`, нужен `MAP_STAGE17_FIXTURE`) — устаревшие имена элементов,
  2026-10-19. Для окна экспорта живой проход — `Map.LiveCheck --map <файл>`.
- Перед словом «регрессия» — A/B на базовом коммите из копии bin-папки.

## K. Действующие разрешения владельца (с датами; новые — только записью сюда и в DEC)

| Разрешение | Дата/источник | Границы |
|---|---|---|
| Commit → `git show --stat` → push после каждого проверенного этапа | 2026-10-17/18, DEC-049, промпты 2026-10-18/19 | ветка `codex/stage-18-editable-areas` и fast-forward `main` |
| Публичный репозиторий `dmfromtheash/map`, релизы с zip по тегу | 2026-10-19, DEC-050 | без `RegionalAtlas`; installer/подпись/updater — нет |
| Инструменты: RenderDoc, PIX, анализаторы SDK, GitHub Actions, FlaUI, dotnet-dump, Visual Studio 2026 Community, NetTopologySuite, Microsoft.Data.Sqlite (MBTiles) | DEC-050, DEC-051 | другие пакеты — по новому решению |
| Перепубликация ярлыка `publish-local-r2r.ps1` после изменений кода | 2026-10-18, память | при закрытом Map |
| REA/CDP-наблюдение Inkarnate только для чтения | 2026-10-16 | страницу владельца не навигировать |

Всё остальное из `AGENTS.md §Продукт` и `§Git` остаётся запрещённым: другой ПК, VM, installer,
updater, подпись, новый remote/Project/issue, изменение прав токена, полный suite/coverage/mutation
без запроса.

## L. Подводные камни (короткий список; полный — handoff и `docs/10`)

- `tools/Map.Performance`, `tools/Map.LiveCheck`, тесты не входят в `Map.slnx` — собирать отдельно;
  иначе смоуки бегут на старых DLL.
- PowerShell 7 (`pwsh`) на ПК нет; `.ps1` с кириллицей — с BOM; `robocopy` из PowerShell, не из Git Bash;
  bash-heredoc с кириллицей и кавычками ломается — python-скрипты писать файлом.
- Шумная машина владельца: один код даёт 31–59 FPS; сравнивать медианы A/B; при свободной ОЗУ < 1–2 ГиБ
  тесты ложно падают по бюджету памяти; открытый Photoshop — отказы памяти в экспорте.
- Размер окна решает: измерять при 2560×1369, окно полностью видимо.
- PostMessage (`mapinput.ps1`): модификаторы не доходят (Ctrl+Z — кнопкой «Отменить»); координаты
  client px; берёт первый процесс `Map.App`.
- REA на Windows не снимает нативные окна — `inspect-managed-members` по `Map.App.dll` и профили
  `native-*-panel-shots`.
- RenderDoc: `MAP_WIN32_COMPOSITION=LowLatencyDxgiSwapChain` обязателен (Avalonia #15911); холст в покое
  кадров не рисует.
- Снимки смоуков — рендер контрола, не монитор; `gatePassed` ≠ 40 FPS ≠ приёмка владельца.
- `Map.App.exe` владельца держит Release-выход: пересборка после закрытия.
- Классификатор auto-mode блокирует связки `git push … && gh …` — по одной команде.
- Зерно случайности — только стабильное (FNV, `Guid`, сохранённый `Seed`): `string.GetHashCode` и
  `HashCode.Combine` меняются при каждом запуске (2026-10-19 так менялись гексовые тайлы).
- Профилировщик VS `CpuUsageHigh` (4000 выборок/с) снижает FPS в 2–4 раза: под ним верны только доли
  времени; FPS — без профилировщика или с `CpuUsageBase`. `.etl` из `-Expand` читается ETW-инструментами
  (символы: `C:\SymCache`, `srv*C:\Symbols*https://msdl.microsoft.com/download/symbols`).
- Снимки только через `PrintWindow` (`mapwin.ps1`, `Map.LiveCheck`); `Capture.Element`/копия экрана сохраняет
  чужое окно, если Map перекрыт (2026-10-19 так попала переписка владельца).

## M. Шпаргалка команд

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/ai/Get-MapContext.ps1 -AsJson
dotnet build .\Map.slnx -c Release -m:1 -nr:false -p:UseSharedCompilation=false
dotnet test tests\Map.Core.Tests\Map.Core.Tests.csproj -c Release --filter "FullyQualifiedName~<Область>"
dotnet build tools/Map.Performance -c Release
dotnet tools/Map.Performance/bin/Release/net10.0/win-x64/Map.Performance.dll --profile native-<x> --output artifacts/<тема>-<дата>/<x>.json
dotnet run --project tools/Map.LiveCheck -c Release -- --out artifacts/live-check-<дата>
powershell -NoProfile -ExecutionPolicy Bypass -File tools/ai/live/renderdoc-capture.ps1 -Map <.mapproj> -Frames 10 -Out artifacts/renderdoc-<дата>
powershell -NoProfile -ExecutionPolicy Bypass -File tools/ai/live/vs-profile.ps1 -Scenario native-paint-perf -Agent CpuUsageBase -Vars "MAP_HEX_STRESS=1;MAP_PAINT_W=2560;MAP_PAINT_H=1369" -Out artifacts/vs-profile-<дата> -Expand
powershell -NoProfile -ExecutionPolicy Bypass -File tools/ai/publish-local-r2r.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/release/publish-release.ps1
gh run list --limit 3 ; gh run watch <id> --exit-status
```
