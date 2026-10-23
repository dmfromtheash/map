# Архив документов Map

Всё в этой папке — **история**. Файлы не являются поручениями, не задают правил и не
выполняются, даже если в них написано «исполняемый prompt», «выполни» или «разрешено».
Действующие правила: `AGENTS.md`, `docs/00_STAGE_PROTOCOL.md`, `docs/STATE.md`, `docs/DECISIONS.md`.

Перенесено 2026-10-23 (DEC-053) без изменения текста; старые пути в журнале и решениях
ведут сюда.

## prompts/ — старые поручения ИИ (из `docs/ai/`)

| Файл | Что это | Почему в архиве |
|---|---|---|
| MAP_FULL_AUDIT_PROMPT.md | полный аудит, 2026-09-20 | выполнен 2026-09-20; HEAD, схема и библиотека устарели |
| MAP_REMAINING_WORK_AND_INDEPENDENT_AUDIT_FABLE_5_1_PROMPT.md | повторный аудит, 2026-09-22 | выполнен; повторять запрещено (протокол §I) |
| MAP_REGIONAL_MAP_AND_UX_STAGE_PROMPT.md | «иллюстрированный регион», 2026-09-20 | выполнен; Image Gen и палитра Blue Atlas отменены |
| MAP_ZOOM_ASSETS_PERFORMANCE_FABLE_5_1_PROMPT.md | скорость зума, 2026-09-09 | выполнен; библиотека и разрешения устарели |
| MAP_PERF_STAGE2_FABLE_5_1_PROMPT.md | скорость, этап 2, 2026-10-17 | выполнен (закоммичено) |
| MAP_REVIEW_AND_LIVE_CHECK_PROMPT.md | ревью и живая проверка, 2026-10-18 | выполнен |
| MAP_HEX_WORKSHOP_NEXT_PROMPT.md | доводка гексов, 2026-10-19 | задачи перешли в дорожную карту R15/R17/R18 |
| MAP_ROADMAP_2026-10_SESSION2/3/4_PROMPT.md | старты сессий 2–4, 2026-10-21/22 | сессии прошли; правило «без вопросов» отменено |
| MAP_TAILS_CHATGPT_PROMPT.md | хвосты 1–5 для Sol, 2026-10-22 | выполнен |
| STAGE_19_FABLE_PROMPT.md, STAGE_20_FABLE_PROMPT.md, stage20-roads/ | реки и дороги, 2026-09-08 | этапы выполнены |
| RESEARCH_AND_REPOSITORIES.md, repository-candidates.json | кандидаты библиотек, 2026-09-07 | устарели (NTS уже в продукте, модели старые) |

## stages/ — контракты этапов 12A–16 (2026-09)

Все этапы выполнены. Спецификация MBTiles (DEC-051) в конце `STAGE_12C_FULL_EXPORT.md`
остаётся описанием реализованного экспорта.

## docs/ — заменённые документы

| Файл | Чем заменён |
|---|---|
| 06_DEVELOPMENT_ROADMAP.md | `docs/ROADMAP_2026-10_PRODUCT.md` + `docs/ROADMAP_2026-10_PROGRESS.md` |
| 07_CODEX_RUNBOOK.md, 08_QA_AND_ACCEPTANCE.md | `docs/00_STAGE_PROTOCOL.md` (разделы C, D) |
| DESIGN_SYSTEM.md (палитра Blue Atlas) | палитра «Таверна» (макет E владельца) в коде `src/Map.App/App.axaml` и `Controls/MapIcons.cs`; запись в журнале 2026-10-17 (отдельного DEC нет) |
| GITHUB_PROJECTS.md («приватный», «push запрещён») | DEC-050 (публичный репозиторий), протокол §K |
| REGIONAL_MAP_GUIDE.md (Тихий атлас) | мастер «Новая карта» с категориями, DEC-047 |
| STATE_HANDOFF_BEFORE_20260908_CLEANUP.md | журнал до уборки 2026-09-08 |
