# STATE HANDOFF

## Актуально на 2026-08-31

Map — личное нативное offline-приложение владельца для его текущего Windows x64 ПК. Полезный
production-код Stages 1–11 сохранён. Отменена только лишняя Stage 11 программа для VM, универсальной
чистой Windows, installer/updater, release candidate и многократной независимой приёмки.

Следующий этап — **Stage 12A: именованные checkpoints**. Историческое имя 9B больше не используется.

## Актуальная продуктовая база

- Рабочий worktree:
  `C:\Users\Leshe\.codex\worktrees\stage10f-fonts-bindings\Map`.
- Ветка: `stage/12a-named-checkpoints`.
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

Свежая проверка checkpoint `825d9df`:

```text
dotnet build .\Map.slnx -c Release
PASS — 0 warnings, 0 errors, 13 seconds
Map.App Release output — 31.1 MiB
весь рабочий worktree после сборки — 45 MiB
```

Full test suite, VM, packaging, benchmark, coverage и повтор зелёной сборки не запускались: они не
нужны для этой уборки и прямо исключены текущими правилами владельца.

## Другие checkout

`C:\Projects\Map` теперь содержит чистый локальный commit `9729764` — источник исходной уборки, но
не актуальную продуктовую базу. Ветка Stage 12A находится в worktree, указанном выше.

Worktree `fix/app-icon` остаётся owner-dirty и не изменялся. Утверждённая иконка уже находится в
консолидированной продуктовой истории.

## GitHub

- `origin`: `https://github.com/dmfromtheash/map.git`;
- repository `dmfromtheash/map` — private;
- владелец прямо разрешил необходимые commits и push;
- первым `main` должен стать консолидированный checkpoint, а не старый local `main`;
- GitHub Project `Map` и issues 12A–12D настраиваются по `docs/GITHUB_PROJECTS.md`;
- дополнительный scope `project` запрошен и ждёт обязательного подтверждения device-code на
  официальной странице GitHub.

Не привязывать Map к `nri-archive`, `ensi-bot`, `3-puti` или другому существующему продукту.

## Следующее единственное действие

После публикации правильных `main` и `stage/12a-named-checkpoints` выполнить Stage 12A по
`docs/STAGE_12A_NAMED_CHECKPOINTS.md`. Cleanup завершён; повторно переносить или чистить Stage 10–11
не нужно.
