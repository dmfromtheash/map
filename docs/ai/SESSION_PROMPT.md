# Промпт сессии: продолжение после перезагрузки ПК — апскейл на двух узлах, Astra на USER (60 FPS), залив сотен

Ты — Claude Code в Windows-проекте Map. Модель — Claude Opus 5.5 (`claude-opus-5-5`). Рабочая папка `C:\Projects\Map`,
ветка `codex/stage-18-editable-areas` (`main` — та же линия). Предыдущая сессия (13.11, 08:00–13:30) штатно остановила
всё перед перезагрузкой ПК владельцем. Снимок мог устареть: сначала проверь Git, процессы, диск H:, Mac и USER.

## Владелец — постоянные поручения (13.11)
- «Проверяй Mac, USER и ПК **каждые 5 минут**», «очень внимательно следи за видеокартой ПК» → `/loop`-подобная задача:
  `CronCreate` `*/5 * * * *` с запуском `bash tools/library-pipeline/monitor/check_mac.sh` (печатает Mac, подачу,
  ПК-апскейл, **ПК GPU: температура/нагрузка/мощность/сбросы nvlddmkm за 6 мин и за день**, наши процессы ПК, USER);
  плюс **дежурный агент-наблюдатель** (Agent, фон) с циклом `bash tools/library-pipeline/monitor/watch_all.sh 20`
  (раз в 5 мин, ANOMALY при проблеме; при перезапуске агента в его промпт — что «не тревога»). Проблема — сразу владельцу.
- «Используй максимально ПК/Mac/USER», «много агентов как вчера», агенты на Sol/Astra — с проверкой Claude.
- Ответы — по-русски, просто; таблицы допустимы, когда владелец просит «таблицей».

## Шаг 1 · после перезагрузки (по порядку)
1. Вход: `docs/STATE.md`, `powershell -NoProfile -ExecutionPolicy Bypass -File tools/ai/Get-MapContext.ps1 -AsJson`.
   Чужие незакоммиченные изменения — сообщить владельцу.
2. TDR: `Get-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers | select TdrDelay,TdrDdiDelay`.
   Владелец решал, ставить ли 20/20 (cmd от администратора, две строки `reg add … /v TdrDelay|TdrDdiDelay /t REG_DWORD
   /d 20 /f` ПО ОДНОЙ). Если не поставил — ПК-апскейл НЕ запускать, спросить («без него ПК сбрасывает GPU; Mac один
   досчитает за ~7–9 ч»).
3. **Mac** (`ssh user@192.168.31.25`, всё в `~/MapWork`, DEC-104): обработчик остановлен через STOP. Проверить:
   `source ~/MapWork/env.sh`; версии Pillow 12.2.0 / numpy 2.5.1 / scipy 1.18.0; md5 `~/MapWork/upscale/finish_asset.py` =
   `H:\MapLibrary\pipeline\finish_asset.py`; файла `~/MapWork/upscale/STOP` нет. Запуск:
   `cd ~/MapWork/upscale && nohup caffeinate -is python mac_worker.py 6 >> worker.out 2>&1 < /dev/null &`
4. **Подача** (ПК): `Start-Process cmd -ArgumentList '/c','H:\MapLibrary\pipeline\start_mac_feeder.cmd' -WindowStyle Hidden`;
   через 1–2 мин — строки `sent`/`ok` в `H:\MapLibrary\mac_feeder.log`. Подача пропускает пропавшие файлы, у ssh таймауты.
5. **ПК-апскейл** (только при TDR=20): проба `cd H:\MapLibrary\sol\sol1; $env:MAP_GPU_COOLDOWN='15'; py -3.13 -B
   run_upscale2.py C:\Users\Leshe\Pictures\refs H:\MapLibrary\lists\all.txt --workers 6 --batch 20 --limit 20` и счёт
   `Get-WinEvent … ProviderName='nvlddmkm'` с начала пробы. 0 сбросов → полный запуск `start_upscale.cmd` (скрыто; в нём
   `MAP_GPU_COOLDOWN=15`; ПК пропускает сделанное Mac). **Любой сброс nvlddmkm → немедленно остановить** (процессы
   python `run_upscale2` + пул + `realesrgan`, удалить `H:\MapLibrary\work\upscale2-*`) и сообщить владельцу.
   Брак colourD 14–17 на крошечных/тонких картинках — обычный; при сбросе GPU colourD 40–130.
6. Включить 5-минутную проверку (CronCreate) и дежурного наблюдателя (см. выше).
7. **Astra на USER** — см. ниже, перезапустить.

## Astra (GPT-6, high) на ноутбуке USER — 60 FPS на 1 000 000 разных объектов, без изменения вида
- USER: доступ `& C:\Users\Leshe\.map-remote\r.ps1 '<скрипт>'`; клон `C:\Projects\Map`, ветка **`astra/fps-1m`**: коммиты
  `7b1fccc` (живой проход 4 новых сотен), `1609f92` (кэш до 4096 записей + счётчики, профиль `native-million-assets`).
  **Незакоммиченный шаг 1** (её работа, НЕ откатывать): отдельная полка 128 МиБ для готовых mip в общем бюджете 512 МиБ,
  штамп берёт mip до декодирования исходника (`AssetImageCache`, `CatalogStampImageSource`, `SceneRenderer`, тесты);
  Rendering 468/468, StampImageLeaseTests 11/11, AssetDecodeBudgetTests 21/21. Остался **живой A/B** шага 1.
- Исходные числа (ЖИВОЙ, USER, HD 630): 100 разных ассетов — панорама **0,4 FPS**, кадр p95 3,6 с, ввод→кадр до 10,5 с;
  1 000 000 объектов — первый кадр **76 с**, 25 % не дорисовался за 180 с. 10 разных ассетов — 56–58 FPS.
  Её план — `D:\MapChecks\astra\REPORT.md` раздел «План до 60 FPS» (шаги 0–5); шаг 0 закрыт.
- Перезапуск: задание `tools/library-pipeline/agents/briefs/astra-fps.md` → дописать сверху «Продолжение после
  перезагрузки ПК 13.11: шаг 1 не закоммичен, сделай живой A/B, закоммить, дальше по плану» → скопировать в
  `D:\MapChecks\astra\ASTRA.md`; `run_astra.cmd` (там же, `-m gpt-6-astra -c model_reasoning_effort="high"
  -s danger-full-access` — разрешено владельцем 13.11 только для USER) → задача планировщика `MapAstra` (Interactive,
  user1) → `Start-ScheduledTask MapAstra`. Сверить в `D:\MapChecks\astra\codex.log` модель/sandbox.
- Владелец: «пусть показывает первые результаты сразу и отчитывается о каждом шаге» → фоновое слежение за числом
  коммитов `git log main..astra/fps-1m`; на каждый новый коммит — проверить diff/тесты/снимок окна
  (`D:\MapChecks\astra\shots\`) и показать владельцу таблицу «до → после». Все проверки — ЖИВЫЕ (окно, холст).
- **Сборка на USER**: после `Map.slnx` обязательно `dotnet build src\Map.App\Map.App.csproj -c Release --no-incremental`
  и проверка строки `!XamlLoader` в `Map.App.dll` (сборка решения на USER теряет XAML).
- Взять её работу в основной репозиторий — только после проверки Claude (diff, тесты, живые числа), не раньше.

## Библиотека и залив сотен
- План: `H:\MapLibrary\plan\sets.json` (9 094 набора после удаления копий; правки `plan\fixes\*.json`, `drop_sets`,
  `drop_files` — применяются `plan.py` ПОСЛЕ деления на каталоги). Код конвейера — `tools/library-pipeline/` (README).
- На T7 залиты: textures, battlemaps-classic №1–2, fantasy-battlemaps-1 №1–2, fantasy-battlemaps-2 №1–2.
- **Только на USER** (`D:\MapLibrary`, копия): ещё fantasy-battlemaps-3 №1–2, modern-interiors-1 №1–2 (сборщик с
  `MAP_DRIVE_LIBRARY_BUILD=D:\MapLibrary`; листы чистые; живой проход Astra ✅). На T7 их ещё залить сборщиком на ПК.
- Новые готовые сотни — доливать на USER (`live-check/user/stage_pour.py` + `send_pour.py`, затем сборщик на USER) и
  проходить вживую (`live_batch2.ps1`). Новые сотни на T7 — сборщиком на ПК с листом и живым проходом.

## Ждёт владельца
- **Мелкие превью ×4**: страница https://claude.ai/artifact/M9ebLozvky6DQHVixSuiGD — вариант А «сверка цвета»
  (20/20 по цвету, но дорисованные штрихи остаются), Б «×2 до сети» (10/20), или оставить в браке.
- TDR и ПК-апскейл (см. шаг 1). Syncthing — не ставить (совет: одностороннее только по «да»).
- Вкладка «Ассеты» (5 вариантов, отложено). Решения по split/seams2/search/qa — `docs/library/AGENTS_RESULTS.md`.

## Уборка (своё)
- USER: временные профили и `MapProbe` удалены 13.11 (21,6 МБ); профили новых прогонов Astra — удалять после них.
  Копию библиотеки `D:\MapLibrary` на USER удалить, когда Astra закончит (владелец разрешил «после проверок»).
- Mac: агентские папки `~/MapWork/agents/*` — результаты; удалять только по «да».

## Итоговый отчёт
По-русски: проценты апскейла (всего, ПК, Mac), состояние видеокарты ПК, шаги Astra «до → после», что не проверено;
в конце `## ИТОГИ` (Сделано / Что дальше / Реализация проекта) по `AGENTS.md`.
