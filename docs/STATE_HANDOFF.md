# STATE HANDOFF

## Актуально на 2026-08-31

Map — личное нативное offline-приложение владельца для его текущего Windows x64 ПК. Полезный
production-код Stages 1–11 сохранён. Stage 12A — именованные контрольные точки — реализован и
проверен. Отменена только лишняя Stage 11 программа для VM, универсальной чистой Windows,
installer/updater, release candidate и многократной независимой приёмки.

Следующий этап — **Stage 12B: resize, crop и новая карта из региона**. Он не начат.

## Актуальная продуктовая база

- Рабочий worktree:
  `C:\Users\Leshe\.codex\worktrees\stage10f-fonts-bindings\Map`.
- Ветка: `stage/12a-named-checkpoints`.
- База Stage 12A до изменения: `e0010db268b4a2fd4cb9867e1737271be204183a`.
- Результат Stage 12A: текущий `HEAD` этой ветки, один commit
  `feat(stage-12a): add named checkpoints`.
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

Свежая проверка Stage 12A:

```text
dotnet test .\tests\Map.Persistence.Tests\Map.Persistence.Tests.csproj -c Release
  --filter "FullyQualifiedName~Stage12A"
PASS — 6 passed, 0 failed, 0 skipped

dotnet build .\Map.slnx -c Release
PASS — 0 warnings, 0 errors

background native UI smoke на текущем Windows ПК
PASS — настоящий off-screen Avalonia/Win32 MainWindow без активации и системного ввода:
reopen checkpoint, immutable snapshot/preview, restore, Undo, Redo, cancel delete,
confirm delete, save и повторный reopen
```

Полный suite, VM, packaging, benchmark, coverage и повтор зелёных адресных проверок не запускались:
они не нужны для Stage 12A и прямо исключены текущими правилами владельца.

## Другие checkout

`C:\Projects\Map` теперь содержит чистый локальный commit `9729764` — источник исходной уборки, но
не актуальную продуктовую базу. Ветка Stage 12A находится в worktree, указанном выше.

Worktree `fix/app-icon` остаётся owner-dirty и не изменялся. Утверждённая иконка уже находится в
консолидированной продуктовой истории.

## GitHub

- `origin`: `https://github.com/dmfromtheash/map.git`;
- repository `dmfromtheash/map` — private;
- владелец прямо разрешил необходимые commits и push;
- repo-local Git author использует официальный GitHub noreply-адрес владельца; глобальный приватный
  email и защита аккаунта не меняются;
- `main` остаётся на базе `e0010db`; результат Stage 12A публикуется только в
  `stage/12a-named-checkpoints`;
- default branch — `main`;
- созданы issues [#1](https://github.com/dmfromtheash/map/issues/1),
  [#2](https://github.com/dmfromtheash/map/issues/2),
  [#3](https://github.com/dmfromtheash/map/issues/3) и
  [#4](https://github.com/dmfromtheash/map/issues/4) для Stages 12A–12D;
- issue #1 остаётся открытым для решения владельца после итогового комментария Stage 12A;
- GitHub Project `Map` и issues 12A–12D настраиваются по `docs/GITHUB_PROJECTS.md`;
- статус Project нельзя изменить текущим токеном без `read:project`; интерактивная авторизация не
  запускалась.

Не привязывать Map к `nri-archive`, `ensi-bot`, `3-puti` или другому существующему продукту.

## Следующее единственное действие

После решения владельца по Stage 12A начать **Stage 12B** только отдельным прямым запросом. Не
переходить к resize/crop автоматически и не повторять уже зелёные проверки Stage 12A.
