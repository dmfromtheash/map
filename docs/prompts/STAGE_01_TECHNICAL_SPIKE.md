# Stage 1 — технический spike нативного ядра

> Разрешено владельцем к запуску 25.08.2026. DEC-001—DEC-008 приняты.

## Контекст

Проект `C:\Projects\Map` содержит только исследование, правила и документацию. Кода приложения нет. Цель этапа — не MVP и не красивый интерфейс, а доказательство выбранного нативного стека на самых рискованных свойствах.

Перед работой полностью прочитай:

1. `AGENTS.md`
2. `CLAUDE.md`
3. `docs/00_START_HERE.md`
4. DEC-001—DEC-008 в `docs/DECISIONS.md`
5. `docs/04_TECH_ARCHITECTURE.md`
6. `docs/05_PROJECT_FILE_AND_DATA_MODEL.md`
7. `docs/08_QA_AND_ACCEPTANCE.md`
8. `docs/STATE_HANDOFF.md`

## Единственная цель

Создать изолированный измеримый Windows desktop spike, который доказывает или опровергает пригодность .NET 10 + Avalonia 12.1 + Skia для нашего canvas, command history, атомарного файла/recovery и offscreen export.

## In scope

- solution и минимальные проекты только для spike;
- настоящее desktop window без WebView/server;
- custom canvas с 10 000 детерминированных тестовых объектов;
- pan/zoom вокруг курсора;
- spatial hit-test и select одного объекта;
- один tile-based brush layer, один stroke = одна undo-транзакция;
- одна grayscale mask и red edit overlay;
- минимальный versioned spike document;
- atomic save/reopen;
- контролируемая симуляция сбоя между temp write и replace + recovery proof;
- offscreen transparent PNG 4096×4096;
- benchmark/test report с моделью ПК, renderer, median/P95 frame/hit-test, memory и times;
- `README` spike и обновление handoff.

## Out of scope

- production dashboard и full editor shell;
- финальный UI, темы, бренд, иконки и ассеты;
- полный `.mapproj` v1 и migrations;
- Text/Line/Shape/Land/Stamp catalog/Filters;
- installer, updater, telemetry, cloud, accounts;
- Qt/Godot comparison, пока Avalonia не провалил gate;
- сторонние пакеты, не нужные spike.

## Инварианты

- `Map.Core` не ссылается на Avalonia/Skia.
- Renderer не изменяет document.
- Все edit operations идут через command transaction.
- Save state меняется на saved только после успешного replace.
- Никаких скачанных Inkarnate assets; использовать простые сгенерированные геометрические fixtures.
- Никакого браузера, WebView или локального HTTP.

## Acceptance criteria

1. `doctor.ps1` проходит.
2. Solution собирается из чистого состояния одной документированной командой.
3. Окно запускается как Windows desktop process без слушающего TCP-порта.
4. Сцена 10 000 объектов показывает pan/zoom; измерения и методика сохранены.
5. Hit-test использует spatial index; тест показывает выбор ожидаемого ID.
6. Brush stroke изменяет только затронутые tiles и отменяется/повторяется одной командой.
7. Mask влияет только на соответствующий raster layer; overlay не попадает в export.
8. Save/reopen даёт semantic equality минимального document.
9. Fault injection оставляет последний корректный файл и recoverable temp/journal; UI не заявляет ложный успех.
10. PNG 4096×4096 реально существует, имеет правильные dimensions и alpha.
11. Все тесты проходят; ручной smoke выполнен в настоящем окне.
12. Нет out-of-scope production features.

## Required verification

- clean restore/build/test;
- targeted unit/integration tests для command, spatial hit-test, save/fault/recovery и PNG;
- release run нативного окна;
- benchmark минимум после warmup, с median/P95;
- проверка открытого порта процессом;
- визуальный осмотр canvas, selection, brush, mask overlay и export;
- `git diff --stat` и dependency/license list.

Точные команды запиши в spike README; не выдумывай успешные числа до прогона.

## Required report

Используй точный формат из `docs/07_OPUS_RUNBOOK.md`. Отдельно ответь:

- прошёл ли каждый из 12 критериев;
- какой renderer реально использован и был ли CPU fallback;
- сколько памяти занимают document, render cache и undo после stroke;
- есть ли причины принять DEC-002 или нужно ещё одно ограниченное измерение;
- какие зависимости добавлены и под какими лицензиями.

После отчёта остановись. Не начинай Stage 2.
