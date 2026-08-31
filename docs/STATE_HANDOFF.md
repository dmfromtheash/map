# STATE HANDOFF

## Актуально на 2026-08-31

Map — личное нативное offline-приложение владельца для его текущего Windows x64 ПК. Полезный
production-код Stages 1–11 сохранён. Stage 12A — именованные контрольные точки — и Stage 12B —
resize/crop и новая карта из региона — реализованы и проверены. Отменена только лишняя Stage 11
программа для VM, универсальной чистой Windows, installer/updater, release candidate и
многократной независимой приёмки.

Следующий этап — **Stage 12C: полноценный export и отмена**. Он не начат.

## Актуальная продуктовая база

- Рабочий worktree:
  `C:\Users\Leshe\.codex\worktrees\stage10f-fonts-bindings\Map`.
- Ветка: `stage/12b-resize-crop-region`.
- База Stage 12B до изменения: `750326f967b650b402c67146ab195c2e49c7c4bc` — принятый
  результат `stage/12a-named-checkpoints`.
- Результат Stage 12B: текущий `HEAD` этой ветки, один commit
  `feat(stage-12b): add canvas resize and region extraction`.
- Консолидированный product/code checkpoint:
  `825d9df9a5fec345c30da009b8b00bb594dd20f1`.
- Основной cleanup commit:
  `889e44897acc8de25cb5f3d3690abe5a60e35fb3`.
- База до уборки:
  `2c0a698e15573badb22a54de7ff1687ab784a9b2`.

Checkpoint `825d9df` является потомком Stage 10E, Stage 10F и полезного Stage 11. Новую функцию
нельзя начинать от `1387b1b`, `2af5bd3` или другого старого checkout.

Сохранено:

- четыре geometry-сетки, включая отдельную `IsometricSquares`, общий snap/render и property sheet;
- локальный системный font catalog и редактируемые keyboard bindings;
- именованные контрольные точки с локальным временем, bounded preview, восстановлением как одной
  undoable-командой и явным удалением;
- изменение canvas от 16 до 65 536 px по каждой стороне с девятью anchor, preview, grid snap,
  object counts и одной undoable-командой;
- безопасный crop/expand: данные за canvas сохраняются, но viewport, thumbnail и общий render
  фактически clip-ятся границей документа;
- точный sparse перенос RGBA, coverage, land и layer masks по материализованным tiles;
- независимая `.mapproj` из прямоугольного региона текущего in-memory snapshot с новым id,
  объектами/группами/compositions/notes/assets и без checkpoint catalog источника;
- обратная совместимость старых `.mapproj`, атомарное хранение snapshot без рекурсивного каталога,
  checksum-цепочка и лимиты 20 точек / 64 MiB;
- иконка приложения;
- изоляция тестового профиля от пользовательских Diagnostics/Recovery/Assets/recent;
- весь более ранний editor, Library, persistence, rendering и UI;
- test sources вне обычного solution для точечных проверок рискованной логики.

## Что убрано

- исторический spike, benchmarks, диагностические генераторы и UI drivers;
- старые этапные prompts/reviews и generated evidence;
- VM/ISO/QEMU, packaging/release scripts, installer/update заготовки;
- pre-commit, coverage, mutation и лишние tool manifests;
- поздние Stage 10E/10F/11 process-файлы, появившиеся после исходной уборки.

`Map.slnx` содержит только семь production-проектов из `src`. Общие build-настройки сохраняют
версию `0.2.0`, ограничивают восстановление текущим `win-x64` и не создают self-contained bundle.
Ненужные нативные PDB Skia/HarfBuzz удаляются из output, управляемые PDB Map остаются.

Свежая проверка Stage 12B:

```text
dotnet test .\tests\Map.Presentation.Tests\Map.Presentation.Tests.csproj -c Release
  --filter "FullyQualifiedName~Stage12B"
PASS — 16 passed, 0 failed, 0 skipped

dotnet build .\Map.slnx -c Release
PASS — 0 warnings, 0 errors

background native UI smoke на текущем Windows ПК
PASS — настоящие скрытые Avalonia/Win32 MainWindow (`ShowActivated=false`,
`ShowInTaskbar=false`) без системного ввода: открыть отдельную карту с object/raster/mask/grid и
checkpoint; preview и grid snap; Cancel без изменений; crop, canvas clip, Undo/Redo; save/reopen;
создать и повторно открыть независимую региональную `.mapproj`; проверить новый id, canvas,
координаты, raster/mask, отсутствие checkpoints и неизменность источника
```

Полный suite, VM, packaging, benchmark, coverage и повтор зелёных адресных проверок не запускались:
они не нужны для Stage 12B и прямо исключены текущими правилами владельца.

## Другие checkout

`C:\Projects\Map` теперь содержит чистый локальный commit `9729764` — источник исходной уборки, но
не актуальную продуктовую базу. Ветка Stage 12B находится в worktree, указанном выше.

Worktree `fix/app-icon` остаётся owner-dirty и не изменялся. Утверждённая иконка уже находится в
консолидированной продуктовой истории.

## GitHub

- `origin`: `https://github.com/dmfromtheash/map.git`;
- repository `dmfromtheash/map` — private;
- владелец прямо разрешил необходимые commits и push;
- repo-local Git author использует официальный GitHub noreply-адрес владельца; глобальный приватный
  email и защита аккаунта не меняются;
- `main` остаётся на базе `e0010db`; результат Stage 12B публикуется только в
  `stage/12b-resize-crop-region`;
- default branch — `main`;
- созданы issues [#1](https://github.com/dmfromtheash/map/issues/1),
  [#2](https://github.com/dmfromtheash/map/issues/2),
  [#3](https://github.com/dmfromtheash/map/issues/3) и
  [#4](https://github.com/dmfromtheash/map/issues/4) для Stages 12A–12D;
- issue #2 остаётся открытым для решения владельца после итогового комментария Stage 12B;
- GitHub Project `Map` и issues 12A–12D настраиваются по `docs/GITHUB_PROJECTS.md`;
- статус Project нельзя изменить текущим токеном без `read:project`; интерактивная авторизация не
  запускалась.

Не привязывать Map к `nri-archive`, `ensi-bot`, `3-puti` или другому существующему продукту.

## Следующее единственное действие

После решения владельца по Stage 12B начать **Stage 12C** только отдельным прямым запросом. Не
переходить к export автоматически и не повторять уже зелёные проверки Stage 12B.
