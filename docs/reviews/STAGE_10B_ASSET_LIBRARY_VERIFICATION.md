# Stage 10B — проверка Library v2 и наборов вариантов

## Статус

`FACT / PARTIAL` на 27.08.2026.

- `FACT`: точный Claude-диапазон `dfc2d1d..172f1b4` прочитан и пересчитан Codex; блокирующих
  дефектов production-кода не найдено.
- `FACT`: два пропущенных background-сценария драйвера — выбор media type и exact tag — теперь
  выполняются через UI Automation без вывода окна на рабочий стол.
- `PARTIAL`: keyboard-only и настоящий Windows scale 100/150/200% не доказаны фоновым прогоном.
- `BLOCKED`: нативный Windows file picker не возвращается под текущим automation host; проверка
  использует изолированные `MAP_UI_TEST_*` пути.
- `BLOCKED`: визуальный вывод этой же рабочей сессии не считается независимой clean-context
  критикой. Поэтому весь Stage 10B формально не объявляется закрытым.

## Git-снимок Claude

- Branch: `stage/10-asset-library-sets`.
- BASE: `dfc2d1d`.
- Reviewed HEAD: `172f1b410cc9db18ae7bfd57e61c111117280add`.
- Перед исправлением драйвера worktree был чистым.
- Диапазон: `A=0`, `M=5`, `D=0`, `R=0`; tracked files: `262`.
- Изменены только:
  - `docs/00_START_HERE.md`;
  - `docs/06_DEVELOPMENT_ROADMAP.md`;
  - `docs/STATE_HANDOFF.md`;
  - `scripts/drive-stage10b-asset-library.ps1`;
  - `src/Map.App/Views/EditorView.cs`.
- Все 15 `.csproj` находятся в `Map.slnx`; все `ProjectReference` существуют, запрещённой
  зависимости `Map.Core -> Avalonia/Skia` нет.

## Findings и исправление

1. Background-driver не выбирал `LibraryMediaFilter`, хотя отчёт обобщал проверку форматов.
2. Background-driver не выбирал `LibraryTagFilter`, хотя tag grouping проходил.

Оба условных пропуска удалены. ComboBox выбираются через UI Automation, которая работает и для
окна за пределами рабочего стола. Для exact-tag состояния добавлен отдельный PNG.

## Автоматическая проверка после исправления

- `scripts/doctor.ps1` — PASS.
- `dotnet restore Map.slnx --force-evaluate` — PASS.
- Release build — PASS, `0` warnings, `0` errors.
- Full test suite — `333/333`: Core `82`, Rendering `16`, Persistence `52`, Assets `65`,
  Presentation `118`.
- NuGet vulnerable — нет.
- NuGet deprecated — только прежний `xunit 2.9.3` (`Legacy`).
- `dotnet format --verify-no-changes --severity info` — legacy failure, exit `1`; изменённый
  PowerShell-driver не добавляет C# diagnostics.
- `git diff --check` — PASS.

## Настоящее приложение и артефакт

Фоновый Release-driver запустил настоящий `Map.App.exe` в изолированной Library, не активировал
окно владельца и завершился `PASS`:

- отчёт: `artifacts/stage10b-codex-filter-fix-final/report.json`;
- `99` записанных проверок;
- `22` PNG по `2500x1460`;
- media filter `image/png` реально выбран и присутствует в строке активных фильтров;
- exact tag `рельеф` реально сужает каталог до одной карточки; отдельный снимок
  `11-exact-tag-filter.png`;
- сохранённый `maps/stage10b.mapproj` — читаемый ZIP с `scene.json`, `323` stamps и `12`
  различными SHA;
- undo/redo, reopen без локального каталога, missing/hash-mismatch, atomic refusal и damaged
  index повторно прошли;
- после прогона процессов `Map.App` не осталось.

Артефакты игнорируются Git и не являются release-материалами.

## Acceptance matrix

| Область | Статус | Доказательство |
|---|---|---|
| Упрощённый основной путь Library | `FACT` | свежий Release-driver и просмотр PNG |
| Search/sort/group/filters/reset | `FACT` | UIA assertions; media/exact-tag теперь включены в background |
| Sets/variants/rename/reorder/cover | `FACT` | UIA + restart + `catalog.json` |
| One/Area placement, undo/redo | `FACT` | UIA + сохранённый `.mapproj` |
| Missing/mismatch/damaged/atomic refusal | `FACT` | failure paths драйвера |
| Keyboard-only | `PARTIAL` | намеренно не перехватывался ввод владельца |
| High-DPI | `PARTIAL` | `-SkipDpi`; прежний env override не меняет реальный Windows scale |
| Native file picker | `BLOCKED` | automation host не возвращает диалог |
| Независимая clean-context UX-критика | `BLOCKED` | текущая сессия участвовала в предыдущем UX-контексте |

## Следующий шаг

Не смешивать оставшиеся UI-gates 10B с production-кодом 10C. Следующий функциональный срез по
дорожной карте — 10C multi-selection/groups; его prompt должен сохранять перечисленные ограничения
10B и не объявлять весь Stage 10 завершённым.
