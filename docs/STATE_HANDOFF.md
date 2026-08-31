# STATE HANDOFF

## Актуально на 2026-09-01

Map — личное нативное offline-приложение владельца для его текущего Windows x64 ПК. Полезный
production-код Stages 1–11 сохранён. Stages 12A–12D реализованы и проверены: именованные
контрольные точки, resize/crop/новая карта из региона и полноценный локальный export с отменой.
Отменена только лишняя Stage 11
программа для VM, универсальной чистой Windows, installer/updater, release candidate и
многократной независимой приёмки.

**Stage 12D: migrations и диагностика файла** выполнен по прямому запросу владельца в ветке
`stage/12d-migrations-file-diagnostics`: canonical schema v2, явная copy-only миграция v1→v2,
typed file diagnostics, unknown-object payload, async Dashboard flow и privacy-safe local log.
Исходный v1 никогда не становится save/autosave target и не меняет bytes/timestamp.

Свежая проверка Stage 12D на текущем worktree:

```text
Map.Persistence.Tests --filter FullyQualifiedName~Stage12D: PASS 8/8
Map.Presentation.Tests --filter FullyQualifiedName~Stage12D: PASS 6/6
dotnet build .\Map.slnx -c Release: PASS, 0 warnings, 0 errors
native-stage12d: PASS — real hidden Avalonia/Win32 dashboard + migration prompt; safe Cancel;
validated v2 copy opened clean; source unchanged; corruption/resource/future states distinct;
UI responsive; window closed; unique temporary profile removed
```

## Актуальная продуктовая база

- Рабочий worktree:
  `C:\Users\Leshe\.codex\worktrees\stage10f-fonts-bindings\Map`.
- Ветка: `stage/12d-migrations-file-diagnostics`.
- Принятая стартовая база Stage 12C: `45252b944af8a059cc1a51c2dfb562a12385de22` — локальное
  принятие Stage 12B и исполняемый контракт Stage 12C поверх production commit `5863099`.
- Результат Stage 12C записан одним локальным commit
  `feat(stage-12c): add bounded image export and cancellation`; точный hash — текущий `HEAD` этой
  ветки после завершения задачи.
- База Stage 12B до изменения: `750326f967b650b402c67146ab195c2e49c7c4bc` — принятый
  результат `stage/12a-named-checkpoints`.
- Production-результат Stage 12B: `5863099ce7716812b231deb8dcfe496655a9bd56`, один commit
  `feat(stage-12b): add canvas resize and region extraction`; принят владельцем 2026-08-31.
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
- заметная панель «Экспорт…» с PNG/JPEG/WebP, whole-map/region, точным output size, связью
  пропорций, transparent/background/grid policy, bounded preview и честной raw-memory estimate;
- фоновый snapshot-safe export с monotonic progress и фактической отменой; операция не меняет
  document/revision/dirty/history/selection/viewport/path/checkpoints/autosave;
- атомарный single-file publish через sibling temp, flush и decode/read-back; cancel/error сохраняет
  прежний destination byte-for-byte;
- package export до 65 536 px: последовательные tiles 2048 px с вычисленным effect bleed,
  deterministic filenames, manifest v1, read-back каждого tile и публикация папки одним rename;
- отдельный production-проект `Map.Export`, использующий общий `SceneRenderer` и собственные
  bounded caches/resolver без UI-owned Skia state;
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

`Map.slnx` содержит только восемь production-проектов из `src`, включая `Map.Export`. Общие build-настройки сохраняют
версию `0.2.0`, ограничивают восстановление текущим `win-x64` и не создают self-contained bundle.
Ненужные нативные PDB Skia/HarfBuzz удаляются из output, управляемые PDB Map остаются.

Свежая проверка Stage 12C:

```text
dotnet test .\tests\Map.Presentation.Tests\Map.Presentation.Tests.csproj -c Release
  --filter "FullyQualifiedName~Stage12C"
PASS — 8 passed, 0 failed, 0 skipped

dotnet build .\Map.slnx -c Release
PASS — 0 warnings, 0 errors

background native UI smoke на текущем Windows ПК
PASS — настоящее скрытое Avalonia/Win32 окно (`ShowActivated=false`, `ShowInTaskbar=false`) без
системного ввода: открыть «Экспорт…» на отдельной vector/raster/mask/asset/grid карте; получить
bounded preview; создать и decode/read-back проверить PNG региона 320×240; запустить multi-tile
операцию, подтвердить отзывчивость UI, отменить, проверить отсутствие опубликованной папки/temp и
неизменность source document/session
```

Полный suite, VM, packaging, benchmark, coverage и повтор зелёных адресных проверок не запускались:
они не нужны для Stage 12C и прямо исключены текущими правилами владельца.

## Другие checkout

`C:\Projects\Map` теперь содержит чистый локальный commit `9729764` — источник исходной уборки, но
не актуальную продуктовую базу. Ветка Stage 12C находится в worktree, указанном выше.

Worktree `fix/app-icon` остаётся owner-dirty и не изменялся. Утверждённая иконка уже находится в
консолидированной продуктовой истории.

## GitHub

- Существующее состояние remote сохранено без изменений.
- Владелец 2026-08-31 прямо решил: **GitHub не трогать**.
- До нового отдельного разрешения запрещены push/fetch, PR, issue/comment/close, GitHub Project,
  auth/scope, tag/release и любые другие сетевые действия с repository.
- Локальные ветки и commits допустимы только в рамках прямо запрошенной локальной работы.
- Подробная замороженная граница записана в `docs/GITHUB_PROJECTS.md`.

Не привязывать Map к `nri-archive`, `ensi-bot`, `3-puti` или другому существующему продукту.

## Следующее единственное действие

Сначала использовать реализованный Stage 12C в реальной работе владельца. Stage 12D не начинать
без отдельного прямого запроса; зелёные проверки Stage 12C не повторять без изменения входов.
GitHub не трогать.
