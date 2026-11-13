# Промпт сессии: включить Astra, Sol и ПК — свести пакет FPS, взять в main, довести 60 FPS; refs2 в библиотеку

Ты — Claude Code в Windows-проекте Map. Модель — Claude Opus 5.5 (`claude-opus-5-5`). Рабочая папка `C:\Projects\Map`,
ветка `codex/stage-18-editable-areas` (`main` — та же линия). Предыдущая сессия (13.11, закрыта ~23:45) остановила
всё на всех машинах по слову владельца. Снимок ниже мог устареть: сначала проверь Git, процессы, Mac, USER.

## Вход (по порядку)
1. `docs/STATE.md`; `powershell -NoProfile -ExecutionPolicy Bypass -File tools/ai/Get-MapContext.ps1 -AsJson`. Чужие
   незакоммиченные изменения — сообщить владельцу. Локальные ветки ПК: `review/astra-fps-1m` (= Astra c8765cf),
   `claude/fps-pack` (self-diag + overview-delta WIP), `codex/roadmap-export-step5` (старая, не трогать без вопроса).
2. Решения DEC-106…110 (`docs/DECISIONS.md`), журнал `docs/journal/2026-11.md` — записи 13.11 «(вечер)» и «(ночь)».
   Правила `AGENTS.md` §Как работать (поиск перед решением, автоматизация, DEC-110).
3. Ответы — по-русски, просто. Тихие проверки: о проблемах сразу, сводка раз в 30 мин.

## Контекст: что сделано 13.11 (подробно — журнал)
- **FPS, причина:** 78 % кадра — распаковка картинок в кадре, кэш на 64 записи (DEC-106). Решения DEC-106…110.
- **Astra `astra/fps-1m` (USER, head c8765cf, в main НЕ взято):** `994b872` навигация готовыми GPU-кадрами по умолчанию
  (100 000, USER: pan 11–28 → 52–56 FPS, колесо ~10 → 26–30, max работы в жесте 357–379 → 14–17 мс, первый кадр
  1,15 с вместо 44–76 с, Rendering 480/480, пиксели ≤ 2/255; Mac-тесты зелёные кроме golden сетки); `243f46e` бюджет
  кадров 4K/`UnresolvedImages`/край страницы (ревью Claude); `90096af` review-fixes (наблюдатель не поднимается к корню
  диска; «кэшируемо отсутствующий» ассет, тихий повтор 60 с; поиск по SHA — правка Claude); `503949a` edit-fixes
  (place 83 → 0,3 мс, панель 540 → 0,03 мс на 1 млн); `eaa41c2` derived-janitor (потолок/очистка кэша на диске);
  `c8765cf` edit-fixes-2 (Undo 94 → 0,3 мс). Портативные заготовки (portable) — 9 файлов WIP у неё.
  **Не готово:** 1 % low 22–31 FPS, точная дорисовка после жеста до 295 мс, колесо ~30 FPS.
- **Claude `claude/fps-pack` (ПК, локально, от 243f46e):** `2909820` self-diag — журнал `<profile>/diagnostics/perf-*.jsonl`
  (окна 10 с, 1 % low/интервалы только в жестах; Windows Presentation 1024/1025); `9ff64f1` overview-delta (Mac CPU
  100 000 / 1 000 ассетов: сборок 10 000 → 100, медиана кадра 74 → 2,5 мс; слито с seed Astra; полные тесты НЕ прогнаны).
- **Sol, Mac `~/MapWork/agents/`:** готово и использовано — review-fixes, edit-fixes(-2), derived-janitor, self-diag,
  overview-delta, fps-matrix (`pack\fps-matrix.patch` + `FPS_MATRIX.md` у Astra — ещё не интегрирован), derived-bench
  (`bench.patch`, Windows-замер не сделан), refs2-plan. Не доделаны: `mem-pressure` (патч есть, отчёта нет),
  `overview-prep`. Ревью Claude к каждому патчу — письма в `D:\MapChecks\astra\INBOX.md` 13.11 22:04–23:12.
- **Библиотека:** refs2 внедрён (`bf5bb9a`), апскейл 4 501/4 501; refs 38 987/38 990; проверка «нарисован» исправлена
  (`c9f8c36`, `48fda19`); `live-check/live_all.ps1` (`95bf6bf`) — 13.11 battlemaps-classic №3–4 не прошли (321 с,
  «вызов метода для NULL»), затем тестовая Map закрывалась — владелец закрывал окна; причину №3–4 не разбирали.
- **Ошибки Claude 13.11 (не повторять):** regex-замена в `run_dbg.cmd` сломала `>>`; запуск `start_test_map.ps1` через
  конвейер ждал выхода Map (исправлено в `live_all.ps1`); окна на ПК при работающем владельце мешали.

## Слова владельца 13.11 (действуют)
«используй максимально ПК/Mac/USER», «Сол запускай где нужно», «загрузи ПК тоже», «сделай максимально коммитов»,
«разбить коммиты по устройствам, чтобы ускорить». На ПК окна (живые проходы, FPS-окна) 13.11 мешали — перед оконным
прогоном на ПК спросить; Astra на USER — полный доступ там (слово владельца 13.11).

## Шаг 1 — включить всё (первые 10 минут)
- **Наблюдение:** CronCreate `*/5` тихой проверки (`bash tools/library-pipeline/monitor/check_mac.sh`), Monitor писем и
  коммитов Astra (OUTBOX + `git rev-parse astra/fps-1m` через `r.ps1`), `tools/library-pipeline/fps/auto_check.ps1` в фоне.
- **Astra (USER):** письмо `D:\MapChecks\astra\ASTRA-MSG-6.md` → запуск через задачу `MapAstraMsg` (Interactive, user1).
  Рабочий запускатель — `D:\MapChecks\astra\run_dbg.cmd` (`exec resume` сессии `01a1d4ae-199b-7c40-a0d2-f997dd76f765`,
  вывод в `dbg.log`); поменять в нём имя файла письма через `.Replace("ASTRA-MSG-5.md","ASTRA-MSG-6.md")` — не regex
  (13.11 regex сломал `>>`). Проверить, что процесс `exec resume` жив и `dbg.log` растёт. Если «usage limit» — сказать владельцу.
  Её задачи: (а) portable WIP (9 файлов) → коммит; (б) точный кадр после жеста порциями ≤ 16 мс (сейчас до 295 мс);
  (в) 1 % low до 60 (сейчас 22–31) и колесо ~30 — честный A/B способа вывода (`MAP_WIN32_COMPOSITION`, DXGI);
  (г) накатить `claude/fps-pack` (bundle от Claude) и интеграцию `overview-prep`/`mem-pressure` после ревью Claude;
  (д) механизмы DEC-110 (автоподготовка, прогрев). Коммит после каждого зелёного шага, письмо в OUTBOX после коммита.
- **Sol (Mac):** перезапустить `overview-prep` и `mem-pressure`: `bash ~/MapWork/agents/run_agent.sh <имя> high`
  (брифы в `~/MapWork/agents/<имя>/brief/`). `mem-pressure.patch` уже есть — пусть агент доведёт проверку и SOL_REPORT.
  Свободный Mac загружать новыми агентами по плану FPS (не дублируя Astra): например A/B-инструмент худших кадров
  для `fps-matrix`, `derived-build` команда конвейера (DEC-107) — бриф по образцу `review-fixes`.
- **Sol на ПК (где нужен Windows):** по DEC-089 — навык `codex`, Sol (GPT-6.1) с явным
  `-c 'default_permissions=":workspace"'`, временная копия только под задачу. Кандидаты: Windows-замер `derived-bench`
  (SQLite vs файлы) на реальном кэше заготовок (сначала спросить владельца про копию и назвать размер), Windows-прогон
  `fps-matrix` без окон. Diff и прогон — проверяет Claude.
- **ПК (Claude, без окон), параллельно сведению:** проверить по одному `EditorViewRefreshTests.Selection_burst…`;
  Windows-замеры «до/после» в консоли — `tools/Map.EditLatency` (1 млн объектов, edit-fixes/-2) и бенчмарк обзора
  (`~/MapWork/agents/overview-delta/benchmark/` — адаптировать пути на Windows); ревью `mem-pressure.patch`.
- **ПК (Claude, без окон):** `claude/fps-pack` → `git rebase review/astra-fps-1m` (конфликты ожидаются в
  `SceneRenderer.cs` у `DrawOverviewObjects`/интерфейса `IStampImageSource` и в `StampDerivedAssetStore.cs`: сохранить
  локальный seed Astra — палитра только видимых ассетов + `UsesAsset`), Release `Map.slnx`, все 5 наборов тестов
  (`fps/pc_review_tests.ps1`, ~25 мин), коммиты, bundle → `D:\MapChecks\astra\pack\` + письмо.

## Шаг 2 — взять в main
Когда `review/astra-fps-1m` + `claude/fps-pack` сведены и зелёные (Windows 5 наборов, Mac `mac_tests.sh`, ревью Claude
пакетом, живое USER-сравнение Astra) — fast-forward/merge в `codex/stage-18-editable-areas`, CHANGELOG `[Unreleased]`,
push ветки и `main`, CI `quick` (и `full` до закрытия пакета). Если менялся `src/` и Map владельца закрыта —
`tools/ai/publish-local-r2r.ps1` и проверка запуска через ярлык. Числа FPS «до → после» — из отчётов Astra (USER).

## Шаг 3 — библиотека (параллельно, без окон)
- refs2: план с refs2 (`H:\MapLibrary\pipeline\plan.py … --refs2-inventory/--refs2-files/--refs2-classification ×2/
  --refs2-pixel-duplicates`, файлы в `H:\MapLibrary\plan\refs2\`), затем `plan/pour_all.ps1 -Catalogs
  shop-surfaces,shop-effects,textures-2` (сотни без окна). Проверить отчёты `checks\<каталог>\`.
- 3 узкие лестницы (6–16 px, ×2): свой путь на Mac (finish_asset `cycle_rgb=True`, factor 2) — или оставить и сказать.
- Живые проходы сотен (`live-check/live_all.ps1`) и FPS-окно ПК (`fps/pc_live.ps1`, 144 Гц, заготовки включены) —
  только по «можно» владельца; первым разобрать провал battlemaps-classic №3–4 (`H:\MapLibrary\sol\sol7\live\`).

## Правила и границы
- Окна Map/замеров — только на неосновном экране, журнал положения обязателен. Без ИИ-зависимостей в продукте (DEC-110).
- Новая проблема — сначала поиск готовых решений (`deep-code-research`), находки — исполнителю.
- Mac: всё внутри `~/MapWork`; результаты агентов — только после проверки Claude (diff + прогон).
- Чужая незакоммиченная работа (portable WIP Astra) — не трогать.
- Удаление `refs`/`refs2` — только после условий в STATE и «да» владельца. Временное — удалять сразу.
- Закрытие: остановить Astra (`taskkill /T` процесса `exec resume`), агентов (`pid`), автопроверку, cron/monitor;
  забрать коммиты Astra bundle'ом; журнал, STATE, push.

## Итоговый отчёт
По-русски: что взято в main (коммиты), FPS «до → после» (USER; ПК — если было окно), что у Astra/агентов, библиотека
в числах, что не проверено; в конце `## ИТОГИ` (Сделано / Что дальше / Реализация проекта) по `AGENTS.md`.
