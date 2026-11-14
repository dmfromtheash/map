# frameplan — отчёт Sol

Статус: FACT — чистая логика готова, тематические тесты прошли, полный Rendering совпадает с baseline по падениям. Подключение к canvas намеренно не входит в поручение.

## Карточка этапа

Поручение: «pure FramePlan arbiter + gesture resolution hysteresis (DEC-111, DEC-115)».
Исходная точка: `claude/reserve`, `7b1f32cb118ffc3c25428966a11315503764f771`; стартовое дерево чистое.
Входит: неизменяемый план, единственные владельцы настроек, общий арбитраж, гистерезис, детерминированные headless-тесты.
Не входит: применение плана рендерером, GPU/Win32/Avalonia, новые хранилища/пакеты, визуал, сохранение документа, push.

## Что изменено — FACT

- `src/Map.Rendering/Reserve/FramePlan.cs`: readonly record struct плана, парные лимиты bytes/count, sample, перечень настроек и владельцев. `RequireExact` выводится из scale=1 и запрета камеры; это требование к сцене, не подтверждение завершённого refinement.
- `src/Map.Rendering/Reserve/FramePlanTuning.cs`: проверяемые начальные значения, без аппаратных гарантий. Окно 1 с, вход >0,75·T за два окна, выход <0,55·T за 4 с измеренного запаса, ухудшение/восстановление 3/5 с, разрешения 1 → 0,75 → 0,5. Лимиты приёма/загрузки 8/2 МиБ, 16 элементов, коэффициент при давлении 0,5; фон 1, максимум 4. Это стартовая политика, а не измеренная настройка машины.
- `src/Map.Rendering/Reserve/FramePlanner.cs`: один последовательный владелец состояния; время явно передаётся в миллисекундах; стоимость каждого кадра делится на его T. Максимум неперекрывающегося короткого окна, постоянная память и работа O(1), без обхода объектов документа. Пропуски/NaN/неверные измерения не засчитываются как восстановление.
- `tests/Map.Rendering.Tests/FramePlanArbiterTests.cs`, `FramePlanHysteresisTests.cs`, `FramePlanReplayTests.cs`, `FramePlanTestData.cs`: 64 новых тестовых случая; конфликт запросов, байты и количества, нулевые/предельные бюджеты, порядок изменений, задержки, шум, пропуски, смена T, экспорт, остановка жеста, replay и совместная работа с ReserveController.
- `docs/journal/2026-11.md`: краткая запись этапа по обязательному правилу AGENTS.md; прежние записи не менялись.

Владение: GestureResolution — масштаб; FrameBudget — отдельно upload и adoption; CameraPolicy — камера; WorkScheduler — конкурентность и prefetch; ExactnessPolicy — немедленное восстановление точности. ReserveDecision только запрашивает. Арбитр применяет максимум одну обычную настройку за скользящий период; сначала ухудшения, затем восстановления. Ожидающее уменьшение разрешения резервирует следующий слот. В парном бюджете сначала ужесточаются обе затронутые размерности, ослабление ждёт отдельного восстановления.

Остановка жеста и ForceExact — явное исключение DEC-115: оба визуальных поля возвращаются к точным немедленно, даже внутри dwell. Период начинается заново, ресурсные лимиты в этом вызове не восстанавливаются. Запрос разрешения квантуется вниз до доступной ступени, но переход идёт только на одну ступень.

Лимиты upload/adoption объединяют минимальный запрос reserve и measured pressure, затем жестовый потолок; конкурентность ограничивается scheduler cap, при давлении запрос дополнительных workers уступает бюджету. Ноль CPU hint означает отсутствие запроса CPU-помощи. Камера означает разрешение переиспользовать валидный кадр, при отсутствии которого нужен fallback сцены.

## Соответствие RESERVE_ENGINE §3 — PARTIAL

`FramePlan` и `FramePlanner` реализуют чистую часть общего координатора QualityGovernor. `ReserveController` остаётся источником запросов, без изменения его логики. Максимум полной стоимости кадра — дополнительный сигнал бюджета, он не подменяет GPU p95, pacing/VRAM/recovery в ReserveController. Метки OwnerOf/LastChangedKnob подходят для будущего журнала решений.

FrameBudget здесь выдаёт количественные рекомендации; общего deadline/TryTake с EWMA ещё нет. WorkScheduler получает только hint, реальные очереди и допуск по памяти не меняются. GraphicsProfileProbe/RenderHealthMonitor/GPU timer/CpuTileAssist/SceneSurfacePresenter/GraphicsRecoveryCoordinator не изменены. SimplerEffects и смена backend не поддерживаются этим планом.

Следующее подключение: один planner на поток/контекст canvas; перед `editor.StampImages?.BeginFrame()` построить план по измерениям завершённого кадра и единому состоянию gesture/settle. Один immutable plan передать приёму mip, upload, scheduler, camera cache и SceneSurfacePresenter; валидность/поколение кадра камеры проверяет cache. После stop обязательно запросить точный refinement. Сначала реализовать и проверить потребителей scale/камеры, затем включать применение плана. Сейчас вызовов planner в renderer нет: исполняемые пути отрисовки и сохранения не изменены.

## Проверки и числа до → после

Все команды выполнялись из `/Users/user/MapWork/agents/frameplan` после:

```sh
source ~/MapWork/env.sh
source ./run-env.sh
```

Никакой сети/новых пакетов; только offline NuGet cache. RID osx-arm64, анализаторы отключены.

Baseline создан указанной командой:

```sh
git -C /Users/user/MapWork/agents/frameplan/repo worktree add --detach /Users/user/MapWork/agents/frameplan/tmp/baseline 7b1f32cb118ffc3c25428966a11315503764f771
```

Сборки тестов до и после:

```sh
dotnet build /Users/user/MapWork/agents/frameplan/tmp/baseline/tests/Map.Rendering.Tests -c Release -p:RuntimeIdentifier=osx-arm64 -p:NuGetAudit=false -p:RunAnalyzers=false --configfile /Users/user/MapWork/agents/frameplan/NuGet.Config -m:1 -nr:false -p:UseSharedCompilation=false > /Users/user/MapWork/agents/frameplan/results/baseline-rendering-build.log 2>&1
dotnet build /Users/user/MapWork/agents/frameplan/repo/tests/Map.Rendering.Tests -c Release -p:RuntimeIdentifier=osx-arm64 -p:NuGetAudit=false -p:RunAnalyzers=false --configfile /Users/user/MapWork/agents/frameplan/NuGet.Config -m:1 -nr:false -p:UseSharedCompilation=false > /Users/user/MapWork/agents/frameplan/results/rendering-build.log 2>&1
dotnet build /Users/user/MapWork/agents/frameplan/test-runner/TestRunner.csproj -c Release --configfile /Users/user/MapWork/agents/frameplan/NuGet.Config -m:1 -nr:false -p:UseSharedCompilation=false > /Users/user/MapWork/agents/frameplan/results/runner-build.log 2>&1
```

FACT: сборки Rendering.Tests: 0 → 0 ошибок. Runner собран, 0 ошибок.

```sh
dotnet /Users/user/MapWork/agents/frameplan/test-runner/bin/Release/net10.0/TestRunner.dll /Users/user/MapWork/agents/frameplan/tmp/baseline/tests/Map.Rendering.Tests/bin/Release/net10.0/osx-arm64/Map.Rendering.Tests.dll ReserveControllerTests > /Users/user/MapWork/agents/frameplan/results/baseline-reserve.log 2>&1
dotnet /Users/user/MapWork/agents/frameplan/test-runner/bin/Release/net10.0/TestRunner.dll /Users/user/MapWork/agents/frameplan/repo/tests/Map.Rendering.Tests/bin/Release/net10.0/osx-arm64/Map.Rendering.Tests.dll FramePlanArbiterTests FramePlanHysteresisTests FramePlanReplayTests ReserveControllerTests > /Users/user/MapWork/agents/frameplan/results/frameplan-reserve.log 2>&1
```

FACT: тематические 28 passed / 0 failed → 92 passed / 0 failed (новые 64/64, прежние ReserveControllerTests 28/28). Число discovery cases меньше числа исполняемых случаев из-за MemberData; сравнивается итог Total runner.

```sh
dotnet /Users/user/MapWork/agents/frameplan/test-runner/bin/Release/net10.0/TestRunner.dll /Users/user/MapWork/agents/frameplan/tmp/baseline/tests/Map.Rendering.Tests/bin/Release/net10.0/osx-arm64/Map.Rendering.Tests.dll > /Users/user/MapWork/agents/frameplan/results/baseline-rendering-all.log 2>&1
MAP_TEST_PROGRESS=1 dotnet /Users/user/MapWork/agents/frameplan/test-runner/bin/Release/net10.0/TestRunner.dll /Users/user/MapWork/agents/frameplan/repo/tests/Map.Rendering.Tests/bin/Release/net10.0/osx-arm64/Map.Rendering.Tests.dll > /Users/user/MapWork/agents/frameplan/results/rendering-all.log 2>&1
```

FACT: полный набор Total 591 / passed 590 / failed 1 / skipped 0 → Total 655 / passed 654 / failed 1 / skipped 0. Список падений идентичен: `Stage10EGridRenderingTests.Four_proposed_golden_fixtures_match_without_overwriting_expected`, expected 0, actual 7145 в обоих прогонах. Новых падений нет. Сверка списков и прироста ровно на 64 passed — `results/verification.log`. Время выполнения набора тестов не является измерением FPS и не сравнивается как выигрыш производительности.

```sh
dotnet build /Users/user/MapWork/agents/frameplan/tmp/baseline/Map.slnx -c Release -p:DirectoryBuildPropsPath=/Users/user/MapWork/agents/frameplan/tmp/baseline.mac.props -p:NuGetAudit=false -p:RunAnalyzers=false --configfile /Users/user/MapWork/agents/frameplan/NuGet.Config -m:1 -nr:false -p:UseSharedCompilation=false > /Users/user/MapWork/agents/frameplan/results/baseline-solution-build.log 2>&1
dotnet build /Users/user/MapWork/agents/frameplan/repo/Map.slnx -c Release -p:DirectoryBuildPropsPath=/Users/user/MapWork/agents/frameplan/mac.props -p:NuGetAudit=false -p:RunAnalyzers=false --configfile /Users/user/MapWork/agents/frameplan/NuGet.Config -m:1 -nr:false -p:UseSharedCompilation=false > /Users/user/MapWork/agents/frameplan/results/solution-build.log 2>&1
```

FACT: Release solution: 0 → 0 ошибок. Baseline props содержит тот же импорт Directory.Build.props из baseline и RuntimeIdentifier=osx-arm64. Предупреждения 18 → 16 не считаются исправленными: повторно используемые проекты уже были собраны при проверке тестов.

FACT: синтетический replay при сдвиге начала времени на 1 234 567 мс выдаёт те же планы. На ускоренном tuning (окно 100 мс, dwell 300/500 мс) горячий сигнал даёт scale 1 до 280 мс, 0,75 в 300 мс, 0,5 в 600 мс; здоровый — 0,75 в 1100 мс, 1 в 1600 мс. Это результаты логики, а не реальные времена рендера и не GPU-запись.

## NOT CHECKED и риски

- Windows, GPU, живой просмотр владельцем, реальные худшие кадры/≥60 FPS, задержка после остановки и пиксельная приёмка не проверялись: Mac/headless, planner не подключён. Существующее golden-падение означает, что весь Rendering набор не зелёный уже на baseline.
- Нулевые аллокации нового planner отдельно не измерялись. Проверка старого ReserveController выполнялась при DOTNET_TieredCompilation=0; любой вывод «zero allocation» нужно перепроверить на Windows с обычным tiering/JIT.
- Пороги перенесены как стартовые. Максимум полной стоимости более чувствителен к единичным тяжёлым кадрам, чем GPU p95; два окна и dwell уменьшают частоту реакции. Настройку проверять на Windows.
- Сериализация может отложить другие запросы и растянуть восстановление ресурсов на несколько периодов; только exactness обходит ограничение. Это политика запросов, не замена жёсткого допуска WorkingMemoryBudget. При подключении нужны реальные лимиты клиентов и общий scheduler cap из доступных CPU.
- Planner требует последовательного вызова одним владельцем, монотонного времени и валидного ReserveDecision; ошибочные requests/timestamps отклоняются до изменения состояния. Невалидные cost/refresh удерживают помощь до достоверного восстановления.
- CI/push, Windows publish, UI-smoke не выполнялись по рамкам поручения. STATE/CHANGELOG/DEC не переписываются: продуктовый путь не изменён, нового решения владельца нет; запись журнала добавлена. Windows-only Get-MapContext.ps1/Check-Scope.ps1 заменены проверками cwd, HEAD, status и списка diff на Mac.

## Коммит, patch, уборка

FACT: один локальный коммит `35c0ff15506a851b831d090c6f64571a51742184` — `feat(perf): add pure frame plan arbitration and gesture hysteresis`. Ветка `claude/reserve`, дерево чистое. Push не выполнялся, remotes не добавлялись.

```sh
git -C /Users/user/MapWork/agents/frameplan/repo format-patch --stdout 7b1f32cb118ffc3c25428966a11315503764f771..HEAD > /Users/user/MapWork/agents/frameplan/frameplan.patch
git -C /Users/user/MapWork/agents/frameplan/tmp/baseline apply --check --index /Users/user/MapWork/agents/frameplan/frameplan.patch
git -C /Users/user/MapWork/agents/frameplan/repo worktree remove /Users/user/MapWork/agents/frameplan/tmp/baseline
```

FACT: `frameplan.patch` — 45 226 байт; применимость проверена на чистом worktree точного стартового коммита до его удаления (`results/patch-check.log`). SHA256: `89c87ee39a76c3515e7283c0c027e1d650417f3b10b89f240f1cce678807fbc3`.

FACT: diff содержит только 8 перечисленных файлов, 807 добавленных строк. `git diff --cached --check` без замечаний (`results/diff-check.log`); CRLF и отсутствие BOM сохранены (`results/eol-check.log`). Единственный существующий изменённый файл — журнал: добавлены 9 строк, прежнее содержимое сохранено.

FACT: baseline удалён через git worktree remove; удалена 21 своя папка результатов сборки (bin/obj/artifacts), включая runner; содержимое своего tmp удалено. Уборка и чистое дерево подтверждены в `results/cleanup.log`. Сохранены исходники, конфигурация, patch, этот отчёт и приведённые results/*.log. Для повторного baseline solution build нужно заново создать временный baseline.mac.props с импортом `/Users/user/MapWork/agents/frameplan/tmp/baseline/Directory.Build.props` и `<RuntimeIdentifier>osx-arm64</RuntimeIdentifier>`; его временная копия тоже удалена.

## ИТОГИ

1. Сделано: чистый FramePlan-арбитр и гистерезис, покрытые 64 новыми случаями; текущий renderer не подключён к новой политике.
2. Что дальше: подключить единый plan перед StampImages.BeginFrame и проверить потребителей/пиксели/худшие кадры на Windows.
3. Реализация проекта: не пересчитывалась; это изолированная логическая часть DEC-111/115, без проверки фактического FPS.
