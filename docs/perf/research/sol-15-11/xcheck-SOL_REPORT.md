# xcheck — независимая проверка FramePlan

Этап: cross-check DEC-111/DEC-115.
Результат: проверенный патч только исправлений поверх AM_HEAD, с воспроизводимыми доказательствами.
Входит: независимый разбор, baseline, Release, Rendering/Core, регрессионные и мутационные проверки.
Не входит: подключение к рендереру, UI, storage, Windows/GPU, push.
Файлы: src/Map.Rendering/Reserve/FramePlan*.cs; tests/Map.Rendering.Tests/FramePlan*.cs;
docs/journal/2026-11.md; xcheck.patch; SOL_REPORT.md; results/*.log.
Готовность: отдельный коммит на дефект с красным тестом до исправления; сравнение полных DLL с baseline;
фиксирующий format-patch, чистый checkout, удалённые временные файлы.

Порядок: 1) применить патч и собрать; 2) baseline и полный прогон; 3) adversarial/regression;
4) исправления по одному; 5) mutation; 6) итоговая проверка, patch, очистка.

FACT: старт 7b1f32cb118ffc3c25428966a11315503764f771; checkout изначально чистый.
FACT: AM_HEAD d98751a1787e77d726552af5e1c015b55a3f43f3.

FACT: реальных дефектов алгоритма в выполненном разборе и пробах не обнаружено.
FACT: изменений поверх AM_HEAD нет; xcheck.patch пустой (0 bytes), HEAD=AM_HEAD.
FACT: новых падений относительно baseline нет; оба полных набора завершены.

## Разбор кода

Пути ниже относительно `repo/`; номера строк относятся к AM_HEAD.

| Регулятор | Владелец | Где принимается окончательное решение |
|---|---|---|
| scene scale | GestureResolution | `FramePlanner.cs:113`, `:123`, `:177` |
| upload bytes/count | FrameBudget | `FramePlanner.cs:133`, `:136`, `:152`, `:184` |
| adoption bytes/count | FrameBudget | `FramePlanner.cs:135`, `:137`, `:152`, `:184` |
| camera frame | CameraPolicy | `FramePlanner.cs:139` |
| background concurrency | WorkScheduler | `FramePlanner.cs:145` |
| prefetch | WorkScheduler | `FramePlanner.cs:129` |
| exactness | ExactnessPolicy, предохранитель | `FramePlan.cs:21`, `FramePlanner.cs:57`, `:60` |

Полный префикс `FramePlanner.cs` — `src/Map.Rendering/Reserve/FramePlanner.cs`;
`FramePlan.cs` и `FramePlanTuning.cs` лежат в той же папке.

FACT: `OwnerOf` (`FramePlanner.cs:31`) — диагностическая таблица, а не механизм блокировки.
Реальная гарантия: все записи `_plan` происходят в конструкторе (`:23`), exact-gate (`:62`)
или `Candidate` (`:170`). Reserve передаёт запросы; его state/reasons/assist-level сами ничего
не записывают в план. Оба прохода `Adjust` заканчиваются после первого принятого кандидата
(`:69`, `:123`), общий `_lastChange` запрещает следующий обычный переход раньше периода (`:169`).
Worsening=3000 мс, recovery=5000 мс; валидатор требует recovery>worsening
(`FramePlanTuning.cs:15`, `:36`). Pending worsening блокирует recovery и резервирует слот
для разрешения (`FramePlanner.cs:69`, `:127`). Bytes и count — одна совместная ручка бюджета;
асимметричный переход сначала сжимает обе размерности через min (`:157`).

FACT: исключение из «одна ручка за период» необходимо для DEC-115: остановка жеста/ForceExact
немедленно ставит scale=1 и camera=false одним exact-переходом (`FramePlanner.cs:60`),
сбрасывает общий период и возвращает план без изменения ресурсов (`:63`). Exactness вычисляется
из двух визуальных полей (`FramePlan.cs:21`), независимого изменяемого флага нет.

FACT: проверенная конфликтная последовательность: длительные hot-кадры + Reserve upload=.75,
concurrency=99, scale=1 дают scale=.5, upload=500/5, adoption=1000/10, concurrency=1,
camera=true, prefetch stopped (ускоренный tuning). Чередование Reserve upload=1/.75 после
стабилизации плана не меняет ни одной ручки. Доказательство —
`tests/Map.Rendering.Tests/FramePlanArbiterTests.cs:36` и `results/am-frameplan.log`.
Это следует из min(reserve,pressure), ограничения CPU-hint общим cap и приоритета frame pressure.
Интеграционный replay с настоящим ReserveController —
`tests/Map.Rendering.Tests/FramePlanReplayTests.cs:59`.
Контрпример борьбы владельцев в этих проверках не найден. Гарантия относится к чистому контракту;
единство владельцев будущих GPU/upload/scheduler-потребителей пока проверить нельзя.

FACT: вход — два последовательных валидных окна с max(work/refresh)>.75;
выход — 4000 мс валидных окон с max<.55. Равенство порогу не считается соответственно hot/healthy
(`FramePlanner.cs:86`, `:89`, `:91`). Max по окну защищает худший кадр, это намеренно не GPU p95.
Текущий neutral/bad/unknown кадр обнуляет healthy-длительность до `Adjust` (`:105`), поэтому
плохой кадр на границе уже завершившегося здорового окна не разрешает recovery.
Шаги по умолчанию 1→.75→.5 и обратно, не более одного шага (`:118`, `:120`, `:177`).
Начало нового жеста начинает собственный dwell (`:58`); смена scale начинает следующий (`:172`).

FACT: атакующие последовательности в исходных тестах и дополнительных пробах:

- Чередование 7.4/7.6 мс при refresh=10: только два снижения, без восстановления.
- Чередование 5.4/5.6 мс: удержание .5 без восстановления.
- Чередование целых hot/healthy окон: нет входа.
- Постоянные 7.49/7.50/7.51: конечные scale=1/1/.5.
- После давления постоянные 5.49/5.50/5.51: конечные scale=1/.5/.5.
- 299.999→300 мс: 1→.75; 1099.999→1100 мс: .5→.75;
  повтор того же timestamp не тратит второй слот.
- На due-boundary 2000 мс neutral/hot/NaN кадр блокирует изменение;
  контроль с healthy в тот же момент меняет план.
- NaN в 1100 мс: scale остаётся .5 до 1380 включительно и восстанавливается в .75 в 1400.
- 50 000 кадров с детерминированным LCG: меняющиеся конфликтующие запросы, timestamps с повторами,
  ForceExact, остановки жестов и NaN; соблюдены интервалы, пределы и exactness.

Параметры быстрых проб: window=100, pressureWindows=2, release=200,
worsening=300, recovery=500 мс. Это проверки политики, не измерения скорости рендера.
Исходный код дополнительных проб сохранён в `results/crosscheck-source.log`;
временный файл FramePlanCrossCheckTests.cs после проверки удалён.

FACT: общие defaults совпадают с ReserveTuning: window=1000, enter=.75, exit=.55,
pressureWindows=2, release=4000, dwell=3000/5000, adoption=8/2 MiB,
busy concurrency=1, max concurrency=4 (`ReserveTuning.cs:6`, `:10`, `:20`, `:24`, `:32`).
PARTIAL: совпадение не буквальное: Reserve level-1 запрашивает .85 (`ReserveTuning.cs:38`),
FramePlanner квантует такой потолок в .75 (`FramePlanner.cs:177`); это более сильное снижение.
Двухступенчатая лестница самого planner задана явно в tuning. Перед подключением к renderer
надо подтвердить, что такая квантизация, worst-frame max и начальные count=16 соответствуют
актуальному RESERVE_ENGINE/DEC-111. Тексты RESERVE_ENGINE и DEC-111/115 в этой копии отсутствуют;
прямое требование DEC-115 из поручения проверено. Менять политику на основании отсутствующего
документа я не стал. На текущий рендерер квантизация не влияет: потребителей пока нет.
Все временные пороги/коэффициенты вынесены в FramePlanTuning; 0/1, индексы трёх ступеней и
нулевой CPU-hint вне tuning — структурные значения контракта, дополнительных policy-порогов нет.

FACT: детерминизм — только явные samples и nowMs; нет часов, Random, I/O, platform API,
статического изменяемого состояния или перечисления сцены (`FramePlanner.cs:12`, `:41`).
Постоянное число скалярных полей и кандидатов означает O(1) работы/памяти на вызов;
ничего в документе не меняется. `FramePlan` и вложенные бюджеты — readonly record struct,
возвращаются по значению (`FramePlan.cs:4`, `:12`). Tuning — sealed record с init-полями.

FACT: nowMs<0/NaN/infinity/назад отвергается до изменения плана, clocks и diagnostics
(`FramePlanner.cs:43`); равный timestamp разрешён. Невалидные Reserve-запросы также
отвергаются до mutation (`:46`). Refresh=0/NaN/infinity, отрицательный/NaN/infinity work,
переполнение отношения — unknown (`:98`), не аргумент за recovery. Gap>window сбрасывает
окна и healthy-длительность, сохраняя pressure до измеренного recovery (`:79`).
Пустые окна не досчитываются циклом; простой не превращается в здоровое время.
Нулевые бюджеты, округление count вниз и long.MaxValue проверены исходными тестами.

FACT: мёртвых новых типов/полей, не используемых ни тестами, ни чистым seam, не найдено:
FramePlanSample/FramePlan/FrameWorkBudget используются Plan и тестами; tuning используется
валидатором/кандидатами; enum/OwnerOf/LastChangedKnob проверяются диагностическими тестами.
`rg 'FramePlan|FramePlanner' repo/src -g '*.cs'` находит только три новых файла в Reserve.
Минимальный seam — вызов Plan до BeginFrame в будущем; реальной интеграции сейчас нет
(`FramePlan.cs:7`). Existing renderer, Core, document, save format и UI не изменены патчем.
По коду поведение текущего renderer неизменно; отдельный pixel hash до/после NOT CHECKED.

## Мутации

FACT: каждая логическая мутация компилировалась отдельно во временной сборке из тех же
Reserve/*.cs и FramePlan*.cs; исходный renderer DLL не перезаписывался. Контроль до мутаций:
75/75 (64 исходных + 11 проб), `results/mutation-control.log` и
`results/mutation-control-build.log`; после добавления пробы unknown-window и полного восстановления
исходника: 76/76 (64+12), `results/mutation-restored-control.log`.

| Мутация | Исходные: pass/fail | Дополнительные: pass/fail | Какие исходные тесты ловят / вывод |
|---|---:|---:|---|
| 01 отключить exact-gate | 60/4 | 10/1 | Gesture_stop_or_force_exact (2), оба replay |
| 02 enter `>`→`>=` | 64/0 | 10/1 | пропущена исходными; Enter_threshold_is_strict ловит равенство |
| 03 exit `<`→`<=` + текущий `>=`→`>` | 64/0 | 9/2 | пропущена исходными; Exit_threshold и due-boundary ловят |
| 04 засчитывать одно hot-окно как все требуемые | 63/1 | 11/0 | Alternating_hot_and_healthy_windows_do_not_enter |
| 05 убрать общий `_lastChange`-dwell | 61/3 | 7/4 | At_most_one_knob, Worsening_beats_recovery, Asymmetric_budget |
| 06 считать unknown-окно здоровым | 64/0 | 11/0, затем 11/1 | новая Unknown_window_restarts_the_entire_healthy_release ловит ранний recovery |
| 07 min→max для upload factor | 57/7 | 9/3 | Budgets_intersect, Conflicting_requests, Worsening_beats и ещё 4 |
| 08 count Floor→Ceiling | 62/2 | 12/0 | Budgets_intersect, Byte_ceiling_handles_long_max_value |
| 09 убрать резервирование visual-slot при pending | 49/15 | 5/7 | Enter/Exit_dwell, replay, Unknown_cost, Sampling_gap и другие |

FACT: 6/9 мутаций пойманы исходным набором, 9/9 — совокупностью исходного набора и
дополнительных проб. Полные имена упавших тестов и точный diff каждой мутации находятся
в `results/mutation-01-exactness.log` … `results/mutation-09-visual-reservation.log`;
для 06 после новой пробы — `results/mutation-06b-unknown-health-probe.log`.
Мутации не коммитились; после отката `git diff --exit-code` подтвердил побайтовое совпадение
tracked-файлов с AM_HEAD, включая CRLF. Код и команды harness сохранены в
`results/mutation-harness-source.log`.

PARTIAL: исходный набор содержателен, но не доказывает точную строгость порогов и полный
healthy-reset после unknown-окна. В `FramePlanHysteresisTests.cs:159` проверяются 1280 и 1400,
поэтому ошибочный recovery в 1300 проходит. Это обнаруженный пробел тестового покрытия,
а не воспроизведённый дефект рабочего алгоритма. Постоянный фикс алгоритма без красного
регрессионного теста на оригинале не делался.

## Сборка и полные тесты

FACT: Release-сборка Map.slnx — 0 errors / 18 warnings;
`results/am-solution-build.log`. Сборки Rendering/Core тестов и runner также успешны:
`results/am-rendering-build.log`, `am-core-build.log`, `runner-build.log`;
baseline тестовые проекты собраны с теми же параметрами:
`results/baseline-rendering-build.log`, `baseline-core-build.log`.

| Проверка | Baseline pass/fail | AM_HEAD pass/fail | Комментарий |
|---|---:|---:|---|
| FramePlanArbiter/Hysteresis/Replay | отсутствуют | 64/0 | results/am-frameplan.log |
| ReserveControllerTests | 28/0 | 28/0 | results/*-reserve.log |
| Map.Rendering.Tests, вся DLL | 590/1 | 654/1 | 591→655 всего, +64 passed, +0 failed |
| Map.Core.Tests, вся DLL | 654/4 | 654/4 | 658→658 всего, без изменений |
| Независимые пробы, чистая политика | неприменимо | 12/0 | results/mutation-restored-control.log |

AM Rendering: единственное падение
`Map.Rendering.Tests.Stage10EGridRenderingTests.Four_proposed_golden_fixtures_match_without_overwriting_expected`,
expected=0, actual=7145. Baseline воспроизвёл то же падение с actual=7145.
AM Core: четыре WorkingMemoryBudgetTests:

- Small_edit_preview_and_save_validation_work_below_the_normal_OS_reserve;
- Rejected_heavy_operation_releases_the_cross_window_slot;
- The_heavy_slot_is_granted_while_the_thread_pool_is_busy;
- Separate_window_budgets_share_the_heavy_slot_and_cancelled_waiters_do_not_strand_it.

Все четыре Core падения связаны с PlatformNotSupportedException при WaitMultiple с named
synchronization primitive на Mac (одно обёрнуто Assert.Throws с другим ожидаемым типом).
FACT: списки имён FAIL отсортированы и сравнены через Python `assert` для каждой DLL;
они полностью совпадают, новых падений 0. Лог сравнения и totals:
`results/failure-comparison.log`. Golden-падение — подтверждённое существующее падение baseline,
его первопричина в этой задаче не исследовалась. Полные DLL не объявляются зелёными.
Время исполнения этих общих тестов не используется как сравнение производительности FramePlan.

Точные команды (выполнялись из `/Users/user/MapWork/agents/xcheck`; перед каждой shell-командой
`source ~/MapWork/env.sh; source /Users/user/MapWork/agents/xcheck/run-env.sh`):

```sh
git -C /Users/user/MapWork/agents/xcheck/repo am -3 /Users/user/MapWork/agents/xcheck/brief/patches/frameplan.patch
git -C /Users/user/MapWork/agents/xcheck/repo worktree add /Users/user/MapWork/agents/xcheck/tmp/baseline 7b1f32cb118ffc3c25428966a11315503764f771
dotnet build /Users/user/MapWork/agents/xcheck/repo/Map.slnx -c Release -p:DirectoryBuildPropsPath=/Users/user/MapWork/agents/xcheck/mac.props -p:NuGetAudit=false -p:RunAnalyzers=false --configfile /Users/user/MapWork/agents/xcheck/NuGet.Config -m:1 -nr:false -p:UseSharedCompilation=false > /Users/user/MapWork/agents/xcheck/results/am-solution-build.log 2>&1
dotnet build /Users/user/MapWork/agents/xcheck/repo/tests/Map.Rendering.Tests -c Release -p:RuntimeIdentifier=osx-arm64 -p:NuGetAudit=false -p:RunAnalyzers=false --configfile /Users/user/MapWork/agents/xcheck/NuGet.Config -m:1 -nr:false -p:UseSharedCompilation=false > /Users/user/MapWork/agents/xcheck/results/am-rendering-build.log 2>&1
dotnet build /Users/user/MapWork/agents/xcheck/repo/tests/Map.Core.Tests -c Release -p:RuntimeIdentifier=osx-arm64 -p:NuGetAudit=false -p:RunAnalyzers=false --configfile /Users/user/MapWork/agents/xcheck/NuGet.Config -m:1 -nr:false -p:UseSharedCompilation=false > /Users/user/MapWork/agents/xcheck/results/am-core-build.log 2>&1
dotnet build /Users/user/MapWork/agents/xcheck/tmp/baseline/tests/Map.Rendering.Tests -c Release -p:RuntimeIdentifier=osx-arm64 -p:NuGetAudit=false -p:RunAnalyzers=false --configfile /Users/user/MapWork/agents/xcheck/NuGet.Config -m:1 -nr:false -p:UseSharedCompilation=false > /Users/user/MapWork/agents/xcheck/results/baseline-rendering-build.log 2>&1
dotnet build /Users/user/MapWork/agents/xcheck/tmp/baseline/tests/Map.Core.Tests -c Release -p:RuntimeIdentifier=osx-arm64 -p:NuGetAudit=false -p:RunAnalyzers=false --configfile /Users/user/MapWork/agents/xcheck/NuGet.Config -m:1 -nr:false -p:UseSharedCompilation=false > /Users/user/MapWork/agents/xcheck/results/baseline-core-build.log 2>&1
dotnet build /Users/user/MapWork/agents/xcheck/test-runner/TestRunner.csproj -c Release --configfile /Users/user/MapWork/agents/xcheck/NuGet.Config -p:NuGetAudit=false -p:RunAnalyzers=false -m:1 -nr:false -p:UseSharedCompilation=false > /Users/user/MapWork/agents/xcheck/results/runner-build.log 2>&1
dotnet /Users/user/MapWork/agents/xcheck/test-runner/bin/Release/net10.0/TestRunner.dll /Users/user/MapWork/agents/xcheck/repo/tests/Map.Rendering.Tests/bin/Release/net10.0/osx-arm64/Map.Rendering.Tests.dll FramePlanArbiterTests FramePlanHysteresisTests FramePlanReplayTests > /Users/user/MapWork/agents/xcheck/results/am-frameplan.log 2>&1
dotnet /Users/user/MapWork/agents/xcheck/test-runner/bin/Release/net10.0/TestRunner.dll /Users/user/MapWork/agents/xcheck/repo/tests/Map.Rendering.Tests/bin/Release/net10.0/osx-arm64/Map.Rendering.Tests.dll ReserveControllerTests > /Users/user/MapWork/agents/xcheck/results/am-reserve.log 2>&1
dotnet /Users/user/MapWork/agents/xcheck/test-runner/bin/Release/net10.0/TestRunner.dll /Users/user/MapWork/agents/xcheck/repo/tests/Map.Rendering.Tests/bin/Release/net10.0/osx-arm64/Map.Rendering.Tests.dll > /Users/user/MapWork/agents/xcheck/results/am-rendering-all.log 2>&1
dotnet /Users/user/MapWork/agents/xcheck/test-runner/bin/Release/net10.0/TestRunner.dll /Users/user/MapWork/agents/xcheck/repo/tests/Map.Core.Tests/bin/Release/net10.0/osx-arm64/Map.Core.Tests.dll > /Users/user/MapWork/agents/xcheck/results/am-core-all.log 2>&1
dotnet /Users/user/MapWork/agents/xcheck/test-runner/bin/Release/net10.0/TestRunner.dll /Users/user/MapWork/agents/xcheck/tmp/baseline/tests/Map.Rendering.Tests/bin/Release/net10.0/osx-arm64/Map.Rendering.Tests.dll ReserveControllerTests > /Users/user/MapWork/agents/xcheck/results/baseline-reserve.log 2>&1
MAP_TEST_PROGRESS=1 dotnet /Users/user/MapWork/agents/xcheck/test-runner/bin/Release/net10.0/TestRunner.dll /Users/user/MapWork/agents/xcheck/tmp/baseline/tests/Map.Rendering.Tests/bin/Release/net10.0/osx-arm64/Map.Rendering.Tests.dll > /Users/user/MapWork/agents/xcheck/results/baseline-rendering-all.log 2>&1
MAP_TEST_PROGRESS=1 dotnet /Users/user/MapWork/agents/xcheck/test-runner/bin/Release/net10.0/TestRunner.dll /Users/user/MapWork/agents/xcheck/tmp/baseline/tests/Map.Core.Tests/bin/Release/net10.0/osx-arm64/Map.Core.Tests.dll > /Users/user/MapWork/agents/xcheck/results/baseline-core-all.log 2>&1
git -C /Users/user/MapWork/agents/xcheck/repo format-patch --stdout d98751a1787e77d726552af5e1c015b55a3f43f3..HEAD > /Users/user/MapWork/agents/xcheck/xcheck.patch
```

Для мутаций точная команда была
`zsh /Users/user/MapWork/agents/xcheck/tmp/run-mutation.sh <id>`.
В `results/mutation-harness-source.log` до `#!/bin/zsh` сохранён полный временный csproj,
после — скрипт с точными build/run командами. Скрипт читает реальные исходники Reserve и тестов;
каждый mutation-log начинается с применённого diff. Снимок 12 проб:
`results/crosscheck-source.log` (чистый C#; можно вернуть как временный тестовый файл).
Первоначальные 11 проб также запускались в настоящем проекте Map.Rendering.Tests:
`results/crosscheck-build.log`, `results/crosscheck.log`; затем 12/12 — через чистый harness.

FACT: `xcheck.patch` сформирован указанной командой и пустой (0 bytes): HEAD=AM_HEAD.
Локальный conventional commit исходного патча создан `git am`; дополнительных коммитов
исправлений нет, поскольку рабочего дефекта для исправления не воспроизведено.
Ничего не push-илось, remotes не добавлялись. Документы проекта/STATE не менялись:
результат cross-check находится в этом отчёте, продуктовый этап и состояние не изменились.

## Итоговое состояние и очистка

FACT: удалены baseline worktree, все bin/obj в repo и test-runner, repo/artifacts и всё
содержимое tmp, включая CLI home, временный harness и скрипты. Остались исходники/config
checkout и runner, входные brief, xcheck.patch, этот отчёт и указанные results/*.log.
Проверки конечного состояния — `results/final-state.log`; список удалённых временных путей —
`results/cleanup.log`. Checkout чистый, HEAD=AM_HEAD, diff --check чистый, ignored-файлов нет,
зарегистрирован только основной worktree. Чужие папки/процессы не менялись.

Чек-лист проекта: Windows Get-MapContext/смоук/публикация/CI/push не выполнялись по ограничениям
поручения; вместо Windows context проверены git branch/HEAD/status в разрешённой Mac-копии.
Новая запись journal/STATE/CHANGELOG не внесена: исправлений и продуктовых изменений этого
cross-check нет, результат и ограничения зафиксированы отдельно здесь.

## NOT CHECKED и риски

- Реальные frame times, GPU, Windows, UI и живой просмотр владельцем — NOT CHECKED.
- Нулевая аллокация — перепроверить на Windows: run-env.sh задаёт DOTNET_TieredCompilation=0;
  алгоритмическая O(1) не равна доказательству нулевых runtime-аллокаций.
- Анализаторы отключены по поручению. Windows RID/runtime не собирались.
- Pixel-identical текущего renderer подтверждено отсутствием подключения по коду;
  отдельные снимки/сравнение pixel hash не выполнялись.
- Для будущего подключения надо проверить фактическое соблюдение совместного bytes/count
  каждым потребителем, camera fallback без cached-frame и фоновые лимиты. Сейчас это контракт.
- Фиксированный приоритет может задержать остальные ручки, пока reserved visual dwell
  не истёк; немедленный exact-переход разрешает менять два визуальных поля и начинает
  новый ресурсный период. Это явно заданная политика, не обещание справедливости scheduler.

## ИТОГИ

1. Сделано: независимый разбор с file:line, Release, оба полных набора против baseline,
   9 мутаций и 12 дополнительных проб. Дефект рабочего алгоритма не воспроизведён;
   xcheck.patch пустой, новых регрессий 0. Три пробела исходного тестового набора описаны выше.
2. Что дальше: обязательных исправлений по выполненным проверкам нет. Перед подключением
   planner сверить квантизацию .85→.75 и остальные PARTIAL/NOT CHECKED пункты со спецификацией;
   нулевые аллокации и реальные GPU/frame times проверить на Windows.
3. Реализация проекта: не пересчитывалась; поручение касается проверки одного патча.
