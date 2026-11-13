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
- Живые проходы сотен (`live-check/live_all.ps1`) и FPS-окно ПК — только по «можно» владельца.

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
