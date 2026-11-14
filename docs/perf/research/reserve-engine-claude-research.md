# «Запасной двигатель» отрисовки Map: исследование и проект

Автор: Claude (исследование, только чтение). Дата: 2026-11-14. Код Map: `C:\Projects\Map-wt-pack4`, HEAD `d81db90`
(ветка `claude/fps-pack-4`); `docs/perf/perf-core-design.md` есть только в `C:\Projects\Map` (в worktree его нет),
прочитан оттуда. Ни один файл в `C:\Projects` не менялся. Чужой код не запускался.

Метки: **[КОД]** — подтверждено чтением исходника (ссылка со строками); **[АВТОРЫ]** — сказано в документации или
комментарии авторов, кодом не проверено; **[НЕ ПРОВЕРЕНО]** — моя оценка или предположение, нужен замер.

---

## 1. Ответ одним абзацем

В зрелых системах «запасной двигатель» — это не второй рендерер рядом, а **лестница режимов с односторонними
ступенями и счётчиком сбоев**: Chromium спускается HARDWARE → SOFTWARE_GL (SwiftShader) → DISPLAY_COMPOSITOR после 3
падений GPU-процесса (одно прощается каждые 5 мин) и обратно в сессии не поднимается [КОД]; Firefox спускается
WebRender → **Software WebRender + D3D11-композиция** (CPU рисует, GPU только складывает) → чистый Software WebRender
[КОД]. Плавную же нагрузку («видеокарта не успевает») игровые движки решают регулятором с гистерезисом и
«паникой»: Unreal — динамическое разрешение с запасом 10 % бюджета, порогом изменения 2 %, историей кадров и
мгновенным сбросом после N подряд просроченных кадров [АВТОРЫ]; Unity — классификатор «CPU/GPU/упор в частоту» с
разными порогами входа и выхода [КОД]. **Avalonia 12.1.1 переключать GPU↔Software во время работы не умеет**: режим
выбирается один раз при старте для всего процесса (`Win32GlManager.InitializeCore`, один глобальный `Compositor`), при
потере устройства она только пересоздаёт GPU-контекст на том же пути, без перехода на Software [КОД]. Но внутри
нашей операции отрисовки можно рисовать CPU-поверхностями `SKSurface.Create(info)` и класть результат на GPU-холст —
Map уже так делает для детальных тайлов [КОД]. Поэтому для Map правильный «запасной блок» — это **уровень ассистента
`ReserveLevel {Off, Assist, Heavy, Software}`**, который (а) делит общие с регулятором качества датчики (GPU-таймер,
интервалы кадров, очередь таймера, VRAM-бюджет, смены GR-контекста), (б) в режимах Assist/Heavy переносит растр
сцены на CPU-полосы общего планировщика, снижает разрешение и загрузку текстур во время жеста и удлиняет
переиспользование кадра, а (в) режим Software включает только при следующем старте (сохранённое решение, как
gfx-prefs/счётчик сбоев в браузерах) — с CPU-версией кэша кадров камеры, которого сейчас в Software нет.

---

## 2. Источники

| # | Ссылка | Лицензия | Что показывает | Статус |
|---|---|---|---|---|
| A1 | [Avalonia `Win32GlManager.cs` L34-L77](https://github.com/AvaloniaUI/Avalonia/blob/e33eaed9c106846b200680751022385d9cc5dc6f/src/Windows/Avalonia.Win32/Win32GlManager.cs#L34-L77) | MIT | Режим отрисовки выбирается перебором списка при старте; `Software` → `null` (нет GPU) | КОД |
| A2 | [`Win32PlatformOptions.cs` L110-L121, L170-L178](https://github.com/AvaloniaUI/Avalonia/blob/e33eaed9c106846b200680751022385d9cc5dc6f/src/Windows/Avalonia.Win32/Win32PlatformOptions.cs#L110-L121) | MIT | По умолчанию `[AngleEgl, Software]`; `GraphicsAdapterSelectionCallback` выбирает адаптер (только D3D11 ANGLE) | КОД |
| A3 | [`Win32Platform.cs` L115-L137](https://github.com/AvaloniaUI/Avalonia/blob/e33eaed9c106846b200680751022385d9cc5dc6f/src/Windows/Avalonia.Win32/Win32Platform.cs#L115-L137) | MIT | Один `s_compositor = new Compositor(platformGraphics)` на процесс | КОД |
| A4 | [`PlatformRenderInterfaceContextManager.cs` L28-L60](https://github.com/AvaloniaUI/Avalonia/blob/e33eaed9c106846b200680751022385d9cc5dc6f/src/Avalonia.Base/Rendering/PlatformRenderInterfaceContextManager.cs#L28-L60) | MIT | При `IsLost` контекст выбрасывается и создаётся заново через тот же `_graphics` | КОД |
| A5 | [`D3D11AngleWin32PlatformGraphics.cs` L27-L35](https://github.com/AvaloniaUI/Avalonia/blob/e33eaed9c106846b200680751022385d9cc5dc6f/src/Windows/Avalonia.Win32/OpenGl/Angle/D3D11AngleWin32PlatformGraphics.cs#L27-L35) | MIT | Новый контекст = новый D3D11-дисплей (новое устройство) | КОД |
| A6 | [`AngleWin32EglDisplay.cs` L53-L200](https://github.com/AvaloniaUI/Avalonia/blob/e33eaed9c106846b200680751022385d9cc5dc6f/src/Windows/Avalonia.Win32/OpenGl/Angle/AngleWin32EglDisplay.cs#L53-L200) | MIT | Перечисление адаптеров, `D3D11CreateDevice`, проверка потери через `DeviceRemovedReason != 0` (L169-L171), доступ к `ID3D11Device` через EGL (L195-L200) | КОД |
| A7 | [`EglContext.cs` L90-L133](https://github.com/AvaloniaUI/Avalonia/blob/e33eaed9c106846b200680751022385d9cc5dc6f/src/Avalonia.OpenGL/Egl/EglContext.cs#L90-L133), [`EglDisplay.cs` L166-L176](https://github.com/AvaloniaUI/Avalonia/blob/e33eaed9c106846b200680751022385d9cc5dc6f/src/Avalonia.OpenGL/Egl/EglDisplay.cs#L166-L176) | MIT | `EGL_CONTEXT_LOST` → `PlatformGraphicsContextLostException` | КОД |
| A8 | [`ServerCompositor.cs` L251-L277](https://github.com/AvaloniaUI/Avalonia/blob/e33eaed9c106846b200680751022385d9cc5dc6f/src/Avalonia.Base/Rendering/Composition/Server/ServerCompositor.cs#L251-L277), [`ServerCompositor.UserApis.cs` L23-L31](https://github.com/AvaloniaUI/Avalonia/blob/e33eaed9c106846b200680751022385d9cc5dc6f/src/Avalonia.Base/Rendering/Composition/Server/ServerCompositor.UserApis.cs#L23-L31) | MIT | Каждый тик `EnsureValidBackendContext()`; фильтр исключения всегда `false` → ошибка уходит в лог цикла отрисовки | КОД |
| A9 | [`ServerCompositionTarget.cs` L151-L180](https://github.com/AvaloniaUI/Avalonia/blob/e33eaed9c106846b200680751022385d9cc5dc6f/src/Avalonia.Base/Rendering/Composition/Server/ServerCompositionTarget.cs#L151-L180) | MIT | Испорченная цель отрисовки пересоздаётся | КОД |
| A10 | [`DrawingContextImpl.cs` L105-L175](https://github.com/AvaloniaUI/Avalonia/blob/e33eaed9c106846b200680751022385d9cc5dc6f/src/Skia/Avalonia.Skia/DrawingContextImpl.cs#L105-L175) | MIT | `ISkiaSharpApiLease`: `GrContext` (null в Software), `SkSurface`, `TryLeasePlatformGraphicsApi` | КОД |
| A11 | [`DxgiConnection.cs` L91-L123](https://github.com/AvaloniaUI/Avalonia/blob/e33eaed9c106846b200680751022385d9cc5dc6f/src/Windows/Avalonia.Win32/DirectX/DxgiConnection.cs#L91-L123), [`WinUiCompositorConnection.cs` L162-L187](https://github.com/AvaloniaUI/Avalonia/blob/e33eaed9c106846b200680751022385d9cc5dc6f/src/Windows/Avalonia.Win32/WinRT/Composition/WinUiCompositorConnection.cs#L162-L187) | MIT | Таймер кадров: `WaitForVBlank` (LowLatency) или `RequestCommitAsync` (WinUI, с тайм-аутом после потери устройства) — внутренние, подменить нельзя | КОД |
| C1 | [Chromium `gpu_data_manager_impl_private.cc` L474-L530, L1634-L1662](https://github.com/chromium/chromium/blob/b124f80df73fe467ac48846536e2542acfc3be20/content/browser/gpu/gpu_data_manager_impl_private.cc#L474-L530) | BSD-3 | Стек режимов и спуск по нему; пустой стек → намеренный крах | КОД |
| C2 | [`gpu_process_host.cc` L182-L195, L741-L767, L1454-L1522](https://github.com/chromium/chromium/blob/b124f80df73fe467ac48846536e2542acfc3be20/content/browser/gpu/gpu_process_host.cc#L741-L767) | BSD-3 | Счётчик падений, прощение 1 падения за 5 мин (10 мин в режиме композитора), лимит 3 на десктопе | КОД |
| C3 | [`gpu_data_manager_impl_private.cc` L1121-L1158](https://github.com/chromium/chromium/blob/b124f80df73fe467ac48846536e2542acfc3be20/content/browser/gpu/gpu_data_manager_impl_private.cc#L1121-L1158) | BSD-3 | Отбрасывание режимов, которые GPU-процесс уже признал неподдержанными | КОД |
| C4 | [`compositor_util.cc` L49-L50, L402-L435](https://github.com/chromium/chromium/blob/b124f80df73fe467ac48846536e2542acfc3be20/content/browser/gpu/compositor_util.cc#L402-L435) | BSD-3 | Потоков растра = ядра/2, в пределах 1…4 | КОД |
| C5 | [`cc/tiles/tile_priority.h` L37, L68-L91](https://github.com/chromium/chromium/blob/b124f80df73fe467ac48846536e2542acfc3be20/cc/tiles/tile_priority.h#L37) | BSD-3 | Корзины NOW/SOON/EVENTUALLY, политика памяти, `SMOOTHNESS_TAKES_PRIORITY` | КОД |
| C6 | [`cc/trees/layer_tree_settings.h` L63-L65, L102, L122, L127](https://github.com/chromium/chromium/blob/b124f80df73fe467ac48846536e2542acfc3be20/cc/trees/layer_tree_settings.h#L63-L127) | BSD-3 | Прогноз «skewport»: 1 с для CPU-растра, 0,2 с для GPU-растра; 32 MiB на staging-загрузки; пре-растр 1000 px | КОД |
| C7 | [`gpu/config/software_rendering_list.json` L323-L333](https://github.com/chromium/chromium/blob/b124f80df73fe467ac48846536e2542acfc3be20/gpu/config/software_rendering_list.json#L323-L333) | BSD-3 | Пример блок-листа: VMware (0x15ad) на Windows → всё без GPU | КОД |
| F1 | [Firefox `gfxPlatform.cpp` L3911-L3981](https://github.com/mozilla/gecko-dev/blob/5836a062726f715fda621338a17b51aff30d0a8c/gfx/thebes/gfxPlatform.cpp#L3911-L3981) | MPL-2.0 | Спуск WR → SW-WR + D3D11-композиция → SW-WR | КОД |
| F2 | [`GPUProcessManager.cpp` L614-L637, L698-L735, L835-L866](https://github.com/mozilla/gecko-dev/blob/5836a062726f715fda621338a17b51aff30d0a8c/gfx/ipc/GPUProcessManager.cpp#L698-L735) | MPL-2.0 | Стабильный процесс сначала перезапускается, а не понижается; порог «слишком много сбросов» | КОД |
| F3 | [`DeviceManagerDx.cpp` L733-L743, L790-L795, L916-L967, L1214-L1231](https://github.com/mozilla/gecko-dev/blob/5836a062726f715fda621338a17b51aff30d0a8c/gfx/thebes/DeviceManagerDx.cpp#L1214-L1231) | MPL-2.0 | WARP-устройство, VendorId 0x1414, причины сброса HUNG/REMOVED/RESET/… | КОД |
| F4 | [`StaticPrefList.yaml` L6928-L6936, L7703-L7771, L7904-L7913, L9409-L9412](https://github.com/mozilla/gecko-dev/blob/5836a062726f715fda621338a17b51aff30d0a8c/modules/libpref/init/StaticPrefList.yaml#L6928-L6936) | MPL-2.0 | Лимит сбросов 10, программный WR как запасной включён, 6 перезапусков GPU-процесса | КОД |
| F5 | [`gfxConfigManager.cpp` L189-L205](https://github.com/mozilla/gecko-dev/blob/5836a062726f715fda621338a17b51aff30d0a8c/gfx/config/gfxConfigManager.cpp#L189-L205) | MPL-2.0 | Блок-лист и «нет аппаратной композиции» отключают WR при старте | КОД |
| U1 | [Unity `AdaptivePerformanceBottleneckDetection.cs` L9-L95](https://github.com/Unity-Technologies/UnityCsReference/blob/cf583fbe6da6b9fb11e3a1ac03f23df4eb120e9b/Modules/AdaptivePerformance/Core/AdaptivePerformanceBottleneckDetection.cs#L9-L95) | Unity Reference-Only (**переносить нельзя**) | Классификатор узкого места с порогами, зависящими от прошлого состояния | КОД |
| U2 | [Unity Adaptive Performance user guide](https://docs.unity3d.com/Packages/com.unity.adaptiveperformance@5.1/manual/user-guide.html), [Scalers reference](https://docs.unity.com/en-us/engine/6000.3/manual/analysis/runtime-performance-scaling/adaptive-performance/performance-metrics/scalers/reference) | документация | Indexer/Scalers: цель по узкому месту, «визуальная цена», уровни | АВТОРЫ |
| E1 | [Unreal Dynamic Resolution](https://dev.epicgames.com/documentation/en-us/unreal-engine/dynamic-resolution-in-unreal-engine), [TargetedGPUHeadRoomPercentage=10](https://unrealdirective.com/resources/console-variables/r-dynamicres-targetedgpuheadroompercentage/), [ChangePercentageThreshold=2](https://unrealdirective.com/resources/console-variables/r-dynamicres-changepercentagethreshold/) | документация | Бюджет, запас, порог, период, история, «паника» | АВТОРЫ (исходник UE закрыт) |
| M1 | [MapLibre `map.ts` L4354-L4392](https://github.com/maplibre/maplibre-gl-js/blob/04359f204e3e73ff0de36a30bba840a7091c5193/src/ui/map.ts#L4354-L4392); там же `failIfMajorPerformanceCaveat:false` по умолчанию | BSD-3 | При потере WebGL — снимок стиля, уничтожение, восстановление; программного запасного рендера нет | КОД |
| W1 | [DXGI overview — Enumerating adapters](https://learn.microsoft.com/windows/win32/direct3ddxgi/d3d10-graphics-programming-guide-dxgi#enumerating-adapters), [WARP guide](https://learn.microsoft.com/windows/win32/direct3darticles/directx-warp) | документация | «Microsoft Basic Render Driver» VendorId 0x1414 / DeviceId 0x8c, флаг SOFTWARE; есть в `EnumAdapters1` | АВТОРЫ |
| W2 | [`IDXGIAdapter3::QueryVideoMemoryInfo`](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/nf-dxgi1_4-idxgiadapter3-queryvideomemoryinfo), [Residency](https://learn.microsoft.com/windows/win32/direct3d12/residency), `RegisterVideoMemoryBudgetChangeNotificationEvent` | документация | Бюджет VRAM процесса и событие его смены; превышение → заморозки | АВТОРЫ |
| W3 | [`DWM_TIMING_INFO`](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ns-dwmapi-dwm_timing_info), [`DCompositionWaitForCompositorClock`](https://learn.microsoft.com/en-us/windows/win32/api/dcomp/nf-dcomp-dcompositionwaitforcompositorclock) (Win11 22000+) | документация | Период обновления, QPC вертикального гашения, счётчики пропусков; ожидание такта композитора | АВТОРЫ |

Не открывались полностью: исходник Unreal (закрыт), статьи Mozilla о SWGL, исходники SwiftShader/ANGLE-WARP. Выводы о них
не делаю.

---

## 3. Как устроены лучшие механизмы

### 3.1 Chromium: стек режимов + «протекающий» счётчик падений [КОД]

- При старте собирается стек: внизу `DISPLAY_COMPOSITOR` (программная композиция), выше `SOFTWARE_GL`
  (SwiftShader), сверху аппаратные режимы; берётся верхний ([C1 L474-L530](https://github.com/chromium/chromium/blob/b124f80df73fe467ac48846536e2542acfc3be20/content/browser/gpu/gpu_data_manager_impl_private.cc#L474-L530)).
- `FallBackToNextGpuMode()` снимает верх стека; пустой стек → намеренный крах браузера
  ([C1 L1634-L1647](https://github.com/chromium/chromium/blob/b124f80df73fe467ac48846536e2542acfc3be20/content/browser/gpu/gpu_data_manager_impl_private.cc#L1634-L1647)).
  **Обратного подъёма в сессии нет** — только при новом запуске.
- Причины спуска: GPU-процесс не инициализировался ([C2 L1086-L1093](https://github.com/chromium/chromium/blob/b124f80df73fe467ac48846536e2542acfc3be20/content/browser/gpu/gpu_process_host.cc#L1086-L1093));
  ≥ 3 падения ([C2 L1454-L1477, L1510-L1522](https://github.com/chromium/chromium/blob/b124f80df73fe467ac48846536e2542acfc3be20/content/browser/gpu/gpu_process_host.cc#L1454-L1522));
  режим признан неподдержанным самим GPU-процессом ([C3](https://github.com/chromium/chromium/blob/b124f80df73fe467ac48846536e2542acfc3be20/content/browser/gpu/gpu_data_manager_impl_private.cc#L1121-L1158)).
- Счётчик падений «протекает»: одно падение прощается за каждые 5 мин (10 мин в режиме композитора), сброс при
  смене режима ([C2 L182-L195, L741-L767](https://github.com/chromium/chromium/blob/b124f80df73fe467ac48846536e2542acfc3be20/content/browser/gpu/gpu_process_host.cc#L741-L767)).
  Это и есть гистерезис для редких событий.
- Растр на CPU: потоков = ядра/2, 1…4 ([C4](https://github.com/chromium/chromium/blob/b124f80df73fe467ac48846536e2542acfc3be20/content/browser/gpu/compositor_util.cc#L402-L435));
  приоритеты тайлов NOW/SOON/EVENTUALLY и режим «плавность важнее нового» ([C5](https://github.com/chromium/chromium/blob/b124f80df73fe467ac48846536e2542acfc3be20/cc/tiles/tile_priority.h#L37-L91));
  при CPU-растре прогноз движения вида на 1 с вперёд, при GPU-растре — 0,2 с; staging-загрузки ≤ 32 MiB
  ([C6](https://github.com/chromium/chromium/blob/b124f80df73fe467ac48846536e2542acfc3be20/cc/trees/layer_tree_settings.h#L63-L127)).
  Вывод для Map: если растр уходит на CPU, предрастр вокруг вида надо делать **дальше**, потому что CPU медленнее.

### 3.2 Firefox: промежуточная ступень «CPU рисует, GPU складывает» [КОД]

`FallbackFromAcceleration` ([F1](https://github.com/mozilla/gecko-dev/blob/5836a062726f715fda621338a17b51aff30d0a8c/gfx/thebes/gfxPlatform.cpp#L3911-L3981)):
1) выключить аппаратный WebRender; 2) если D3D11-композиция ещё жива — включить **Software WebRender + D3D11**
(растр SWGL на CPU, композиция на GPU) и вернуть «нужен перезапуск композиторов»; 3) при следующем сбое — убрать и
D3D11, остаётся чистый SWGL. Перед понижением стабильный GPU-процесс просто перезапускают или имитируют сброс
устройства ([F2 L698-L735](https://github.com/mozilla/gecko-dev/blob/5836a062726f715fda621338a17b51aff30d0a8c/gfx/ipc/GPUProcessManager.cpp#L698-L735)):
единичный сбой считается «плохим состоянием драйвера», а не слабой машиной. Порог сбросов — `gfx.device-reset.limit=10`
([F4](https://github.com/mozilla/gecko-dev/blob/5836a062726f715fda621338a17b51aff30d0a8c/modules/libpref/init/StaticPrefList.yaml#L6928-L6936)).
Причины сброса из `GetDeviceRemovedReason` разделены на «виноваты мы» (HUNG/RESET/INVALID_CALL) и прочие
([F2 L835-L866](https://github.com/mozilla/gecko-dev/blob/5836a062726f715fda621338a17b51aff30d0a8c/gfx/ipc/GPUProcessManager.cpp#L835-L866),
[F3 L1214-L1231](https://github.com/mozilla/gecko-dev/blob/5836a062726f715fda621338a17b51aff30d0a8c/gfx/thebes/DeviceManagerDx.cpp#L1214-L1231)).
WARP распознаётся по VendorId 0x1414 ([F3 L733-L743](https://github.com/mozilla/gecko-dev/blob/5836a062726f715fda621338a17b51aff30d0a8c/gfx/thebes/DeviceManagerDx.cpp#L733-L743)).

**Для Map ступень 2 (CPU-растр + GPU-композиция) — самое ценное**: у Map она уже частично есть (детальные тайлы на
CPU), её можно расширить до всей сцены во время жеста, не трогая Avalonia.

### 3.3 Unity: классификатор узкого места с гистерезисом порогов [КОД, переносить нельзя]

`DetermineBottleneck` ([U1](https://github.com/Unity-Technologies/UnityCsReference/blob/cf583fbe6da6b9fb11e3a1ac03f23df4eb120e9b/Modules/AdaptivePerformance/Core/AdaptivePerformanceBottleneckDetection.cs#L9-L95))
работает по средним за окно: «упор в частоту», если кадр ≤ цели или превышение < 2 % (3 %, если уже были в этом
состоянии); GPU-упор при загрузке GPU > 0,92 (0,90 при удержании) и GPU·0,90 > CPU; CPU-упор при CPU > 0,90 (0,87). У
каждого порога два значения: вход строже выхода. Замечание: в L40 сравнивается `averageGpuFrametime > 0.9` (миллисекунды
с долей) — по виду ошибка авторов; идею берём, код нет (лицензия Reference-Only). Решение принимается, только когда
окно заполнено ([AdaptivePerformanceManager.cs ~L495](https://github.com/Unity-Technologies/UnityCsReference/blob/cf583fbe6da6b9fb11e3a1ac03f23df4eb120e9b/Modules/AdaptivePerformance/Core/AdaptivePerformanceManager.cs)).
Масштабаторы: у каждого цель (CPU / GPU / заполнение), «визуальная цена», число уровней [АВТОРЫ, U2].

### 3.4 Unreal: регулятор с запасом и «паникой» [АВТОРЫ]

Бюджет кадра, запас 10 %, порог изменения 2 %, минимальный период между изменениями, история кадров, отдельный
процент для CPU-упора и сброс истории после N подряд просроченных GPU-кадров ([E1](https://dev.epicgames.com/documentation/en-us/unreal-engine/dynamic-resolution-in-unreal-engine)).
Прямо сказано: динамическое разрешение **не лечит CPU-упор**.

### 3.5 Avalonia 12.1.1: что запасной блок может внутри программы [КОД]

| Вопрос | Ответ |
|---|---|
| GPU→Software во время работы? | **Нет.** `Win32GlManager.Initialize` вызывается один раз ([A3 L129](https://github.com/AvaloniaUI/Avalonia/blob/e33eaed9c106846b200680751022385d9cc5dc6f/src/Windows/Avalonia.Win32/Win32Platform.cs#L115-L137)); результат кладётся константой в `AvaloniaLocator` и в единственный `Compositor`. |
| По окнам / TopLevel? | **Нет**: один compositor на процесс (A3 L137). |
| Как выбирается Software? | Перебор `RenderingMode`; `Software` → `IPlatformGraphics = null` ([A1 L44-L47](https://github.com/AvaloniaUI/Avalonia/blob/e33eaed9c106846b200680751022385d9cc5dc6f/src/Windows/Avalonia.Win32/Win32GlManager.cs#L42-L47)). По умолчанию `[AngleEgl, Software]` — если ANGLE не поднялся при старте, Avalonia **уже сама** стартует в Software ([A2 L118-L121](https://github.com/AvaloniaUI/Avalonia/blob/e33eaed9c106846b200680751022385d9cc5dc6f/src/Windows/Avalonia.Win32/Win32PlatformOptions.cs#L110-L121)). Map (Program.cs:71-74) создаёт `Win32PlatformOptions` только с `CompositionMode`, значит этот запасной путь сохранён. |
| WARP вместо GPU? | Возможно при старте через `GraphicsAdapterSelectionCallback` ([A6 L70-L122](https://github.com/AvaloniaUI/Avalonia/blob/e33eaed9c106846b200680751022385d9cc5dc6f/src/Windows/Avalonia.Win32/OpenGl/Angle/AngleWin32EglDisplay.cs#L70-L122)): колбэк получает описание (строчными) и LUID, «microsoft basic render driver» есть в `EnumAdapters1` [W1, АВТОРЫ]. Быстрее ли WARP, чем CPU Skia, для Map — [НЕ ПРОВЕРЕНО]. |
| Потеря устройства? | Проверка `DeviceRemovedReason != 0` ([A6 L169-L171](https://github.com/AvaloniaUI/Avalonia/blob/e33eaed9c106846b200680751022385d9cc5dc6f/src/Windows/Avalonia.Win32/OpenGl/Angle/AngleWin32EglDisplay.cs#L169-L171)) или `EGL_CONTEXT_LOST` ([A7](https://github.com/AvaloniaUI/Avalonia/blob/e33eaed9c106846b200680751022385d9cc5dc6f/src/Avalonia.OpenGL/Egl/EglContext.cs#L90-L133)) → на следующем тике `EnsureValidBackendContext` удаляет контекст и создаёт новый D3D11-дисплей ([A4](https://github.com/AvaloniaUI/Avalonia/blob/e33eaed9c106846b200680751022385d9cc5dc6f/src/Avalonia.Base/Rendering/PlatformRenderInterfaceContextManager.cs#L28-L60), [A5](https://github.com/AvaloniaUI/Avalonia/blob/e33eaed9c106846b200680751022385d9cc5dc6f/src/Windows/Avalonia.Win32/OpenGl/Angle/D3D11AngleWin32PlatformGraphics.cs#L27-L35)); исключение тика только логируется ([A8](https://github.com/AvaloniaUI/Avalonia/blob/e33eaed9c106846b200680751022385d9cc5dc6f/src/Avalonia.Base/Rendering/Composition/Server/ServerCompositor.cs#L251-L277)). Если устройство не создаётся снова — окно просто перестаёт обновляться; перехода на Software нет. Живьём на ПК владельца — [НЕ ПРОВЕРЕНО]. |
| Своя операция рисует через CPU `SKSurface` и кладёт на GPU? | **Да.** Map уже делает это (SceneRenderer.DetailRaster.cs:128, :157 — `SKSurface.Create(new SKImageInfo(514, 514))` на рабочих потоках). В Software `lease.GrContext == null`, `lease.SkSurface` — растровая поверхность кадра ([A10](https://github.com/AvaloniaUI/Avalonia/blob/e33eaed9c106846b200680751022385d9cc5dc6f/src/Skia/Avalonia.Skia/DrawingContextImpl.cs#L105-L175)). |
| Можно ли сдвинуть начало кадра к vblank? | Таймер кадра внутренний: WinUI — `RequestCommitAsync`, LowLatency — `WaitForVBlank` ([A11](https://github.com/AvaloniaUI/Avalonia/blob/e33eaed9c106846b200680751022385d9cc5dc6f/src/Windows/Avalonia.Win32/DirectX/DxgiConnection.cs#L91-L123)). Подменить можно только выбором `CompositionMode` при старте или своим `CustomPlatformGraphics` (только с `RedirectionSurface`, A3 L116-L125) — второе дорого. `DCompositionWaitForCompositorClock` в своей операции отрисовки ничего не даст: она уже внутри тика. |
| Достать `ID3D11Device`/`IDXGIAdapter3` для VRAM-бюджета? | Avalonia сама берёт устройство через EGL (`eglQueryDisplayAttribEXT(EGL_DEVICE_EXT)` + `EGL_D3D11_DEVICE_ANGLE`, [A6 L195-L200](https://github.com/AvaloniaUI/Avalonia/blob/e33eaed9c106846b200680751022385d9cc5dc6f/src/Windows/Avalonia.Win32/OpenGl/Angle/AngleWin32EglDisplay.cs#L195-L200)); Map может повторить это из `TryLeasePlatformGraphicsApi()` (как GpuFrameTimer берёт `glGetString`). Работоспособность из кода Map — [НЕ ПРОВЕРЕНО]. Проще и без COM: свой `CreateDXGIFactory1` + сравнение LUID. |

### 3.6 Что уже есть в Map и мешает/помогает (код Map, [КОД])

- Программный путь сцены уже существует (экспорт): `RenderImageCache.CreateSurface` берёт CPU-поверхность при
  `context == null` (`src/Map.Rendering/RenderImageCache.cs:385-393`), но в формате **RgbaF16** — на CPU это,
  скорее всего, медленный путь растра [НЕ ПРОВЕРЕНО, мерить].
- В Software (или если `GrContext == null`) сейчас **выключаются**: кэш кадров камеры (`MapCanvasControl.cs:894`,
  вход `GpuSurface`), уточнение рельефа (`MapCanvasControl.cs:981`), плотный атлас штампов
  (`SceneRenderer.cs:4547`), атласная пачка с уменьшением (`SceneRenderer.cs:4093-4094`). То есть без GPU Map
  теряет как раз механизмы плавного движения.
- CPU-растр тайлов: `DetailRasterWorkers = Clamp(ProcessorCount − 2, 1, 8)` (`SceneRenderer.DetailRaster.cs:62`),
  очередь до `2×` рабочих (`:244`). Отдельно `BackgroundWork.Concurrency = ProcessorCount − 2`
  (`src/Map.Core/Resources/BackgroundWork.cs:17`). На 4 ядрах это 2 + 2 фоновых потока + UI + render — уже
  на грани; Chromium берёт ядра/2 ≤ 4 (C4).
- Датчик GPU: `GpuFrameTimer` (асинхронные запросы, кольцо 6; «кольцо полно = GPU отстаёт на несколько кадров»,
  `GpuFrameTimer.cs:74`; disjoint-события отбрасываются, `:140-142`). Это готовый датчик **глубины очереди**.
- Гистерезис уже принят в памяти: давление кэшей 50 % ± 5 % (`WorkingMemoryBudget.Caches.cs:76-79`), рост бюджета
  с шагом раз в 5 с и повторный рост не раньше 60 с после падения (`:106-121`). Тот же стиль нужен для GPU.

---

## 4. Сигналы и пороги на Windows (с гистерезисом)

Окно решений: 120 кадров или 2 с (что дольше); решения — не чаще одного раза за 1 с; любой выход — на одну ступень.
`T` — период обновления монитора (из `DwmGetCompositionTimingInfo(NULL).qpcRefreshPeriod`, на 60 Гц 16,7 мс).

| Сигнал | Откуда | Надёжность | Вход | Выход |
|---|---|---|---|---|
| Нет GPU: `GrContext == null` в первом кадре | `lease.GrContext` (`MapCanvasControl.cs:831-838`), `LastFrameHadGpuContext` | высокая | сразу `Software` | только новый запуск |
| WARP / «Basic Render» | DXGI: VendorId 0x1414, DeviceId 0x8c или флаг SOFTWARE [W1]; описание адаптера в колбэке Avalonia | высокая [АВТОРЫ] | `Software`-класс | новый запуск |
| VM / удалённый стол | VendorId 0x15AD (VMware, ср. C7), VirtualBox и т. п.; `GetSystemMetrics(SM_REMOTESESSION)` | средняя [НЕ ПРОВЕРЕНО по списку ID] | `Weak` | новый запуск / конец RDP |
| Слабая графика при старте | интегрированная (DedicatedVideoMemory мала, есть SharedSystemMemory), + самопроверка: p95 GPU-таймера на первых 120 кадрах стандартного вида > 0,5·T | средняя | `Assist` по умолчанию в жестах | p95 < 0,35·T на 3 подряд окнах |
| GPU не успевает | GPU-таймер p95 > 0,85·T **и** доля интервалов > 1,5·T больше 10 %, при CPU-работе кадра < 0,5·T (Unity-логика: GPU-упор, а не CPU) | высокая на HD 630 (таймер есть) | `Assist` | p95 < 0,60·T и пропуски < 3 % в течение 5 с |
| «Паника» | 3 подряд кадра с GPU > 2·T или кольцо таймера полно 3 кадра подряд (`GpuFrameTimer.cs:74`) | высокая | сразу +1 ступень, сброс истории (как Unreal) | обычные правила выхода |
| Очередь растёт | рост `Dropped` таймера (кольцо полно) | высокая | как «паника» | — |
| VRAM-бюджет | `IDXGIAdapter3::QueryVideoMemoryInfo` (Local): `CurrentUsage > 0,90·Budget`; событие `RegisterVideoMemoryBudgetChangeNotificationEvent` [W2] | средняя; на iGPU «local» — доля общей ОЗУ [НЕ ПРОВЕРЕНО] | ужать GR-кэш 256→128 MiB (`MapCanvasControl.cs:204, 836-837`), кэш кадров 128→64 MiB | `< 0,75·Budget` 10 с |
| Потеря устройства | смена `lease.GrContext.Handle`; лог Avalonia; `DeviceRemovedReason` | высокая | 1-я: `Assist` до конца сессии; 3 за 10 мин (одна прощается каждые 5 мин, как Chromium C2): флаг «следующий старт — Software/WARP» | флаг снимается после 3 чистых запусков с самопроверкой |
| Тепло/батарея | `GetSystemPowerStatus` (ACLineStatus); частота | низкая | только снижает пороги входа на 10 % | — |
| Пропуски DWM | `DWM_TIMING_INFO.cFramesMissed/cFramesDropped` [W3] | **низкая в режиме WinUIComposition** — счётчики про DX-present окна [НЕ ПРОВЕРЕНО] | только журнал | — |

Анти-дребезг: (1) пороги входа строже выхода (Unity U1); (2) минимальное время в ступени 3 с; (3) повторный вход в
ту же ступень после выхода — с удвоением задержки 5 → 10 → 20 → 60 с (как `CacheBudgetRegrowDelay`);
(4) решения только по заполненному окну; (5) один регулятор меняет одну ручку за период (см. §6.3);
(6) жест камеры и покой меряются раздельно — в покое кадров мало, их не смешивать с жестом.

---

## 5. Что делает запасной блок, по эффект/цена для Map

| Ранг | Действие | Эффект (оценка) | Цена | Где в Map |
|---|---|---|---|---|
| 1 | **Ход кадра по vblank**: на слабой машине выбирать при старте `LowLatencyDxgiSwapChain` (тик сразу после `WaitForVBlank`, A11) вместо WinUI-коммита | возможно главный рычаг при 47 % просроченных интервалов при кадрах ~11 мс [НЕ ПРОВЕРЕНО, нужен A/B PresentMon] | очень низкая: переключатель уже есть как диагностика (`Program.cs:68-74`); минус — нет прозрачности окна, нужен FL 11_3 (HD 630 поддерживает) | Program.cs `BuildAvaloniaApp` |
| 2 | **Меньше загрузок текстур в жесте**: бюджеты приёма mip/обзора ниже при GPU-упоре | прямо срезает пики p99 | низкая — ручки уже есть (`ObjectOverviewCache.cs:126-154`, приём mip 2 MiB в движении) | через FrameBudget |
| 3 | **Дольше держать кадр камеры**: разрешить увеличение > 2× и сдвиг кадра (репроекция 2D — это просто матрица) пока GPU-упор; добавить CPU-версию кэша для Software | убирает полные перерисовки во время жеста | низкая/средняя: `CameraFrameCache` уже снапшотит GPU-кадр; для CPU — `SKSurface.Snapshot` растровой поверхности | `CameraFrameCache.cs`, `MapCanvasControl.cs:892-924` |
| 4 | **Сцена в жесте в пониженном разрешении** (0,75 → 0,5 по стороне, шаг ≥ 10 %): рисовать в offscreen GPU-поверхность и растягивать | режет заполнение в 1,8–4 раза, если упор в заливку [НЕ ПРОВЕРЕНО — надо разделить GPU-время на заливку/загрузки] | средняя; точный кадр после остановки — без изменений (правило «после остановки побайтно точно») | новая ветка в `SceneDrawOperation.RenderFrame` |
| 5 | **Простые эффекты**: свет, стиль-шейдеры, тени — ручки регулятора качества (не запасного блока) | средний | низкая | QualityGovernor |
| 6 | **CPU-растр сцены тайлами + GPU-композиция** (ступень Firefox SW-WR + D3D11): в `Heavy` весь видимый слой объектов/суши растрится на CPU-полосах тайлами 512 px (как сейчас детальные тайлы), GPU только кладёт квады | сильный при GPU-упоре, **вреден** при CPU-упоре | средняя/высокая: загрузки текстур растут (на iGPU это копия в общей памяти); поэтому только вместе с ранг 2 и прогнозом вида на ~1 с (C6) | `SceneRenderer.DetailRaster.cs`, общий пул полос |
| 7 | **Полный Software** (нет GPU): Avalonia в Software, Map включает CPU-кэш кадров, тайловый многопоточный растр, формат 8888 вместо F16 в жесте | делает программу пригодной без GPU | высокая; F16 vs 8888 и расхождение CPU/GPU-сглаживания требуют решения владельца | Program.cs + RenderOptions |

Сколько ядер под CPU-растр: **одна общая «полоса» на всё фоновое**, размер `Clamp(ядра/2, 1, 4)` (как Chromium C4)
для `Assist`, `Clamp(ядра − 2, 1, 6)` только в `Heavy`/`Software`, и всегда ≥ 2 логических ядер UI+render. При
CPU-упоре (CPU-работа кадра > 0,5·T) полосы урезаются и тайлы возвращаются GPU — это «отдаёт работу обратно».

---

## 6. Архитектура для Map — в связке с ядром DEC-111

### 6.1 Компоненты

1. **`GraphicsHealth`** (Map.Platform.Windows / Map.App, только Windows). Старт: перечисление DXGI-адаптеров (свой
   `CreateDXGIFactory1`, только чтение), флаги, VendorId, память; `SM_REMOTESESSION`; файл
   `Profile/Diagnostics/graphics-health.json` (счётчик потерь с «протеканием», результат самопроверки, решение на
   следующий старт). Выдаёт `GraphicsClass {Full, Weak, Absent}` и, если нужно, параметры для
   `Win32PlatformOptions` (RenderingMode, CompositionMode, колбэк адаптера).
2. **`GpuLoadSensor`** (Map.Rendering, без UI). Один на окно; кормится из `SceneDrawOperation`: GPU-таймер,
   интервал кадра, CPU-работа кадра, `Dropped`, смена `GrContext.Handle`, VRAM-проба раз в 1 с с фонового потока.
   Выдаёт `GpuLoad {Ok, Tight, Exhausted, Lost}` и `Bottleneck {Cpu, Gpu, Pacing, Unknown}`. **Это тот же датчик,
   что нужен регулятору качества** — один объект, два читателя, не дублировать.
3. **`ReserveController`** — конечный автомат уровней (§6.2), чистый C# с `TimeProvider` (как `WorkingMemoryBudget`).
4. **Стратегии** — не новый рендерер, а флаги в `RenderOptions` и параметры существующих частей:
   `Reserve.Level`, `GestureResolutionScale`, `CameraReuseMaxMagnification`, `CpuSceneTiles`,
   `UploadBudgetScale`, `GpuCacheBytes`.
5. **Журнал**: в `PerformanceFrame` — уровень, причина перехода, Bottleneck (DEC-111 «журнал цены функций»).

### 6.2 Автомат

```
            старт: GraphicsClass
   Absent ───────────────────────────────► Software (до конца процесса)
   Full/Weak ──► Off ◄──────────────┐
                  │ GPU-упор ≥ окно  │ спокойно 5 с, мин. 3 с в ступени
                  ▼                  │
               Assist ──────────────►┘   (ранги 1-5: загрузки↓, кадр камеры дольше, разрешение жеста↓)
                  │ GPU-упор держится 3 окна при Assist / «паника»
                  ▼
                Heavy ──► (выход только в Assist)   (+ранг 6: CPU-тайлы сцены, полосы ядер↑)
   Lost (смена контекста): любой → Assist на сессию; 3 за 10 мин → флаг «Software/WARP при следующем старте»
   CPU-упор в Assist/Heavy → сначала урезать CPU-полосы, затем ступень вниз
```

### 6.3 Связка с бюджетом кадра, планировщиком, регулятором, памятью

- **Разделение ролей**: регулятор качества решает «что рисовать» (тир, свет, стиль, подписи); запасной блок — «кто и
  как часто рисует» (GPU или CPU, разрешение, переиспользование). Один **арбитр решений**: за период меняется только
  одна ручка; порядок эскалации — сначала дешёвые ручки регулятора (низкая визуальная цена, как Unity Visual Impact),
  потом Assist, потом Heavy; спуск обратно — в обратном порядке. Так два регулятора не раскачивают друг друга.
- **FrameBudget** (план DEC-111 п.1): запасной блок не вводит свой мс-бюджет; он меняет доли (меньше приёма mip/обзора
  при GPU-упоре) и сообщает оценку «GPU-кадр» как уже потраченное время.
- **WorkScheduler**: CPU-тайлы сцены — задачи `VisibleNow` с ключом отмены по виду/ревизии; `DetailRasterWorkers` и
  `BackgroundWork` сливаются в один пул полос с общим лимитом (сейчас их два, см. §3.6). Запасной блок меняет
  только размер пула.
- **WorkingMemoryBudget**: кэш CPU-тайлов и кэш кадров камеры — через `RegisterCache` (кадры камеры сейчас вне
  бюджета, `CameraFrameCache.cs:27`); VRAM — отдельный счёт «GPU» в том же бюджете, источник — `QueryVideoMemoryInfo`.
- **Refinement/точный кадр**: всё, что делает запасной блок, касается только кадров движения; кадр после остановки
  рисуется полным путём, как сейчас (`MapCanvasControl.cs:1000-1008` — захват только из точного кадра).

### 6.4 Точки подключения (файл:строка, HEAD `d81db90`)

| Что | Где |
|---|---|
| Выбор режима Avalonia при старте (Software, WARP-колбэк, LowLatency) | `src/Map.App/Program.cs:62-77` (`BuildAvaloniaApp`; сейчас только `MAP_WIN32_COMPOSITION`, `:71-74`) |
| Датчики кадра: начало/конец GPU-таймера, контекст, предел GR-кэша | `src/Map.App/Controls/MapCanvasControl.cs:831-843`, `:1156-1158`; предел 256 MiB — `:204` |
| Тир кадра (влияние Heavy/Software) | `MapCanvasControl.cs:864-871` |
| Кэш кадров камеры: снять требование GPU, менять предел увеличения | `MapCanvasControl.cs:892-924`; `src/Map.Rendering/CameraFrameCache.cs:27, 38-57, 71-91` |
| Новые поля `RenderOptions` | `MapCanvasControl.cs:925-998` |
| Журнал | `src/Map.App/Controls/MapCanvasControl.PerformanceJournal.cs:27-55` |
| CPU-полосы растра | `src/Map.Rendering/SceneRenderer.DetailRaster.cs:62-87, 113-172, 244`; бюджет 8 мс — `SceneRenderer.cs:617, 3735` |
| Приём/загрузки обзора | `src/Map.Rendering/ObjectOverviewCache.cs:123-154, 267-280` |
| Фоновые слоты, «жест» | `src/Map.Core/Resources/BackgroundWork.cs:14-28` |
| Образец гистерезиса и регистрации кэшей | `src/Map.Core/Resources/WorkingMemoryBudget.Caches.cs:76-121, 191-214` |
| Датчик очереди GPU | `src/Map.App/Controls/GpuFrameTimer.cs:74, 129-145` |
| CPU-формат поверхностей в Software | `src/Map.Rendering/RenderImageCache.cs:385-393` |

### 6.5 Проверка (форсировать «слабо / кончилось / нет»)

- **Модульные** (Map.Rendering.Tests, фальшивые часы): автомат — пила вокруг порога не даёт > 1 перехода за 10 с;
  «паника» за 3 кадра; удвоение задержки повторного входа; протекающий счётчик потерь (3 за 10 мин → флаг; 1 за 6 мин
  прощена); классификатор CPU/GPU с порогами входа/выхода; арбитр — одна ручка за период.
- **Форсирование через переменные среды** (стиль DEC-050, как `MAP_PERF_CAMERA_FRAMES`): `MAP_RESERVE=off|assist|heavy|software`
  (прибить уровень), `MAP_RESERVE_SIMULATE=weak|exhausted|lost` (датчик подмешивает GPU-время ×N, полное кольцо,
  смену контекста — без порчи настоящего GPU), `MAP_RENDERING_MODE=Software` и `MAP_GPU_ADAPTER=warp` (настоящий
  «нет GPU» и WARP через колбэк Avalonia).
- **Настоящая перегрузка GPU**: тестовый «грузчик» — N полноэкранных квадов с дорогим смешиванием в offscreen-поверхность
  в каждом кадре (только в смоук-профиле). Настоящая потеря устройства — `dxcap -forcetdr` (сбрасывает GPU всей машины;
  **только с разрешения владельца**).
- **Смоук** `tools/Map.Performance` на HD 630 с PresentMon: A/B WinUI vs LowLatency; Off vs Assist в зуме колесом;
  метрики — доля интервалов > 16,7 мс, p99/max, худшие кадры (DEC-109); снимки разных моментов жеста (правило
  владельца о живых проверках со снимками, ассеты — горы владельца).
- **Пиксели**: кадр после остановки побайтно равен кадру при `MAP_RESERVE=off` (GPU-путь); для Software — отдельный
  эталон (CPU и GPU Skia сглаживают по-разному).

---

## 7. Риски и открытые вопросы

**Риски**
1. Два регулятора раскачивают друг друга (качество ↔ запасной блок) — нужен один арбитр (§6.3), иначе дребезг.
2. CPU-тайлы на 4 ядрах переводят упор с GPU на CPU → автомат должен уметь «отдать работу обратно»; два пула потоков
   сегодня уже могут занять все ядра.
3. Загрузки текстур от CPU-тайлов съедают выигрыш GPU (на iGPU — копии в общей памяти) [НЕ ПРОВЕРЕНО].
4. Software-режим без CPU-кэша кадров камеры будет дёргаться при любом жесте — без доработки §5 ранг 3 Software хуже,
   чем сейчас кажется.
5. RgbaF16 на CPU медленный, а 8888 даёт другие пиксели полупрозрачных берегов (причина выбора F16 в
   `RenderImageCache.cs:387-388`) — конфликт с правилом точного кадра.
6. Восстановление Avalonia после потери устройства на ПК владельца не проверено; если D3D11 не создаётся снова,
   окно «замёрзнет» — нужен сторож «нет кадров N секунд → сохранить и предложить перезапуск».
7. `LowLatencyDxgiSwapChain` убирает прозрачность/эффекты окна; у RenderDoc на WinUI свои проблемы (Avalonia #15911,
   упомянуто в Program.cs:68-70).
8. VRAM-бюджет на iGPU может не отражать реальное давление; ID виртуальных адаптеров в таблице — не сверены.

**Открытые вопросы (решения владельца)**
1. Можно ли запасному блоку при следующем старте самому включить Software/WARP (и показывать ли об этом сухую строку)?
2. Допустим ли в Software формат 8888 в жесте (пиксели движения не равны точным)?
3. Пробовать ли LowLatency как режим по умолчанию на слабых машинах (сначала A/B PresentMon на HD 630)?
4. Разрешён ли `dxcap -forcetdr` для проверки потери устройства?

**Не проверено в этом исследовании**: ни одного замера (только чтение кода и документации); поведение Avalonia при
потере устройства живьём; скорость WARP и CPU Skia для сцен Map; доступность `IDXGIAdapter3` из кода Map;
исходники Unreal, SwiftShader, SWGL.
