# Поручение: ревью оптимизаций 2026-10-18 и проверка в настоящем окне (Claude Code)

Рабочая копия: `C:\Users\Leshe\.codex\worktrees\stage10f-fonts-bindings\Map`, ветка `codex/stage-18-editable-areas`
(на GitHub: `origin`). Папку `C:\Projects\Map` не трогать — это устаревший checkout.
Перед началом прочитать `AGENTS.md`, `CLAUDE.md`, `CODEX.md` и два верхних раздела `docs/STATE_HANDOFF.md`
(«2026-10-18 ночь» и «2026-10-18 вечер»). Владелец разрешил: commit, diff и push после каждого проверенного этапа.

## Что уже сделано (проверять, а не переделывать)

Коммиты `3c082ab..e2bc2a0` (от `f838624`): кэш готовых картинок слоёв при неподвижном виде, панораме и зуме
(`LayerImageCache`), пересчёт рельефа только вокруг правки (`TerrainPatchRegion`, `ComposeTerrainPatch`), лестница
детализации в превью сохранения (`ExportStampImageSource`), общие версии плиток и пространственный индекс для
записей слоя с тем же списком объектов (`RevisionsFor`, `ObjectSpatialIndex.For`), поиск готовых плиток ±12 уровней и
кэш плиток 256 МиБ, отложенные панели (250 мс), `Ui.SetClass` без лишних смен классов, `SelectedBlocksLight` через
индекс, `PrefixedRowList` как IList, ReadyToRun-публикация для ярлыка (`tools/ai/publish-local-r2r.ps1`).

## Инструменты сессии — использовать

- Скилы проекта `.claude/skills`: `map-local-feature` (порядок работы), `map-visual-compare` (сравнение холста).
- `/code-review` (или скил `code-review`) — ревью диффа `f838624..HEAD`.
- Плагин `dotnet-diag` (скил `analyzing-dotnet-performance`, `dotnet-trace-collect`) — установлен 2026-10-18.
- MCP `ETW` (`Microsoft.Windows.EventTracing.MCP`) — трассы `.etl`, сравнение «до/после»; если запись `.etl`
  требует прав администратора, попросить владельца выполнить команду (`! <команда>`), самому права не повышать.
- MCP `windows-mcp` — клики, ввод и снимки настоящего окна с учётом масштаба экрана Windows (экран сейчас 1536×864
  с масштабированием; самодельный `tools/ai/live/mapinput.ps1` там промахивается).
- RenderDoc 1.46 (`C:\Program Files\RenderDoc`): захват — см. `tools/Map.Performance/RenderDocCapture.cs`.
- Инструмент замеров: `native-paint-perf` с `MAP_NEW_ART_PERF=1`, `MAP_NEW_ART_OBJECTS=50000|100000`,
  `MAP_OWNER_ART_LIBRARY=<КОПИЯ %LOCALAPPDATA%\Map\Assets>`; собирать отдельно `dotnet build tools/Map.Performance -c Release`.

## Этап 1 — независимое ревью

Ревью `git diff f838624..HEAD` по коду `src/` и тестам: корректность (устаревшие картинки, неверные ключи кэшей,
швы при вклейке куска рельефа, потокобезопасность, утечки GPU-ресурсов, ConditionalWeakTable по спискам объектов,
отложенные панели — не теряется ли обновление), граничные случаи (смена масштаба экрана, потеря GPU-контекста,
экспорт, нехватка памяти). Каждую находку подтвердить тестом или воспроизведением; исправлять только
подтверждённое, небольшими правками, с тестом. Commit + diff + push.

## Этап 2 — проверка в настоящем окне

Через `windows-mcp`, **только в изолированном профиле** (переменные `MAP_UI_TEST_MODE=1`,
`MAP_UI_TEST_PROFILE_ROOT`, `MAP_UI_TEST_RECOVERY_ROOT`, `MAP_UI_TEST_SHORTCUT_SETTINGS_PATH`; в профиль положить копию
библиотеки и тестовую карту — её сохраняет замер в `test-map.mapproj`). Запускать ускоренную сборку
`C:\Users\Leshe\Apps\Map\Map.App.exe`. Если Map уже запущен владельцем — не запускать второй и спросить.
Пройти: открыть карту (50 000 гор), панорама, зум колесом, мазок суши, раскраска текстурой, ярус ±, ползунок
обрывов, маска, выбор горы и серия кликов, отмена/повтор, сохранение. Снимать только окно Map, не весь экран.
`MAP_FRAME_TRACE` + `tools/ai/live/analyze_frames.py` для кадров. Сравнить вид с `map-visual-compare`.
Закрыть тестовое окно. Записать находки; явные регрессии исправить (этап 1-style), commit + diff + push.

## Этап 3 — (если время) открытые пункты

Холодные первые действия сеанса (первый выбор горы ~270 мс, первые нажатия ~100 мс, первые клики 39 FPS);
отдельные клики размещения с кадром ~100 мс. Сначала измерить (`dotnet-diag`, ETW), потом править.

## Правила безопасности

- Никогда не трогать профиль владельца `%LOCALAPPDATA%\Map` и его карты; только копии.
- Не делать reset/restore/stash, не переписывать историю, ничего не удалять безвозвратно; перед правкой файлов вне
  репозитория — резервная копия в `%USERPROFILE%\Backups\Map\`.
- Скриптовые правки файлов — через запись во временный файл и замену (урок 2026-10-18: `open(p,'wb').write(expr)`
  с падающим `expr` обнулил `SceneRenderer.cs`). Сохранять CRLF/BOM файлов.
- Сообщения коммитов писать не через `Set-Content -Encoding utf8` (добавляет BOM в заголовок).
- Известные падения тестов (не от этой работы): Rendering — Stage14 ghost, lake outflow ×2,
  RenderImageCacheDiagnostics, water runs past, tributary (иногда); Presentation — Stage10C ×2, Stage10F shortcuts,
  Stage10E portal, EditorIntegrity; Stage18 memory-refusal падает только под полным прогоном.
- В конце, если менялся код: `powershell -NoProfile -ExecutionPolicy Bypass -File tools/ai/publish-local-r2r.ps1`
  (Map закрыт) — ярлык владельца получит исправления.

## Отчёт

По формату `AGENTS.md`: что изменено, что проверено (FACT / PARTIAL / NOT CHECKED), что не проверялось и почему.
Отчёт обязан заканчиваться разделом `## ИТОГИ` простым языком: «Сделано:», «Что дальше:», «Реализация проекта: N%»
(или «не пересчитывалась» с причиной).
