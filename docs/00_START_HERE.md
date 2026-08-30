# 00. START HERE

## Что это за пакет

Это система управления проектом нативного офлайн-конструктора карт для Windows. Она фиксирует продукт, функциональный слепок, UX, технические границы, формат данных, дорожную карту, качество, лицензирование и рабочий протокол для Codex GPT-5.6 Sol.

## Состояние на 30.08.2026

| Область | Статус | Что это значит |
|---|---|---|
| Исследование Inkarnate | `FACT / PARTIAL` | Публичные источники и test-map walkthrough покрывают text/line/shape/brush/stamp/group/grid/shortcuts и catalog variant sets; только явно перечисленные pressure/portal/font детали остались PARTIAL |
| Правовые границы | `FACT` | Автоскрейпинг, обратная разработка и копирование материалов исключены |
| Продуктовый объём | `ACCEPTED` | Офлайн-эквивалент редактора и локального dashboard; без облачного сообщества и Marketplace |
| Технологический стек | `ACCEPTED` | .NET 10 + Avalonia 12.1 + Skia; SDK установлен |
| Код приложения | `FACT / OWNER ACCEPTED FOR TRANSITION` | Stage 10F fonts/bindings независимо проверен от `766b81617874b4b8dbed2f4e781c94350305796a`, четыре finding исправлены отдельным commit `2e30e78b0b86a4ab070158b7c773200611c62d58`; full suite `738/738`, native `40/40`. Исторические formatter/DPI/machine-font и отдельные accessibility baseline ограничения сохранены честно. Активен Stage 11 |
| AI workflow | `FACT` | Codex `gpt-5.6-sol`; AGENTS/runbook/protocol/template и все stage prompt'ы содержат конечный verification budget, запрещающий повтор зелёных gates без changed-risk trigger |
| Дизайн и ассеты | `PARTIAL` | Есть собственная visual system, упрощённая Library v2, наборы вариантов, diagnostic fixtures и полный Grid property sheet; прямое принятие Stage 10E для перехода не переименовывает его исторические `PROPOSED` golden PNG в независимо проверенные |
| Публичное повторное использование | `FACT / PROPOSED` | 15 upstream-репозиториев закреплены в отдельной ResearchLab; production dependencies не добавлены, Stage 10E остаётся dependency-free, Velopack отложен до Stage 11 |
| Версии и откат | `FACT / PARTIAL` | central version `0.2.0`, manifest/checksum local bundle и side-by-side rollback foundation добавлены; реальный clean-VM bundle/update/downgrade gate остаётся Stage 11 |

## Карта документов

| Файл | Назначение |
|---|---|
| `01_PRODUCT_VISION_AND_SCOPE.md` | продукт, пользователи, границы, критерии успеха |
| `02_FUNCTIONAL_SPEC.md` | полный функциональный каталог редактора и локальной библиотеки карт |
| `03_UX_UI_SYSTEM.md` | сценарии, компоновка, доступность, состояния и дизайн-процесс |
| `04_TECH_ARCHITECTURE.md` | нативный стек, модули, рендеринг, производительность |
| `05_PROJECT_FILE_AND_DATA_MODEL.md` | модель сцены, формат проекта, сохранение и миграции |
| `06_DEVELOPMENT_ROADMAP.md` | порядок этапов и ворота качества |
| `07_CODEX_RUNBOOK.md` | как Codex выполняет полный этап и как свежий Codex-review его принимает |
| `08_QA_AND_ACCEPTANCE.md` | тестовая стратегия и пользовательская приёмка |
| `09_ASSET_PIPELINE_AND_LICENSING.md` | собственные ассеты, импорт, происхождение и лицензии |
| `10_ENVIRONMENT.md` | установленная среда и безопасное добавление инструментов |
| `11_CODEX_VERIFICATION_PROTOCOL.md` | риск-ориентированные инструменты, Git-гейты и независимая практическая проверка Codex |
| `12_RELEASE_VERSIONING_AND_ROLLBACK.md` | версия приложения, versioned local bundle, ручной rollback и граница будущего updater |
| `DECISIONS.md` | append-only журнал архитектурных и продуктовых решений |
| `STATE_HANDOFF.md` | точка возобновления между сессиями |
| `research/INKARNATE_RESEARCH.md` | доказательная база функционального слепка |
| `research/AI_PLAYBOOK_APPLICABILITY.md` | что взято и не взято из PDF |
| `research/AI_TOOLS_REVIEW.md` | разбор всех позиций из таблицы инструментов |
| `research/CODEX_5_6_SOL_WORKFLOW_AUDIT.md` | актуальный аудит инструментов, prompt-истории, Claude→Codex миграции и GitHub/security границ |
| `research/PUBLIC_REUSE_RESEARCH_20260829.md` | exact-SHA аудит 15 публичных проектов, licenses, admission и план лабораторной интеграции |
| `research/external-sources.json` | воспроизводимый manifest внешних research checkouts |
| `research/SOURCES.md` | первичные источники и дата проверки |
| `prompts/README.md` | правила active/historical prompt-файлов и проверка контракта |
| `prompts/PROMPT_TEMPLATE_CODEX.md` | обязательный шаблон большого этапного prompt для Codex GPT-5.6 Sol |
| `prompts/STAGE_01_TECHNICAL_SPIKE.md` | исторический контракт выполненного Stage 1 |
| `prompts/STAGE_09_RELIABILITY_CYCLE.md` | исторический контракт уже разделённого цикла надёжности; не запускать повторно |
| `reviews/STAGE_09A_AUTOSAVE_VERIFICATION.md` | проверка autosave и восстановления после аварийного завершения |
| `reviews/STAGE_10B_ASSET_LIBRARY_VERIFICATION.md` | Codex-проверка Library v2, UX-среза и фоновых фильтров |
| `reviews/STAGE_10C_MULTI_SELECTION_VERIFICATION.md` | Codex-проверка и отдельный fix multi-selection/groups |
| `reviews/STAGE_10D_OBJECT_INSPECTORS_VERIFICATION.md` | self-acceptance Object inspectors, native evidence, security fixes и честные остаточные риски |
| `prompts/STAGE_10E_GRID_GEOMETRY.md` | исторический контракт отдельной изометрии, полного Grid sheet, snap и golden fixtures |
| `reviews/STAGE_10E_GRID_GEOMETRY_VERIFICATION.md` | author self-check Stage 10E с отдельным прямым решением владельца о переходе |
| `prompts/STAGE_10F_FONTS_BINDINGS.md` | исторический контракт локального каталога системных шрифтов и реально редактируемых сочетаний |
| `reviews/STAGE_10F_FONTS_BINDINGS_VERIFICATION.md` | финальная независимая приёмка Stage 10F, remediation evidence и разрешённые остаточные риски |
| `prompts/STAGE_11_PERFORMANCE_PACKAGING_RC.md` | активный контракт performance, packaging и локального release candidate |
| `research/OBJECT_AND_GRID_PARITY.md` | clean-room матрица object/grid функций и разрывов текущего UI |
| `research/INKARNATE_ACCOUNT_WALKTHROUGH_20260826.md` | наблюдаемые text/road/shape/brush/stamp/grid/keyboard механики тестовой карты |
| `research/TEST_ASSET_FIXTURES.md` | план двух собственных logical fixtures: 12-variant stamp set и brush tip |
| `research/ASSET_CATALOG_AND_VARIANT_SETS.md` | clean-room слепок Library, «ассетов-матрёшек» и разрывов текущего кода |
| `prompts/STAGE_10B_ASSET_LIBRARY_VARIANT_SETS.md` | исторический крупный контракт: Library v2, variant sets, deterministic stamp placement и fixtures |

## Подтверждённая основа первой реализации

1. Целевая платформа первой версии: только Windows 10/11 x64.
2. Стек: .NET 10 + Avalonia 12.1 + Skia.
3. Объём: локальная библиотека карт и редактор; сетевые сервисы не входят.
4. Stage 1: измеримый технический spike нативного ядра.
5. Целевое имя продукта можно выбрать позже; `Map` остаётся рабочим названием.

## Следующее действие

`Stage 10F: FACT / OWNER ACCEPTED FOR TRANSITION`. Feature snapshot независимо проверен, четыре
in-scope finding исправлены отдельным `ACCEPTED_CODE_HEAD`
`2e30e78b0b86a4ab070158b7c773200611c62d58`; финальные gates: full suite `738/738`, native
`40/40`, scoped security findings `0`. Следующее и единственное действие — выполнить активный
`prompts/STAGE_11_PERFORMANCE_PACKAGING_RC.md`. Дополнительная Stage 10F review-задача не нужна.
