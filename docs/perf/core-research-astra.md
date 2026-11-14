**Направление ядра верное: общий допуск к работе, памяти и качеству устранит конфликт независимых бюджетов. Но сам по себе планировщик не гарантирует 16,7 мс: каждая допускаемая операция должна иметь ограниченный размер и возможность продолжения.**

Прочитал `perf-core-design.md`; `perf-functions.md` отсутствует. Файлы не менял. Проверены приведённые исходники и документация; сборка, интеграция и производительность на HD 630 **не проверялись**. Большинство ссылок ведёт на изменяемые ветки — перед переносом кода нужно закрепить commit. Оценки трудоёмкости ниже относятся к прототипам, а не завершённой интеграции.

Ниже — **десять рекомендаций в порядке приоритета**, с конкретными источниками.

1. **Сделать работу прерываемой, а затем объединять её бюджеты.**

   В React, MIT, [`packages/scheduler/src/forks/Scheduler.js`](https://github.com/facebook/react/blob/main/packages/scheduler/src/forks/Scheduler.js) есть очереди по срокам, отмена и возврат продолжения задачи. Перенести **идею и небольшие алгоритмы** в `FrameBudget/WorkScheduler`: `RunSlice(deadline, cancellation) → continuation`. Не копировать поведение просроченных задач, позволяющее работать за пределами дедлайна.

   Для C# интеграции изучить Avalonia 11.3, MIT: [`src/Avalonia.Base/Rendering/Composition/Server/ServerCompositor.cs`](https://github.com/AvaloniaUI/Avalonia/blob/11.3.0/src/Avalonia.Base/Rendering/Composition/Server/ServerCompositor.cs). Здесь видны приём изменений UI, анимации и отрисовка: бюджет Map должен учитывать соседние стадии, а не только `SceneRenderer`.

   **Трудоёмкость:** средняя. **Риск:** неделимый вызов Skia, декодера или геометрического алгоритма всё равно превышает дедлайн.

2. **Связать приоритет задачи с резервированием памяти — как Chromium.**

   Chromium, BSD-3-Clause: [`cc/tiles/tile_manager.cc`](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/cc/tiles/tile_manager.cc) и [`tile_priority.h`](https://github.com/chromium/chromium/blob/main/cc/tiles/tile_priority.h). Здесь уже есть `NOW/SOON/EVENTUALLY`, мягкий/жёсткий предел памяти и вытеснение менее важных тайлов перед запуском более важных.

   Перенести **алгоритм**, связав `WorkScheduler` и `WorkingMemoryBudget`: задача запускается только после резервирования памяти результата и промежуточных буферов; отмена освобождает резерв. Видимые данные и покрывающий обзор получают преимущество перед префетчем.

   **Трудоёмкость:** средняя–высокая. **Риск:** взаимная блокировка зависимых задач и вытеснение ресурсов, которые ещё использует GPU. Нужны учёт владения и ограниченные очереди готовых результатов.

3. **Заменить синхронный GPU-замер асинхронным кольцом запросов.**

   ANGLE, BSD-3-Clause: [`src/libANGLE/renderer/d3d/d3d11/Query11.cpp`](https://github.com/google/angle/blob/main/src/libANGLE/renderer/d3d/d3d11/Query11.cpp). Реализованы D3D11 timestamp/disjoint и проверка готовности. Использовать **API и схему**, получая результат спустя несколько кадров без ожидания. [`EXT_disjoint_timer_query`](https://registry.khronos.org/OpenGL/extensions/EXT/EXT_disjoint_timer_query.txt) прямо предназначен для такого измерения.

   Для `QualityGovernor/PerformanceJournal`: сохранять идентификатор измеренного кадра, задержку результата и признак недостоверности. Запросы должны охватывать фактическую отправку команд Skia в том же контексте.

   PresentMon, MIT, [`PresentData/PresentMonTraceConsumer.cpp`](https://github.com/GameTechDev/PresentMon/blob/main/PresentData/PresentMonTraceConsumer.cpp) полезен как образец **корреляции отображённых кадров**, но его ETW-инфраструктура значительно тяжелее. [DXGI FrameStatistics](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgiswapchain-getframestatistics) не заменяет измерение отдельного GPU-прохода.

   **Трудоёмкость/риск:** высокие; доступность расширения и контекста в конкретной сборке Avalonia/ANGLE **не проверена**.

4. **Построить governor из независимых регуляторов качества с запасом и задержкой восстановления.**

   Unity C#: [`Modules/AdaptivePerformance/Indexer/AdaptivePerformanceIndexer.cs`](https://github.com/Unity-Technologies/UnityCsReference/blob/master/Modules/AdaptivePerformance/Indexer/AdaptivePerformanceIndexer.cs). Лицензия **Unity Reference Only**: использовать как ориентир, код в Map не переносить. Полезны разделение CPU/GPU-состояний и набор управляемых scalers.

   [Unreal Dynamic Resolution](https://dev.epicgames.com/documentation/en-us/unreal-engine/dynamic-resolution-in-unreal-engine) описывает историю измерений, GPU headroom и быстрое снижение при серии превышений. Брать **идеи из документации**; код Unreal регулируется EULA.

   Моя рекомендация для `QualityGovernor`: сначала EWMA плюс запас по разбросу, гистерезис, быстрое снижение и медленное повышение; PID пока не нужен. Отдельно регулировать подписи, детализацию и разрешение эффектов. Состояния: CPU, GPU, смешанное, неизвестное.

   **Трудоёмкость:** средняя. **Риск:** запаздывающая обратная связь и общий bandwidth CPU/GPU на встроенной графике.

5. **Объединить память приложения со скрытым бюджетом Skia.**

   SkiaSharp, MIT: [`binding/SkiaSharp/GRContext.cs`](https://github.com/mono/SkiaSharp/blob/main/binding/SkiaSharp/GRContext.cs) предоставляет `GetResourceCacheUsage`, `SetResourceCacheLimit`, очистку ресурсов. Skia, BSD: [`src/gpu/ganesh/GrResourceCache.cpp`](https://github.com/google/skia/blob/main/src/gpu/ganesh/GrResourceCache.cpp) различает освобождаемые, занятые и учитываемые бюджетом ресурсы.

   Использовать **готовые API**, дополнив `WorkingMemoryBudget` категориями managed/native/GPU, временными буферами, снимками Undo и незавершёнными загрузками. Не считать один ресурс повторно через несколько обёрток.

   **Трудоёмкость:** средняя. **Риск:** лимит кэша Skia не является жёстким пределом всей GPU-памяти; резкая очистка сама вызывает задержки.

6. **Ограничить отрисовку экранной сложностью через пирамиду и пакетирование.**

   SkiaSharp, MIT: [`binding/SkiaSharp/SKCanvas.cs`, `DrawAtlas`](https://github.com/mono/SkiaSharp/blob/main/binding/SkiaSharp/SKCanvas.cs) — готовый C# API для пакетов штампов. Сохранять порядок прозрачных объектов; один вызов не означает гарантированно один GPU draw.

   deck.gl, MIT: [`modules/geo-layers/src/tileset-2d/tileset-2d.ts`](https://github.com/visgl/deck.gl/blob/master/modules/geo-layers/src/tileset-2d/tileset-2d.ts) — **идея покрытия родительскими тайлами**, пока детали готовятся. Supercluster, ISC: [`index.js`](https://github.com/mapbox/supercluster/blob/main/index.js) — **идея многоуровневой агрегации**, применимая к обзору, но не замена точному редактированию.

   Это клиенты всех бюджетов и `Refinement`. Новые ассеты добавлять ограниченными страницами атласа; загрузки ограничивать байтами и временем, без перепаковки всего каталога.

   **Трудоёмкость/риск:** высокие; швы, прозрачность, переполнение атласов. Ускорение `DrawAtlas` на HD 630 не измерено.

7. **Распространить продолжения на подписи, геометрию и экспорт.**

   MapLibre GL JS, BSD-3-Clause: [`src/style/pauseable_placement.ts`](https://github.com/maplibre/maplibre-gl-js/blob/main/src/style/pauseable_placement.ts). Сохраняет текущий слой/тайл/часть, останавливает раскладку и продолжает следующим кадром; есть `forceFullPlacement`.

   Перенести **структуру состояния**, заменив локальные 2 мс общим `FrameBudget`. Подходит для подписей, берегов, гексов и построения превью.

   Экспорт должен требовать точное качество, но продолжать соблюдать `WorkScheduler/WorkingMemoryBudget`. Снятие ограничений качества не должно снимать ограничения ресурсов.

   **Трудоёмкость:** средняя по каждому алгоритму. **Риск:** внутренняя сортировка или обработка одного слишком большого блока остаётся неделимой.

8. **Снимок и Undo строить на разделяемых чанках; индексы обновлять дельтами.**

   .NET runtime, MIT: [`ImmutableList_1.Node.cs`](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Collections.Immutable/src/System/Collections/Immutable/ImmutableList_1.Node.cs) — дерево с разделением неизменяемых узлов; [`ImmutableArray_1.cs`](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Collections.Immutable/src/System/Collections/Immutable/ImmutableArray_1.cs) — массив, изменение которого требует копирования. Использовать **библиотеку и принцип разделения**, не один огромный массив на документ.

   RBush C#, MIT: [`RBush/RBush.cs`](https://github.com/viceroypenguin/RBush/blob/master/RBush/RBush.cs) поддерживает вставку, удаление, поиск. NTS, BSD-3-Clause: [`Index/Strtree/STRtree.cs`](https://github.com/NetTopologySuite/NetTopologySuite/blob/main/src/NetTopologySuite/Index/Strtree/STRtree.cs) запрещает добавление после построения.

   Предлагаю: фон готовит изменённые чанки и индекс; UI проверяет ревизию и публикует новый корень одной командой Undo. Это `WorkScheduler`, память и защита ввода.

   **Трудоёмкость/риск:** высокие; долгоживущие снимки удерживают память, изменяемый индекс нельзя одновременно читать без согласованной схемы.

9. **Разделить CI на детерминированные ограничения и аппаратные измерения.**

   BenchmarkDotNet, MIT: [`src/BenchmarkDotNet/Diagnosers/MemoryDiagnoser.cs`](https://github.com/dotnet/BenchmarkDotNet/blob/master/src/BenchmarkDotNet/Diagnosers/MemoryDiagnoser.cs) — использовать **библиотеку** для стоимости операций и аллокаций. React, MIT: [`SchedulerMock.js`](https://github.com/facebook/react/blob/main/packages/scheduler/src/forks/SchedulerMock.js) — перенести **идею управляемого времени**, остановок и воспроизводимого выполнения очереди.

   В обычном CI проверять посещения узлов/объектов, байты, размеры очередей, отмену и устаревшие результаты. Записывать жесты, seed, ревизии и порядок завершения работ. P99 проверять отдельно на целевом ноутбуке, включая холодный каталог и одновременный экспорт.

   **Трудоёмкость:** средняя. **Риск:** воспроизводимость решений планировщика не означает воспроизводимость драйверных задержек.

10. **Сделать журнал дешёвым и пригодным для автоматического прогрева.**

   .NET, MIT: [`System/Diagnostics/Tracing/EventPipeEventDispatcher.cs`](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Diagnostics/Tracing/EventPipeEventDispatcher.cs) показывает внутренний мост runtime-событий к слушателям. Использовать публичный [`EventListener`](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/eventsource-collect-and-view-traces), а не этот внутренний класс.

   HdrHistogram.NET, BSD-2-Clause либо CC0: [`HdrHistogram/LongHistogram.cs`](https://github.com/HdrHistogram/HdrHistogram.NET/blob/master/HdrHistogram/LongHistogram.cs) — готовая гистограмма для `PerformanceJournal`.

   Моя схема: предвыделенный буфер кадров, запись на диск в фоне, таблица стоимости по функции/размеру/качеству. Между запусками сохранять модель с ключом GPU, драйвера, версии приложения и формата ассетов; использовать как начальную оценку, затем уточнять измерениями.

   **Трудоёмкость:** небольшая–средняя. **Риск:** сам журнал создаёт аллокации; старая модель ошибочно разрешает дорогую работу.

В черновике необходимо исправить следующее:

- **Метрику:** P99 времени кадра ≤16,7 мс допускает 1% более медленных кадров. Это не «каждый худший кадр». `1% low` обычно выражается в FPS; нужно явно определить формулу, окно и измерение фактического показа.
- **Гарантию:** Windows, драйвер и GC не дают жёсткого real-time. Достижимая цель — автоматическое соблюдение бюджета собственной работы с измеренным P99 в заданных условиях.
- **GPU/GC утверждения:** синхронный `Submit` ждёт GPU; это не чистый GPU-таймер. `SustainedLowLatency` также не исключает блокирующие сборки при давлении памяти. [SkiaSharp](https://github.com/mono/SkiaSharp/blob/main/binding/SkiaSharp/GRContext.cs), [документация GC](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/latency).
- **Защиту ввода:** обнаружение операции >8 мс происходит уже после задержки. Нужны заранее ограниченные порции, включая применение результата, уведомления и обновление индексов.
- **Параллелизм:** `ядра−1` не резервирует ядро. Нужны адаптивное ограничение CPU-работ, отдельный контроль I/O и защита `Eventually` от вечного ожидания.
- **Сторож сложности:** считать также узлы индекса и кандидатов; миллион объектов может быть видим одновременно. Нужен отдельный предел работы на экран.
- **Полноту:** остановка камеры не должна запускать монолитный Exact-кадр. Уточнение должно сходиться постепенно. Холст 262 144² RGBA требует **256 GiB** без дополнительных буферов — тайлы обязательны на всём пути, включая экспорт.
- **Автоматичность новых функций:** прохождение через `SceneRenderer` и имя `*Cache` недостаточны. Нужны обязательные контракты порционной работы, владения памятью, отмены и публикации результата.

