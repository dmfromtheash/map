**Map имеет смысл добавить отдельный резервный блок. Наиболее полезная форма — управляемая CPU-помощь существующему рендеру, временное уменьшение разрешения сцены и отдельный механизм восстановления графического backend.** Новый универсальный движок для этого не нужен: существенная часть строительных блоков уже существует.

При этом есть принципиальная граница: **помогать работающему GPU на лету и перевести всё окно Avalonia на программный вывод после потери устройства — две разные задачи.** В Avalonia 12.1.1 первая реализуется на уровне Map, а для второй я не нашёл готового публичного переключателя, который можно просто вызвать во время работы.

Ниже — полный отчёт с архитектурным предложением. Все пороги и ожидаемые эффекты, не измеренные в Map, явно обозначены как предложения.

**1. Область проверки и обозначения**

Исследована указанная рабочая копия `C:\Projects\Map-wt-pack4`:

- ветка — `claude/fps-pack-4`;
- HEAD — `d81db90af72bc0dc846afe315e16cab3bf38ba13`;
- незакоммиченных изменений в начале и конце проверки нет;
- файлы не менялись; сборки, приложение, нагрузочные проверки и сторонний код не запускались.

Внешнее исследование выполнено через Exa по пяти направлениям: браузеры, графические библиотеки, Windows, игровые движки и картографические системы. Просмотрены 127 поисковых позиций; значимые выводы дополнительно сверены с первичными документами и исходниками. Для Avalonia проверен тег **12.1.1**, для Chromium и Firefox ниже приведены ссылки на зафиксированные ревизии.

Обозначения:

| Метка | Что означает |
|---|---|
| **FACT / КОД** | Поведение подтверждено прочитанным исходным кодом. Это не подтверждает работу на конкретном компьютере. |
| **АВТОРЫ** | Поведение описано разработчиками в документации, статье или обсуждении проекта. |
| **PROPOSED** | Предлагаемое решение для Map, требующее реализации и измерений. |
| **NOT CHECKED** | В этом исследовании не проверялось. |
| **ДАНО** | Сведения и измерения из вашего brief; заново не воспроизводились. |

Цифры PresentMon — CPU `< 2 мс`, GPU busy p50 `6,8 мс`, p95 `13 мс`, p99 `24 мс`, максимум `55 мс`, а также примерно `47 %` интервалов свыше `16,7 мс` при zoom — здесь имеют статус **ДАНО**.

---

**2. Что уже есть в Map и что действительно нужно добавлять**

**FACT / КОД.** В текущей копии уже присутствует значительная часть того, что обычно называют гибридным рендером.

| Механизм | Что подтверждено | Следствие для резервного блока |
|---|---|---|
| Единый вход в отрисовку | `MapCanvasControl` получает Skia lease, формирует `RenderOptions` и вызывает `SceneRenderer.Render`. | Можно менять способ подготовки и представления сцены, сохраняя один evaluator сцены. |
| CPU detail tiles | Плотные тайлы растеризуются фоновыми потоками; используются отдельные CPU `SKSurface`, закреплённые изображения и готовые результаты. | Второй самостоятельный пул растеризации будет дублированием. |
| Число CPU workers | `Math.Clamp(Environment.ProcessorCount - 2, 1, 8)`, пониженный приоритет, общая FIFO-очередь. | Нужны управление конкурентностью и приоритетами, особенно на четырёх физических ядрах с Hyper-Threading. |
| Ограничение принятия detail tiles | В соответствующем пути принимается не больше одного готового тайла за кадр. | Этот лимит нужно включить в общий бюджет загрузок, а не наращивать независимо. |
| Повторное использование кадра | Хранятся широкий и детальный виды; изображения рисуются с преобразованием камеры без чтения пикселей с GPU. | Это уже основа самого дешёвого режима помощи при pan/zoom. |
| Ограничение mip adoption | По умолчанию `8 MiB` на спокойный кадр и `2 MiB` при движении, плюс ограничение CPU-времени. | Нужна координация с остальными изображениями и поверхностями. |
| GPU timing | Асинхронные timer queries, кольцо из шести запросов, проверка готовности и disjoint. | Можно строить обратную связь без синхронного ожидания GPU. |
| CPU offscreen surfaces | `RenderImageCache.CreateSurface` умеет создавать raster surface при отсутствии `GRContext`. | Часть рендера уже пригодна для программного пути, но весь сценарий без GPU ещё нужно проверить. |

Точки подтверждения: [вход в кадр и lease](/C:/Projects/Map-wt-pack4/src/Map.App/Controls/MapCanvasControl.cs:818), [фоновые detail tiles и workers](/C:/Projects/Map-wt-pack4/src/Map.Rendering/SceneRenderer.DetailRaster.cs:58), [принятие готового тайла](/C:/Projects/Map-wt-pack4/src/Map.Rendering/SceneRenderer.DetailRaster.cs:211), [camera frame cache](/C:/Projects/Map-wt-pack4/src/Map.Rendering/CameraFrameCache.cs:29), [mip adoption](/C:/Projects/Map-wt-pack4/src/Map.Presentation/Services/CatalogStampImageSource.cs:612), [GPU timer](/C:/Projects/Map-wt-pack4/src/Map.App/Controls/GpuFrameTimer.cs:55), [CPU/GPU offscreen surface](/C:/Projects/Map-wt-pack4/src/Map.Rendering/RenderImageCache.cs:381).

Два уточнения к исходному описанию:

1. **Ограничение увеличения сохранённого camera frame до `2×` в проверенных `CameraFrameCache`, `CameraFramePolicy` и месте вызова я не нашёл.** `TryDraw` проверяет покрытие видимой области сохранённым изображением, но явного предела увеличения в нём нет. Поэтому `≤ 2×` нельзя считать подтверждённым свойством этой ревизии. [Код проверки покрытия](/C:/Projects/Map-wt-pack4/src/Map.Rendering/CameraFrameCache.cs:71).
2. Лимит mip adoption — **оценка будущих загрузок**, а не жёсткий счётчик фактически переданных драйверу байтов. Проверка выполняется после принятия изображения; как прямо сказано в коде, одно изображение пропускается всегда. Это полезная защита от всплесков, но не строгий потолок всей GPU-передачи за кадр. [Реализация](/C:/Projects/Map-wt-pack4/src/Map.Presentation/Services/CatalogStampImageSource.cs:639).

**Вывод:** новый блок должен добавлять **общую политику использования существующих ресурсов, пониженное разрешение сцены и аварийный путь вывода**. Повторно строить overview, detail tiles или mip pipeline не требуется.

---

**3. Как это устроено в зрелых системах**

**3.1. Chromium: отдельные решения для возможностей, растеризации, композиции и сбоев**

**FACT / КОД.** Chromium применяет blocklist к отдельным графическим возможностям. Код вычисляет список запрещённых features по GPU/driver information и сохраняет применённые записи. Это более точная модель, чем один флаг «плохая видеокарта». [Chromium `gpu_util.cc`, строки 647–666](https://github.com/chromium/chromium/blob/b124f80df73fe467ac48846536e2542acfc3be20/gpu/config/gpu_util.cc#L647-L666).

Есть отдельная лестница режимов и переход на следующий режим при невозможности инициализации или достаточном числе сбоев GPU process. Счётчик недавних сбоев со временем уменьшается; единичные редкие аварии не обязаны навсегда отключать ускорение. [Выбор fallback modes](https://github.com/chromium/chromium/blob/b124f80df73fe467ac48846536e2542acfc3be20/content/browser/gpu/gpu_data_manager_impl_private.cc#L474-L530), [переход режима](https://github.com/chromium/chromium/blob/b124f80df73fe467ac48846536e2542acfc3be20/content/browser/gpu/gpu_data_manager_impl_private.cc#L1634-L1652), [счётчик сбоев](https://github.com/chromium/chromium/blob/b124f80df73fe467ac48846536e2542acfc3be20/content/browser/gpu/gpu_process_host.cc#L741-L762), [проверка crash limit](https://github.com/chromium/chromium/blob/b124f80df73fe467ac48846536e2542acfc3be20/content/browser/gpu/gpu_process_host.cc#L1513-L1522).

**АВТОРЫ.** Документ Chromium по композиции различает:

- software raster: CPU workers создают тайлы, GPU затем их композитит;
- GPU raster: Skia растеризует в GPU textures;
- software compositor: готовые изображения остаются в системной памяти и компонуются CPU.

Документ обновлён в **мае 2014 года**, поэтому он надёжен как объяснение архитектурного разделения, но не как описание всех нынешних классов Chromium. [GPU Accelerated Compositing in Chrome](https://www.chromium.org/developers/design-documents/gpu-accelerated-compositing-in-chrome/).

**FACT / КОД.** В актуальном коде сохраняется явная модель приоритетов тайлов `NOW`, `SOON`, `EVENTUALLY`, расстояние до видимой области и режим `SMOOTHNESS_TAKES_PRIORITY`. [Chromium `tile_priority.h`, строки 36–61 и 86–91](https://github.com/chromium/chromium/blob/b124f80df73fe467ac48846536e2542acfc3be20/cc/tiles/tile_priority.h#L36-L91).

Для Map особенно полезны три идеи:

- резервировать CPU и память сначала под **видимый результат**;
- во время движения отдавать приоритет плавности уже доступной сцены;
- различать **запрет конкретной возможности**, **временную перегрузку** и **аварию устройства**.

**SwiftShader — важное ограничение аналогии.** Нельзя использовать утверждение «Chromium всегда автоматически подхватывает SwiftShader» как современный контракт. Официальная документация объявляет автоматический WebGL fallback устаревающим; разработчики отдельно описывают его отключение и переход Windows-конфигураций на WARP. Это относится к WebGL fallback и не тождественно отключению программной отрисовки интерфейса браузера. [Документ Chromium](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/docs/gpu/swiftshader.md), [обсуждение разработчиков, включая WARP](https://groups.google.com/a/chromium.org/g/blink-dev/c/yhFguWS_3pM).

**NOT CHECKED:** точный набор включённых экспериментов и fallback-политик в конкретной установленной версии Chrome.

**3.2. Firefox: наиболее близкий образец лестницы резервирования**

**FACT / КОД.** В проверенной ревизии Firefox функция `FallbackFromAcceleration` явно содержит Windows-переходы:

1. аппаратный WebRender → **Software WebRender + D3D11 composition**;
2. Software WebRender + D3D11 → **Software WebRender без D3D11 composition**.

Переходы зависят от разрешённых возможностей и настроек. Причинами вызова служат ошибки инициализации, `MakeCurrent`, рендера, `BeginDraw`, чрезмерное число reset и ошибки GPU process. [Лестница fallback в `gfxPlatform.cpp`, строки 4000–4042](https://github.com/mozilla-firefox/firefox/blob/c72617c016491f426f33f84b8822aad7fdd532c2/gfx/thebes/gfxPlatform.cpp#L4000-L4042), [причины в `GPUProcessManager.cpp`, строки 717–746](https://github.com/mozilla-firefox/firefox/blob/c72617c016491f426f33f84b8822aad7fdd532c2/gfx/ipc/GPUProcessManager.cpp#L717-L746).

**АВТОРЫ.** SWGL — специализированный software OpenGL для WebRender. Его README описывает сам rasterizer как однопоточный, использующий SIMD и обработку spans. Это не означает, что весь Firefox работает одним потоком: программная композиция имеет отдельные очереди и распределение работ. [SWGL README](https://github.com/mozilla-firefox/firefox/blob/c72617c016491f426f33f84b8822aad7fdd532c2/gfx/wr/swgl/README.md#L1-L9), [описание разделения compositing work](https://bugzilla.mozilla.org/show_bug.cgi?id=1661427).

Полезный образец для Map — именно **CPU raster + доступный аппаратный вывод → полностью программный вывод**. Переносить SWGL целиком для этого не требуется.

В проверенных местах fallback запускается из-за ошибок и ограничений; я не нашёл подтверждения, что Firefox использует эту лестницу как обратимый регулятор каждого временного падения FPS. Для обычной перегрузки Map нужен собственный governor.

**3.3. Skia: CPU raster — полноценный backend, многопоточность организует клиент**

**АВТОРЫ.** Raster backend рисует в блок памяти; GPU surfaces требуют графического контекста. Для OpenGL клиент отвечает за правильный текущий контекст потока. [SkCanvas Creation](https://skia.org/docs/user/api/skcanvas_creation/).

Разработчики Skia прямо указывают: `SkSurface` и `SkCanvas` не thread-safe; для параллельной растеризации нужны отдельные surface/canvas на каждом потоке. [Ответ разработчиков Skia](https://groups.google.com/g/skia-discuss/c/yqF1rwi98pg).

Следовательно, правильная схема CPU-помощи:

- независимые тайлы или полосы;
- отдельные CPU surfaces;
- неизменяемые входные данные;
- публикация завершённого изображения;
- композиция в правильном порядке.

Совместное рисование несколькими потоками в один обычный `SKCanvas` для Map не подходит. GPU-backed изображения также нельзя считать свободно доступными CPU workers только потому, что объект `SKImage` неизменяем.

**АВТОРЫ.** Graphite улучшает организацию записи GPU-команд: независимые `Recorder` могут готовить `Recording` на разных потоках, после чего записи отправляются GPU. Это не CPU fallback и не добавляет мощности встроенной графике. [Статья разработчиков Graphite](https://blog.google/chromium/introducing-skia-graphite-chromes/).

**Рекомендация:** Ganesh → Graphite не включать в резервный блок. Это отдельная миграция backend с самостоятельной проверкой совместимости Avalonia/SkiaSharp.

**3.4. ANGLE и WARP: совместимый программный GPU, а не дополнительная видеокарта**

**АВТОРЫ.** WARP — программный rasterizer Direct3D. Для D3D11 он выбирается через `D3D_DRIVER_TYPE_WARP`; Windows также предоставляет Microsoft Basic Render Adapter. [WARP Guide](https://learn.microsoft.com/en-us/windows/win32/direct3darticles/directx-warp), [создание WARP device](https://learn.microsoft.com/en-us/windows/win32/direct3d11/overviews-direct3d-11-devices-create-warp).

**FACT / КОД И СПЕЦИФИКАЦИЯ.** ANGLE поддерживает выбор WARP через атрибут устройства. В текущем тексте расширения используется имя `EGL_PLATFORM_ANGLE_DEVICE_TYPE_D3D_WARP_ANGLE`. [ANGLE extension, строки 59–65 и 83–94](https://github.com/google/angle/blob/main/extensions/EGL_ANGLE_platform_angle_d3d.txt#L59-L94).

Но проверенный путь Avalonia 12.1.1 самостоятельно создаёт D3D11 device для выбранного адаптера с `D3D_DRIVER_TYPE_UNKNOWN` и передаёт его ANGLE. Это не прямой вызов общего EGL-пути с атрибутом WARP. [Avalonia `AngleWin32EglDisplay.cs`, строки 135–170](https://github.com/AvaloniaUI/Avalonia/blob/12.1.1/src/Windows/Avalonia.Win32/OpenGl/Angle/AngleWin32EglDisplay.cs#L135-L170).

Практическая оценка:

- WARP полезен, когда требуется сохранить D3D/GL-совместимый путь;
- это не объединение CPU и HD 630 в один быстрый renderer;
- для растрового 2D-редактора прямой Skia CPU raster может быть проще, но **какой путь быстрее в Map, не проверено**;
- `D3D_DRIVER_TYPE_SOFTWARE` и `D3D_DRIVER_TYPE_WARP` — разные значения API; их нельзя подменять друг другом.

**3.5. Игровые движки: менять качество в ответ на конкретное узкое место**

**АВТОРЫ.** Unity Adaptive Performance разделяет targets `CPU`, `GPU`, `FillRate`. Scalers меняют resolution, LOD, shadows и другие параметры; источник метрик зависит от provider. Это полезный образец разделения «датчик → оценка bottleneck → подходящее действие». [Unity scalers](https://docs.unity3d.com/Manual/adaptive-performance/scalers-reference.html), [providers](https://docs.unity3d.com/Manual/adaptive-performance/providers-introduction.html).

Unreal Dynamic Resolution использует историю GPU frame time, целевой бюджет, запас производительности, минимальный период изменения разрешения и минимальный значимый шаг. Предусмотрена ускоренная реакция на резкое превышение бюджета. [Unreal Dynamic Resolution](https://dev.epicgames.com/documentation/en-us/unreal-engine/dynamic-resolution-in-unreal-engine).

Для Map это означает:

- fill-rate pressure → уменьшить разрешение сцены;
- upload pressure → уменьшить принятие новых изображений;
- CPU pressure → уменьшить фоновую конкуренцию;
- нехватка памяти → сократить residency и prefetch;
- сбой backend → запускать recovery.

В одном состоянии «низкий FPS» эти причины смешивать нельзя. Dynamic resolution не исправит ожидание compositor, если работа GPU уже укладывается в его доступное окно.

**3.6. Картографические системы: растровый fallback требует отдельного представления**

**АВТОРЫ.**

- В обсуждении MapLibre разработчики указывают, что готовый fallback без WebGL из коробки не предоставляется; возможен отдельный Leaflet/raster путь. [MapLibre discussion #4473](https://github.com/maplibre/maplibre-gl-js/discussions/4473).
- В Mapbox GL JS есть явная проверка `supported()`. Она позволяет приложению выбрать другой путь, но сама по себе не создаёт software renderer. [Официальный пример Mapbox](https://docs.mapbox.com/mapbox-gl-js/example/check-for-support/).
- В Leaflet `Canvas` — альтернативный renderer для `Path`; это не автоматический CPU-backend MapLibre и не готовая система для миллионов растровых stamps. [Leaflet reference](https://leafletjs.com/reference.html#canvas).
- Google Maps JavaScript API документирует fallback vector → raster и событие `renderingtype_changed`. [Google Maps WebGL support](https://developers.google.com/maps/documentation/javascript/webgl/support).
- Для пользовательского Google Maps актуальная справка описывает **2D Mode** для слабых компьютеров: без WebGL и 3D, с более простыми переходами. Старое название Lite Mode не стоит считать текущим техническим контрактом. [Google Maps Help](https://support.google.com/maps/answer/3031966).

Для Map переносима идея **того же вида карты, заранее сведённого в растровые блоки**. Google генерирует raster tiles сервером; Map должен делать это локально и офлайн. У Map соответствующая основа уже есть в overview/detail caches.

---

**4. Что Avalonia 12.1.1 действительно позволяет**

Это основной платформенный предел предложения.

**FACT / КОД.**

1. `Win32PlatformOptions.RenderingMode` по умолчанию содержит `AngleEgl, Software`. [Строки 110–121](https://github.com/AvaloniaUI/Avalonia/blob/12.1.1/src/Windows/Avalonia.Win32/Win32PlatformOptions.cs#L110-L121).
2. `Win32GlManager.InitializeCore()` перебирает этот список при инициализации; выбор `Software` возвращает отсутствие platform GPU graphics. [Строки 34–76](https://github.com/AvaloniaUI/Avalonia/blob/12.1.1/src/Windows/Avalonia.Win32/Win32GlManager.cs#L34-L76).
3. При `IsLost` менеджер backend context освобождает ресурсы и получает новый контекст от **того же** `_graphics` provider. Он не возвращается к перебору `RenderingMode`. [Строки 28–58](https://github.com/AvaloniaUI/Avalonia/blob/12.1.1/src/Avalonia.Base/Rendering/PlatformRenderInterfaceContextManager.cs#L28-L58).
4. ANGLE provider при создании следующего контекста может создать новый D3D11 display. [Строки 27–34](https://github.com/AvaloniaUI/Avalonia/blob/12.1.1/src/Windows/Avalonia.Win32/OpenGl/Angle/D3D11AngleWin32PlatformGraphics.cs#L27-L34).
5. Skia backend при `graphicsContext == null` создаёт программный `SkiaContext`. Skia lease feature доступна и не требует обязательного ненулевого `GRContext`. [Создание backend](https://github.com/AvaloniaUI/Avalonia/blob/12.1.1/src/Skia/Avalonia.Skia/PlatformRenderInterface.cs#L33-L45), [lease feature](https://github.com/AvaloniaUI/Avalonia/blob/12.1.1/src/Skia/Avalonia.Skia/DrawingContextImpl.cs#L897-L901).

Таким образом:

| Возможность | Вывод |
|---|---|
| Автоматический startup fallback на software при неудачной GPU-инициализации | **Подтверждён кодом.** |
| Принудительный Software при старте | **Подтверждён API и кодом.** |
| Пересоздание потерянного GPU context | **Подтверждено кодом.** Успех на конкретном драйвере не гарантирован. |
| Смена всего окна GPU → Software простым изменением свойства во время работы | **Не подтверждена; проверенный путь этого не делает.** |
| Обратная смена Software → GPU на лету | **Готового публичного механизма в исследованных местах не найдено.** |

Есть дополнительная деталь восстановления: WinUI composition содержит watchdog для ситуации, когда `RequestCommitAsync` не завершается после потери D3D device. Пример в комментарии — pause/resume VM. Watchdog принудительно запускает следующий tick после ожидания порядка секунды. Это средство разблокирования, а не software fallback. [Avalonia, строки 166–190](https://github.com/AvaloniaUI/Avalonia/blob/12.1.1/src/Windows/Avalonia.Win32/WinRT/Composition/WinUiCompositorConnection.cs#L166-L190).

**Следствие:** полностью программный путь сцены внутри Map полезен, но если само окно по-прежнему выводится через погибший GPU backend, нарисованная CPU картинка не решает проблему показа.

---

**5. Сигналы Windows: чему доверять и что они не доказывают**

**5.1. Идентификация и capabilities**

**АВТОРЫ.** `DXGI_ADAPTER_DESC1` предоставляет Vendor/Device IDs, объёмы памяти, LUID и flags. Это основа идентификации, но объём `DedicatedVideoMemory` не является текущим доступным бюджетом процесса. [DXGI_ADAPTER_DESC1](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/ns-dxgi-dxgi_adapter_desc1).

**PROPOSED.** Профиль Map должен включать:

- фактически используемый adapter LUID;
- Vendor/Device IDs и версию драйвера;
- выбранный backend: hardware ANGLE, software adapter/WARP или Skia raster;
- необходимые возможности и результаты маленькой пробы отрисовки;
- размер framebuffer в физических пикселях и DPI;
- доступность timing/memory sensors;
- RDP/VM как контекст.

**RDP или VM сами по себе не означают отсутствие GPU.** Аналогично отсутствие timer query не означает, что аппаратное ускорение недоступно. Неудача диагностического API должна давать `Unknown`, а не `Absent`.

Нужно связать DXGI-датчики с **адаптером, реально выбранным Avalonia**, особенно на системах с несколькими GPU. Созданный отдельно диагностический device может относиться к другому адаптеру.

**5.2. Бюджет видеопамяти**

**АВТОРЫ.** `IDXGIAdapter3::QueryVideoMemoryInfo` сообщает бюджет и использование памяти текущим процессом. Microsoft предупреждает, что превышение бюджета может приводить к paging и stuttering. Для изменений бюджета есть notification event. [QueryVideoMemoryInfo](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/nf-dxgi1_4-idxgiadapter3-queryvideomemoryinfo), [поля структуры](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/ns-dxgi1_4-dxgi_query_video_memory_info), [уведомление](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/nf-dxgi1_4-idxgiadapter3-registervideomemorybudgetchangenotificationevent).

**PROPOSED.**

- Наблюдать `Budget`, `CurrentUsage`, запас до бюджета и его изменение.
- Учитывать `LOCAL`/`NON_LOCAL` в соответствии с фактической конфигурацией.
- Получать уведомления и периодически обновлять usage.
- Для HD 630 не выводить «GPU слабый/памяти нет» из маленького dedicated VRAM.
- Учитывать также системную память: CPU tiles, pinned images и готовые результаты workers могут создать другую нехватку.

Skia resource-cache usage — лишь часть памяти процесса. В него не обязаны входить все активные surfaces, swapchain и ресурсы других компонентов.

**5.3. Потеря устройства**

**АВТОРЫ.** `GetDeviceRemovedReason` различает, в частности, `DEVICE_HUNG`, `DEVICE_REMOVED`, `DEVICE_RESET`, `DRIVER_INTERNAL_ERROR`; Microsoft требует пересоздания device-dependent ресурсов после потери устройства. [GetDeviceRemovedReason](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11device-getdeviceremovedreason), [handling device lost](https://learn.microsoft.com/en-us/windows/uwp/gaming/handling-device-lost-scenarios).

Это **аварийный сигнал**, а не повод ждать усреднения FPS.

В Map для его надёжного получения понадобится проверенный мост к платформенному backend. Нельзя предполагать, что Avalonia предоставляет приложению готовое публичное событие со всеми нужными HRESULT.

**5.4. GPU timer**

**FACT / КОД.** Имеющийся `GpuFrameTimer` получает результат асинхронно и не ждёт незавершённый query. [Код](/C:/Projects/Map-wt-pack4/src/Map.App/Controls/GpuFrameTimer.cs:129).

**АВТОРЫ.** `EXT_disjoint_timer_query` измеряет выполнение диапазона GL-команд; disjoint делает связанные измерения недостоверными. [Спецификация Khronos](https://registry.khronos.org/OpenGL/extensions/EXT/EXT_disjoint_timer_query.txt).

Ограничения для политики:

- значение приходит с задержкой;
- длительность диапазона не равна общей загрузке всех GPU engines;
- query не измеряет весь путь до показа кадра;
- отсутствие результата не равно нулевой стоимости;
- `LastMilliseconds` нельзя повторно учитывать как новый sample на каждом кадре.

**PROPOSED:** передавать в telemetry номер исходного кадра, generation контекста, время получения и признак достоверности. После восстановления sensors должны переинициализироваться.

**5.5. Frame pacing и фактический показ**

**PROPOSED.** Внутри приложения нужно разделить четыре момента:

1. поступление/объединение ввода;
2. начало работы над кадром;
3. завершение CPU-подготовки и submission;
4. подтверждение/показ — если доступен достоверный платформенный источник.

Только первые три можно достаточно просто измерить около `MapCanvasControl`. Callback отрисовки или завершение commit не следует объявлять доказательством scanout.

**АВТОРЫ.**

- `DwmGetCompositionTimingInfo` на Windows 8.1+ требует `hwnd = NULL`; это данные DWM, которые нельзя автоматически приписать одному окну Map. [Документация](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmgetcompositiontiminginfo).
- `IDXGISwapChain::GetFrameStatistics` имеет ограничения presentation model и надёжности на нескольких мониторах. [Документация](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgiswapchain-getframestatistics).
- `IDXGIOutput::GetFrameStatistics` поддерживается только в fullscreen и не подходит как общий датчик оконного редактора. [Документация](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgioutput-getframestatistics).

**Важное уточнение измерений:** превышение ровно `16,7 мс` ещё нужно отличить от пропуска полного display tick. Период может быть `16,683… мс`, присутствуют jitter и ошибки округления. Для диагностики полезна гистограмма интервалов около `T`, `2T`, `3T` и подтверждение ETW/PresentMon.

Ваши данные согласуются с проблемой cadence/queueing, но сами по себе не исключают scheduling jitter, другие GPU clients или особенности конкретного presentation path.

---

**6. Что резервный блок должен делать: приоритет по пользе и стоимости**

Ниже — **PROPOSED**, ранжирование именно для Map с уже существующими caches.

| Приоритет | Действие | Когда помогает | Цена и предел |
|---|---|---|---|
| **1** | Использовать готовый camera frame / готовые tile images; объединять устаревшие camera updates | Pan/zoom, сцена не менялась | Очень дёшево; ограничено покрытием и допустимым размытием |
| **1** | Общий бюджет принятия и первого использования изображений | Upload bursts, cold cache | Снижает всплески; временно увеличивает время уточнения |
| **1, отдельная проверка** | Исправить cadence выбранного presentation path | GPU укладывается, но кадры регулярно опаздывают | Потенциально большой эффект; требует работы на уровне Avalonia output |
| **2** | Dynamic resolution только сцены во время жеста | Fill-rate, blending, большие offscreen passes | Высокий потенциальный эффект; видимая временная потеря резкости |
| **2** | CPU raster устойчивых блоков сцены существующими workers | Повторяющиеся stamps и дорогая повторная композиция | Выгодно при повторном использовании; затраты CPU/RAM/uploads |
| **3** | Откладывать дорогие preview effects | Подтверждённая стоимость конкретного эффекта | Требует согласованной политики качества |
| **Аварийный путь** | Полностью программные raster и композиция | GPU недоступен или backend не восстанавливается | Сохраняет работоспособность; 60 FPS заранее не гарантируются |
| **Вариант для сравнения** | ANGLE/WARP | Нужен совместимый D3D/GL путь | Производительность и интеграция требуют отдельной проверки |

**6.1. CPU-помощь: какие работы и сколько потоков**

CPU стоит отдавать работу, которую можно выполнить заранее и повторно использовать:

- подготовку и raster видимых detail tiles;
- overview и ближайшие необходимые уровни;
- стабильные подслои сцены, если итоговая композиция остаётся эквивалентной;
- декодирование/подготовку mip в рамках общего scheduler.

Невыгодная схема — каждый кадр заново рисовать всю сцену CPU и полностью загружать её на GPU.

Для масштаба: один BGRA8 framebuffer `1920×1080` занимает примерно `7,91 MiB`; передача 60 таких кадров в секунду — около `475 MiB/s` без учёта дополнительных копий. Для `3840×2160` — примерно `1,85 GiB/s`. Это расчёт объёма, **не измерение стоимости Map**.

**PROPOSED для четырёх физических ядер:** начать с **двух одновременно работающих raster jobs**, допускать третий только после подтверждения пользы. Считать общий лимит вместе с overview/mip/decode workers.

Текущее `ProcessorCount - 2` на машине с восемью логическими процессорами может дать шесть raster workers. Для встроенной графики это не обязательно выгодно: CPU и GPU конкурируют за память и энергобюджет. Число потоков должно быть actuator scheduler, а не постоянной характеристикой «резерв включён».

Обязательные условия:

- отдельная surface на worker;
- ограничение памяти выполняющихся и завершённых jobs;
- приоритет текущего viewport;
- отмена устаревшей работы;
- сохранение порядка stamps, premultiplied alpha, layer opacity и blend modes;
- корректные поля вокруг тайлов для filters/shadows.

Сведение полупрозрачных слоёв в произвольном порядке способно изменить изображение. Его нельзя обосновать только ускорением.

**6.2. Dynamic resolution**

**PROPOSED:** во время camera gestures рисовать сцену в offscreen surface с линейным масштабом `1,0 → 0,85 → 0,7 → 0,5`, затем выводить её в окно.

По числу пикселей это:

| Масштаб | Пикселей относительно полного разрешения | Теоретическое уменьшение пиксельной работы |
|---|---:|---:|
| 0,85 | 72,25 % | 27,75 % |
| 0,70 | 49 % | 51 % |
| 0,50 | 25 % | 75 % |

Это **не прогноз такого же ускорения FPS**: traversal, draw calls, uploads и финальное растягивание не уменьшаются пропорционально.

Практические условия:

- разрешение считать в физических пикселях с учётом DPI;
- координаты документа и hit testing сохранять точными;
- редакторские handles, курсор и рамки рисовать поверх в полном разрешении;
- после остановки движения возвращать точный кадр;
- обновлять resolution/mip decisions согласованно;
- не пересоздавать render target на каждом небольшом колебании метрики.

Новый визуальный режим сначала требует сравнения на материалах владельца. Политика качества не должна влиять на сохранение или экспорт.

**6.3. Reprojection / повторное использование кадра**

Для обычного 2D pan/zoom преобразование старого изображения геометрически простое; 3D-reprojection алгоритмы здесь не нужны.

Полезное расширение текущего cache:

- заранее готовить небольшую область по направлению движения;
- подставлять доступный overview в открывшиеся области;
- пересобирать только нужные tiles;
- ограничивать увеличение по качеству;
- инвалидировать результат по revision сцены и поколениям ресурсов.

Старое изображение допустимо как preview камеры. При изменении документа оно не должно скрывать фактический результат редактирования.

**6.4. Загрузки и память**

Нужен один бюджет для:

- mip adoption;
- detail/overview tiles;
- новых scene surfaces;
- восстановления textures после context loss;
- фоновых prefetch.

Данные CPU и GPU residency нужно учитывать отдельно. Удаление CPU-изображения может привести к повторному decode, а удаление GPU texture — к повторному upload.

При memory pressure разумнее сначала убрать невидимый prefetch и дорогое удержание ненужных уровней. Массовый `PurgeResources` каждый кадр может создать цикл «удалили → снова загрузили» и ухудшить хвост задержек.

**6.5. Frame pacing**

**FACT / КОД.** Avalonia уже имеет разные источники tick:

- WinUI path связан с завершением commit и следующим `RequestCommitAsync`;
- DXGI path использует `WaitForVBlank`.

[WinUI loop](https://github.com/AvaloniaUI/Avalonia/blob/12.1.1/src/Windows/Avalonia.Win32/WinRT/Composition/WinUiCompositorConnection.cs#L129-L163), [DXGI wait](https://github.com/AvaloniaUI/Avalonia/blob/12.1.1/src/Windows/Avalonia.Win32/DirectX/DxgiConnection.cs#L101-L112).

**АВТОРЫ.**

- `DCompositionWaitForCompositorClock` предназначен для управления render thread, поддерживает дополнительные события и доступен с Windows build 22000. [Compositor clock](https://learn.microsoft.com/en-us/windows/win32/directcomp/compositor-clock/compositor-clock), [API requirements](https://learn.microsoft.com/en-us/windows/win32/api/dcomp/nf-dcomp-dcompositionwaitforcompositorclock).
- Waitable DXGI swapchain позволяет дождаться подходящего момента **до начала** следующего рендера. [Microsoft DXGI latency guide](https://learn.microsoft.com/en-us/windows/uwp/gaming/reduce-latency-with-dxgi-1-3-swap-chains).
- `DwmFlush` блокирует вызывающий поток до показа поставленных приложением изменений; это не универсальный неблокирующий frame scheduler. [DwmFlush](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmflush).

**Рекомендация:** не вставлять дополнительный `WaitForVBlank`, `DwmFlush` или compositor-clock wait внутрь `MapCanvasControl.Render`. Получится второе ожидание поверх существующего цикла.

Проверять pacing нужно в одном месте — владельце presentation loop. И «начинать сразу после vblank» следует рассматривать как проверяемую стратегию, а не универсальный оптимум: важны deadline compositor, очередь и actual present path.

---

**7. Конкретная архитектура резервного блока Map**

**PROPOSED.** Один вспомогательный модуль, условно `ReserveRendering`, с двумя зонами ответственности:

- **помощь кадру:** подготовить более дешёвое представление и снять часть работы;
- **восстановление backend:** обеспечить путь вывода при потере устройства.

Он не должен становиться вторым quality governor. **Все решения сводятся в один неизменяемый `FramePlan`, который утверждает общий coordinator/governor.**

| Компонент | Ответственность | Подключение |
|---|---|---|
| `GraphicsProfileProbe` | Адаптер, backend, capabilities, короткая калибровка | Startup до/после фактической графической инициализации |
| `RenderHealthMonitor` | CPU/GPU timings, memory pressure, возраст кадра, достоверность sensors | App/platform layer |
| `ReserveController` | Детерминированное состояние и причина включения; запрос помощи | Чистая policy-логика без Win32-вызовов |
| `CpuTileAssist` | Приоритеты и допустимая конкурентность существующих raster jobs | Detail/overview preparation через общий scheduler |
| `SceneSurfacePresenter` | Пониженное разрешение сцены, использование готовых изображений, полноразмерный overlay | Около текущего вызова `SceneRenderer.Render` |
| `GraphicsRecoveryCoordinator` | Потеря устройства, generation ресурсов, bounded retry, software recovery | Между приложением и платформенным backend |
| Диагностика и fault injection | Принудительные состояния и журнал причин | Тестовые/диагностические параметры |

Windows-specific API остаются в `Map.App`/platform layer. В Core не должны появляться зависимости от DXGI, ANGLE или Skia.

**7.1. Контракт кадра**

Минимальный `FramePlan` должен содержать:

- номер кадра, revision сцены, generation backend;
- фактическое состояние движения камеры;
- целевой бюджет и допустимый запас;
- разрешение сцены;
- политику camera-frame reuse;
- общий лимит принятия новых raster images;
- разрешённую конкурентность и приоритеты background jobs;
- требования к точности;
- причину и срок действия ограничений.

**Важная точка подключения:** план должен быть известен **до `StampImages.BeginFrame()`**, где уже принимаются готовые mip. Сейчас `BeginFrame` вызывается раньше вычисления `movingNow`, а `FrameMoving` обновляется позже. Для нового общего coordinator это нужно учесть, иначе часть ограничений будет реагировать с задержкой на предыдущий кадр. [Порядок начала кадра](/C:/Projects/Map-wt-pack4/src/Map.App/Controls/MapCanvasControl.cs:818), [обновление движения](/C:/Projects/Map-wt-pack4/src/Map.App/Controls/MapCanvasControl.cs:910).

Последовательность:

1. Получить актуальное camera/input state и health snapshot.
2. Утвердить `FramePlan`.
3. Принять готовые изображения в его пределах.
4. Использовать пригодный cached frame либо нарисовать сцену в выбранном разрешении.
5. Нарисовать редакторский overlay.
6. Завершить telemetry и передать новые задачи scheduler.

Нельзя выполнять полный обычный render, а затем поверх него ещё раз полный «резервный»: помощь должна **замещать дорогую работу в данном кадре**.

**7.2. Состояния и причины**

Не стоит делать отдельное взаимоисключающее состояние для каждой причины. Перегрузка GPU и памяти может происходить одновременно.

Предлагаю состояния:

| Состояние | Поведение |
|---|---|
| `Probing` | Определяется реальный backend и начальные возможности |
| `Normal` | Существующий pipeline, резервные ограничения сняты |
| `Assisted` | Включена адресная помощь; набор действий зависит от причины |
| `Recovering` | Старые device-dependent ресурсы больше не используются; идёт ограниченное восстановление |
| `Software` | CPU raster/композиция с работоспособным software output |
| `RecoveryProbe` | Осторожная проверка возможности возврата аппаратного пути |

Независимые причины: `WeakProfile`, `GpuCost`, `UploadPressure`, `MemoryPressure`, `Pacing`, `DeviceFailure`, `UnknownTelemetry`.

Переходы:

- слабый подтверждённый startup profile → `Assisted`;
- нет работоспособного GPU → `Software`;
- устойчивое превышение бюджета → `Assisted`;
- потеря устройства → немедленно `Recovering`;
- восстановление успешно → `Assisted`, затем постепенный возврат;
- восстановление не удалось → `Software` через предусмотренный механизм output recovery;
- устойчивая нормальная работа → постепенное снятие помощи;
- возвращение GPU после software → `RecoveryProbe`, затем восстановление аппаратного backend.

**7.3. Стартовые пороги и гистерезис**

Следующие числа — **PROPOSED для первых измерений**, не стандарты Windows или Skia.

Пусть `T` — целевой период кадра. Для 60 FPS это примерно `16,67 мс`. Частоту монитора и целевой FPS нужно хранить отдельно.

| Сигнал | Включение/усиление помощи | Ослабление помощи |
|---|---|---|
| GPU cost | p95 достоверных свежих samples выше `0,75T` в двух последовательных окнах примерно по секунде | Ниже `0,55T` и устойчивый запас несколько секунд |
| Пропуски кадра | Более 5 % подтверждённых пропусков в активном взаимодействии; использовать только достоверный источник | Менее 1 % в течение примерно 10 секунд |
| Memory pressure | `CurrentUsage/Budget > 0,90` либо резкое сокращение бюджета | Ниже `0,75` несколько секунд |
| Превышение memory budget | Немедленно прекратить необязательные новые allocations и prefetch | После восстановления запаса |
| Device lost | Немедленно `Recovering` | Только после успешного восстановления |
| Неизвестная telemetry | Консервативный профиль; не интерпретировать как «GPU исправен» | После появления валидных samples |

Дополнительно:

- минимум порядка **5 секунд** удержания обычного assist state;
- ухудшение допускается быстрее улучшения;
- улучшать по одному actuator за раз;
- после изменения resolution/upload/concurrency дать системе стабилизироваться;
- при недавнем context loss использовать более длинный cooldown;
- не оценивать плавность по кадрам простоя, minimized/occluded окну;
- после смены display, DPI или backend сбрасывать соответствующую историю.

**Ключевой нюанс:** низкая стоимость кадра при включённом пониженном разрешении ещё не доказывает, что можно полностью отключить помощь. Нужны небольшие контролируемые пробы следующей ступени качества. Иначе получится цикл «помощь помогла → выключили → снова перегрузка».

Порог в `0,75T` — намеренный запас, а не вывод, что оставшиеся `0,25T` точно покрывают все стадии. Реальную доступную GPU-часть бюджета должен определить замер presentation path.

**7.4. Startup self-benchmark**

**PROPOSED:**

- короткая проба существующими ассетами владельца;
- отдельно тёплая композиция, blending и ограниченный cold upload;
- фактический backend приложения и размер framebuffer;
- жёсткий предел времени, ориентировочно до 1–2 секунд суммарно;
- результат — начальный профиль, который затем корректируется реальной работой.

Такой тест не должен пытаться моделировать миллионы объектов или надёжно измерять p99 по нескольким десяткам samples.

Кэшировать профиль можно по adapter/driver/backend и классу разрешения, с повторной проверкой после их изменения. Это предложение для локальных настроек, не данных документа.

**7.5. Полностью программный путь и runtime recovery**

Здесь нужна честная граница реализуемости.

**Подтверждённый фундамент:** startup Software mode Avalonia и CPU surfaces Map.

**NOT CHECKED:** полный редакторский сценарий Map без GPU, включая все styles, effects, большие карты, captures и восстановление ресурсов.

Для runtime GPU → Software есть два архитектурных варианта:

| Вариант | Преимущество | Стоимость/ограничение |
|---|---|---|
| Контролируемый перезапуск в Software с восстановлением сессии | Использует подтверждённый startup-механизм Avalonia | Требует безошибочного сохранения/передачи состояния и перезапуска; переход видим |
| Специальная поддержка смены graphics/output backend внутри процесса | Может обеспечить непрерывный автоматический переход | Требует отдельного прототипа host/framework integration; готовый публичный API не подтверждён |

**Рекомендую для первого полного аварийного пути опираться на startup Software и контролируемое восстановление сессии.** Его нельзя включать автоматически до доказательства сохранности несохранённых изменений, viewport и необходимой истории Undo. Простое сохранение `.mapproj` не следует объявлять полным переносом редакторской сессии.

Для требования **«GPU пропал и вернулся без перезапуска и без разрыва сессии»** нужен отдельный прототип интеграции Avalonia output/backend. Это существенная зависимость проекта, которую один контроллер качества не закрывает.

При любом варианте:

- после loss нельзя читать старый GPU frame для спасения картинки;
- CPU-ready tiles и подготовленные mip следует сохранять, если они независимы от device;
- GPU resources инвалидируются по **монотонному generation**, а не только native handle;
- загрузки после recovery идут постепенно, без одновременного восстановления всех caches;
- возврат аппаратного backend требует успешной пробы настоящего пути, а не только успешного `D3D11CreateDevice`.

Для `Assisted ↔ Normal` автоматический возврат можно делать обычным гистерезисом. Для `Software ↔ Hardware` тот же принцип должен управлять coordinator восстановления, но сам механизм смены backend необходимо сначала реализовать и проверить.

---

**8. Как проверять резервный блок**

Все проверки ниже — **план**, они в этом исследовании не выполнялись.

**8.1. Принудительные режимы**

| Режим проверки | Что подменяется | Что должно произойти |
|---|---|---|
| `weak` | Начальный graphics profile | Старт в ограниченном assist; постепенная проверка возможности улучшения |
| `gpu-pressure` | Последовательность свежих GPU timings | Усиление подходящих действий без колебаний состояния |
| `memory-pressure` | Снижение DXGI budget | Остановка prefetch/allocations, освобождение необязательных ресурсов |
| `pacing-pressure` | Поздние frame starts при дешёвом GPU | Причина определяется как pacing; CPU pool не разгоняется автоматически |
| `absent-at-start` | Принудительный Avalonia Software | Полный редактор работает без `GRContext` |
| `lost-during-work` | Контролируемая потеря тестового backend | Старые GPU resources не используются; выполняется recovery |
| `recovered` | Успешные probes и нормальные метрики | Поэтапный выход из помощи |
| `unknown-sensors` | Нет queries/нет DXGI statistics | Консервативное поведение без ложных нулевых метрик |

Принудительные сигналы проверяют policy. Они **не заменяют** настоящую проверку Software mode и реального жизненного цикла device resources.

Отключение видеокарты или принудительный reset драйвера на рабочем ПК владельца не нужны для первых этапов.

**8.2. Policy и scheduler**

Нужны тесты:

- дрожание значений около порога не вызывает flapping;
- старый query не учитывается повторно;
- отсутствие sensor не означает нулевую нагрузку;
- device failure имеет приоритет над cooldown;
- combined pressure не даёт конфликтующих команд;
- переключение разрешения не создаёт неограниченные allocations;
- устаревшие jobs освобождают pinned images;
- после recovery не возникает upload storm;
- ограниченный режим не блокирует forever уточнение сцены.

**8.3. Правильность изображения и редактирования**

На ассетах владельца проверить:

- прозрачные перекрывающиеся stamps;
- границы detail tiles, shadows и filters;
- opacity/blend слоёв;
- координаты выбора и кисти при пониженном resolution;
- Undo/Redo во время и после переключения;
- pan/zoom за пределы сохранённого frame;
- возврат точного изображения после остановки;
- экспорт и сохранение при активной помощи.

Уменьшенный preview должен иметь явный внутренний статус незавершённого точного кадра — как уже сделано для camera reuse.

**8.4. Производительность и критерии результата**

Для HD 630 сравнить одинаковые сценарии:

1. текущий pipeline;
2. только общий upload/adoption budget;
3. только dynamic resolution;
4. только изменение CPU concurrency;
5. итоговую комбинацию;
6. отдельно выбранный presentation path.

Измерять:

- displayed/present intervals;
- p50/p95/p99 и максимум;
- количество интервалов около `2T`, `3T`;
- GPU timing и его покрытие достоверными samples;
- input-to-visible latency, насколько её позволяет источник;
- время до точного кадра после жеста;
- RAM, DXGI usage/budget, allocations и bytes adoption;
- переключения состояния и их причины.

Сначала достаточно коротких воспроизводимых трасс тёплого и холодного сценария. Удлинение нужно для проверки теплового поведения и хвостов, а не для повторения уже зелёного smoke.

**Ограничение целевого обещания:** резервный блок не позволяет заранее гарантировать 60 FPS в абсолютно худшем кадре Windows, особенно при device loss, paging или software-only. Требование следует проверять в определённых сценариях, разрешении и границах памяти. Отдельно фиксировать:

- плавность активного pan/zoom;
- редкие системные/аварийные паузы;
- доступность редактирования без GPU;
- время восстановления полной точности.

Средний FPS не будет достаточным критерием.

---

**9. Лицензии и пригодность для переноса**

Предпочтительно переносить **паттерны**, реализуя их небольшим C#-кодом в архитектуре Map.

| Источник | Лицензия/статус | Что разумно использовать |
|---|---|---|
| Chromium | BSD-style | Приоритеты, ограничение очередей, crash backoff; проверять конкретные файлы |
| Skia | BSD-3-Clause | Существующий raster backend и модель работы с surfaces |
| ANGLE | BSD-style | При необходимости — интеграционные примеры и capabilities |
| Avalonia | MIT | Минимальную адаптацию платформенного слоя, если потребуется |
| SwiftShader | Apache-2.0 | Только при доказанной необходимости отдельного software graphics API |
| Firefox/SWGL | MPL-2.0 | Архитектурный пример; прямой перенос требует учёта лицензии |
| Unity/Unreal | Не считать permissive-кодом | Использовать публично описанные идеи, не переносить реализацию без проверки условий |
| Windows WARP | Системный компонент Windows | Использовать API; это не библиотека для копирования исходников |

Проверенные первичные тексты: [Chromium file header](https://github.com/chromium/chromium/blob/b124f80df73fe467ac48846536e2542acfc3be20/cc/tiles/tile_priority.h#L1-L3), [Skia LICENSE](https://github.com/google/skia/blob/main/LICENSE), [ANGLE LICENSE](https://github.com/google/angle/blob/main/LICENSE), [Avalonia MIT](https://github.com/AvaloniaUI/Avalonia/blob/12.1.1/licence.md), [SwiftShader Apache-2.0](https://github.com/google/swiftshader/blob/master/LICENSE.txt), [SWGL Cargo license](https://github.com/mozilla-firefox/firefox/blob/c72617c016491f426f33f84b8822aad7fdd532c2/gfx/wr/swgl/Cargo.toml#L1-L6).

Установка WARP/SwiftShader/SWGL как нового пакета в рамках этого исследования не предлагается: сначала нужно доказать, что уже имеющегося Skia raster недостаточно.

---

**10. Рекомендуемый порядок реализации и основные риски**

**PROPOSED — четыре последовательных пакета.**

1. **Наблюдение и единое решение.**  
   Добавить достоверные samples, reasons, backend generation и `FramePlan`; объединить существующие ограничения adoption и scheduler. Результат — понятная причина каждого замедления и отсутствие конфликтующих регуляторов.

2. **Помощь работающему GPU.**  
   Управляемая CPU concurrency, приоритеты текущего viewport, согласованный reuse и dynamic resolution сцены. Для визуальной части — сначала согласование вариантов и сравнение.

3. **Проверка cadence.**  
   Отдельно сравнить output paths. Менять источник tick только на уровне presentation loop и только после доказанного эффекта. Для описанного zoom это может дать больше, чем дополнительные CPU workers.

4. **Аварийный Software и recovery.**  
   Сначала проверить полный startup Software. Затем реализовать выбранный механизм runtime recovery, сохранность сессии и безопасный возврат GPU.

Главные риски:

| Риск | Как его ограничить |
|---|---|
| CPU-помощь замедлит встроенный GPU | Общая конкурентность, измерение всей системы, постепенное увеличение |
| Два governor начнут противодействовать | Один утверждённый `FramePlan`, один владелец каждого actuator |
| Дешёвый preview скроет устаревшую сцену | Revision, generation, явный статус точности, invalidation |
| Низкое разрешение изменит координаты или качество окончательного результата | Разделить scene pixels и document coordinates; точный overlay/export |
| Удаление ресурсов вызовет постоянные повторные uploads | Приоритетное освобождение, минимальное удержание, учёт residency |
| Device recovery запустит лавину восстановления caches | Новый generation и ограниченное принятие ресурсов |
| Software scene не появится из-за сломанного output backend | Проверять весь путь окна, отдельно от raster evaluator |
| Автоперезапуск потеряет сессию или Undo | Не включать до проверки полного протокола восстановления |
| Слишком ранний возврат вызовет flapping | Более длинный выход, ступенчатые probes и cooldown |

**Самая сильная рекомендация для Map:** строить резерв вокруг уже существующих CPU tiles и caches, добавив контролируемое представление сцены и явный recovery coordinator. Начальная гипотеза для HD 630 — **ограничить всплески загрузок, уменьшать пиксельную работу во время жеста и независимо проверить cadence**. Дополнительные CPU workers полезны только там, где измерено, что они сокращают итоговую задержку.

## ИТОГИ

1. **Сделано:** подготовлено архитектурное исследование резервного блока с первичными источниками, ссылками на код и планом проверки. Подтверждены существующие механизмы Map и платформенная граница Avalonia: startup Software и восстановление GPU context есть; готовый runtime-переход всего окна GPU ↔ Software не подтверждён. Рабочая копия осталась чистой; изменений, сборок и запусков — **0**.
2. **Что дальше:** первый практический шаг — пакет наблюдения и единого `FramePlan`, который позволит отдельно распознать GPU cost, uploads, memory pressure и pacing. Затем выбирать действие по измеренной причине.
3. **Реализация проекта:** **не пересчитывалась**, поскольку выполнено исследование; резервный блок не реализован и на HD 630 не проверен.

<oai-mem-citation>
<citation_entries>
MEMORY.md:1368-1368|note=[Used historical guidance to reverify the live worktree and preserve its state]
</citation_entries>
<rollout_ids>
01a0dd8c-03a3-7d02-b64f-faff10e4c242
</rollout_ids>
</oai-mem-citation>