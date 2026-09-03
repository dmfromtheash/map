# Inkarnate: размер холста и performance-ориентиры

Дата проверки: **31.08.2026**.

Статус: **FACT / PARTIAL / PROPOSED / CLEAN-ROOM**.

## Граница проверки

Владелец прямо разрешил открыть его уже авторизованный платный Inkarnate и проверить максимальные
параметры размера и наблюдаемое поведение редактора. Использовалась существующая карта
`Map UX Reference Test` с четырьмя top-level объектами.

- пароль, 2FA, cookies, storage, network/API payload и исходный код Inkarnate не читались;
- рабочие карты владельца не открывались;
- новые карты и объекты не создавались;
- export не запускался и файл не скачивался;
- test map не изменялась, tab после проверки закрыт;
- не копировались assets, UI graphics, branding, названия packs или закрытые алгоритмы.

DOM-размеры canvas использовались только как наблюдаемое свойство уже показанного редактора. Они
не доказывают внутреннюю архитектуру, renderer backend или способ хранения данных Inkarnate.

## FACT — доступные размеры платного аккаунта

В видимом create flow доступны editor quality:

- Low — 1K;
- Medium — 2K;
- High — 3K;
- Ultra — 4K.

Preset aspect grid: Landscape `40×30`, Portrait `30×40`, Square `40×40`. Custom поля имеют
видимые HTML-границы `min=2`, `max=256` отдельно для columns и rows. Это grid/aspect contract, а
не доказательство bitmap `256×256` tiles или бесконечного координатного пространства.

В export dialog текущего аккаунта показаны `1K`, `2K`, `4K`, `6K`, `8K`, `16K` и `Custom`.
Экспорт не запускался. Официальный [Inkarnate FAQ](https://inkarnate.com/faq) независимо указывает:

- Creator: editor до 4K, export до 8K;
- Studio: editor до 4K, export до 16K beta.

Следовательно, честная конкурентная граница на дату проверки — **4K editable surface и до 16K
image export**, а не 16K интерактивный editor.

## FACT — наблюдаемая 2K тестовая карта

Открытая карта показала статус `2K`. В странице находились:

- основной `canvas#output` — `2048×1536`;
- отдельный `canvas#ui-canvas` — `2048×1536`;
- `fixed-ui` и скрытый `fixed-ui-buffer` — по `1280×720`;
- map canvases визуально масштабировались как единое полотно внутри viewport;
- справа существовали Layers и раскрываемый Objects list; на тестовой карте было четыре строки;
- zoom, Fit Screen, object selection, layer controls и export отделены от map canvas.

Это подтверждает fixed-resolution surface для конкретной 2K карты. `NOT CLAIMED`: нельзя из этого
выводить точный способ culling, spatial index, texture allocation, GPU/CPU backend или поведение
при 50 000 объектах.

## PARTIAL — большие сцены Inkarnate

Безопасный walkthrough не создавал десятки тысяч объектов в чужом cloud-продукте. Поэтому
производительность Inkarnate при 15K/50K/100K не объявляется `FACT`.

Публичные пользовательские отчёты противоречивы: встречаются карты 44K–100K, но также сообщения о
сильном лаге уже после 10K–20K, зависимости от filters/browser/desktop app и необходимости
flatten. Это полезно только для выбора stress fixtures Map; это anecdotal evidence, не benchmark и
не численная база конкурентного заявления.

## Независимая цель Map

Map не должен повторять fixed-resolution web canvas. Превосходство формулируется измеримо:

1. native offline editor без обязательного browser/network;
2. sparse logical canvas независимо от единого bitmap;
3. 50 000 полностью редактируемых объектов как основной acceptance-профиль;
4. 100 000 объектов как stress/read/save-open профиль;
5. 16K single export и больший tile-package export без full-canvas allocation;
6. normal zoom до 60 FPS и full overview 50K не хуже 30 FPS на текущем ПК владельца;
7. object list, hit-test, marquee и one-object edit не выполняют скрытую работу по всем объектам на
   каждом pointer move;
8. публичный canvas cap повышается с 65 536 до 131 072 только после зелёных native gates;
9. 262 144 проверяется сначала как разреженный test-only ceiling.

## Репозитории и механизмы для Stage 13

Точные pins находятся в `external-sources.json`. Они подготовлены для isolated research lab, а не
для автоматического копирования или добавления NuGet dependencies.

| Источник | Что проверить | Решение до benchmark |
|---|---|---|
| Avalonia core, MIT | `VirtualizingStackPanel`, realized containers, recycling/scroll tests | использовать встроенный механизм первым |
| SkiaSharp, MIT, уже используется | `SKPictureRecorder` с R-tree recording, `DrawAtlas`, `SKVertices`, bounded native disposal | разрешены prototype branches внутри Stage 13 |
| Mapsui, MIT | viewport queries, vector cache, rasterizing layer, Skia/Avalonia benchmark patterns | reference probe, не переносить framework целиком |
| NetTopologySuite, BSD-3 + JTS notices | packed `STRtree`/quadtree query и memory против текущего uniform grid | dependency не добавлять без измеренного преимущества |
| BenchmarkDotNet, MIT | warmup, isolated process, MemoryDiagnoser, JSON exporter | dev-only runner candidate, вне production solution |
| Godot, MIT + third-party notices | low-level 2D batching/MultiMesh/visibility trade-off | concept-only, не dependency Map |
| Исторический Map spike в Git | `UniformGridIndex.QueryPoint`, `HitTester`, `ObjectGeometryCache`, native frame timing | адаптировать идеи поверх текущего HEAD, не восстанавливать spike целиком |

## Проверка кода, найденного сайтом или LLM

Любой предлагаемый фрагмент проходит одинаковые ворота:

1. найти первичный upstream URL, точный commit и автора;
2. прочитать license и notices; неизвестная лицензия означает `REJECTED`;
3. доказать, что код относится к текущей версии API, а не к старой сигнатуре;
4. не переносить чужой UI, assets, branding, тестовые карты или закрытую семантику;
5. сначала воспроизвести идею минимальным clean-room prototype на synthetic fixture;
6. сравнить с текущим решением на одинаковых 10K/50K/100K данных;
7. принять только если быстрее весь пользовательский путь, не ломает результат и не увеличивает
   память/сложность без оправдания;
8. записать происхождение и обязательные notices при фактическом переносе кода.

Ответ LLM без проверяемого первичного источника является только `PROPOSED`. Он может подсказать
эксперимент, но не даёт лицензию, авторство или доказательство производительности.

## Что переносить нельзя

- код, bundle, shaders, API payload, network protocol или data model Inkarnate;
- чужие assets, карты, pack taxonomy, UI graphics, trade dress и branding;
- непроверенные Reddit-цифры как acceptance threshold;
- GPL/AGPL/reference-only код в production без отдельного решения о лицензии;
- целый Mapsui/Godot/NTS только ради одной структуры данных;
- benchmark, который измеряет только CPU draw и выдаёт это за native input-to-present.

## Следующий документ

Исполняемый clean-room контракт реализации и stress gates:
`docs/STAGE_13_LARGE_CANVAS_PERFORMANCE.md`.
