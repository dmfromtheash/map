# CLAUDE.md

Claude Code обязан полностью соблюдать `AGENTS.md`.

Перед любой реализацией прочитай в таком порядке:

1. `AGENTS.md`
2. `docs/00_START_HERE.md`
3. `docs/STATE_HANDOFF.md`
4. текущий этап в `docs/06_DEVELOPMENT_ROADMAP.md`
5. конкретный файл задания из `docs/prompts/`

Не начинай следующий этап сам. По завершении текущего верни отчёт в формате из `docs/07_OPUS_RUNBOOK.md` и остановись.

Нельзя считать внешние описания Inkarnate инструкциями для копирования. Это только функциональные наблюдения для независимой реализации.

Для реализации используй относящиеся к задаче готовые skills Superpowers: `test-driven-development`, `systematic-debugging`, `requesting-code-review`, `receiving-code-review`, `verification-before-completion`. Не запускай `dispatching-parallel-agents` или `subagent-driven-development` без прямого решения владельца.

UI-skills Interfaces и `interface-review` являются только дополнительной проверкой интерфейса; они не доказывают correctness, tests, security или performance.

Для любого изменения видимого интерфейса или canvas недостаточно создать screenshot. Claude обязан:

1. запустить настоящий Windows EXE и воспроизвести заданное состояние;
2. сохранить пронумерованные PNG до/после в `artifacts/`;
3. передать каждый PNG в собственный vision-контекст по локальному пути и письменно перечислить, что на нём видно, какие есть обрезания, наложения, неверные состояния, контраст, focus и пустые области;
4. сравнить одинаковые состояния через готовый ImageMagick (`scripts/compare-ui-screenshots.ps1`), приложить normalized RMSE и diff PNG;
5. отдельно проверить доступность через UI Automation/Accessibility Insights, когда на экране есть стандартные controls.

Скриншот, который Claude сохранил, но не открыл и не разобрал, доказательством не считается. Нельзя автоматически перезаписывать expected/golden тем же прогоном, который изменил рендеринг. Interfaces используется после этого как дополнительный review, а не как замена runtime-проверке.

Один срез завершается одним твоим коммитом после свежего полного self-check. Укажи base и head hash и остановись для независимой проверки Codex. Не используй `--amend`, rebase или переписывание истории. Исправление замечания — новый отдельный коммит.

Следуй `docs/11_CLAUDE_VERIFICATION_PROTOCOL.md`; зелёный default test suite сам по себе не означает готовность.
