# STATE HANDOFF

## Актуально на 2026-08-31

Map — личное нативное offline-приложение владельца для его текущего Windows x64 ПК. Stage 11 как
release/VM/packaging-программа отменён, но это не откатывает полезный production-код Stage 10 или
Stage 11.

Следующий этап называется **Stage 12A — именованные checkpoints**. Историческое название 9B больше
не используется для новой ветки.

## Последний продуктовый снимок

- Последний HEAD линейной продуктовой истории:
  `2c0a698e15573badb22a54de7ff1687ab784a9b2`.
- Ветка-указатель: `fix/stage11-native-acceptance`.
- Этот HEAD является потомком Stage 10E, Stage 10F и Stage 11.
- Новая продуктовая ветка создаётся от него либо от более нового явно принятого checkpoint,
  записанного в этом файле.
- Начинать от `1387b1b` Stage 10F, `2af5bd3` Stage 10D или другого старого checkout запрещено:
  это отбросит уже существующие функции.

Полезное, которое обязательно сохраняется:

- четыре geometry сетки, включая отдельную `IsometricSquares`, общий snap/render и property sheet;
- локальный системный font catalog и редактируемые keyboard bindings;
- иконка приложения;
- изоляция тестового профиля от настоящих пользовательских Diagnostics/Recovery/Assets/recent;
- весь более ранний editor, Library, persistence, rendering и UI.

## Текущий checkout с уборкой

- Корень: `C:\Projects\Map`.
- Ветка: `stage/10d-object-inspectors`.
- Исходный HEAD: `2af5bd3495851f032897435fc957f5705a692bbe`.
- Здесь находится незакоммиченная уборка process/release-мусора и упрощение правил под DEC-016.
- Этот checkout нужен как источник смысла уборки, но не как функциональная база Stage 12A.
- Нельзя делать reset/restore/stash или переключать эту грязную папку на поздний commit.

До уборки Git показывал изменения владельца в пяти путях:

- `src/Map.Assets/CatalogRelativePath.cs`;
- `src/Map.Core/Documents/AppearanceContract.cs`;
- `src/Map.Core/Documents/ProjectLinkedAssetPath.cs`;
- `tests/Map.Persistence.Tests/Stage10DInspectorRoundTripTests.cs`;
- `tests/Map.Presentation.Tests/RecentMapsStoreTests.cs`.

Сейчас нормализованное содержимое этих файлов совпадает с их old-HEAD blobs, но их всё равно не
нужно сбрасывать или присваивать новой работе: текущий checkout сохраняется как есть до
консолидации.

Отдельный worktree `fix/app-icon` также грязный и принадлежит владельцу. Его файлы не удалять и не
переносить автоматически. Иконка, уже вошедшая в product HEAD `2c0a698`, сохраняется независимо.

## Что сделано уборкой

- удалены Stage 11 VM/ISO/QEMU и три чистых лишних worktree; Git-ветки/commits сохранены;
- очищены `bin/obj/artifacts/tmp`, в том числе игнорируемые outputs других worktree;
- удалены исторические spike, benchmarks, diagnostic generator, UI drivers и тяжёлые process docs;
- удалены pre-commit/tool/coverage/Stryker manifests;
- `Map.slnx` сокращён до семи production-проектов из `src`;
- framework-dependent build ограничен `win-x64`;
- нативные PDB Skia/HarfBuzz удаляются из output, управляемые PDB Map сохраняются;
- Release build прошёл с 0 warnings/errors; output Map.App — 29,9 MiB, весь текущий checkout после
  сборки — 37,7 MiB.

При переносе уборки поверх последнего product HEAD нужно объединить, а не заменить
`Directory.Build.props`: сохранить version `0.2.0` из поздней линии и добавить `win-x64` /
`SelfContained=false` из уборки.

## GitHub — текущее состояние

- `origin`: `https://github.com/dmfromtheash/map.git`.
- `dmfromtheash/map` существует как private repository; до первого push он был пустым.
- `.github/` отсутствует.
- `gh` авторизован как `dmfromtheash` через HTTPS.
- Существующие репозитории других проектов к Map не относятся.
- Токен не имеет `read:project`, поэтому GitHub Projects сейчас нельзя даже перечислить.
- Владелец прямо разрешил необходимые локальные commits и push в этот repository.

Текущая/целевая связь:

`C:\Projects\Map` → private `dmfromtheash/Map` (`origin`) → user Project `Map` → issues по одному
на Stage 12A–12D.

Не привязывать Map к `nri-archive`, `ensi-bot`, `3-puti` или другому существующему репозиторию.
Подробности: `docs/GITHUB_PROJECTS.md`.

## Следующее единственное действие

В чистом подходящем worktree создать `stage/12a-named-checkpoints` **от**
`2c0a698e15573badb22a54de7ff1687ab784a9b2`, перенести поверх него смысл текущей уборки без потери
production-кода, сделать один локальный cleanup checkpoint и затем выполнить Stage 12A по
`docs/STAGE_12A_NAMED_CHECKPOINTS.md`.

Remote и push разрешены. GitHub Project настраивается после получения `project` scope; отсутствие
Project не должно приводить к отправке неправильной ветки или откату product HEAD.
