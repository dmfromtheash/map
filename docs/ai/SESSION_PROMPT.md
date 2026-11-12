# Промпт сессии: библиотека референсов на T7 — два узла апскейла (ПК + Mac), залив сотнями (DEC-103/104)

Ты — Claude Code в Windows-проекте Map. Выбранная модель: Claude Opus 5.5 (`claude-opus-5-5`); имя в промпте модель не
переключает. Рабочая папка `C:\Projects\Map`, ветка `codex/stage-18-editable-areas` (HEAD при подготовке — коммит
документов «DEC-104»). Снимок мог устареть: перед правками проверь Git, процессы, диск H: и Mac.

## Цель (слова владельца)
Все его референсы из `C:\Users\Leshe\Pictures\refs` (39 081 картинка + 537 текстур) — в Map в высоком качестве вдали и
вблизи, с тегом «Тестовое», грамотными названиями («общее название + подробность про вид»), разделами, местами и
сеттингами; апскейлы на диске T7, «подключил — Map сразу видит». «Каждые 100 ассетов проверяй в программе вживую… Не
верь только тестам». «Без изменений в картинках — только разрешение». Mac: «нагружай железо Мака… выжимай максимум»,
«всё внутри пространства, не выходило за пределы», «ничего случайно не закрой и не удали вне рабочего процесса»,
«постоянно следи за маком, он часто виснет; если есть проблемы — пиши мне». Отчёт о процентах — по просьбе.

## Что работает в фоне (FACT на 22:11 12.11; могло завершиться или упасть — проверь)
- **ПК-апскейл:** `H:\MapLibrary\pipeline\start_upscale.cmd` → `sol\sol1\run_upscale2.py` (процессы `py`/`python`
  `run_upscale2` + `realesrgan-ncnn-vulkan`), лог `H:\MapLibrary\upscale.log`, журнал `upscaled\manifest.jsonl`.
  Идёт с НАЧАЛА `lists\all.txt`, ≈1 800 файлов/ч. Упал — перезапусти тем же `.cmd` (готовое не переделывается).
- **Подача на Mac (ПК):** `pipeline\start_mac_feeder.cmd` → `mac_feeder.py` (один процесс, замок `work\mac_feeder.lock`),
  лог `H:\MapLibrary\mac_feeder.log`. Берёт файлы с КОНЦА списка, шлёт задания по 60, забирает WebP, сверяет SHA и
  размер, пишет `upscaled\manifest-mac.jsonl` / `failed-mac.jsonl`. Брак переотправляется только при перезапуске.
  Остановится сама, когда встретит файлы, уже сделанные ПК («reached files the PC already did»).
- **Mac-обработчик:** `ssh user@192.168.31.25` (ключ этого ПК), `source ~/MapWork/env.sh`, процесс
  `python mac_worker.py 6` под `caffeinate` в `~/MapWork/upscale`, лог `~/MapWork/upscale/worker.log`, задания
  `~/MapWork/upscale/jobs/<id>` (READY → TAKEN → DONE → FETCHED, затем обработчик удаляет папку).
  Остановить: `touch ~/MapWork/upscale/STOP` (обработчик доделает текущее и сам удалит STOP); ждать по PID
  (`pgrep -f "^python mac_worker.py"`), НЕ по `pgrep -f mac_worker.py` (найдёт саму команду). Запуск:
  `cd ~/MapWork/upscale && nohup caffeinate -is python mac_worker.py 6 >> worker.out 2>&1 < /dev/null &`.
- Ход: `py -3.13 H:\MapLibrary\sol\sol1\progress.py` (только ПК) + число `ok` в `manifest-mac.jsonl` (уникальные rel).
  На 22:11: ПК 7 598, Mac 2 312, вместе 9 910/39 081 (25 %). Mac: 3 000–4 000/ч на мелких превью (их 8 825 в конце
  списка), ≈540/ч на обычных. Общий конец ≈ через 10 ч от 22:00 12.11.

## Mac — пространство `~/MapWork` (DEC-104)
- Всё внутри: `env.sh` (PATH, uv, кэши, TMPDIR), `.venv` (Python 3.13.7, numpy 2.5.1, scipy 1.18.0, Pillow 12.2.0 —
  ровно как на ПК; Pillow 13 давал RGB-WebP), `esr/` (Real-ESRGAN macOS + модели), `upscale/` (`mac_worker.py`,
  `finish_asset.py` — копии из `H:\MapLibrary\pipeline`, md5 сверять после правок), `agents/<имя>/`, `bin/codex`
  (обёртка над `/Applications/ChatGPT.app/.../codex-cli/bin/codex`: у скачанного CLI нет `codex-code-mode-host`).
- Ничего вне `~/MapWork` не ставить, не закрывать, не удалять. Процессы владельца (ChatGPT.app, его Codex) не трогать.
- Агенты Sol: только `~/MapWork/agents/run_agent.sh <имя> <low|medium|high|xhigh>` (папка с `brief/COMMON.md` и
  `brief/<имя>.md`; `--ignore-user-config`, `-s workspace-write`). После запуска сверить в `~/.codex/sessions`
  `"sandbox_policy":{"type":"workspace-write","network_access":false` и `cwd`. В песочнице НЕТ видеокарты (Metal) —
  GPU-замеры и подбор режимов Real-ESRGAN делает Claude сам через ssh. Codex `exec` по ssh — с `< /dev/null`.
- Помощники на диске: `H:\MapLibrary\pipeline\mac\` — `watch_mac.sh` (раз в минуту: обработчик, простой >5 мин,
  молчащие агенты, память Mac, возраст `mac_feeder.log`), `run_agent.sh` (копия того, что на Mac), `stage.py`
  (раскладка входов агентам), `COMMON.md` (общие правила агентам), `bench.py` (замер GPU-вариантов);
  `pipeline\start_test_map.ps1` (тестовая Map + новая карта + «Объекты», печатает PID; профиль `%TEMP%\map-live-profile`
  удалить после проверки); `pipeline\audit_blocks.py` (проверка готовых WebP по блокам 8×8).
  Monitor истекает через 5 мин и оставляет `tail`/`bash` — запускать `timeout 290 bash …watch_mac.sh & timeout 290 tail …; wait`.
- Хук Blast Radius держит `rm -rf`/`rm -f` в ssh-командах — не обходить; удаление на Mac — спросить владельца.
- Остаток прошлой сессии: `~/MapWork/agents/esr-tune/claude_bench` (867 МБ, замер) — спросить владельца, удалить.

## Состояние библиотеки
- Залито: `textures` 430; `battlemaps-classic`, `fantasy-battlemaps-1`, `fantasy-battlemaps-2` — по 2 сотни
  (`checks\<каталог>\batch-000N.*`, состояние `checks\state-<каталог>.json`). Листы всех — просмотрены, чисто.
  Живьём ✅ №1 трёх каталогов; **№2 трёх каталогов вживую НЕ проверены** — начать с них.
- Исправления агентов (12.11): `plan\fixes\{name_fixes,tag_fixes,field_fixes,vocab_merge}.json`, применяет
  `pipeline\apply_fixes.py` из `plan.py` (только при совпадении старого значения; отчёт `plan\fixes\applied.json`).
  Новые правки к уже залитым наборам — убрать ключи из `Renamed` в `state-<к>.json`, сборщик переименует.
- Не применено (решения): textures — 6 прозрачных «Штриховка/Линии/Тени» ОСТАВЛЕНЫ краской для кисти (Claude, по
  «подумай, как лучше»); previews — «повтор пикселей» ОТВЕРГНУТ (лесенка вблизи).

## Порядок работы
1. Вход: `docs/STATE.md`, `tools/ai/Get-MapContext.ps1 -AsJson`, `docs/00_STAGE_PROTOCOL.md`. Сообщи о чужих
   незакоммиченных изменениях. Проверь: ПК-апскейл, подача, Mac-обработчик (ssh), диск H:, свободная память ПК/Mac.
   Есть ли второй дисплей (`[System.Windows.Forms.Screen]::AllScreens`); если нет — окно Map только по «да» владельца.
2. Sol/Astra — спросить владельца заново (DEC-089); 12.11 было «солы как были», запуск — на Mac по правилам выше.
3. Каждые ~30 мин: `pipeline\plan.py` (`py -3.13 H:\MapLibrary\pipeline\plan.py H:\MapLibrary\plan\cls H:\MapLibrary\plan\cls\inventory.json`),
   `--dry-run` сборщика по всем каталогам, залив готовых сотен по одному каталогу (ПК занят апскейлом).
4. Каждую сотню: лист глазами (25/100/400 %) + живой проход: изолированный профиль (4 `MAP_UI_TEST_*` +
   `MAP_DRIVE_LIBRARY_ROOTS=H:\MapLibrary`), `NewMapButton` → `ChooseCategoryButton` → `CreateMapButton` →
   `WorkspaceObjectsTab` (через `H:\MapLibrary\sol\sol7\uia.ps1 -Action invoke`), затем `live_batch2.ps1 -ProcessId
   <pid> -Batch <n> -Catalog <к> -Place 10` (путь отчёта `checks\<к>\` — уже поправлен), снимки посмотреть.
   Тестовую Map после прохода закрывать; следить за памятью (12.11 Windows отметил RADAR_PRE_LEAK_64) — разобрать.
5. Мелкие превью магазина (~30 % брака verify, тонкие линии): подобрать режим без изменения рисунка (вес моделей,
   зерно 0, ×2 вместо ×4 для самых мелких, порог по размеру) — замеры на Mac сам, листы «исходник ×4 | сейчас |
   предложение», показать владельцу ДО применения; пороги verify не снижать.
6. Скорость Mac: проверить два Real-ESRGAN одновременно на Mac (на ПК так НЕЛЬЗЯ — мусор), `-j`, размер задания;
   принимать только при совпадении видимых пикселей (p99.9 ≤ 2). Обработчик на время замера ставить на паузу.
7. Исходники refs удалять только после: файл ok в журнале, его сотня прошла лист и живой проход; перед первым
   удалением — объём и «необратимо», получить «да».
8. После шага: документы (журнал, STATE ≤60 строк, DEC при новом решении), коммит, push по `AGENTS.md`.

## Инварианты
- Картинки не менять (без склейки краёв, без смены моделей/рецепта ради скорости); две модели 60/40; на ПК один
  Real-ESRGAN за раз. Внешние наборы — только на диске, ни одного файла refs/upscaled в git.
- Экран владельца — только с его «да»; курсор не трогать (UIA и сообщения окну тестовой Map).
- Свои временные файлы, профили, замеры — убирать после проверки; копии/архивы — только спросив.

## Итоговый отчёт
По-русски, коротко: проценты апскейла (всего, ПК, Mac, скорость), сколько сотен залито/проверено вживую, проблемы,
что не проверено; в конце `## ИТОГИ` (Сделано / Что дальше / Реализация проекта) по `AGENTS.md`.
