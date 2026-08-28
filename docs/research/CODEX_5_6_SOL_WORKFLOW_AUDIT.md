# Аудит coding workflow и миграция на Codex GPT-5.6 Sol

Дата проверки: 28.08.2026
Локальный sealed security target: `4b50ba0a707b45ace00da607650ee4d5c1a952bb`
Текущая branch: `stage/10d-object-inspectors`

## Итог

`FACT`: активный рабочий контур Map переведён с Claude Code/Opus на Codex `gpt-5.6-sol`. Новая система использует один полный вертикальный stage prompt, внутренние checkpoint'ы, обязательную risk/tool matrix, один понятный authority block, полный self-check и fresh-context Codex review.

`FACT`: подтверждённо вредоносных repository tools не найдено. Codex Security Standard scan sealed `HEAD` зафиксировал четыре finding: два `MEDIUM` product-security, один `LOW` product-memory и один `LOW` supply-chain. Mutable pre-commit tags заменены exact commits; `.claude/settings.json`, которая включала внешние plugins без repository pin/digest, удалена. Все четыре finding исправлены в текущем dirty Stage 10D; последующий независимый fix-review дополнительно закрыл JSON/ZIP/path/format/recent-index boundaries.

`BLOCKED`: GitHub-состав проекта проверить невозможно: `git remote -v` пуст, tracking branches отсутствуют, а подключённый GitHub connector не нашёл доступный repository Map. Локальный Git, branches, commits, files и diff исследованы; облачный repository нельзя выдумывать.

## Метод и границы

Проверено:

- активные инструкции, handoff, roadmap, decisions, QA, environment, research и review reports;
- 11 prompt-файлов: 3 tracked stage contracts и 8 ignored historical artifacts;
- локальная Git history и доказательства Stages 1–10C;
- repository hooks, local .NET tool manifest, PowerShell drivers, package/project files и Claude/Codex configuration;
- официальные OpenAI, Microsoft, Avalonia, pre-commit, Stryker, FsCheck, ArchUnitNET, Repomix, ImageMagick и Accessibility Insights sources;
- официальный YouTube result OpenAI и несколько community comparisons; результаты community помечены anecdotal;
- отдельный Codex Security Standard scan на immutable `HEAD` и независимые baseline/architecture/supply-chain passes.

Не входило:

- uncommitted Stage 10D production code в первоначальный sealed security scan; после него live
  tree проверялся отдельным независимым Codex fix-review до отсутствия release-blockers;
- runtime exploit reproduction;
- переписывание исторических review/handoff фактов о прежнем исполнителе;
- push/PR/release или создание GitHub repository;
- удаление пользовательских глобальных Claude/Codex plugins.

## Что официально важно для GPT-5.6 Sol

По [официальной model card](https://developers.openai.com/api/docs/models/gpt-5.6-sol) `gpt-5.6-sol` — основной frontier coding/agentic model; alias `gpt-5.6` направляется на Sol. Для модели доступны большие reasoning levels, tool use и контекст до 1.05M tokens, но большой контекст не отменяет необходимость отбирать релевантные файлы.

[Официальное руководство GPT-5.6](https://developers.openai.com/api/docs/guides/latest-model?model=gpt-5.6) рекомендует lean outcome-first prompts, один раз заданные constraints/authority, явные success criteria и только релевантные tools. Приведённые OpenAI внутренние coding-agent оценки сообщают directional improvement примерно на 10–15% при сокращении prompt и заметное уменьшение tokens/cost; это не универсальный benchmark Map, а основание убрать повторяющиеся запреты.

[AGENTS.md guide](https://learn.chatgpt.com/docs/agent-configuration/agents-md) подтверждает, что repository-wide инструкции должны быть короткими и устойчивыми, а детали этапа — локальными. [Long-running work guide](https://learn.chatgpt.com/docs/long-running-work) рекомендует outcome, constraints и verification для длительных задач. [Subagents guide](https://learn.chatgpt.com/docs/agent-configuration/subagents) полезен для независимых read-heavy investigations, но предупреждает о росте token use; write-heavy parallelism в Map по умолчанию не применяется.

Практический профиль Map:

- implementation: `gpt-5.6-sol`, reasoning `high`;
- architecture/security/fresh acceptance: `xhigh`;
- `max` только для конкретно обоснованной трудной задачи;
- один full-stage prompt, 3–7 internal checkpoints, один final stage gate;
- новый контекст для независимого review;
- one-time authority block вместо повторов «ask first / do not mutate»;
- tools выбираются по claims/risks, а не запускаются всем каталогом.

## YouTube и community evidence

В YouTube найден официальный ролик OpenAI [Codex just got better for developers](https://www.youtube.com/watch?v=eiQgljOrkWU) с видимой главой про GPT-5.6 Sol/Ultra. Export transcript вернул `No transcript is available`, поэтому из видео не делались содержательные выводы сверх title/visible chapter. Также найден first-look Fireship, но он не использован как источник продуктовых спецификаций.

Независимые community comparisons дают смешанную картину:

- в [неформальном опыте шести проектов](https://www.reddit.com/r/ClaudeAI/comments/1tbkpa8/a_practical_claude_code_vs_codex_experiment_6/) автор оценил Codex выше по empirical runtime validation, а Claude — по объяснению, письменному анализу и self-correction;
- [обсуждение Hacker News](https://news.ycombinator.com/item?id=46391391) содержит противоположные мнения: часть пользователей отмечает необходимость заранее показать Codex representative files/style, другие получают лучший результат от подробного brief и высокого reasoning;
- [side-by-side greenfield video](https://www.youtube.com/watch?v=G7EUj4d6lkU) показывает близкое время двух агентов на одном небольшом приложении, но не переносится на большой native repository;
- community-практика `PROMPT.md + STATE.md` подтверждает пользу durable state/decisions; в Map это уже лучше реализовано через stage prompt + `STATE_HANDOFF.md` + append-only `DECISIONS.md`, поэтому второй mutable state-файл не добавлен.

Эти источники anecdotal, быстро устаревают и не заменяют факты Map.

## Инвентарь инструментов и реальная эффективность

### Активные базовые инструменты

| Инструмент | Фактическое использование | Эффективность для Map | Решение |
|---|---|---|---|
| Codex `gpt-5.6-sol` | новый основной исполнитель/reviewer | высокий потенциал на repo-scale coding; лучше работает с outcome, representative files и explicit evidence | ACTIVE |
| Git | все независимые review | единственная авторитетная граница snapshot/diff; обязательно считать A/M/D/R и worktree | REQUIRED |
| .NET restore/build/test/format | почти каждый code stage | основной regression/compiler/analyzer gate; недостаточен без targeted failure path | REQUIRED |
| NuGet Audit | dependency/full gates | дешёвая проверка известных advisories; требует актуального audit source | REQUIRED BY TRIGGER/FULL GATE |
| pre-commit/hooks | перед stage commits | быстро ловит structural problems; исполняет third-party code, поэтому exact pins обязательны | REQUIRED BEFORE COMMIT |
| Lychee | documentation/link gates | полезен для local links/anchors; offline не подтверждает доступность web | REQUIRED FOR DOC CHANGES |
| project UIA drivers | Stages 10B/10C и UX reviews | лучше generic desktop MCP: воспроизводимы, ограничены сценарием и создают evidence JSON/PNG | REQUIRED FOR UI STAGES |
| ImageMagick | visual diff evidence | объективные dimensions/RMSE/diff, но не смысловая правильность | REQUIRED WHEN COMPARABLE VISUAL STATE EXISTS |
| Accessibility Insights | environment и ручные accessibility gates | сильный UIA/focus/tab/contrast inspector; не проверяет canvas semantics | REQUIRED BY CONTROL CHANGE |

[pre-commit](https://pre-commit.com/) автоматически устанавливает и запускает hook code, иногда скачивая tool runtimes; это объясняет и пользу, и supply-chain cost. [NuGet Audit](https://learn.microsoft.com/en-us/nuget/concepts/auditing-packages) проверяет known vulnerabilities direct/transitive dependencies. [ImageMagick compare](https://imagemagick.org/compare/) даёт математическую метрику и diff, а [Accessibility Insights](https://accessibilityinsights.io/docs/windows/overview/) — UIA/FastPass/keyboard/contrast evidence.

Свежий NuGet audit 28.08.2026 не нашёл известных vulnerable packages в `Map.slnx`. Deprecated audit пометил `xunit 2.9.3` и его v2 graph как `Legacy` в пяти test projects и предложил xUnit v3; это отдельная совместимостная миграция, а не автоматический update внутри workflow rewrite.

### Риск-ориентированные инструменты

| Инструмент | Когда полезен | Ограничение | Решение |
|---|---|---|---|
| dotnet-coverage | новая сложная ветвящаяся логика, coverage claim | высокий процент не означает сильные assertions | ON DEMAND |
| Stryker.NET | commands, persistence/recovery/import и tests, которые могут быть зелёными без проверки результата | дорог; требуется narrow mutate set и разбор survivors | ON DEMAND |
| dotnet-counters/trace/gcdump | performance/memory claim | trace требует методики; gcdump вызывает full GC | PERFORMANCE ONLY |
| Avalonia Headless | control tree/layout/input tests без окна | не доказывает native focus/DPI/accessibility | ADD WHEN A SPECIFIC GAP EXISTS |
| Appium/WinAppDriver | E2E через Windows accessibility tree | WinAppDriver latest public release старый и нужен отдельный service; current project drivers уже эффективнее | NOT NOW |
| FsCheck | geometry/path/state invariants и minimal generated counterexample | свойства надо проектировать; новая dependency | PROPOSED BY STAGE |
| ArchUnitNET | автоматическое правило `Core !-> Avalonia/Skia` | новая dependency; current project/reference tests уже покрывают часть | PROPOSED IF ARCHITECTURE DRIFTS |
| Repomix | передача repo внешней модели без native filesystem access | Codex уже читает repo; packed copy повышает leakage/staleness risk | NOT DEFAULT |
| Codex Browser | current official docs/public behavior | не разрешает closed-interface scraping или copying | RESEARCH ONLY |
| Codex Computer Use | ручной native smoke | не заменяет project driver/test/file checks | UI REVIEW |
| Codex Security | explicit request или новый trust boundary | не нужен на каждом unchanged offline slice; safeguards нельзя отключить | BOUNDARY-TRIGGERED |

Официальные определения: [Stryker.NET](https://stryker-mutator.io/docs/stryker-net/introduction/) вставляет mutants и проверяет, замечают ли их tests; [dotnet-coverage](https://learn.microsoft.com/en-us/dotnet/core/additional-tools/dotnet-coverage) собирает coverage; [.NET diagnostics](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-counters) наблюдает runtime counters; [Avalonia Headless](https://docs.avaloniaui.net/docs/testing/setting-up-the-headless-platform) тестирует controls/layout/input без native window; [FsCheck](https://fscheck.github.io/FsCheck/) генерирует cases и уменьшает counterexample; [ArchUnitNET](https://github.com/TNG/ArchUnitNET) проверяет architecture rules; [Repomix](https://github.com/yamadashy/repomix) создаёт AI-friendly копию repository.

### Удалённые legacy dependencies

Claude Code, Opus, Superpowers и Interfaces больше не являются active Map tools. Их полезные идеи перенесены как vendor-neutral process:

- test-first/targeted regression;
- systematic reproduction/debugging;
- fresh verification before completion;
- explicit code/UI review;
- accessibility/layout/typography checks как отдельный gate, не как proof of correctness.

Исторические review/handoff files сохраняют имена прежнего исполнителя, потому что переписывание evidence исказило бы историю.

## Частота упоминаний в 11 старых prompt-файлах

Это lexical inventory, не подтверждённые запуски: запрет инструмента тоже считается mention.

| Инструмент/группа | Prompt files | Mentions | Что показал аудит |
|---|---:|---:|---|
| dotnet restore/build/test/format | 9 | 40 | хорошо закреплён базовый gate |
| NuGet vulnerable/deprecated | 4 | 10 | пропущен во многих крупных prompt'ах |
| pre-commit | 7 | 13 | часто, но не единообразно |
| Lychee | 4 | 10 | в основном documentation-heavy tasks |
| Stryker/mutation | 7 | 13 | большая часть mentions — blanket prohibition; обязательным был лишь в нескольких fix/critical prompts |
| coverage | 6 | 6 | часто упомянут как пропуск, durable evidence сконцентрировано в Stage 1 и Stage 10C |
| counters/trace/gcdump | 2 | 4 | только performance-sensitive work |
| ImageMagick | 1 | 1 | явно недопредставлен в UI prompts |
| UIA/Accessibility | 7 | 23 | наиболее последовательно учитываемый native UI tool family |
| Repomix | 3 | 3 | в основном запрет; фактической необходимости не доказано |
| Computer Use | 0 | 0 | отсутствовал в implementation prompts |
| Avalonia Headless | 0 | 0 | ни одного этапного применения |
| Appium/WinAppDriver | 0 | 0 | ни одного этапного применения |
| FsCheck | 0 | 0 | каталог был, trigger не был сформулирован |
| ArchUnitNET | 0 | 0 | каталог был, trigger не был сформулирован |
| Superpowers/Interfaces skills | 0 | 0 | plugins были включены project config, но stage prompts не фиксировали использование/evidence |

Главный пробел: каталог существовал отдельно от stage risk. Новый prompt не обязан запускать всё, но обязан по каждой группе риска сказать `required/skipped` и почему.

## Полнота старых prompt'ов

Проверялись разделы: core docs, roadmap/DEC, outcome, in/out scope, invariants, acceptance, verification, failure path, native UI, Git baseline, commit/stop, no history rewrite, no push, status taxonomy и required report.

Наблюдения:

- tracked Stage 10B был самым полным: все проверяемые структурные пункты присутствовали;
- Stage 1 хорошо фиксировал outcome/scope/acceptance, но не содержал нового authority/tool-selection contract;
- Stage 9 имел сильные failure paths, но неполную явную связь с decisions и итоговым report;
- большие Stages 3–5 и 6–8 доказали, что один prompt может закрывать широкий вертикальный block, но их заголовки/отчётность были неединообразны;
- small fix prompts были оправданы после независимых findings и сохраняются только для review-fix;
- blanket-фразы «не запускать security/coverage/mutation/Repomix» иногда экономили время, но скрывали, почему инструмент нерелевантен и когда policy должна измениться;
- повторяющиеся «не трогай / спроси / остановись» увеличивали вероятность ненужного approval stop.

Исправление:

- `PROMPT_TEMPLATE_CODEX.md` содержит все обязательные sections и полную risk/tool matrix;
- `scripts/check-stage-prompts.ps1` проверяет каждый `Status: ACTIVE` prompt;
- local pre-commit hook запускает этот contract check при изменении `docs/prompts/`;
- выполненные tracked prompts помечены `HISTORICAL CONTRACT`;
- шаблон требует полный stage outcome, internal checkpoints и native/manual evidence либо явное `N/A`;
- один authority block разрешает read-only и reversible in-scope actions и оставляет approval только для значимых границ.

## Что было сильным у прежнего исполнителя и что перенесено

По фактам Map прежний Opus хорошо справлялся с длинным self-contained contract, большими implementation batches, большим количеством тестов/evidence и подробным handoff. Это особенно видно в крупных Stages 3–5, 6–8, 10B и 10C. Слабость: self-check и объём evidence не гарантировали независимую полноту — свежий Codex review находил production defects и закрывал их отдельными fix commits.

Чтобы Codex не проигрывал по этим сильным сторонам, новые инструкции требуют:

- не останавливаться после первого локального green;
- довести весь stage outcome через Core/Persistence/Rendering/Presentation/UI, если это один acceptance;
- 3–7 checkpoints без отдельных user prompts;
- exact final report с changed files, evidence, tool outcomes и gaps;
- warm-up обязательными docs и representative existing implementation/tests;
- preservation текущего style и no unrelated refactor;
- один stage commit после full self-check и обязательную остановку;
- fresh Codex-review на `BASE..HEAD`.

Сильная сторона Codex в текущем процессе — более скептическое сравнение claims с runtime/Git/files и умение объединить browser/GitHub/security/native tools. Она закреплена в verification protocol, но не выдаётся за универсальное превосходство модели.

## Security и вредоносность инструментов

### Verdict по tools

`FACT`: явных malware/backdoor/credential-stealer действий в repository-controlled tooling не найдено.

`PARTIAL`: отсутствие обнаруженного вредоносного поведения не доказывает безопасность любого будущего binary/update. Tool execution наследует права developer account.

Фактические риски:

1. `.claude/settings.json` автоматически включал Superpowers и сторонний Interfaces только по именам, без repository version/digest/permissions. Файл удалён вместе с Claude workflow.
2. pre-commit repositories были закреплены mutable tags. Они заменены на exact cached commit hashes; release tag сохранён комментарием.
3. `lychee-system` исполняет binary из `PATH`. Фактическая версия `0.24.2`; binary unsigned, поэтому при update требуется source/hash/channel check.
4. `rg` также unsigned, но установлен через WinGet; Git, .NET, Python и Codex executables имеют valid Authenticode signatures соответствующих publishers. Unsigned не равно malicious.
5. PowerShell drivers имеют developer authority. Более новые Stage 10B/10C drivers проверяют workspace containment и завершают только свой process; старый `scripts/drive-stage02.ps1` способен удалить `*.png` в caller-supplied `OutDir`, изменяет clipboard и посылает global input. Он не auto-runs и не признан malware, но требует отдельного hardening/removal decision перед повторным использованием.
6. доступные Codex connectors/plugins не становятся dependencies Map и не получают write authority от факта установки.

### Product security leads sealed HEAD

Codex Security scan `002992c2-ec0c-421a-8e51-773452feffdb` завершён и sealed для immutable `4b50ba0`; его coverage закрыл 12 выделенных security surfaces. Итог: четыре reportable finding (`2 MEDIUM`, `2 LOW`):

- `MEDIUM`, исправлено в текущем worktree: linked paths теперь отклоняют rooted/UNC/device/ADS/DOS aliases до probe, canonical fallback остаётся внутри project directory, reparse chain проверяется.
- `MEDIUM`, исправлено в текущем worktree: единый encoded/pixel/decoded budget действует до decode, cache ограничен по resident bytes, thumbnails имеют отдельный меньший budget.
- `LOW`, исправлено в текущем worktree: `.mapproj` использует меньшие aggregate budgets, exact bounded reads без лишнего `ToArray`, pre-materialization JSON/ZIP admission и ownership transfer.
- `LOW`, уже исправлено в текущем worktree: sealed `HEAD` выбирал remote pre-commit hooks mutable tags. Текущая конфигурация закрепляет reviewed full commit SHA.

Дополнительное архитектурное наблюдение, не включённое в четыре finding: документация обещает не логировать paths без consent, но open/save errors включают full paths в bounded local diagnostics log. Это local privacy discrepancy, не внешняя exfiltration. Direct NuGet versions заданы, но `packages.lock.json` отсутствует; locked restore остаётся отдельным `PROPOSED` supply-chain решением.

Первоначальные findings относятся к immutable `HEAD`; исправления находятся в ещё не
закоммиченном Stage 10D snapshot и поэтому не являются immutable `BASE..HEAD` evidence до
разрешённого владельцем checkpoint commit.

## GitHub и состав repository

Локально:

- branch `stage/10d-object-inspectors`;
- HEAD `4b50ba0` перед текущими user changes;
- repository имеет многопроектную native структуру `src/`, `tests/`, `benchmarks/`, `tools/`, `scripts/`, `docs/` и `spike/`;
- текущий worktree содержит незакоммиченный Stage 10D production/test slice; миграция его не очищает и не присваивает себе;
- история показывает stage/fix branches и независимые review/fix commits; push/rewrite не выполнялись.

Облачно:

- `remote.origin.url` отсутствует;
- tracking remote branches отсутствуют;
- GitHub connector search не вернул repository Map.

Поэтому GitHub visibility, branch protection, Actions, releases, issues, permissions и remote composition имеют статус `BLOCKED`, а не «в порядке».

## Изменения workflow

- `CLAUDE.md` заменён на `CODEX.md`;
- `07_OPUS_RUNBOOK.md` заменён на `07_CODEX_RUNBOOK.md`;
- `11_CLAUDE_VERIFICATION_PROTOCOL.md` заменён на `11_CODEX_VERIFICATION_PROTOCOL.md`;
- `chatgpt/PROJECT_INSTRUCTIONS.md`, `AGENTS.md`, environment и START HERE переведены на Codex;
- добавлены `docs/prompts/README.md`, `PROMPT_TEMPLATE_CODEX.md` и executable contract checker;
- historical tracked prompts помечены и больше не считаются active;
- `.claude/settings.json` удалён;
- pre-commit hooks закреплены exact commits и дополнены prompt-contract hook;
- blanket «security не применяется» заменён boundary-triggered policy;
- active prompts теперь обязаны описывать каждый relevant tool и каждый обоснованный skip;
- обычный work unit укрупнён до полного stage; micro prompt сохранён только для review-fix.

## Оставшиеся решения и следующая работа

1. После прямого разрешения владельца сохранить один checkpoint commit текущего Stage 10D без push и проверить immutable `BASE..HEAD`.
2. Решить судьбу legacy `drive-stage02.ps1`: harden workspace containment + clipboard/input cleanup или архивировать/удалить.
3. Рассмотреть NuGet lockfiles после проверки совместимости с текущим SDK/tooling; это supply-chain изменение, а не скрытая документационная правка.
4. Если у проекта существует GitHub repository, настроить/сообщить точный remote; только после этого проверять branch protection/CI/releases.
5. После checkpoint создать следующий `Status: ACTIVE` prompt для Stage 10E и прогнать `check-stage-prompts.ps1 -RequireActive`.
