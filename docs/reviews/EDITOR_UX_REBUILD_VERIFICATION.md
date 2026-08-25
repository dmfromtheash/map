# Независимая проверка Editor UX Rebuild

Дата проверки: 2026-08-26.

Статус: **PARTIAL / НЕ ПРИНЯТО**.

Проверяемый снимок собирается и запускается, полный набор тестов зелёный, а основной оконный
сценарий воспроизводится. Приёмку блокируют два accessibility-дефекта и неверная семантика
счётчика несохранённых изменений. Автодрайвер также не покрывает один из семи инструментов и не
доказывает эффект undo/redo.

## Git identity и область

- Branch: `fix/editor-ux-rebuild`.
- BASE: `7eae9f3afba5a64e6804b852c350d1b7d8997b52`.
- HEAD: `d42433b32600eb748490cc13bfb2c625127c27cf`.
- Перед созданием этого отчёта worktree был чистым; `git status --porcelain=v2 --branch` содержал
  только branch metadata.
- Диапазон: `A=6`, `M=6`, `D=0`, `R=0`, всего 12 файлов, `+4416/-901`.
- Всего tracked files: 218.
- `git diff --check BASE..HEAD`: exit 0.
- В diff нет `bin`, `obj`, recovery/export/benchmark-артефактов и изображений.

Затронуты:

- `scripts/drive-ux-overhaul.ps1` — A;
- `src/Map.App/App.axaml` — M;
- `src/Map.App/Controls/MapIcons.cs` — A;
- `src/Map.App/MainWindow.cs` — M;
- `src/Map.App/Services/TestMode.cs` — A;
- `src/Map.App/Views/DashboardView.cs` — M;
- `src/Map.App/Views/EditorView.cs` — M;
- `src/Map.App/Views/Ui.cs` — A;
- `src/Map.App/Views/WizardWindow.cs` — M;
- `src/Map.Presentation/ViewModels/EditorLabels.cs` — A;
- `src/Map.Presentation/ViewModels/EditorViewModel.cs` — M;
- `tests/Map.Presentation.Tests/EditorPresentationStateTests.cs` — A.

Solution содержит все 14 `*.csproj` из `src`, `tests` и `benchmarks`. Все `ProjectReference`
разрешаются. Направление production-ссылок сохранено: `Map.Core` ни от чего не зависит,
Persistence/Rendering/Assets/Platform.Windows зависят от Core, Presentation — от Core и
прикладных библиотек, App остаётся composition root. Новых пакетов в diff нет.

## Автоматические проверки

| Проверка | Результат |
|---|---|
| `dotnet clean Map.slnx -c Release` | exit 0 |
| `dotnet restore Map.slnx --force-evaluate` | exit 0 |
| `dotnet build Map.slnx -c Release --no-restore` | exit 0; 0 warnings, 0 errors |
| `dotnet test Map.slnx -c Release --no-build --blame-hang --blame-hang-timeout 2m` | exit 0; 181/181 |
| Core / Persistence / Rendering / Assets / Presentation | 61 / 45 / 16 / 6 / 53 |
| `git diff --check BASE..HEAD` | exit 0 |
| `pre_commit run --all-files` с установленным `lychee 0.24.2` в process-local PATH | exit 0; все hooks Passed |
| `scripts/doctor.ps1` | exit 0 |
| NuGet vulnerable, direct + transitive | exit 0; известных уязвимостей нет |
| NuGet deprecated, direct + transitive | exit 0; xUnit 2.9.3 Legacy в пяти test-проектах, существующая вне diff зависимость |
| `dotnet format analyzers ... --severity warn` по изменённым C# | exit 0 |
| `dotnet format whitespace ...` по изменённым C# | exit 2 на `EditorViewModel.cs:905-906` |

Форматирование не является регрессией этого снимка: обе whitespace-строки принадлежат коммиту
`d657dcf6` и не входят в hunks `BASE..HEAD`. Полный `dotnet format --severity info` дополнительно
показывает информационные IDE/CA-рекомендации. Gate форматирования репозитория формально не зелёный,
но новый diff не добавляет warning-level analyzer diagnostics.

Security/secret scan не запускался по прямому правилу проекта. Выполнен только предусмотренный
NuGet audit зависимостей.

## Настоящее окно Release EXE

Запущен точный файл
`src/Map.App/bin/Release/net10.0/Map.App.exe`, SHA-256
`70BA759C5C197F97659FEF6B7662285196895882EC5E3CA1913E7E557C12360E`.

Проектный UIA-драйвер завершился exit 0 и записал 16 декодируемых PNG плюс `evidence.json` в
`artifacts/codex-ux-review-d42433b/driver/`. Все 16 сохранённых PNG независимо открыты и
просмотрены. Подтверждены:

1. dashboard с основной кнопкой новой карты, открытием проекта, недавней картой и плиткой `+`;
2. мастер с длинным русским названием и пользовательским размером `65536x65536`;
3. редактор с пятью различимыми зонами;
4. контекстные панели Select, Line, Text, Brush, Mask и Land;
5. слои, выделение Line, отдельный инспектор Text, Assets и Notes;
6. видимый keyboard focus, переключение B/L/V и отправка Ctrl+Z;
7. два нативных окна одного процесса;
8. minimum client area `960x640` (outer `976x679`) без горизонтального скролла;
9. `tcpListeners=0`.

Отдельный скрытый запуск подтвердил реальный native window handle, точный executable path,
`tcpListeners=0` и отсутствие модулей `libcef`, `WebView2`, Chromium/Chrome/Electron. Каждый
созданный проверкой процесс завершён; оставшихся `Map.App.exe` нет.

## Acceptance matrix

| Пункт | Статус | Доказательство / предел |
|---|---|---|
| Dashboard и мастер новой карты | FACT | PNG 01-03, UIA ids, long Russian title, 65536x65536 |
| Пять зон редактора и одна контекстная область | FACT | PNG 03-12; заголовок соответствует текущему tool/selection |
| Все инструменты | PARTIAL | Реальный UI содержит 7; драйвер проверяет 6 и пропускает Shape |
| Слои, Objects, Assets, Notes | FACT | PNG 10-13 и UIA assertions |
| Undo/redo | PARTIAL | кнопки и Ctrl+Z вызываются, но драйвер не сравнивает документ до/после; счётчик неверен |
| Keyboard path и focus | PARTIAL | Tab и B/L/V подтверждены; все команды и shortcut не пройдены |
| Второе нативное окно | FACT | UIA count = 2 |
| Minimum 960x640 | FACT | client 960x640, screenshot 976x679 |
| Offline/native runtime | FACT | 0 listeners, 0 WebView/CEF/Chromium/Electron modules |
| Доступный selected/checked state | BLOCKED | активность задана CSS-классом; UIA selected state отсутствует |
| Контраст UI-текста | BLOCKED | `MapFaintBrush` даёт 3.739:1 на panel и 3.989:1 на surface при требовании 4.5:1 |
| Полный регресс Stages 2-8 | BLOCKED | старые UI-драйверы ещё не обновлены под новое дерево |
| DPI 150/200 %, save-as/reopen/export | BLOCKED | в этом срезе независимо не прогонялись |

## Findings

### P1 — активное состояние не опубликовано в accessibility tree

`Ui.ToolButton` создаёт обычный `Button`, а `SetToolActive` меняет только CSS-класс и видимость
декоративного marker. Аналогично presets/background в мастере и строки структуры используют
обычные кнопки/Border с классом. В реальном UIA-снимке `selected_elements` пуст даже при явно
активном инструменте, пресете и слое. Пользователь screen reader не получает `selected/checked`
state, а QA-требование `docs/08_QA_AND_ACCEPTANCE.md` требует role/control type и selected state.

### P1 — `MapFaintBrush` не проходит WCAG AA для обычного текста

`#6B7583` даёт 3.739:1 на `#161A20` и 3.989:1 на `#101317`. Brush используется не только для
декора: `EmptyState` применяет его к 12 px инструкции, `StructureRow` — к вторичной подписи,
`PropertyRow` — к единицам, dashboard placeholder — к 12 px подписи. Требование проекта — минимум
WCAG AA, то есть 4.5:1 для такого текста.

### P2 — undo увеличивает число «несохранённых изменений»

`PendingChanges = Revision - SavedRevision`, но `EditingSession.Revision` монотонно увеличивается
при Execute, Undo и Redo. В настоящем окне после Ctrl+Z объект исчез и Redo включился, однако
строка выросла с `2` до `3` изменений. Это противоречит комментарию «edits made since save» и
тесту «how many edits are at risk». Тесты проверяют только последовательное добавление и не
покрывают save/edit/undo.

### P2 — UIA-драйвер оставляет слепые зоны

В редакторе семь инструментов, но массив `contextTitles` и снимки 04-09 не включают
`ShapeToolButton`. Ctrl+Z вызывается, однако документ/selection/count до и после не сравниваются;
semantic selected/checked state тоже не проверяется. Поэтому exit 0 драйвера не доказывает
полный keyboard/accessibility/undo acceptance.

## Точный следующий fix-prompt

Создать новый отдельный fix-коммит поверх `d42433b`; проверенный commit не amend/rebase.

1. Перевести mutually-exclusive tool/preset/mode controls на семантические ToggleButton/selection
   controls либо эквивалентно публиковать UIA `checked/selected` state. Для layer/object rows
   использовать реальную selection-модель, не класс на внешнем Border. Добавить UIA assertions.
2. Для фактического текста заменить `MapFaintBrush` на цвет с контрастом не ниже 4.5:1 на обеих
   поверхностях; faint оставить только для крупной графики/декора. Повторить контрастные проверки.
3. Исправить модель счётчика так, чтобы undo к сохранённому снимку давал ноль изменений, а redo
   возвращал корректное значение. Добавить regression tests для save -> edit -> undo и
   edit -> undo -> redo. Не подменять смысл строкой про число операций.
4. Расширить `drive-ux-overhaul.ps1`: Shape panel/shortcut, asserted effect undo/redo,
   selected/checked state и reachability при minimum size.
5. После нового коммита повторить Gate A/B/C, обновлённый UX driver и регрессионные драйверы
   Stages 2-8. Отдельно пройти 100/150/200 % DPI и save-as/reopen/export.
