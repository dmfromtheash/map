# 00. Справочник этапа Map

Справочник к чек-листу `docs/00_STAGE_PROTOCOL.md`: как проверять, стоп-условия, отвергнутые
подходы, известные падения, разрешения владельца, подводные камни, команды. Буквы разделов
сохранены из прежнего протокола DEC-051 (A, B, E, G заменены чек-листом и `AGENTS.md`).
Списки §I, §J, §L переписываются по факту; новые разрешения §K — только записью сюда и в DEC.

## C. Реализация

| № | Пункт | Как выполнить | Доказательство |
|---|---|---|---|
| C1 | Только запрошенное, небольшими вертикальными срезами, до конца поручения; production-код прошлых этапов не удаляется (`AGENTS`) | — | `git diff --stat` |
| C2 | Правки файлов сохраняют кодировку: UTF-8 BOM и CRLF как в HEAD; скрипты `tools/ai/*.ps1` — с BOM (PowerShell 5.1); правки — инструментом редактирования (Edit / apply_patch); `sed -i` и python-замены ломают BOM/CRLF и запрещены (`.gitattributes`, `.editorconfig`) | `git diff` без ложных изменений строк | «Кодировка/переводы строк сохранены» |
| C3 | Изолированный профиль для любого запуска Map: `MAP_UI_TEST_MODE=1`, `MAP_UI_TEST_PROFILE_ROOT`, `MAP_UI_TEST_RECOVERY_ROOT`, `MAP_UI_TEST_SHORTCUT_SETTINGS_PATH` (абсолютные пути); профиль владельца `%LOCALAPPDATA%\Map`, его карты, `recent.json` и окна не трогаются; live-скрипты не запускать при открытом Map владельца | — | Путь профиля |
| C4 | Ничего не копируется из Inkarnate и других программ: только наблюдаемое поведение, read-only CDP, страница владельца не навигируется (`AGENTS`, память) | — | «Копирования нет» |
| C5 | Новая функция получает имя контрола и попадает в смоук-профиль `tools/Map.Performance` (новый или ближайший существующий) и/или в `tools/Map.LiveCheck` | код | Имя профиля |
| C6 | Снимки — только окна Map (`mapwin.ps1 -Action shot`, PrintWindow), никогда весь рабочий стол; windows-mcp — только после `App switch` на Map | — | Пути снимков |
| C7 | При нехватке контекста сессии точное состояние и следующий шаг записываются в журнал и `docs/STATE.md` до продолжения | — | — |

## D. Проверка — по одному разу, соразмерно изменению

Обязательный минимум зависит от типа изменения (`AGENTS.md §Проверка`); строки суммируются.

| Тип изменения | Обязательно | Команда / способ | Что записать |
|---|---|---|---|
| Любой production-код или сборочная конфигурация | D1 Release-сборка один раз | `dotnet build .\Map.slnx -c Release -m:1 -nr:false -p:UseSharedCompilation=false` (Map владельца закрыт; свои тестовые `Map.App` убиты) | «0 ошибок, N предупреждений» (анализаторы SDK — предупреждения, не ошибки) |
| Конкретная логика с риском регрессии | D2 адресные тесты по затронутым проектам | `dotnet test tests\<Проект>\<Проект>.csproj -c Release --filter "FullyQualifiedName~<Класс или область>"` | `N/N` по каждому фильтру; полный suite запрещён без запроса |
| Движок/рендер/Core широкого действия | D3 весь затронутый тестовый проект и сверка с перечнем давних падений (раздел J) | `dotnet test tests\<Проект>\<Проект>.csproj -c Release` | «падают только известные: …» |
| UI или пользовательский сценарий | D4 один native-смоук нужного профиля **и** короткая живая проверка именно этого сценария в изолированном профиле; обход всего интерфейса (`--ui-tour`) — только по поручению | `dotnet build tools/Map.Performance -c Release` (не в slnx!) → `dotnet tools/Map.Performance/bin/Release/net10.0/win-x64/Map.Performance.dll --profile native-<x> --output artifacts/<тема>-<дата>/<x>.json`; живой проход: `dotnet run --project tools/Map.LiveCheck -c Release -- --out artifacts/live-check-<дата>` или Release-exe в изолированном профиле + `mapinput.ps1`/`mapwin.ps1` | `N/N, gatePassed=true`; снимки просмотрены глазами; найденные расхождения |
| Скорость/память | D5 A/B медианами на шумном ПК | `native-paint-perf` с `MAP_PAINT_W=2560 MAP_PAINT_H=1369` (+ `MAP_NEW_ART_PERF=1 MAP_NEW_ART_OBJECTS=50000`, `MAP_HEX_STRESS=1`, `MAP_CLIFF_PERF`), базовый коммит в копии bin, 3–5 чередующихся пар; при GPU-вопросах `tools/ai/live/renderdoc-capture.ps1 -Map <.mapproj> -Frames 10`; CPU/память — `dotnet-trace`, `dotnet-counters`, `dotnet-gcdump`, `MAP_FRAME_TRACE` + `analyze_frames.py` | Медианы до/после, пути отчётов |
| Сохранение/формат файла | D6 save → reopen, старые схемы, именованные версии | тесты Persistence по фильтру + ручной save/reopen в смоуке | «прежние схемы до текущей (`docs/05`, таблица схем) открываются» |
| Экспорт | D7 все режимы вывода, которых коснулась правка | единый файл, пакет тайлов, MBTiles, dd2vtt, hex JSON | Размеры и пути результатов |
| Арт/оформление | D8 визуальное сравнение | `map-visual-compare`: настоящий холст рядом с образцом, 1× и 2×, расхождения названы | Снимки и список расхождений; приёмка владельца не заявляется |
| Инструменты, скрипты, CI, релиз | D9 инструмент реально запущен и дал результат | запуск скрипта/профиля/workflow (`gh run watch <id>`) | Код выхода, путь результата, ссылка на run |
| Любой этап | D10 Git-граница: функции прошлых этапов на месте; ничего из поручения не осталось «готово без кнопки» | `git log --all`, прогон сценария | «Граница цела» |
| Любой этап | D11 Зелёная проверка не повторяется без изменения входных данных; каждая проверка — на тех же DLL, что и приложение (SHA-256 `Map.*.dll` между `src/Map.App/bin` и инструментом при сомнении) | — | — |

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
| F7 | Новая версия (по решению владельца; правило — шапка `CHANGELOG.md`: этап дорожной карты → MINOR, исправления → PATCH): поднять `VersionPrefix`/`AssemblyVersion`/`FileVersion` в `Directory.Build.props`, раздел `[Unreleased]` в `CHANGELOG.md` переименовать в `[X.Y.Z] — дата`, коммит, push, тег `vX.Y.Z` → `release.yml` собирает zip; при сбое `tools/release/publish-release.ps1` + `gh release upload`. `RegionalAtlas` в zip не входит | — |
| F8 | Хеши коммитов и ссылка на релиз записаны в журнал этапа и, если меняют состояние, в `docs/STATE.md` | — |

## H. Стоп-условия: остановиться и спросить владельца

- Нужен новый пакет NuGet, глобальный инструмент, программа на ПК, аккаунт, секрет (DEC-016, docs/10).
- Меняется схема файла `.mapproj`, формат экспорта или совместимость старых карт (DEC-019, docs/05).
  Исключение — повышения схемы, заранее разрешённые этапам дорожной карты (DEC-052 п. 2): номер
  бронируется в `docs/ROADMAP_2026-10_PROGRESS.md`.
- Меняется дефолт поведения, раскладка клавиш, геометрия QuickPropertyBar, палитра, иконки, звук.
- Нужен арт, текстура, шрифт, которых нет среди материалов владельца; любая генерация арта.
- Внешние действия сверх разрешённых в разделе K: новый репозиторий, Project, issue, права токена,
  удаление релизов, изменение видимости, действия в чужих окнах/страницах.
- Действие необратимо для данных владельца: удаление файлов вне `artifacts/` и `bin/obj`, правка
  `%LOCALAPPDATA%\Map`, удаление `_local/` (там материалы владельца), любой `reset/restore/stash`.
- Задача из отложенного этапа (21/22) или без явного поручения (например «заодно начать 23»).
- Проверка требует запуска при открытом окне Map владельца или снимка его экрана.

## I. Отвергнутые подходы — не повторять без нового решения владельца

Из `docs/DECISIONS.md` (REJECTED) и заметок 2026-09…10: area-first размещение по умолчанию;
генерированный ИИ арт и текстуры без выбора владельца (2026-09 удалены целиком; с 2026-10-22 разрешены
картинки Gemini Pro по стилю его гор — делает Claude, внедряется только одобренное); копирование библиотеки Inkarnate или «близкая перерисовка»;
векторная трассировка живописи владельца; движущиеся фоны (EmberField); звонкие тональные звуки;
колёсико мыши на ползунках; установка иконок/макетов до выбора владельца (откат b899401);
lazy-expander, PrepareMethod warm-up и idle pre-mount панелей (без выигрыша); GPU clip-path при
протяжке гексов; dilate/erode через stroke для филлетов дорог; синхронная тяжёлая работа областей
на UI-потоке; оптимизация по скрытому окну 1440×900; снимки всего рабочего стола; live-скрипты при
открытом Map владельца; generic `:is(Button)` правила в конце `App.axaml`; запись файлов через
`open(p,'wb').write(expr)` без временного файла; повторный аудит 2026-09-22 и переоткрытие Stage 19/20;
REA по файлам игр и чтение React-props Inkarnate.

## J. Давние падения, которые не считаются регрессией (список переписывать по факту)

- **Unit-тесты: давних падений нет** (R04, 2026-10-21). Rendering 225/225, Persistence 198/198, Presentation
  650/650 на 2026-10-21; `tests/KnownFailures.txt` пуст, CI краснит любое падение этих проектов.
  2026-10-22, хвосты: `PlacementWorkflowTests.Stage18_temporary_preview_memory_refusal…`
  дожидается отменённого фонового подсчёта перед проверкой `ReservedBytes`;
  `Trees_sway_and_only_trees` использует управляемые часы; переправы сравнивают одну сцену.
- **Смоуки (2026-10-21, после R04):** зелёные `native-stage19` (31/31), `native-stage20` (26/26), `native-hex-workshop`.
  Остаются и объяснены:
  - `native-stage18` — `FutureStampScaleSlider` не монтируется даже при инструменте ассетов и области «Инструмент»
    (12/12 проверок до него зелёные); следующий шаг — выяснить, какой контрол задаёт масштаб у сохранённой
    области после редизайна панелей 2026-10-19, и переписать шаг `reopen-edit-without-catalog`.
  - `native-object-workflow` — NRE на `ObjectSublayerSlider` (прежнее имя `ObjectLevelSlider` заменено): слайдер
    уровня выбранного объекта не находится и после раскрытия всех секций; проверить, где теперь «Уровень».
  - `native-land-materials` — поиск «подножие» в окне каталога материалов даёт 0 карточек в группе «Акварель»
    (название снежной текстуры владельца изменилось 2026-10-18); переписать на текущее имя.
  - `native-land-cliffs` — одна проверка `island-and-plateau-tiers` (пиксель берега 512,625 после подъёма острова и
    плато); остальные 5 зелёные.
  - `native-history-memory` — `closed-editor-collected` (закрытый редактор не собран GC за отведённое время;
    6/7 зелёные) — проверить одиночно без второго агента, затем искать удержание.
  - `native-editor-integrity` — 2 сценария: `gradient_is_created_and_edited_from_the_panel` и
    `layer_rows_stay_reachable_at_both_sizes` (ряд слоя размером 0×0 на широком окне, 96×34 в компактном — см.
    notes отчёта).
  - `native-stage17` — нужен настоящий «обычный» фикстурный `.mapproj` (`MAP_STAGE17_FIXTURE`); тестовая карта
    новых ассетов не подходит (`Fixture is not the requested ordinary map`).
  - Требуют `RegionalAtlas` (удалён по DEC-037) или удалённое наполнение: `native-stamp-family`,
    `native-mountain-foundation`, `native-editor-defaults`, старые `native-regional*` (кроме
    `native-regional-roles`), «обычный» маршрут `native-paint-perf`
    (`MAP_REGIONAL_PROJECT`); `native-brush-stress`, `native-placement-workflow` — память.
  - R11 (2026-10-26): `native-asset-catalog` (45/45, PASS) проверяет роль через `PassportAssetRoleBox`, JSON,
    повторное открытие и неизменность документа/Undo. Новый `native-regional-roles` использует
    только свои процедурные PNG (65/65, PASS): все семь ролей леса/хребта/поселения, архив, Undo/Redo,
    экология воды/обрывов, пересчёт и save→reopen v30. Оба — настоящие окна Avalonia вне экрана,
    без системного ввода; JSON и 13+7 кадров — `_local/r11-smokes/`. Они не требуют старого комплекта.
    R11 остаётся PARTIAL до живого просмотра Claude и приёмки владельцем.
  - Помощник `NativeEditorScenarioApplication.RevealTab` (общий для stage17/18/19/20, object-workflow) открывает вкладки
    `Reveal:`, переключает область контекста «Объект/Инструмент» (`ContextScopeObject/Tool`) и раскрывает
    свёрнутые секции; новые смоуки используют его, а не прямой `First(c => c.Name == …)`.
  Для окна экспорта живой проход — `Map.LiveCheck --map <файл>`.
- Перед словом «регрессия» — A/B на базовом коммите из копии bin-папки.

## K. Действующие разрешения владельца (с датами; новые — только записью сюда и в DEC)

| Разрешение | Дата/источник | Границы |
|---|---|---|
| Commit → `git show --stat` → push после каждого проверенного этапа | 2026-10-17/18, DEC-049, промпты 2026-10-18/19 | ветка `codex/stage-18-editable-areas` и fast-forward `main` |
| Публичный репозиторий `dmfromtheash/map`, релизы с zip по тегу | 2026-10-19, DEC-050 | без `RegionalAtlas`; installer/подпись/updater — нет |
| Инструменты: RenderDoc, PIX, анализаторы SDK, GitHub Actions, FlaUI, dotnet-dump, Visual Studio 2026 Community, NetTopologySuite, Microsoft.Data.Sqlite (MBTiles) | DEC-050, DEC-051 | другие пакеты — по новому решению |
| Перепубликация ярлыка `publish-local-r2r.ps1` после изменений кода (ярлык с 2026-10-21 запускает `tools/ai/start-map.ps1`, который сам перепубликует при новых исходниках) | 2026-10-18/21 | при закрытом Map |
| REA/CDP-наблюдение Inkarnate только для чтения | 2026-10-16 | страницу владельца не навигировать |
| Невизуальную работу Claude отдаёт Codex (GPT-6.1 Sol, GPT-6 Astra; high/xhigh) | 2026-10-22, DEC-053 | каждый раз спросить владельца: задача, модель, уровень, права; результат проверяет Claude; визуал — только Claude |

Всё остальное из `AGENTS.md §Продукт` и `§Git` остаётся запрещённым: другой ПК, VM, installer,
updater, подпись, новый remote/Project/issue, изменение прав токена, полный suite/coverage/mutation
без запроса.

## L. Подводные камни (короткий список; подробности — журнал и `docs/10`)

- `tools/Map.Performance`, `tools/Map.LiveCheck`, тесты не входят в `Map.slnx` — собирать отдельно;
  иначе смоуки бегут на старых DLL.
- `Map.Presentation.Tests` локально — только последовательно, как в CI:
  `… -- xUnit.ParallelizeTestCollections=false`. Параллельно 20 тестов экспорта ложно падают за миллисекунды
  (2026-10-23).
- Тесты с общим статическим состоянием (счётчики, кэши вроде `LandCliffField`) — в коллекции xUnit с
  `DisableParallelization = true`; ожидание фоновой задачи — с запасом (30 с), а не 5 с: на CI пул потоков
  занят (2026-10-23: два флейка, ветка и `main` одного коммита расходились).
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
