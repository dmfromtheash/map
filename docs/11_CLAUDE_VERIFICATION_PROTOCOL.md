# 11. Обязательная проверка работы Claude Code Opus

## Назначение

Отчёт Opus, зелёная сборка и один успешный запуск не являются приёмкой. Каждый ограниченный срез сначала фиксируется отдельным коммитом Opus, затем ChatGPT / Codex проверяет точный диапазон Git, запускает свежие проверки и наблюдает пользовательский результат. Работа переходит дальше только после независимого статуса `FACT`.

Собственных анализаторов в проекте нет. Используются готовые инструменты из переданного списка и официальных источников.

## Подготовленные навыки и инструменты

| Средство | Версия / источник | Что действительно доказывает | Чего не доказывает |
|---|---|---|---|
| Claude Superpowers | plugin `6.3.0`, официальный Claude marketplace | TDD, системная отладка, запрос ревью, обязательные свежие доказательства перед завершением | не заменяет независимое ревью Codex; автоматическая оркестрация субагентов не разрешена этим проектом |
| Interfaces | plugin `1.6.2`, `jakubkrehel/skills` | accessibility, layout, typography и diff-review интерфейса | автор прямо отделяет это от correctness, security, tests и performance |
| pre-commit | `4.6.2` | запускает готовые быстрые Git-hooks до каждого коммита | не принимает функциональность этапа |
| pre-commit-hooks | `v6.0.0` | конфликт имён/регистра, merge markers, JSON/YAML, symlinks, большие файлы, private keys, line endings, запрет submodules и прямого коммита в `main` | не анализирует поведение приложения |
| Gitleaks | `8.30.1` | ищет известные форматы секретов в staged diff и истории | эвристика не гарантирует отсутствие любого секрета |
| Lychee | `0.24.2` | проверяет локальные Markdown-ссылки и anchors перед коммитом; полный сетевой прогон запускается отдельно | доступность внешнего сайта не гарантирует правильность содержания |
| dotnet-coverage | local tool `18.10.0`, Microsoft | фактическое покрытие исполнявшегося .NET-кода и объединение отчётов | высокий процент не означает сильные assertions |
| Stryker.NET | local tool `4.16.0` | временно вносит ошибки в production code и показывает, способны ли тесты их заметить | один mutation score без разбора survivors не является приёмкой |
| .NET SDK analyzers / NuGet Audit | SDK `10.0.400` | compiler/analyzer diagnostics и известные уязвимости прямых и транзитивных NuGet-зависимостей | не доказывает отсутствие логических или неизвестных уязвимостей |
| .NET diagnostics | Microsoft `dotnet-counters`, `dotnet-trace`, `dotnet-gcdump`, `dotnet-dump`, `dotnet-stack` по необходимости | реальные CPU, exceptions, allocation/GC, trace, dump и thread stacks работающего процесса | не заменяет воспроизводимый benchmark и пользовательский сценарий |
| Avalonia Headless | официальный Avalonia test package, подключается когда появляется UI test project | control tree, layout, binding, ввод и pixel capture через Skia без окна | не доказывает работу native window, focus, menus и accessibility ОС |
| Appium + WinAppDriver | официальный путь Avalonia для Windows, подключается на UI-этапе | управляет собранным EXE через accessibility tree в настоящем окне | не оценивает визуальное качество canvas без screenshot/golden/manual review |
| FsCheck | подключается к тестовому проекту для инвариантов | ищет минимальный контрпример на множестве сгенерированных входов | свойства должны быть корректно сформулированы и проверены ревьюером |
| ArchUnitNET | подключается при закреплении слоёв архитектуры | автоматически ловит запрещённые зависимости, например `Map.Core -> Avalonia/Skia` | не заменяет смысловое архитектурное ревью |
| Repomix | `v1.18.0`, одноразовый `npx`, только локальный repo | полный снимок учитываемых файлов, tree/token inventory и контекст diff для ревью | не является тестом и не заменяет `git diff` |
| Codex Security | уже установленный security diff/full scan | отдельный security-проход по коммиту или repository | запускается со своей явно указанной областью; не заменяет runtime checks |
| Computer Use | уже доступен Codex | ручной smoke настоящего Windows-окна и проверка видимого результата | наблюдение без файла/process evidence недостаточно |

FsCheck, ArchUnitNET, Avalonia Headless, Appium и диагностические CLI не добавляются «на всякий случай». Они подключаются в том этапе, где существует проверяемый объект и acceptance-сценарий.

## Git-протокол одного среза

1. Тимлид фиксирует принятый baseline на `main`.
2. Для среза создаётся ветка `stage/NN-short-name` или `fix/short-name`.
3. До кода задание фиксирует base commit, разрешённые пути, acceptance и реальные failure paths.
4. Opus использует только относящиеся к задаче навыки: `test-driven-development`, `systematic-debugging`, `requesting-code-review`, `receiving-code-review`, `verification-before-completion`. Автоматический запуск параллельных агентов не разрешён без отдельного решения владельца.
5. Перед своим коммитом Opus обязан запустить свежую полную проверку для сделанного среза, прочитать весь вывод и указать hash коммита. Нельзя коммитить известный failing state как завершённый этап.
6. После коммита Opus останавливается. Новую функцию он не начинает.
7. Codex проверяет точный диапазон `BASE..HEAD` и отдельно рабочее дерево. Незакоммиченные изменения означают, что проверяемого снимка ещё нет.
8. Найденный дефект не исправляется через `--amend`, rebase или переписывание истории. Opus получает отдельный узкий fix-prompt и создаёт новый исправляющий коммит.
9. Codex повторяет не только упавшую проверку, но и полный набор gates среза на новом `HEAD`.
10. После фактической приёмки можно поставить локальный annotated tag `stage-NN-verified`. Merge, push, PR и release выполняются только по прямому запросу владельца.

## Обязательный учёт файлов и указателей

Для каждого коммита в отчёте сохраняются:

```powershell
git status --porcelain=v2 --branch
git diff --name-status BASE..HEAD
git diff --stat BASE..HEAD
git diff --check BASE..HEAD
git ls-files
dotnet sln spike/Map.Spike.slnx list
dotnet list spike/Map.Spike.slnx reference
```

Отдельно считать `A` (added), `M` (modified), `D` (deleted), `R` (renamed) и общее число tracked files. Проверить:

- каждый `.csproj` находится в solution или имеет записанную причину исключения;
- каждый `ProjectReference` существует и не нарушает направление слоёв;
- ни один authored file не потерян между `.gitignore`, solution и отчётом;
- нет случайных `bin`, `obj`, recovery, export, benchmark и test artifacts в Git;
- Markdown-ссылки, относительные пути, anchors и команды ведут к существующим целям;
- изменение имени/регистра пути не сломает Windows/Linux tooling;
- package versions и лицензии совпадают с реально восстановленными зависимостями.

Repomix можно использовать как второй инвентарный снимок, но число изменённых файлов берётся из Git, а не из отчёта AI.

## Четыре уровня проверки

### Gate A — быстрый перед каждым коммитом

`pre-commit` запускает готовые hooks: структура файлов, конфликты, секреты и локальные ссылки. Hook должен быть установлен командой:

```powershell
python -m pre_commit install
python -m pre_commit run --all-files
```

Успех Gate A не означает, что код собирается или функция работает.

### Gate B — self-check Opus до коммита

Минимум для .NET-среза:

```powershell
dotnet restore spike/Map.Spike.slnx --force-evaluate
dotnet build spike/Map.Spike.slnx -c Release --no-restore
dotnet test spike/Map.Spike.slnx -c Release --no-build --blame-hang --blame-hang-timeout 2m
dotnet format spike/Map.Spike.slnx --verify-no-changes --severity info --no-restore
dotnet package list spike/Map.Spike.slnx --vulnerable --include-transitive
dotnet package list spike/Map.Spike.slnx --deprecated --include-transitive
```

Команды уточняются под этап. Opus обязан проверить исходный симптом и failure path, а не только happy path.

### Gate C — независимая приёмка Codex после коммита

Codex выполняет свежий clean restore/build/test и не использует вывод Opus как результат. Затем по риску этапа добавляет:

- targeted tests по изменённым ветвям и границам;
- coverage с проверкой непокрытых файлов и ветвей, а не одного общего процента;
- Stryker на изменённом критическом коде, разбор `Survived` и `NoCoverage` mutants;
- повторные/параллельные прогоны для flaky tests;
- corrupted, missing, read-only, locked, full-disk-like и kill-process сценарии для файлов;
- semantic roundtrip, hash, dimensions, alpha и декодирование созданного файла;
- реальный EXE: process identity, отсутствие запрещённого listener, ввод, focus, high-DPI, screenshot и видимый результат;
- counters/trace/GC evidence и median/P95 после warmup для performance claims;
- security diff scan для security-sensitive изменения и полный scan на release gate.

Coverage и mutation testing оцениваются по риску. Нельзя «поднять процент» тестами без assertions или автоматически принять новые golden-файлы, созданные тем же дефектным кодом.

### Gate D — повтор после исправления

После нового fix-коммита:

1. воспроизвести исходный дефект;
2. подтвердить, что regression test проходит;
3. для нового regression test доказать red/green: без исправления тест падает, с исправлением проходит;
4. повторить Gate A, весь Gate B и относящийся к этапу Gate C;
5. пересчитать файлы и убедиться, что исправление не расширило scope.

## Практические проверки по типу изменения

| Изменение | Обязательная атака на реализацию |
|---|---|
| command / undo | пустой ввод, один элемент, границы, повторный undo/redo, отмена транзакции, память после длинной истории |
| geometry / hit-test | отрицательные/огромные координаты, zoom extremes, overlapping objects, stable ID, сравнение spatial index с brute-force oracle на сгенерированных сценах |
| raster / mask | край tile, соседние tiles неизменны, zero/full alpha, mask ownership, overlay отсутствует в export |
| save / recovery | kill отдельного процесса в каждой фазе, locked/read-only target, corrupt temp/journal, повторное recovery, semantic equality после reopen |
| import / archive | path traversal, duplicate canonical names, compression bomb limits, oversized dimensions, unsupported/corrupt file |
| export | настоящий decode, width/height, alpha, deterministic content при фиксированном seed, отсутствие edit overlays, нехватка памяти/отмена |
| native UI | AutomationId, keyboard-only path, focus, long Russian text, Windows scale 100/150/200%, реальное окно и screenshot |
| performance | Release без debugger, warmup, одинаковый fixture/seed, median/P95, allocation/GC, минимум один trace для провала |

## Что считается непринятым

- Opus написал «готово», но нет точного commit hash и свежего вывода;
- есть незакоммиченные или неучтённые authored files;
- проходит только default test suite;
- coverage высокий, но критическая ветвь не исполнялась;
- mutation survivors не разобраны;
- golden/snapshot был создан и принят тем же прогоном без независимого просмотра;
- UI проверен только headless или только наличием процесса;
- save/export подтверждён только toast/status, а файл не открыт и не проверен;
- benchmark не содержит методику, warmup, median/P95 и характеристики среды;
- `PARTIAL` или `BLOCKED` выдан за `FACT`.

## Артефакт независимого ревью

Каждый этап получает `docs/reviews/STAGE_NN_VERIFICATION.md` со следующими фактами:

- base commit, проверяемый head commit, branch и чистота worktree;
- число `A/M/D/R`, полный список затронутых authored files и total tracked files;
- acceptance matrix по пунктам задания;
- команды, exit codes, ключевые числа и пути реальных артефактов;
- findings по приоритету с файлом/строкой/сценарием воспроизведения;
- что проверено в настоящем окне;
- `FACT`, `PARTIAL`, `BLOCKED` и точный следующий fix-prompt.

Этот файл создаётся по фактам после проверки, а не заранее как обещание.

