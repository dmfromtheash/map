# Pyramid — DEC-114: пороги, гистерезис и непрерывность

## Карточка этапа

**FACT.** Работа в `/Users/user/MapWork/agents/pyramid/repo`, ветка
`claude/pyramid-raster`, исходный коммит
`9698a07b4ba8408e42cb6d64d0c9d543a0407df7`. Старт чистый.
Поручение: чистая детерминированная политика overview/pyramid, без GPU/Avalonia,
диска и потоков, плюс тесты и локальный format-patch. Новые пакеты, сеть,
push/remotes, UI/XAML, видимые тексты, документ/Undo и сохранение не менялись.

## Что изменено — FACT

- `src/Map.Rendering/Pyramid/PyramidViewPolicy.cs`: вход статистики, tuning,
  пороги enter/exit, выбор уровня, два гистерезиса, dwell, неизменяемое состояние
  replay и минимальный контракт кадра для будущего canvas.
- `src/Map.Rendering/Pyramid/PyramidContinuity.cs`: линейный вес, взвешенная сумма
  premultiplied RGBA и чистый допуск по максимальной/средней ошибке каждого канала.
- `tests/Map.Rendering.Tests/PyramidViewPolicyTests.cs`,
  `PyramidContinuityTests.cs`: 30 новых тестов.
- `docs/perf/research/pyramid-view-policy.md`: формулы, defaults, путь live,
  обязательства canvas, ограничения совместимости и измеренные пиксели.
- `docs/journal/2026-11.md`: короткая запись проверки. `STATE.md` не менялся:
  продуктовая интеграция и состояние владельческой Windows-копии этим seam не меняются.

Действующие файлы рендера не менялись; вызовов новой политики из canvas нет.
`Enabled=false` по умолчанию. Поэтому существующая картинка не меняется.
Это подтверждено отсутствием изменений действующего пути; реальная картинка
Windows не проверялась. В Core зависимости не добавлялись.

## Формулы и числа — FACT

`e = MeanObjectExtentWorld`, если передано среднее `max(width,height)`;
иначе `e = sqrt(width*height/objectCount)`. Второе — прежний density proxy ядра,
**средний шаг**, а не измеренный размер силуэта. Статистика относится ко всей
карте, по участвующим объектам; передавать число только из viewport неправильно.
Владелец документа должен обновлять статистику по edit-delta, без полного
обхода на кадр. Сбор статистики в этом пакете не реализуется.

`enter = min(1, 1.5/e)`, `exit = min(1, 2/e)`.
Live входит при scale <= enter; pyramid выходит при scale > exit.
Пустая и single-tile карта остаются live.
Для 131072² и 2 млн: `e=92.68190002368316`,
enter `0.016184389828183307` (1.6184 %), exit `0.021579186437577745` (2.1579 %).

Default-настройки: enter/exit 1.5/2 px (DEC-114, полоса 33 %), cap scale 1
(native предел core), maximum upscale 1.25, level hysteresis 1.1,
dwell 12 кадров, fade 8 кадров, Enabled=false.
При 60 FPS это примерно 200/133 мс, но политика не предполагает 60 FPS и не читает
часы. Номера кадров передаёт вызывающий код; пропуски и повторы детерминированы.

Выбор уровня минимизирует `abs(log2(level.Scale)-log2(viewScale))` среди
`viewScale/level.Scale <= 1.25`, при равенстве выбирает более подробный.
Для соседних scale c/f: `b=min(sqrt(c*f), c*1.25)`;
переход вверх на `min(b*1.1,c*1.25)`, вниз на `b/1.1`.
Cap не переворачивает полосу гистерезиса. Если даже native не подходит — live.
Cap относится к новой цели; исходящее изображение удерживается во время fade,
также при резком изменении масштаба. Это надо учитывать при интеграции.

Вес `w=start+(1-start)*clamp(elapsed/8,0,1)`, конец ровно 1.
Разворот меняет пару источников и вес на 1-w без скачка текущей смеси.
Третий источник ждёт завершения пары; dwell ограничивает частоту начал переходов.
Смесь — взвешенная сумма полных premultiplied-картинок, включая alpha, затем
одно наложение SrcOver. Два SrcOver с opacity не дают эту смесь.

Покадровая работа зависит только от числа уровней, не числа объектов.
Zero-allocation и стоимость кадров не измерялись; любые zero-allocation
утверждения **требуют перепроверки на Windows** (DOTNET_TieredCompilation=0 здесь).

## Непрерывность цвета — FACT / PARTIAL

Live сейчас — `DrawObjects` → overview chunks + exact objects, иногда dense
detail tiles от scale 0.03. Bucket `floor(log2(scale)*4)`, объекты >=4 bucket px
остаются точными. Растеризатор пирамиды вызывает тот же SceneRenderer отдельно
для каждого уровня. Нельзя строить родителей простым box-downsample другого LOD
или менять gamma только у пирамиды. Нужны одинаковые настройки, encoded-space
фильтрация, RGBA8888/Premul и согласованная готовая композиция всего viewport.

В `PyramidColourTuning`: максимум 8/255 и среднее 0.05/255 **для каждого RGBA**.
Это явный численный допуск, не побитовая идентичность и не визуальная приёмка.
Для строгой проверки можно задать нули. Первичный допуск 2/255 не прошёл на
синтетике 1/16; действующий renderer не исправлялся. Это отражено в тестах,
документации и числах ниже, а не скрыто как «pixel-identical».

Синтетика — 500 цветных фигур, поворот, частичная прозрачность, exact + dots,
четыре собранных CPU-тайла против одного live viewport на enter scale.

| Scale | Max RGBA /255 | Mean RGBA /255 |
|---|---|---|
| 1/64 | 0, 0, 0, 0 | 0, 0, 0, 0 |
| 1/32 | 1, 1, 1, 1 | .00001144, .00003052, .00000763, .00003433 |
| 1/16 | 7, 7, 1, 5 | .00014496, .00013733, .00001907, .00010681 |

Off-grid premultiplied colour ramps при .025/.0390625/.045:
max <=1.1102230246251565e-16; mean <=5.27890908109042e-18
(нормализованные компоненты), включая все веса fade.

**PARTIAL:** совместимость при произвольном scale не доказана и вообще не
следует из одного cross-fade. Отрицательная синтетическая проверка при .02
даёт max **239/255**, максимальное среднее канала **25.2766796875/255** между
live dots и растянутым уровнем. `IsCompatible` отвергает цель, `Advance`
оставляет live. Нужна совместимая конечная картинка, иначе включение пирамиды
нарушило бы условие владельца. Известная Watercolor-зависимость от viewport
из предыдущего отчёта также остаётся.

## Проверки и точные команды

Перед **каждой** командой:

```sh
source ~/MapWork/env.sh; source ./run-env.sh
```

Рабочий каталог: `/Users/user/MapWork/agents/pyramid`.
`run-env.sh` уже задаёт DOTNET_TieredCompilation=0.
Сеть и новые пакеты не использовались; config — только offline cache.

```sh
git -C /Users/user/MapWork/agents/pyramid/repo worktree add /Users/user/MapWork/agents/pyramid/tmp/baseline 9698a07b4ba8408e42cb6d64d0c9d543a0407df7
dotnet build /Users/user/MapWork/agents/pyramid/tmp/baseline/tests/Map.Rendering.Tests -c Release -p:RuntimeIdentifier=osx-arm64 -p:NuGetAudit=false -p:RunAnalyzers=false --configfile /Users/user/MapWork/agents/pyramid/NuGet.Config -m:1 -nr:false -p:UseSharedCompilation=false > /Users/user/MapWork/agents/pyramid/results/baseline-rendering-build.log 2>&1
dotnet build /Users/user/MapWork/agents/pyramid/test-runner/TestRunner.csproj -c Release --configfile /Users/user/MapWork/agents/pyramid/NuGet.Config -m:1 -nr:false -p:UseSharedCompilation=false > /Users/user/MapWork/agents/pyramid/results/runner-build.log 2>&1
dotnet build /Users/user/MapWork/agents/pyramid/repo/tests/Map.Rendering.Tests -c Release -p:RuntimeIdentifier=osx-arm64 -p:NuGetAudit=false -p:RunAnalyzers=false --configfile /Users/user/MapWork/agents/pyramid/NuGet.Config -m:1 -nr:false -p:UseSharedCompilation=false > /Users/user/MapWork/agents/pyramid/results/rendering-build.log 2>&1
dotnet build /Users/user/MapWork/agents/pyramid/repo/Map.slnx -c Release -p:DirectoryBuildPropsPath=/Users/user/MapWork/agents/pyramid/mac.props -p:NuGetAudit=false -p:RunAnalyzers=false --configfile /Users/user/MapWork/agents/pyramid/NuGet.Config -m:1 -nr:false -p:UseSharedCompilation=false > /Users/user/MapWork/agents/pyramid/results/solution-build.log 2>&1
dotnet /Users/user/MapWork/agents/pyramid/test-runner/bin/Release/net10.0/TestRunner.dll /Users/user/MapWork/agents/pyramid/repo/tests/Map.Rendering.Tests/bin/Release/net10.0/osx-arm64/Map.Rendering.Tests.dll PyramidViewPolicyTests PyramidContinuityTests > /Users/user/MapWork/agents/pyramid/results/pyramid-new.log 2>&1
dotnet /Users/user/MapWork/agents/pyramid/test-runner/bin/Release/net10.0/TestRunner.dll /Users/user/MapWork/agents/pyramid/tmp/baseline/tests/Map.Rendering.Tests/bin/Release/net10.0/osx-arm64/Map.Rendering.Tests.dll Pyramid > /Users/user/MapWork/agents/pyramid/results/baseline-pyramid.log 2>&1
dotnet /Users/user/MapWork/agents/pyramid/test-runner/bin/Release/net10.0/TestRunner.dll /Users/user/MapWork/agents/pyramid/repo/tests/Map.Rendering.Tests/bin/Release/net10.0/osx-arm64/Map.Rendering.Tests.dll Pyramid > /Users/user/MapWork/agents/pyramid/results/pyramid-all.log 2>&1
MAP_TEST_PROGRESS=1 dotnet /Users/user/MapWork/agents/pyramid/test-runner/bin/Release/net10.0/TestRunner.dll /Users/user/MapWork/agents/pyramid/tmp/baseline/tests/Map.Rendering.Tests/bin/Release/net10.0/osx-arm64/Map.Rendering.Tests.dll > /Users/user/MapWork/agents/pyramid/results/baseline-rendering-all.log 2>&1
MAP_TEST_PROGRESS=1 dotnet /Users/user/MapWork/agents/pyramid/test-runner/bin/Release/net10.0/TestRunner.dll /Users/user/MapWork/agents/pyramid/repo/tests/Map.Rendering.Tests/bin/Release/net10.0/osx-arm64/Map.Rendering.Tests.dll > /Users/user/MapWork/agents/pyramid/results/rendering-all.log 2>&1
```

**FACT:** Release Map.slnx — 0 errors, 16 warnings; тестовые проекты собираются
отдельно. Новые тесты — 30 passed / 0 failed. `Pyramid` фильтр:
baseline **75 passed / 0 failed / 0 skipped → 105 / 0 / 0**.
Логи: `results/solution-build.log`, `rendering-build.log`, `pyramid-new.log`,
`baseline-pyramid.log`, `pyramid-all.log`.
Фиксированная синтетическая последовательность wheel-noise: **194 кадра,
2 смены режима**, идентичный replay. Реальный ввод мыши не записывался.
Sweep уровней: по 2049 scale в обе стороны, монотонность и upscale cap проходят.

Полный Rendering: baseline завершён — **646 total / 640 passed / 1 failed / 5 skipped**.
Падение: `Stage10EGridRenderingTests.Four_proposed_golden_fixtures_match_without_overwriting_expected`
(ожидается 0 различий, на Mac получается 7145). Это факт baseline; причина
различий этим пакетом не устанавливалась и не исправлялась.
Итоговый прогон — **676 total / 670 passed / 1 failed / 5 skipped**.
Имена падений и пропусков совпали побуквенно, новых/исчезнувших нет:
`results/rendering-comparison.log`. Полные логи —
`results/baseline-rendering-all.log`, `results/rendering-all.log`.
Сравнение проверило также точную прибавку 30 total и 30 passed.
Время runner 584.04 → 549.27 секунд не является benchmark и ничего не доказывает
о скорости кадров; сборки выполнялись последовательно в разных процессах.
Первый baseline без вывода текущего теста был прерван своим Ctrl-C и повторён
с MAP_TEST_PROGRESS=1 для диагностики долгих CPU-тестов стилей. Прерванный запуск
не считается завершённой проверкой: `results/baseline-rendering-initial.log`.

## Коммиты, patch и очистка — FACT

Локальные коммиты (без push):

- `68bc0cb` — `feat(render): add deterministic pyramid view policy and continuity contract`.
- `25b7599` — `docs(perf): record pyramid policy validation and colour limits`.

Итог: пять новых файлов и 10 строк в существующем журнале. Журнал добавлен
по обязательному правилу repo/AGENTS.md; основной STATE/CHANGELOG не менялся,
поскольку продуктовый путь не подключён и видимых изменений нет.
Существующие файлы сохранены в исходном UTF-8/CRLF; новые checkout-файлы также
CRLF согласно .gitattributes (в Git LF). `git diff --check` прошёл.

```sh
git -C /Users/user/MapWork/agents/pyramid/repo format-patch --stdout 9698a07b4ba8408e42cb6d64d0c9d543a0407df7..HEAD > /Users/user/MapWork/agents/pyramid/pyramid.patch
git -C /Users/user/MapWork/agents/pyramid/tmp/baseline apply --check --verbose /Users/user/MapWork/agents/pyramid/pyramid.patch > /Users/user/MapWork/agents/pyramid/results/patch-check.log 2>&1
git -C /Users/user/MapWork/agents/pyramid/repo diff --check 9698a07b4ba8408e42cb6d64d0c9d543a0407df7..HEAD
git -C /Users/user/MapWork/agents/pyramid/repo worktree remove --force /Users/user/MapWork/agents/pyramid/tmp/baseline
```

Проверка применения обоих коммитов к untouched baseline прошла:
`results/patch-check.log`. SHA-256 `pyramid.patch`:
`e005b0074624f0fee0180eca48a20d456abe68aa8fc153aea9606e1e8691201c`.

Baseline worktree удалён после проверки. Все свои generated bin/obj (включая
runner), repo/artifacts и содержимое tmp удалены, `git status --short --ignored`
пустой: `results/cleanup.log`. Сохранены исходники, входная инфраструктура,
patch, этот отчёт и указанные логи. Общие каталоги и чужие процессы не затрагивались.

## Что остаётся — NOT CHECKED / риски

- Canvas: подключить Select/Advance, device scale, statistics по дельтам,
  readiness/invalidation **всего** viewport, хранение обоих источников,
  правильную premultiplied-композицию и editor overlays после неё.
- Для произвольного масштаба нужны цветосовместимые представления: ближайший
  dyadic level сам по себе не гарантирует равенства scale-dependent live dots.
  До этого gate может оставлять live и не дать ожидаемого ускорения.
- Реальные стили, Watercolor, свет, dynamic overlays, корректность при смене
  viewport/ревизии и CPU↔GPU parity не приняты этой синтетической проверкой.
- Диск/DB/cache storage, бюджет хранения 40 % / 2 GiB и публикация/вытеснение
  готовых уровней — отдельная работа Claude, не добавлялись.
- Windows RID/runtime, UI, GPU, живая картинка, frame times, worst frames,
  ≥60 FPS, аллокации и анализаторы — **NOT CHECKED**. Смоук UI не запускался:
  действующая UI/отрисовка не меняется, Mac разрешён только для headless.

## ИТОГИ

1. Сделано: чистая отключённая по умолчанию политика выбора и непрерывного перехода;
   тесты формул, цвета, гистерезиса и replay. Действующая картинка не менялась.
2. Что дальше: подключение canvas с проверкой совместимости конечных картинок;
   без неё нельзя считать требование непрерывности всей интеграции выполненным.
3. Реализация проекта: не пересчитывалась — это отдельный логический seam,
   без Windows/GPU/продуктовой приёмки.
