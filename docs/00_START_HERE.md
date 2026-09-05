# 00. С чего начать

## Текущий режим

Map — личное офлайн-приложение для одного владельца на его текущем Windows x64 ПК.

Обычная работа использует production solution `Map.slnx`, одну Release-сборку и при необходимости
один адресный тест или короткий smoke в настоящем окне.

Последняя линейная продуктовая основа включает Stage 10E, Stage 10F, полезные production-правки
Stage 11 и реализованные Stages 12A–16. Отмена отдельного scope не является откатом к более
старому коду. Нового номерного этапа сейчас нет; точный product `HEAD` и единственное следующее
действие указаны в `STATE_HANDOFF.md`.

## Читать в таком порядке

1. `AGENTS.md`.
2. Для Codex — `CODEX.md`, для Claude Code — `CLAUDE.md`.
3. `docs/STATE_HANDOFF.md`.
4. Относящийся к задаче раздел `docs/02_FUNCTIONAL_SPEC.md`.
5. Текущий пункт `docs/06_DEVELOPMENT_ROADMAP.md`.
6. Относящиеся записи `docs/DECISIONS.md`.
7. Для истории уже принятого Stage 12A — `docs/STAGE_12A_NAMED_CHECKPOINTS.md`.
8. Для истории реализованного Stage 12C — `docs/STAGE_12C_FULL_EXPORT.md`.
9. Для границы GitHub — `docs/GITHUB_PROJECTS.md`; сейчас любые remote-действия запрещены.
10. Для разовой передачи задачи — один из коротких шаблонов в `docs/ai/`.

## Основные документы

| Файл | Назначение |
|---|---|
| `01_PRODUCT_VISION_AND_SCOPE.md` | личный продукт и границы |
| `02_FUNCTIONAL_SPEC.md` | функции редактора |
| `03_UX_UI_SYSTEM.md` | UX и визуальные правила |
| `04_TECH_ARCHITECTURE.md` | архитектура production-кода |
| `05_PROJECT_FILE_AND_DATA_MODEL.md` | формат `.mapproj` и данные |
| `06_DEVELOPMENT_ROADMAP.md` | оставшиеся функции |
| `07_CODEX_RUNBOOK.md` | короткий рабочий цикл |
| `08_QA_AND_ACCEPTANCE.md` | минимальная проверка изменений |
| `09_ASSET_PIPELINE_AND_LICENSING.md` | собственные ассеты и права |
| `10_ENVIRONMENT.md` | нужная локальная среда |
| `DECISIONS.md` | решения владельца |
| `STATE_HANDOFF.md` | актуальное состояние |
| `../CLAUDE.md` | вход Claude Code в общие правила |
| `ai/PROMPT_CODEX.md` | короткий шаблон задачи для Codex |
| `ai/PROMPT_CLAUDE.md` | короткий шаблон задачи для Claude Code |
| `STAGE_12A_NAMED_CHECKPOINTS.md` | исторический контракт реализованного Stage 12A |
| `STAGE_12C_FULL_EXPORT.md` | исторический исполняемый контракт реализованного Stage 12C |
| `GITHUB_PROJECTS.md` | замороженная граница локального репозитория и GitHub |

Исторические prompt/review/benchmark-пакеты удалены: они не влияли на работу приложения и
создавали ложную обязательность повторных проверок.
