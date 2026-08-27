# Stage 10C — проверка multi-selection и групп

## Статус

`FACT / PARTIAL` на 27.08.2026.

- `FACT`: production-логика multi-selection/groups независимо проверена; два найденных дефекта
  исправлены отдельным Codex fix-срезом и закрыты regression-тестами.
- `PARTIAL`: настоящий нативный ввод keyboard/pointer намеренно не отправлялся в фоновое окно;
  marquee, Ctrl+click, Esc и keyboard transforms подтверждены адресными тестами и routing-review.
- `BLOCKED`: clean-context UX-критика не подменяется просмотром в текущем проектном контексте.

## Проверенный Git-снимок Opus

- Branch: `stage/10c-multi-selection-groups`.
- BASE: `58bef7a8324a8da06ba7ddb72e3575341df80fb8`.
- Reviewed HEAD: `5e87cf51c0c402b4b0f5b2a85d28ed85a19452c4`.
- Один прямой commit; исходный worktree чистый.
- Диапазон: `A=11`, `M=19`, `D=0`, `R=0`; tracked files: `274`.
- Все проекты перечислены в `Map.slnx`; запрещённых новых project/package boundaries нет.

## Findings

1. `SelectAllByFilter` сканировал отдельные объекты всего документа вместо текущих строк
   `ObjectTree`. Закрытая группа могла выделиться частично; поиск по её имени показывал строку,
   но выбирал `0`; внутри открытой группы Ctrl+A захватывал outsiders.
2. `EditableSelectionList` проверял только `Visible/Locked` объекта. Член межслойной группы на
   скрытом или заблокированном `ObjectLayer` всё равно изменялся общей трансформацией.
3. Верхняя сводка `STATE_HANDOFF.md` ошибочно утверждала, что Stage 10C не начат.

## Исправление

- Ctrl+A проходит по фактически видимым строкам `ObjectTree`.
- Закрытая group-row разворачивается во всех участников или пропускается целиком, если хотя бы
  один участник недоступен.
- Внутри открытой группы выбираются только её отфильтрованные child-rows.
- Доступные ObjectId строятся одним линейным проходом по видимым незаблокированным слоям и
  объектам; повторного квадратичного поиска слоя для каждого объекта нет.
- Move/scale/rotate/sublayer/opacity отклоняются одной операцией, если объект или его слой
  скрыт/заблокирован.
- Background-driver проверяет обе исправленные Ctrl+A ветви через настоящее Release-окно и UIA.

## Red/green и автоматические проверки

- До production-правки пять новых cases воспроизвели оба исходных дефекта: `5 failed / 0 passed`.
- После правки весь `Stage10CMultiSelectionViewModelTests`: `33/33`.
- Добавлен ещё один guard-case: закрытая группа с недоступным участником пропускается целиком.
- Полный suite: `436/436` — Core `137`, Persistence `57`, Rendering `20`, Assets `65`,
  Presentation `157`.
- `scripts/doctor.ps1` — PASS.
- Locked restore — PASS.
- Release build — PASS, `0` warnings, `0` errors.
- NuGet vulnerable — нет.
- NuGet deprecated — только прежний `xunit 2.9.3` (`Legacy`).
- `dotnet format --verify-no-changes --severity info` — прежний legacy failure, exit `2`; в
  изменённых строках production/test C# новых diagnostics нет.
- `pre-commit run --all-files` — PASS.
- `git diff --check` — PASS.

## Настоящее приложение

Свежий запуск
`scripts/drive-stage10c-multi-selection-groups.ps1 -Background` завершился `PASS`:

- `16` PNG `2500x1460`;
- `39` записанных checks;
- поиск по имени `Тестовая группа` сохраняет одну group-row и Ctrl+A выбирает всех трёх членов;
- внутри breadcrumb Ctrl+A выбирает только трёх детей, не пять внешних объектов;
- прежние group/undo/redo/reopen/mixed-state/delete сценарии повторно прошли;
- отчёт: `artifacts/stage10c-fix-codex-commit-20260827/report.json`;
- окно находилось вне рабочего стола и не принимало клавиатуру владельца;
- после завершения процессов `Map.App` не осталось.

## Acceptance matrix

| Область | Статус | Доказательство |
|---|---|---|
| Закрытая группа как атомарная строка и Ctrl+A | `FACT` | red/green tests + UIA driver |
| Ctrl+A внутри открытой группы | `FACT` | red/green test + UIA driver |
| Блокировка/скрытие слоя при общей трансформации | `FACT` | два red/green cases |
| Group persistence/undo/redo/delete | `FACT` | полный suite + повторный Release-driver |
| Native keyboard/pointer input | `PARTIAL` | безопасный background-режим не отправляет ввод |
| Независимая clean-context UX-критика | `BLOCKED` | текущий контекст знает спецификацию и findings |

## Следующий шаг

Следующий отдельный функциональный срез — **10D Object inspectors**: Stamp/Text/Line/Shape/Brush
controls и color/effects parity. Не смешивать его с 10E Grid geometry, 10F font catalog или
Stage 9B–9E; не закрывать прежние PARTIAL/BLOCKED gates декларативно.
