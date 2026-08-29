# 00. START HERE

## Что это за пакет

Это система управления проектом нативного офлайн-конструктора карт для Windows. Она фиксирует продукт, функциональный слепок, UX, технические границы, формат данных, дорожную карту, качество, лицензирование и рабочий протокол для Codex GPT-5.6 Sol.

## Состояние на 29.08.2026

| Область | Статус | Что это значит |
|---|---|---|
| Исследование Inkarnate | `FACT / PARTIAL` | Публичные источники и test-map walkthrough покрывают text/line/shape/brush/stamp/group/grid/shortcuts и catalog variant sets; только явно перечисленные pressure/portal/font детали остались PARTIAL |
| Правовые границы | `FACT` | Автоскрейпинг, обратная разработка и копирование материалов исключены |
| Продуктовый объём | `ACCEPTED` | Офлайн-эквивалент редактора и локального dashboard; без облачного сообщества и Marketplace |
| Технологический стек | `ACCEPTED` | .NET 10 + Avalonia 12.1 + Skia; SDK установлен |
| Код приложения | `PARTIAL` | Stages 1–8, Stage 9A, Stage 10 input slice, 10B Library/variant sets и production-логика 10C независимо проверены; bounded Gate D test-only range `2af5bd3..8de17de` принят отдельно (`1/1` exact regression и `209/209` Map.Presentation.Tests); keyboard/DPI/file-picker и clean-context UX-gaps не закрыты |
| AI workflow | `FACT` | Codex `gpt-5.6-sol`; AGENTS/runbook/protocol/template и все stage prompt'ы содержат конечный verification budget, запрещающий повтор зелёных gates без changed-risk trigger |
| Дизайн и ассеты | `PARTIAL` | Есть собственная visual system, упрощённая Library v2, наборы вариантов и diagnostic fixtures; полный inspector/grid идут позже |
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

Bounded Gate D exact test-only range
`2af5bd3495851f032897435fc957f5705a692bbe..8de17de27546783bdd6247cb4df094d9df505711`
уже принят отдельно: `1/1` exact regression и `209/209` Map.Presentation.Tests. Следующее действие —
сохранить текущий research/release foundation candidate одним локальным checkpoint и передать точный
`8de17de..checkpoint HEAD` свежей независимой review-задаче. **Stage 10E Grid geometry** не начинать до
этой проверки и отдельного решения владельца.
