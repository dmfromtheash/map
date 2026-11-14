# Ослабленные тесты и гейты: 082e9de..ab848a7 (69 коммитов: 39 Phoenix/Claude, 30 Astra)

Аудит только чтением: `git diff`, `git log -p`, `git grep`. Ничего не собиралось и не запускалось.
Замечание: `ab848a7` (сейчас) НЕ предок `main` (`git merge-base --is-ancestor ab848a7 main` = нет). Слияние — `c568630`
в `codex/stage-18-editable-areas`.

## Общая картина (FACT)
- В tests/ + tools/Map.Performance: +13 135 / −48 строк. Почти всё — новые тесты. Удалено только 48 строк, и каждая
  из них разобрана ниже.
- `.github/workflows/*` и `tools/ci/*` в диапазоне не менялись: проекты, фильтры и повтор (1 повтор для 1–3 упавших) остались прежними.
- Нет удалённых тестовых методов. Переименован один (90096af). Ни один эталонный PNG не заменён (в диапазоне нет изменений в tests/fixtures).
- Новые `[Fact(Skip=…)]`: 6 штук, все в новых файлах, пропущены с первого коммита (fd26c3e).
- Новые «проверки, которые без переменной окружения проходят молча» (`if (env…) return;` вместо Skip): ~15 зондов и бенчмарков.

## Список пунктов

| # | Коммит | Файл:строка | Было → стало | Обоснование в коммите | Вердикт |
|---|---|---|---|---|---|
| 1 | fd26c3e (Claude) | tests/Map.Core.Tests/HotPathCostGuardTests.cs:40,43,46 | новые тесты сразу `[Fact(Skip="O(N): …")]`: LabelBounds, PlacementAreaRuleCheck, ReplaceObject preview | «documented O(N) paths», запускаются только при MAP_RENDER_GUARDS_AUDIT=1 | JUSTIFIED как честно записанный долг, но **покрытия нет**: на 200k+ объектах эти пути линейные, и CI это не ловит |
| 2 | fd26c3e | tests/Map.Rendering.Tests/RenderPathCostGuardTests.cs:41,44,47 | `[Fact(Skip=…)]`: дельта плотного обзора (кусок 8192 перестраивается целиком), превью реки и превью дороги (Resolve копирует все) | то же | JUSTIFIED-долг. Скрывает тормоза при правке реки/дороги на большой карте и при правке в плотном куске |
| 3 | 462ab10 (Claude) | tests/Map.Rendering.Tests/OverviewZoomOutSettleTests.cs:46 | перед проверкой «вид держит свои куски» вставлено 60 пустых кадров (`StaleFallbackFrames`) | в продукте заглушка уступает только после 60 кадров без использования; иначе ColdPanTileTests.Detail_churn терял seed страницы | JUSTIFIED (смена поведения продукта, а не подгонка). **SUSPICIOUS по последствию**: при быстром непрерывном отдалении (<1 с) недавние заглушки снова вытесняют куски текущего вида. Это и был исходный баг (56 из 256 кусков) |
| 4 | ab848a7 (Claude) | tests/Map.Rendering.Tests/CacheMemoryPressureTests.cs:30 | у тестов обрезки бюджет прохода 2 мс → 10 000 мс (`trimMilliseconds = 10_000`) | на CI холодный JIT съедал 2 мс на скан; продукт исправлен (src ObjectOverviewCache.cs:114: проход всегда снимает ≥1 плитку); новый тест с 0 мс | JUSTIFIED. Остаётся слепое пятно: не измерено, за сколько кадров кэш опускается до водяной отметки при 2 мс на медленном ПК (по 1 плитке за кадр) |
| 5 | ab848a7 | DenseDetailTilesTests.cs:14, OverviewZoomOutSettleTests.cs:18 | классы перенесены в непараллельную коллекцию `RealTimeSettleCollection` | фоновые воркеры BelowNormal голодали от параллельных циклов кадров на 4-ядерном раннере; бюджеты времени не менялись | JUSTIFIED для CI. **SUSPICIOUS для продукта**: воркеры детали BelowNormal, при любой CPU-нагрузке на ПК (сборка, браузер, второй процесс) детализация может не дойти — «мыло» держится |
| 6 | 90096af (Astra) | tests/Map.Presentation.Tests/StampDerivedAssetStoreTests.cs:690 | `Persistent_missing_source_stops_retries_until_watched_file_is_restored` → `…ignores_file_hints_until_the_slow_retry_confirms_restoration`: раньше восстановленный файл подхватывался наблюдателем сразу, теперь подсказка наблюдателя игнорируется, картинка возвращается только по медленному повтору через 60 с | тела у коммита нет; журнал: «тихо повторяет через 60 с». DEC нет | **SUSPICIOUS**: тест переписан под новое поведение. Скрывает регрессию «вернул файл или подключил диск, а гора появляется только через минуту» |
| 7 | f9ef272 (Claude, только тест) | tests/Map.Presentation.Tests/PortableDerivedAssetTests.cs:66 | `state=="current"` → `state is "current" or "source-time"`: файл-источник с новой датой и теми же байтами раньше считался устаревшим, теперь текущим | «portable matched by path, length and verified content hash»; в src (StampDerivedAssetStore.Portable.cs:47-63) есть `VerifyPortableSources` | JUSTIFIED (содержимое сверяется хешем). Пробел: нет случая «та же длина, другие байты» |
| 8 | c8765cf (Astra) | tests/Map.Presentation.Tests/Stage10LibraryStampTests.cs:484,503,585 | в три отрицательных теста (файл удалён, подменён, заблокирован) добавлен `PreparePlacementAssetsAsync(refresh: true)` перед кликом | размещение теперь берёт заранее подготовленные байты | JUSTIFIED-по-дизайну. Скрытое поведение: если файл удалить, подменить или заблокировать после выбора варианта, клик всё равно разместит ранее проверенные байты. Первый клик до конца подготовки получает отказ «Checking» |
| 9 | 503949a (Astra) | AssetKindRowsAndSurfaceLooksTests.cs:51; Stage10CObjectTreeRowTests.cs:192 | `MemberIds` у строки вида → `Assert.Null`, состав проверяется через `ObjectTreeRows.AssetMembers` (теперь с порядком, строже) | ленивые члены строки | JUSTIFIED, не ослабление |
| 10 | 4a98c15 | Stage14PlacementPreviewRenderingTests.cs:102 | фейк теперь считает и `TryGetImageForPixelSize` как запрос; `Assert.Equal(1, Requests)` остался | — | Стало СТРОЖЕ |
| 11 | dad44be | StampDerivedAssetStoreTests (Assert.Same(small, …) → NotSame + байт-в-байт + Same повтора); LocalDetailPreparationTests (`Assert.True(requested>0)` удалён) | идентичность заменена копией-заглушкой с теми же пикселями | заглушка с собственным временем жизни | JUSTIFIED. Удалённый `requested>0` — мелочь |
| 12 | 5f241dc (Astra) | tools/Map.Performance/NativeMillionAssetsSmoke.cs:230 | `fps-target`: было `SceneP95≤16.7 && FrameIntervalP95≤16.7 && PausesOver100==0 && Fps≥59` (pan/wheel) → стало `FrameWorkP99≤16.7, FrameWorkMax≤33, RefinementWorkMax≤33, LatencyMax≤33, FrameIntervalP99≤16.7 или null` (все маршруты) | — | В основном СТРОЖЕ (p99/max вместо p95, задержка ≤33 вместо ≤100). Пропало `Fps≥59`; при одном кадре интервал = null и проходит. Профиль сам новый в диапазоне |
| 13 | 45e54d8 (Claude) | tests/Map.Presentation.Tests/DerivedCacheJanitorTests.cs:9 | `GB = 1e9` → `GB = 1024³` (бюджет 5 «ГБ» = 5 GiB = 5,37 ГБ) | «как показывает Проводник», DEC-111 | JUSTIFIED (решение владельца), +7 % к лимиту диска |
| 14 | bf968a9 | OverviewPreparationGestureBenchmark.cs:38 | допуск на кадр жеста 2 Мпкс при бюджете продукта 1 Мпкс (ObjectOverviewCache.cs:140) | «1 Мпкс + одна картинка, переступившая бюджет» | JUSTIFIED; тест по env (MAP_GESTURE_BENCH) |
| 15 | 1609f92 / 5f241dc / f7d4881 | NativeZoomAssetsSmoke.cs:1090, :817 | таймаут кадра 30 → 180 с только для нового `native-million-assets`; settle 10 с → MAP_PERF_SETTLE_SECONDS (10…600, по умолчанию 10) | миллион объектов | JUSTIFIED (по умолчанию не изменилось). Попутно `completed` стал строже (ещё и DetailTilesPending==0) |
| 16 | ab70007 DEC-112 (владелец) | процесс, не код | живое сравнение CPU↔GPU: вместо ≤2/255 визуальная приёмка, допускается до 101/255 в отдельных пикселях | решение владельца | JUSTIFIED (DEC-112), но это ручной гейт: «мыло» ловит только глаз Claude |
| 17 | 7d2d754 + журнал | PixelBisectCatalogRepro.cs:33, PixelBisectRepro.cs | регрессия 32768/500к fit 35–92/255 исправлена. Сквозная проверка — только зонд с `if (Out is null) return;`, в CI он зелёный без запуска | есть модульный тест механизма `Exact_lease_on_a_worker_waits…` | PARTIAL-покрытие: **SUSPICIOUS**, возврат регрессии на масштабе CI не поймает |
| 18 | журнал docs/journal/2026-11.md (+462,474,508,526,623,642,651) | живые гейты | `60FPS FAIL`, `PIXELS FAIL` записаны честно, пакет всё равно влит в ветку; STATE на ab848a7: «в main не взят (стоп пикселей)», хотя потом исправлено в 7d2d754 | — | Не подгонка теста, а приёмка с открытым FAIL. Код гейтов не ослаблен |

Без претензий (только перенос на общий UI-поток или ожидание async, без потери проверок): cc5522e UiAsyncButtonTests и
LibraryTexturePickerTests; 9aad2fd DashboardAndWizardTests (стал `await RecoveryScan`, добавлен тест устаревшего скана);
PerformanceContract/Program (новые профили; mem-pressure, derived-build и stamp-cache-probe с `gatePassed = true`: это зонды без гейта).

## Поведение продукта без покрытия в CI
1. O(N) на 200k: границы подписей, правила зоны размещения при многих реках и дорогах, превью замены объекта,
   дельта плотного обзора, превью реки и дороги при правке (6 Skip).
2. Пиксели обзора на масштабе 32768/500к: только env-зонд PixelBisect*.
3. Мгновенное восстановление картинки после возврата файла: заменено задержкой до 60 с (90096af).
4. Быстрое отдаление: окно в 60 кадров, когда старые заглушки сильнее кусков вида (462ab10).
5. Детализация под CPU-нагрузкой: в тестах её убрали сериализацией (ab848a7).
6. Скорость сброса кэша под давлением памяти при реальных 2 мс.
7. Размещение после удаления, подмены или блокировки файла уже после выбора варианта; первый клик до готовности подготовки.
8. Все живые FPS-гейты (не в CI по замыслу): последние прогоны FAIL.
9. ~15 env-зондов (DocMemoryProbe, LoadMemoryProbe, SaveCostProbe, SaveWorkflowCostProbe, OverviewMemoryProbe,
   *Benchmark, PixelBisect*, StyleScaleBenchmark, LargeSlots) в CI «зелёные» без выполнения, потому что это `return`, а не Skip.

## Что проверить вживую (по убыванию)
1. Пиксели fit 32768/500к после 7d2d754: settled против эталона ≤2/255 (регрессия без CI-стража).
2. Быстрое отдаление колесом fit←1600 % за <1 с на 1 млн: пустые или размытые куски в первую секунду (462ab10).
3. Детализация до резкости при фоновой нагрузке CPU (сборка в фоне): держится ли «мыло» (ab848a7, воркеры BelowNormal).
4. Отсутствующая картинка → вернуть файл или подключить диск → время до появления (ожидается до 60 с, 90096af).
5. Правка реки или дороги и клик-размещение в зоне на карте 200k–1 млн: время кадра (Skip-пути из fd26c3e).
6. Давление памяти: за сколько кадров кэш обзора опускается до отметки (по одной плитке за кадр).
7. Выбор варианта → сразу клик: нет ли отказа «проверяется» на первом клике (c8765cf).
