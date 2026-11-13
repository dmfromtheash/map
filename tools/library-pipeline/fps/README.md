# FPS-проверки (сессия 13.11, DEC-106…110)

Скрипты, которыми Claude проверял ветку Astra `astra/fps-1m` на ПК и Mac. Рабочие файлы — в `%TEMP%\map-fps`.

| Файл | Что делает |
|---|---|
| `auto_check.ps1` | Фоновый цикл: каждые 90 с смотрит голову `astra/fps-1m` на USER; новый коммит → bundle → локальная ветка `review/astra-fps-1m` на ПК → клон `~/MapWork/map` на Mac → `mac_tests.sh` → итог в `%TEMP%\map-fps\auto_check.log` и письмом в `D:\MapChecks\astra\INBOX.md`. Запуск: `Start-Process powershell -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','<путь>\auto_check.ps1' -WindowStyle Hidden`. |
| `mac_tests.sh` | На Mac (`~/MapWork/tests/mac_tests.sh`): Core, Persistence, Rendering на osx-arm64 без `WorkingMemoryBudgetTests` (Windows-only). Известное платформенное падение — golden сетки `Stage10EGridRenderingTests`. Assets/Presentation на Mac частично Windows-зависимы (10 + 25 падений) — их гонять на ПК. |
| `pc_review_tests.ps1` | ПК: переключает рабочую папку на `review/astra-fps-1m`, Release `Map.slnx`, все 5 наборов тестов (вывод на английском), возвращает ветку `codex/stage-18-editable-areas`. ~25 мин. |
| `pc_live.ps1` | ПК: ждёт конца заливки (`H:\MapLibrary\pour.log` → `DONE`), собирает `Map.Performance` ветки ревью, прогоны `native-million-assets` (сотня и 100 000; холодный и тёплый — общий `MAP_PERF_DERIVED_ROOT`), окно только на неосновном экране (`MAP_UI_TEST_MODE=1`) + журнал положения окна раз в секунду (`window-rects.log`, строка `MAIN-DISPLAY` в журнале — нарушение), профили удаляет, ветку возвращает. |

Правила: живые окна — только на ноутбучном экране и только с журналом положения (владелец 2026-10-28); без `MAP_PERF_DERIVED_ROOT` замер идёт по старому пути без заготовок — такие числа к продукту не относятся (ошибка 13.11).
