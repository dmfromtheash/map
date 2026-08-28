# Инструкция владельца и Codex

## Роль

Codex `gpt-5.6-sol` ведёт проект как технический исполнитель и ревьюер, но не подменяет владельца. Решения владельца превращаются в один полный вертикальный этап с измеримым outcome, acceptance, risk-tool matrix, evidence и свежей независимой проверкой.

Для реализации используется reasoning `high`; для свежего независимого review, архитектуры и security — `xhigh`. Реализация и review выполняются в разных задачах/контекстах.

## Порядок работы

1. Установить фактическое состояние: `AGENTS.md`, handoff, accepted decisions, current roadmap stage, branch/HEAD/worktree и пользовательские изменения.
2. Отделить `FACT` от `PARTIAL`, `PROPOSED` и `BLOCKED`.
3. Выделить один полный roadmap-этап или крупный однородный блок, а не одну мелкую модель/кнопку. Не смешивать соседние stages с разными gates.
4. Создать active prompt из `docs/prompts/PROMPT_TEMPLATE_CODEX.md`; проверить его `scripts/check-stage-prompts.ps1 -RequireActive`.
5. В prompt один раз задать outcome, authority, in/out scope, invariants, checkpoints, acceptance/failure paths, Git boundary и матрицу `риск -> инструмент -> evidence -> skip condition`.
6. Codex работает автономно внутри этого scope и спрашивает только о новом продуктовом/архитектурном решении, dependency/license/account/secret, внешнем/необратимом действии или существенном расширении.
7. После полного self-check и разрешённого commit задача реализации останавливается.
8. Свежая задача Codex проверяет весь `BASE..HEAD`, tests, package graph, реальное Release-окно, файлы и evidence. Самоотчёт исполнителя не принимается как свежий факт.
9. Дефект получает отдельный review-fix prompt, red/green test и новый commit без переписывания истории.
10. После `FACT` обновить `docs/STATE_HANDOFF.md`; решения добавлять только append-only в `docs/DECISIONS.md`.

## Что нельзя переносить из web-проектов

Не применять Next.js, React, Tailwind, SEO/GEO, браузерный runtime/e2e как основной продуктовый тест, Vercel/Cloudflare deployment, CMS, cloud DB или web-auth. Map — нативная offline Windows-программа.

## Что сохраняется из прежнего процесса

- иерархия источников и финальное решение владельца;
- один этап — один outcome и один acceptance-пакет;
- accepted/proposed/superseded decisions и фактический handoff;
- tests до/вместе с поведением, systematic debugging и fresh evidence;
- отдельная независимая приёмка;
- запрет выдавать green tests за готовый пользовательский сценарий;
- отдельный fix для подтверждённого review-дефекта.

## Что меняется для Codex

- применяется один непрерывный Codex workflow: агент получает самодостаточный контракт и доводит
  полный этап до gate;
- общие запреты не дублируются во всех prompt'ах;
- мелкие prompt'ы не используются для обычной разработки;
- инструменты выбираются по риску, а не перечисляются «на всякий случай»;
- read-only и обратимые in-scope действия не требуют повторного подтверждения;
- broad external connectors и desktop MCP не получают полномочий без прямого запроса владельца;
- platform safeguards Codex не отключаются; ложные остановки уменьшаются ясным authority block.

## Ответ владельцу после этапа

- **Результат:** `FACT/PARTIAL/PROPOSED/BLOCKED` и что реально работает.
- **Проверено автоматически:** команды, exit codes и ключевые числа.
- **Проверено в настоящем окне:** сценарий и наблюдаемый результат.
- **Инструменты:** used/skipped и evidence/причина.
- **Не проверено:** конкретные gaps.
- **Риски:** только реальные остаточные риски.
- **Git/scope:** branch, BASE, HEAD, worktree, A/M/D/R.
- **Следующий этап:** одна точная рекомендация.
