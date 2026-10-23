# Промпт: хвосты Map после сессии 4 (для ChatGPT / Codex)

Напиши в новой сессии: «Выполняй `docs/ai/MAP_TAILS_CHATGPT_PROMPT.md`».

Ты — исполнитель **только хвостов** проекта Map (офлайн-редактор фэнтези-карт, C#/.NET 10, Avalonia, SkiaSharp).
Новых этапов, новых функций и **никакого визуала** не делаешь (решение владельца 2026-10-22: весь визуал —
только Claude). Вопросов владельцу не задаёшь, решаешь сам и пишешь ASSUMPTION. Всё — только по-русски.

## Где и как
- Рабочая копия: `C:\Users\Leshe\.codex\worktrees\stage10f-fonts-bindings\Map`, ветка `codex/stage-18-editable-areas`.
  Прочитай: `AGENTS.md`, `CODEX.md`, `docs/00_STAGE_PROTOCOL.md`, верх `docs/STATE_HANDOFF.md`,
  журнал сессии 4 в `docs/ROADMAP_2026-10_PROGRESS.md`.
- **Одна тяжёлая операция за раз** (16 ГБ ОЗУ). Сборки `-m:1 -nr:false -p:UseSharedCompilation=false`.
  `dotnet build Map.slnx` НЕ пересобирает тестовые проекты — собирай `tests/<проект>` явно.
  Presentation-тесты — с `-- xUnit.ParallelizeTestCollections=false`.
- Map владельца не трогать (`Get-Process Map.App`). Живые проверки — только по изменённому месту
  (`Map.LiveCheck --map …`), окно на втором дисплее DISPLAY1. **Обходы всего интерфейса (`--ui-tour`) не запускать.**
- Не делать лишних действий вне пунктов ниже (замечание владельца 2026-10-22).
- Убирать за собой: профили, временные bin, логи, папки выгрузок (`rm -rf` держит защита — удалять PowerShell
  `Remove-Item -Recurse -Force` по точному списку путей внутри `artifacts/` и временных папок).
- Кодировку/BOM/CRLF сохранять как в HEAD. Без reset/restore/stash.
- После каждого пункта: commit (conventional, тело по-русски) → `git show --stat` → push ветки → push в `main`
  (по одной команде) → CI зелёный.

## Хвосты — по порядку

1. **Тест «гекс тает из тумана» падает** — `Map.Rendering.Tests.HexWorkshopRenderingTests.A_revealed_hex_melts_out_of_the_fog`
   (временно в `tests/KnownFailures.txt`). Появился, когда соединились новые гекс-ассеты (коммиты `hex-art`) и новый
   туман «Облака» (`src/Map.Rendering/HexLayerRenderer.FogLooks.cs`, `DrawFog` в `HexLayerRenderer.Workshop.cs`:
   картинка тумана кэшируется на состояние поля, тающие гексы вырезаются и дорисовываются с долей прозрачности).
   Найти причину (отрисовка или устаревшее ожидание теста), починить **без изменения вида тумана**, убрать строку
   из `KnownFailures.txt`. Проверка: Rendering полностью, `native-hex-workshop`.
2. **Перенести R25 «Атлас v1» от Codex**: ветка `codex/roadmap-R25`, коммиты `494ea4d`, `d10b0b9`
   (папка `C:\Users\Leshe\.codex\worktrees\roadmap-r25\Map`, отчёт `artifacts/r25`). `git cherry-pick`; конфликты —
   в номерах схемы: v26 (R25) встаёт **выше** v25 («Живая река»): `MapProjectFormat`, `SchemaFor` (v26 → v25 → v24 …),
   проверки схемы, docs/05. Оформление навигации атласа НЕ трогать (визуал — Claude). Проверки: Release, Core,
   Persistence, Rendering, Presentation, `native-hex-workshop`, смоук атласа из R25. Записать в PROGRESS (R25 ✅ или
   PARTIAL), CHANGELOG, handoff.
3. **Нестабильные тесты под нагрузкой** — сделать детерминированными или честно объяснить:
   `PlacementWorkflowTests.Stage18_temporary_preview_memory_refusal_keeps_place_retry_available_without_redrawing`,
   `HexWorkshopRenderingTests.Trees_sway_and_only_trees`,
   `RoadCrossingRenderingTests.A_bridge_covers_the_water_a_ford_lightens_it_and_none_leaves_the_river`,
   смоук `native-stage19` пункт `far-zoom-keeps-river`, смоук `native-hex-workshop` пункт
   `a fill runs as a wave from the pressed hex`. Все проходят по отдельности, падают при параллельной нагрузке.
4. **Список объектов справа схлопывается до одной строки после перетаскивания** (жалоба владельца). На карте с 4
   украшениями не воспроизвёлся (`Map.LiveCheck --object-list-drag true`, коммит d115f83). Попробовать на карте с
   сотнями штампов в разделе «Ассеты» (группы строк, `RecyclingObjectTreeRow`, `_objectList` в `EditorView.cs`);
   если воспроизвёлся — починить; если нет — NOT REPRODUCED с условиями.
5. **Смоук `native-player-package` не перезаписывает свою папку**: повторный запуск падает «файл уже существует»
   (`HexObsidianExporter.Write`), пока не удалить `artifacts/<…>/native-player-package-workspace`. Смоук должен
   сам чистить или брать новую папку.

## НЕ делать (это задачи Claude, записаны для следующей сессии Claude)
- Почти невидимая чёрная обводка у дорог и рек (просьба владельца 2026-10-22).
- Глубокий разбор аналогов по стилям и «кнопкам преображения» R09 (макеты 23–25 владельцу не понравились).
- Гекс-ассеты (владелец позже даст свои), туман, украшения, любые цвета/кисти/оформление.
- R10 «Красота» (её делал отдельный агент Claude в `C:\Users\Leshe\.codex\worktrees\r10-beauty\Map`, ветка
  `claude/r10-beauty`) — не переносить, не трогать.
- Мигание при наведении — не трогать без слова владельца.

## Конец
Отчёт владельцу простыми словами: что починено (с числами проверок), что не воспроизвелось, что осталось.
