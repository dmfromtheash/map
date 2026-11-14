# Промпт сессии: довести пакет FPS до main (пиксели, кэш, живая проверка) и начать ядро DEC-111

Ты — Claude Code в Windows-проекте Map. Модель — Claude Opus 5.5 (`claude-opus-5-5`). Рабочая папка `C:\Projects\Map`,
ветка `codex/stage-18-editable-areas` (= `main`, `cde2345`). Предыдущая сессия (14.11, закрыта ~17:00 по слову
владельца) остановила всё на всех машинах аккуратно. Снимок ниже мог устареть: сначала проверь Git, процессы, USER, Mac.

## 0. Вход (по порядку, ~10 минут)
1. `docs/STATE.md`; `powershell -NoProfile -ExecutionPolicy Bypass -File tools/ai/Get-MapContext.ps1 -AsJson`.
   Чужие незакоммиченные изменения — сразу сообщить владельцу. `git worktree list`: ожидаются `C:\Projects\Map-wt-integ`
   (`claude/fps-pack-2` `af09b5f`) и `C:\Projects\Map-wt-blur` (`claude/blur-wip` + 24 снимка в `shots\`). Других быть не должно.
2. Журнал `docs/journal/2026-11.md` — запись **2026-11-14** целиком (что сделано, числа, причины, подводные камни).
   Решения DEC-106…111 (`docs/DECISIONS.md`, DEC-111 — сверху). Проект ядра: `docs/perf/perf-core-design.md`
   (опись 22 механизмов + ядро из 5 служб), `docs/perf/perf-functions.md` (18 семейств функций, топ-10 рисков),
   `docs/perf/core-research-astra.md` (готовые решения: Chromium, React, Filament, Skia, ANGLE), `docs/perf/LARGE_SLOTS_PLAN.md`.
3. Ответы — по-русски, просто. Тихие проверки; о проблемах — сразу.
4. **Спросить владельца (DEC-089)** одним вопросом: Astra на USER (полный доступ, как 14.11?), Sol 6.1 high на ПК/Mac
   (владелец 14.11: «неважные задачи — Сол», БД/кэш — только Claude), свои агенты Claude — можно без вопроса.

## 1. Что сделано 14.11 (кратко; подробно — журнал)
- Ветка **`claude/fps-pack-2` `af09b5f`** (22 коммита поверх Astra `a5a41b5`): журнал производительности; обзор в фоне
  (холодный кадр 1 млн 1531 → 0,35 мс); память документа 1179 → 561 Б/объект (≈3,8 млн объектов в 2 ГиБ); справочники в фоне
  (выбор 2 млн 7,3 с → 0,4 мс); удаление/перенос/владение областями/подписи — O(изменённого); стили без обхода N; свет
  хранит кадр; кадры камеры при выделении; реки/дороги по словарям; кэш покрытия земли (fit 6,3 → 0,2 с); бюджет жеста
  (GC 2 млн 863 → 44 мс, `Map.Core.Resources.BackgroundWork`); плотная карта сходится (8192/250к: никогда → 6,6 с);
  исправлено зависание `Map.Presentation.Tests` (тест создавал Avalonia-контролы на потоке пула); `af09b5f` — бюджет
  подготовленных данных до 5 ГиБ (доля пирамиды 40 % зарезервирована), janitor впервые реально работает, точечная чистка
  без UI (`PurgeDerived/ByTag("Тестовое")/Unused`), ОЗУ кэшей 1/8–1/4 (`docs/perf/CACHE_BUDGET.md`).
- Windows-тесты: на `f25e329` Core 637, Assets 173, Persistence 509, Presentation 1089/1090, Rendering 558/558; на
  `af09b5f` Core 658, Assets 176, Presentation 1106/1107 (Rendering после него — перепрогнать первым делом).
- **Astra** `review/astra-fps-1m` `04347d7` (на ПК): portable + `fd6253e`, матрица функций `f6d7b9d`, сетка FPS,
  диагностика живых фаз и выбранных mip, её копии моих первых 7 коммитов пакета (`32a9b48`…`8e91a21`).
- Сетка FPS USER (HD 630): ниже 20 FPS только плотные карты (8192 px с ≥ 250 тыс. объектов); на 32 768–262 144 px до
  2 млн ~30/60. Колесо «ровно 30» — артефакт `MAP_MEASURE_GPU_FLUSH=1` замерщика; без него 53–60 FPS, **1 % low ≈ 28**.
- Живое сравнение пакета (131 072/1–2 млн): выбор p95 3,3–6,7 → 0,36–0,45 с; место 2,2–4,7 → 0,32–0,42 с; max кадра с
  открытием 5–10 → 0,55 с; память −0,5…−1 ГБ. Регресс pan/fit 1 % low (GC) исправлен `01d092b` — **вживую не перепроверен**.
- Библиотека: refs2 залит полностью; правки `H:\MapLibrary\plan\exclusions.json` (пустой Grid Square D) и `trims.json`
  (Rocks 64 МиБ → WebP без потерь; 6 космических 8192² → 4096²). Копия shop-* на USER `D:\MapLibrary`.

## 2. Открытые проблемы (в порядке важности)
1. **Разница пикселей (стоп приёмки пакета).** 32 768/500к: fit max 35/255 (4 378 px), после правок 28/255 (15 333 px).
   Синтетика `a5a41b5` = `e97f523` байт-в-байт. Причина (ветка `claude/pixel-wip` `8e05957`, `PIXEL_BISECT.md`): с `64824fe`
   растры кусков обзора с «нарисованными» горами строит фоновый поток через `Source.Lease()`
   (`SceneRenderer.OverviewPreparation.cs:344–346`) → `CatalogStampImageSource` `CachedSample`/оригинал/
   `AssetImageCache.GetForPixelSize` (при нехватке `ByteLimit` отдаёт оригинал, `AssetImageCache.cs:416–419`), а поток кадра
   брал готовые derived-mip или соседний уровень (`SceneRenderer.cs:5186`, `CatalogStampImageSource.cs:243–266, 439–478`).
   План: перед постановкой куска — `PrepareForPixelSizes` его штампов; в `PrepareChunk` брать только готовые точные
   derived-mip (нет — отложить кусок); для запекаемых растров не отдавать оригинал. Проверка: каталожный повторитель
   `tests/Map.Presentation.Tests/PixelBisectCatalogRepro.cs` (25–40 мин) — успокоенный кадр = синхронный эталон и = `a5a41b5`.
2. **«Мыло» и недогрузка** (снимки Astra `D:\MapChecks\astra\shots\blur-review\`, разбор `blur-review.md`):
   (а) сразу после колеса весь вид — кадр камеры, растянутый в 10–120 раз; (б) открытие — только центр обзора, вся карта
   ~30 с; (в) плотная 400 %: `Art Deco Wall` 83×184 вместо 639×1408 через 30 с (pixelQueue 334) — один большой слот mip.
   Ветка `claude/blur-wip` `97054eb` (предел растяжения 2×, мягкие пятна среднего цвета, цветное поле всей карты;
   **блокер**: после отдаления кэш обзора 64 МБ переполнен — 108 записей, 7 325 вытеснений — fit не догружается) +
   24 снимка `C:\Projects\Map-wt-blur\shots\` — **показать владельцу до внедрения** (он выбрал «быстро, с цветной заглушкой»).
   Большие слоты: план `docs/perf/LARGE_SLOTS_PLAN.md` + замер `StampDerivedAssetStoreLargeSlotsTests` (`MAP_LARGE_SLOTS_BENCH=1`).
3. **1 % low ≈ 28** без ожидания GPU: нужна трасса **PresentMon** (`D:\MapChecks\tools\PresentMon\PresentMon.exe`,
   2.6.0, подпись Intel) — окно Astra не имеет прав ETW; запускать из ssh-сеанса Claude (администратор):
   `PresentMon.exe --process_id <pid> --output_file <csv> --timed <s> --terminate_after_timed --no_console_stats`.
   Схема: Astra кладёт `D:\MapChecks\astra\presentmon-request.json` {tag,pid,seconds,out}, Claude запускает и отвечает в INBOX.
4. **Сохранение** (`claude/save-wip` `79245e2`): 2 млн 122,6 → 3,05 с; падает `Stage12D_unrelated_open_failure_keeps_recovery_cards_visible`
   — карточки восстановления теперь приходят асинхронно; решить: поведение или тест.
5. Плотная карта: кадры 61–70 мс в CPU-композиции стенда; точные плитки теперь рисует CPU (не GPU-атлас) — **сравнить
   пиксели с прежним GPU-видом вживую**.

## 3. План сессии
**Шаг 1 — довести пакет до main (первым).**
- Перепрогнать Rendering на `af09b5f` (после кэш-коммита не гонялся); где показать команды чистки в UI — спросить владельца.
- Исправить разницу пикселей по п. 2.1 (агент Claude в `C:\Projects\Map-wt-pix2`), каталожный повторитель → байт-в-байт.
- Свести с Astra: `git rebase --onto review/astra-fps-1m 0e2aa37 claude/fps-pack-2` (её копии моих коммитов до
  `0e2aa37` включительно уже в её ветке: `32a9b48`…`8e91a21` и `fe5e9cf`), конфликты — сохранить обе стороны; Release `Map.slnx` + 5 наборов тестов.
- Живая проверка Astra на USER: те же 4 ячейки (131072×1/2 млн, 32768×500к, 8192×250к), **продуктовый режим** (без
  `MAP_MEASURE_GPU_FLUSH`), пиксели ≤ 2/255, снимки разных моментов (правило владельца) — Claude их отсматривает.
- main: слияние в `codex/stage-18-editable-areas` (слияние, не перемотка: main ушёл вперёд документами; конфликт только
  в `docs/journal/2026-11.md`), `CHANGELOG [Unreleased]`, push ветки и `main`, CI `quick` и `full`; если менялся `src/`
  и Map владельца закрыта — `tools/ai/publish-local-r2r.ps1` и проверка запуска через ярлык.
**Шаг 2 — мыло и загрузка:** большие слоты (план), `claude/blur-wip` без блокера кэша; снимки владельцу; внедрить по «да».
**Шаг 3 — ядро DEC-111** по `docs/perf/perf-core-design.md` (каждый шаг сначала оборачивает старые константы потолками):
датчики CPU/GPU (асинхронные `EXT_disjoint_timer_query` через `IGlContext.GlInterface.GetProcAddress`, не sync-flush) →
единый бюджет кадра (сейчас 2/4/2/8/8 мс независимо) → вся память, включая GPU-поверхности `SKSurface.Create(gpu,false,…)`
вне лимита Skia → общий планировщик на базе `BackgroundWork` (сроки против голодания, как React; потолок «ядра−2») →
регулятор качества (ПИД Filament, Apache-2.0, + мёртвая зона и задержка между шагами) → сторожа O(N) для всех команд.
Затем пирамида карты (доля 40 % бюджета 5 ГиБ уже зарезервирована в `PreparedDataBudget`).
**Параллельно (без окон):** конвейер DEC-107 — после заливки каталога `--profile derived-build` (теперь пропускает
готовые); полный замер SQLite vs файлы `tools/derived-bench` (n=2000, ~2,5 ГБ временно — назвать владельцу) — БД на Claude.

## 4. Машины и как запускать
- **USER** (Intel HD 630): `& C:\Users\Leshe\.map-remote\r.ps1 -Script '<ps>'`; Astra — `D:\MapChecks\astra\` (INBOX/OUTBOX/
  REPORT.md, `pack\`), запуск письмом: `run_dbg.cmd` (`exec resume` сессии `01a1d4ae-199b-7c40-a0d2-f997dd76f765`, codex
  `C:\Users\user1\AppData\Local\OpenAI\Codex\bin\9e1da79431da526b\codex.exe` — путь меняется при обновлении приложения!),
  задача `schtasks /run /tn MapAstraMsg`; замена имени письма — `.Replace(...)`, не regex. Проверить, что `exec resume` жив.
  Письма в INBOX — новые сверху (вставка после заголовка). Наблюдение — Monitor по заголовку OUTBOX и `git rev-parse astra/fps-1m`.
- **Mac** `user@192.168.31.78` (`-o HostKeyAlias=192.168.31.25`): агенты Sol — `~/MapWork/agents/<имя>/` (repo-клон из
  bundle, `run-env.sh`, `mac.props`, `NuGet.Config`, `test-runner/`, `COMMANDS.md`, `brief/COMMON.md` + `brief/<имя>.md`),
  запуск `bash ~/MapWork/agents/run_agent.sh <имя> high`; сверить `sandbox_policy` = workspace-write. Проверка Mac —
  раз в 20 мин (CronCreate). 4 macOS-падения Core (именованный семафор) — норма.
- **ПК, Sol:** `codex exec -m gpt-6.1-sol -c model_reasoning_effort=high -c 'default_permissions=":workspace"'
  -s workspace-write -C C:\Projects\Map-wt-<имя> -o <scratch>\last.txt - < prompt.md` + сверка `sandbox_policy`.
  Codex Astra для исследований — `-c 'default_permissions=":read-only"' -s read-only -c tools.web_search=true`.
- Агенты Claude (Agent tool) — каждый в своей `C:\Projects\Map-wt-<имя>` от актуальной ветки; брифы с абсолютными путями,
  требованием пиксельного равенства, тестов по фильтрам и отчёта `<ИМЯ>.md`. Сводить патчем
  (`git diff --cached --binary <base> -- . ':!<ОТЧЁТ>.md'` → `git apply --3way`), сразу коммитить, копию удалять.

## 5. Подводные камни (не повторять)
- Хук-автоформатер переформатирует .cs вне `C:\Projects\Map` и `C:\Projects\Map-wt-*` → копии только там.
- Агенты дважды писали в `C:\Projects\Map` по относительному пути (откатывали) — проверять `git status` main.
- `Blast Radius` держит `rm -rf`/`rm -f` в Bash → на ПК `Remove-Item` через PowerShell; на Mac — спросить владельца.
- Удалять в `.git` на USER автоклассификатор не даёт (3 файла Astra, 170 КБ) — владелец: «пусть пока будут».
- `MAP_MEASURE_GPU_FLUSH=1` занижает FPS (синхронное ожидание GPU каждый кадр) — для продуктовых чисел без него.
- Тестовые проекты не в `Map.slnx`; `dotnet build-server shutdown` перед тяжёлыми прогонами; Presentation.Tests ~5 мин,
  Rendering ~16–18 мин.
- Пакет `claude/fps-pack-2` и ветка Astra содержат **разные хеши одних изменений** (первые 7) — сводить rebase `--onto`.

## 6. Слова владельца 14.11 (действуют)
«самая большая карта и максимум объектов»; «механизм, чтобы сам улучшал FPS везде, на любой функции… ядро из того, что
есть»; «реализуй, особенно 5 гигов… точечная чистка ненужных ассетов»; «БД вся на тебе»; «неважные задачи — Сол 6.1 high»;
«Астра подкрепляет живые проверки скриншотами разных моментов, ты их отсматриваешь и находишь проблемы»; «коммить везде,
чтобы не копилось»; «постоянно оповещай о результатах Астры по FPS»; «живые прогоны на ПК пока не делай» (на ту сессию —
спросить снова); открытие карты — «быстро, но с цветной заглушкой».

## 7. Закрытие сессии
Остановить Astra (`taskkill /T` процесса `exec resume`), Sol/агентов, cron/monitor; забрать коммиты Astra bundle'ом;
удалить свои временные копии и папки агентов; журнал (запись сверху), STATE (≤ 60 строк), DEC при новых решениях,
промпт следующей сессии — по просьбе владельца; коммит документов + push.

## Итоговый отчёт
По-русски: что взято в main (коммиты), FPS «до → после» (USER, продуктовый режим), что у Astra/агентов, что не проверено;
в конце `## ИТОГИ` (Сделано / Что дальше / Реализация проекта) по `AGENTS.md`.
