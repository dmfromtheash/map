# library-pipeline — конвейер тестовой библиотеки референсов (DEC-103/104)

Код, которым референсы владельца (`C:\Users\Leshe\Pictures\refs`) превращаются в библиотеку на внешнем диске
(`H:\MapLibrary`, Samsung T7): апскейл на двух узлах (ПК и Mac), план наборов и правки агентов, живой проход
каждой сотни в Map, наблюдение за узлами, запуск агентов Sol/Astra. Сборщик карточек — в самом проекте:
`tools/Map.Performance --profile drive-library` (`DriveLibraryBatch.cs`).

**Источник истины — этот каталог.** На H:, Mac и USER лежат развёрнутые копии; их запускают оттуда (пути по умолчанию
указывают на `H:\MapLibrary`). Данные (картинки, журналы, отчёты, правки JSON) в репозиторий не входят — см. «Где данные».
Выводы агентов — `docs/library/AGENTS_RESULTS.md`.

## Что где

| Папка | Файлы | Узел | Развёрнутая копия |
|---|---|---|---|
| `upscale/` | `finish_asset.py` (рецепт: две модели 60/40, контур из оригинала, зерно, `verify`) | ПК, Mac | `H:\MapLibrary\pipeline\`, `~/MapWork/upscale/` |
| | `run_upscale2.py`, `progress.py`, `rerun_outliers.py`, `test_pipeline.py` (Sol-1) | ПК | `H:\MapLibrary\sol\sol1\` |
| | `start_upscale.cmd`, `audit_blocks.py` (блоки 8×8), `sheet.py` (лист «исходник / результат») | ПК | `H:\MapLibrary\pipeline\` |
| `mac/` | `mac_feeder.py`, `start_mac_feeder.cmd` (подача заданий по ssh, сверка SHA и размера; `lists/mac_priority.txt` — первыми); `small_rerun.py` (Mac, рецепт «сверка цвета» `cycle_rgb`, DEC-108) + `small_runner.py` (ПК, задания мелких превью, обработчик Mac должен быть остановлен) | ПК, Mac | `H:\MapLibrary\pipeline\`, `~/MapWork/upscale/` |
| | `mac_worker.py`, `jobs.sh` (обработчик заданий) | Mac | `~/MapWork/upscale/` (+ копия `mac_worker.py` в `pipeline\`) |
| | `env.sh`, `setup.sh` (пространство `~/MapWork`) | Mac | `~/MapWork/` |
| | `bench.py` (замер GPU-вариантов Real-ESRGAN, запускает Claude) | Mac | `H:\MapLibrary\pipeline\mac\` |
| `plan/` | `plan.py` → `plan/sets.json`; `apply_fixes.py` (правки `plan/fixes/*.json`); `make_lists.py` (`lists/all.txt`); `quality.py` (отчёт качества); `look_sheets.py` (листы для просмотра групп); `pour_all.ps1` (заливка всех готовых сотен сборщиком без окна, журнал `H:\MapLibrary\pour.log`); `refs2_groups.py` (вход разметки refs2) | ПК | `H:\MapLibrary\pipeline\` |
| `fps/` | проверки ветки Astra: `auto_check.ps1`, `mac_tests.sh`, `pc_review_tests.ps1`, `pc_live.ps1` (README там) | ПК, Mac | из репозитория |
| `live-check/` | `live_batch2.ps1` + `LiveBatchNative.cs` + `shot_target.ps1` + `uia.ps1` + `click.ps1`, `README.md` (Sol-7) | ПК | `H:\MapLibrary\sol\sol7\` |
| | `start_test_map.ps1` (тестовая Map в изолированном профиле, печатает PID) | ПК | `H:\MapLibrary\pipeline\` |
| `live-check/user/` | `run-profile.ps1`, `make-contact-sheets.ps1` (прогоны `Map.Performance` и листы снимков) | USER | `D:\MapChecks\` |
| | `stage_pour.py`, `send_pour.py` (выбор следующих сотен и заливка копии библиотеки на USER `D:\MapLibrary`) | ПК | запускать из репозитория |
| `monitor/` | `watch_mac.sh` (раз в минуту), `alarm_mac.sh`, `check_mac.sh` (разовая сводка), `watch_all.sh` (Mac + ПК + USER каждые 5 мин) | ПК (Git Bash) | `watch_mac.sh` — `H:\MapLibrary\pipeline\mac\`; остальные — из репозитория |
| | `user_status.ps1` (ход Sol на USER, `sandbox_policy`) | USER | `C:\Users\user1\map_status.ps1` |
| `agents/` | `run_agent.sh` (единственный способ запустить Sol на Mac, DEC-104), `COMMON.md` (общие правила агентам), `stage.py` (раскладка входов первого раунда) | Mac / ПК | `~/MapWork/agents/`, `H:\MapLibrary\pipeline\mac\` |
| | `briefs/*.md` — задания агентам на Mac (последние версии; `look.md` — общий для look-1…4) | — | `~/MapWork/agents/<имя>/brief/` |
| `agents/user/` | `run_sol.cmd`, `run_astra.cmd` (Codex на USER через разовую задачу планировщика) | USER | `D:\MapChecks\` |
| `Compare-Deployed.ps1` | сверка md5 репозитория с H: и Mac, только чтение | ПК | — |

Отличия от развёрнутых копий (13.11, после переноса): `run_upscale2.py` берёт `finish_asset.py` рядом с собой, если он
есть (в репозитории), иначе `H:\MapLibrary\pipeline\finish_asset.py` как раньше; `test_pipeline.py` берёт горы владельца
из `samples\` рядом, иначе из `tests/fixtures/assets` (md5 совпадают); `plan.py`, `quality.py`, `look_sheets.py`,
`make_lists.py`, `sheet.py` при запуске не из `H:\MapLibrary\pipeline` пишут в `MAP_LIBRARY_ROOT` (по умолчанию
`H:\MapLibrary`), а не в папку репозитория. На H: поведение прежнее (рядом лежит `map-library.json`).

## Как запускать

Требования ПК: `py -3.13` с numpy 2.5.1, scipy 1.18.0, Pillow 12.2.0; Real-ESRGAN ncnn в `H:\MapLibrary\tools\realesrgan\`.
На ПК не запускать второй Real-ESRGAN параллельно с апскейлом (Radeon 680M портит PNG).

**ПК-апскейл** — `start-process cmd /c H:\MapLibrary\pipeline\start_upscale.cmd` (скрыто). Идёт с начала
`lists\all.txt`, лог `H:\MapLibrary\upscale.log`, журнал `upscaled\manifest.jsonl`; `MAP_GPU_COOLDOWN=15` — пауза GPU
после каждого прогона (владелец 13.11). Ход: `py -3.13 H:\MapLibrary\sol\sol1\progress.py`. Остановка — процессы
`run_upscale2`, `realesrgan-ncnn-vulkan` и пул с родителем `run_upscale2`, затем удалить `work\upscale2-*`.

**Mac** (`ssh user@192.168.31.25`, всё внутри `~/MapWork`): `source ~/MapWork/env.sh`; сверить версии библиотек и md5
`finish_asset.py` (`Compare-Deployed.ps1`); запуск обработчика
`cd ~/MapWork/upscale && nohup caffeinate -is python mac_worker.py 6 >> worker.out 2>&1 < /dev/null &`; остановка —
`touch ~/MapWork/upscale/STOP`. Затем на ПК подача `start-process cmd /c H:\MapLibrary\pipeline\start_mac_feeder.cmd`
(лог `H:\MapLibrary\mac_feeder.log`, журнал `upscaled\manifest-mac.jsonl`, брак `failed-mac.jsonl`). Mac берёт файлы
с конца списка, подача останавливается сама, встретив файлы ПК.

**План и правки** — `py -3.13 H:\MapLibrary\pipeline\plan.py H:\MapLibrary\plan\cls H:\MapLibrary\plan\cls\inventory.json`
(читает оба журнала апскейла, применяет `plan\fixes\*.json` через `apply_fixes.py` только при совпадении старого
значения, отчёт `plan\fixes\applied.json`). Затем сборщик:
`dotnet run --project tools\Map.Performance -c Release --no-build -- --profile drive-library --catalogs <к> --object-count 100`.

**Живой проход сотни** — `powershell -NoProfile -ExecutionPolicy Bypass -File H:\MapLibrary\pipeline\start_test_map.ps1`
(печатает PID; профиль `%TEMP%\map-live-profile` удалить после проверки), затем
`powershell -NoProfile -ExecutionPolicy Bypass -File H:\MapLibrary\sol\sol7\live_batch2.ps1 -ProcessId <pid> -Batch <n> -Catalog <к>`.
Окно Map на экран владельца — только с его «да». Подробно — `live-check/README.md`.

**Наблюдение** — из Git Bash: `bash tools/library-pipeline/monitor/check_mac.sh` (разово),
`timeout 290 bash tools/library-pipeline/monitor/watch_all.sh 4` (монитор Claude живёт 5 мин). Всё только читает.

**Агенты Sol на Mac** — папка `~/MapWork/agents/<имя>/` с `brief/COMMON.md` и `brief/<имя>.md`, запуск
`~/MapWork/agents/run_agent.sh <имя> <low|medium|high|xhigh>` (`--ignore-user-config`, `-s workspace-write`); после
запуска сверить `sandbox_policy` в `~/.codex/sessions`. Нужен ли агент — каждый раз спросить владельца (DEC-089).

**USER** (`& C:\Users\Leshe\.map-remote\r.ps1 '<скрипт>'`): Sol — `D:\MapChecks\run_sol.cmd`, Astra —
`agents/user/run_astra.cmd` (пишет в `D:\MapChecks\astra`; на 13.11 папки ещё нет, код Astra — в её ветке
`astra/fps-1m` клона на USER), запускаются разовой задачей планировщика в
интерактивном сеансе. Оба запускают Codex с `-s danger-full-access` — разрешено владельцем 13.11 только для USER
(песочница Windows мешала живым проверкам; «Астра пусть может там выполнять код, но ты перепроверишь»). Каждый diff и
прогон проверяет Claude. На Mac агенты — по-прежнему только `workspace-write` (DEC-104).

## Как разворачивать обновлённую копию

1. Правка — в репозитории; тесты `cd tools\library-pipeline\upscale; py -3.13 -B -m unittest test_pipeline` (22 теста,
   без pytest).
2. Остановить процесс, который использует файл (см. выше), — только если он запущен и только штатно.
3. Скопировать файл в путь из таблицы: на H: — `Copy-Item`; на Mac — `scp <файл> user@192.168.31.25:MapWork/upscale/`
   (только в `~/MapWork`).
4. `powershell -NoProfile -ExecutionPolicy Bypass -File tools\library-pipeline\Compare-Deployed.ps1` — нужная строка
   должна стать `same`. `finish_asset.py` на ПК и Mac обязан совпадать (DEC-104 п. 2).

## Где данные (не в репозитории)

- `H:\MapLibrary`: `upscaled\` (WebP + `manifest*.jsonl`, `failed*.jsonl`), `lists\`, `plan\` (`sets.json`,
  `inventory.json`, `quality.*`, `fixes\*.json` — правки агентов), `checks\` (листы и отчёты сотен, `state-<к>.json`),
  `look\` (`LOOK_SPEC.md`, листы), каталоги библиотеки, `map-library.json`, логи `upscale.log`, `mac_feeder.log`,
  отчёты Sol-1/2/5/7 и отзывы `sol\REVIEW_sol*.md`, разовые скрипты `sol\sol2`, `sol\sol5`.
- Mac `~/MapWork/agents/<имя>/` — входы, отчёты `SOL_REPORT.md`, результаты агентов; `~/MapWork/upscale/worker.log`.
- USER `D:\MapChecks` — отчёты Sol (`SOL_REPORT_final.md`), прогоны `runs\`, профили, `r4-*.ps1`; `D:\MapLibrary` —
  копия части библиотеки для проверок; `D:\MapChecks\astra` — Astra.
