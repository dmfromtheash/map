# Поручение: оптимизация ядра Map, этап 2 (Fable 5.1)

Рабочая копия: `C:\Users\Leshe\.codex\worktrees\stage10f-fonts-bindings\Map`, ветка
`codex/stage-18-editable-areas`. Перед началом прочитать `AGENTS.md`, верхний раздел
`docs/STATE_HANDOFF.md` («2026-10-17 — оптимизация ядра») и этот файл. Commit после каждого
проверенного шага разрешён владельцем (2026-10-17: «делая коммиты сразу же»).

## Что уже сделано (этап 1, коммиты 277aa0f…)

- Живые мазки (суша, материал, маска, кисть) и размещение ассетов на карте «Долина Листвы»
  (4096×3072, 1 564 ассета): 60+ FPS, отпускание мазка 16–28 мс, одно размещение 17 мс.
- Рельеф после правки уточняется в фоне; `MapDocument.WithLayer` — быстрая ревизия;
  GC без блокирующих полных сборок; JIT без tier-0 для циклов; плашка FPS в правом нижнем
  углу холста (`MapCanvasControl.ShowFpsBadge`).
- Инструменты измерения: `native-paint-perf` (мазки/размещение), `native-stress-fill`
  (заполнение карты N ассетами и те же маршруты), `MAP_FRAME_TRACE=<файл>` (построчная трасса
  кадров и ввода настоящего окна), скрипты живого ввода и захвата окна —
  `docs/STATE_HANDOFF.md` описывает, как ими пользоваться.

## Что осталось (по убыванию пользы), цифры из `artifacts/paint-perf-2026-10-17/stress/run3.log`

1. **Панели после каждой правки при большом числе объектов.** На 50 000 ассетов одно
   размещение стоит 106–124 мс, из них `ui:side/object-list` 23–149 мс, остальные панели
   (asset-list, path/area/river/road, context-area) по 5–9 мс каждая; redo — 927 мс UI.
   Причина: `ObjectTreeRows.Build` → `AssetKindRows` и `StackOrder` (`SceneGeometry.InDrawOrder`)
   пересчитываются по всем объектам на каждую ревизию документа. Сделать индекс видов ассетов
   и порядок стека инкрементальными по `ObjectLayerChanges` (O(дельта) на правку) и не
   синхронизировать панели, которые не зависят от объектов. Цель: ≤ 25 мс на размещение при
   50 000 объектов (сейчас 20 мс при 1 564).
2. **Плотные тайлы объектов при смене масштаба.** Zoom-out на 50 000: кадры по 400+ мс,
   zoom-in на 20 000: кадры 20–48 мс; pan на 40 % после смены масштаба: первый кадр ~400 мс.
   Причина: тайл нового zoom-bucket строится синхронно в кадре (`DrawDetailedStampTiles`,
   `TryDrawDenseAtlas` на десятки тысяч штампов). Сделать как `TerrainRefinement`: строить тайлы
   в фоне (CPU-растр, неизменяемые входы), пока рисовать ближайший готовый bucket
   (`TryDrawKeptTiles`) или точки обзора; запросить кадр по готовности. Проверить сначала,
   что именно дорого: `run4.log` печатает `tilePrep/tileRaster/occluded` (запущен в конце
   сессии 1, дождаться/перезапустить).
3. **Первый «обзорный» кадр** при отдалении: 66–198 мс на 1 564 объектах — построение
   `ObjectOverviewPlan`/варианта и (вероятно) синхронное декодирование всех PNG ассетов ради
   среднего цвета точек (`StampOcclusion.Color` через `IStampImageSource.TryGetImage`).
   Проверить трассой, перенести в фон или считать цвет из уже декодированных картинок.
4. **Холодный старт в сессии**: первый мазок/первая активация ассета после запуска 130–280 мс
   (JIT и первое построение панелей). Варианты: прогрев в фоне после открытия карты.
5. **Память**: private 1,1–1,5 ГиБ при 20–50 тыс. объектов (GC-куча 185–315 МиБ, остальное —
   кэши GPU/Skia по бюджетам: тайлы 256 МиБ, атласы 192 МиБ, объекты 64 МиБ, рельеф 6+8 видов).
   Если владелец попросит меньше памяти — уменьшить бюджеты адаптивно к размеру карты.

## Как проверять

```powershell
# базовые маршруты (две прогонки в одном процессе; вторая = тёплая)
$env:MAP_REGIONAL_PROJECT = "<копия Долина Листвы.mapproj>"; $env:MAP_PAINT_PASSES = "2"
dotnet tools/Map.Performance/bin/Release/net10.0/win-x64/Map.Performance.dll --profile native-paint-perf --output artifacts/<папка>/run.json
# стресс: заполнение и маршруты на каждом уровне
$env:MAP_STRESS_LEVELS = "20000,50000"
dotnet tools/Map.Performance/bin/Release/net10.0/win-x64/Map.Performance.dll --profile native-stress-fill --output artifacts/<папка>/stress.json
```

Нужны `MAP_UI_TEST_RECOVERY_ROOT` и `MAP_UI_TEST_SHORTCUT_SETTINGS_PATH`; `RegionalAtlas` должен
лежать рядом с `Map.Performance.dll` (копия из `src/Map.App/bin/.../RegionalAtlas`). Сравнивать
медианы нескольких прогонов: ПК владельца нагружен. Живую проверку настоящего окна делать
через скрипты `mapinput.ps1`/`mapwin.ps1` (описаны в handoff): окно ставить поверх всех и
полностью видимым на время жеста, иначе Windows замедляет презентацию. Карта с 50 000 ассетов
сохранена: `artifacts/paint-perf-2026-10-17/stress/stress-fill-*/paint.mapproj`.

Известные падающие тесты, не связанные с этой работой (падают и на 277aa0f):
`RenderImageCacheDiagnostics.Where_the_cached_frame_differs` (диагностика с `Assert.Fail`),
`Stage14PlacementPreviewRenderingTests`, `Stage19IllustratedWaterTests` ×2,
`Stage19WaterJoinRenderingTests`, `Stage19EstuaryScenarioTests.A_tributary…`.

## Отчёт

Итоговый отчёт по формату `AGENTS.md`: что изменено, что проверено (FACT/PARTIAL/NOT CHECKED),
и обязательный раздел `## ИТОГИ` (Сделано / Что дальше / Реализация проекта: N%).
