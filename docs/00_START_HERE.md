# 00. START HERE

## Что это за пакет

Это система управления проектом нативного офлайн-конструктора карт для Windows. Она фиксирует продукт, функциональный слепок, UX, технические границы, формат данных, дорожную карту, качество, лицензирование и рабочий протокол для Codex GPT-5.6 Sol.

## Состояние на 28.08.2026

| Область | Статус | Что это значит |
|---|---|---|
| Исследование Inkarnate | `FACT / PARTIAL` | Публичные источники и test-map walkthrough покрывают text/line/shape/brush/stamp/group/grid/shortcuts и catalog variant sets; только явно перечисленные pressure/portal/font детали остались PARTIAL |
| Правовые границы | `FACT` | Автоскрейпинг, обратная разработка и копирование материалов исключены |
| Продуктовый объём | `ACCEPTED` | Офлайн-эквивалент редактора и локального dashboard; без облачного сообщества и Marketplace |
| Технологический стек | `ACCEPTED` | .NET 10 + Avalonia 12.1 + Skia; SDK установлен |
| Код приложения | `PARTIAL` | Stages 1–8, Stage 9A, Stage 10 input slice, 10B Library/variant sets и production-логика 10C независимо проверены; Stage 10D функционально завершён, закреплён локальным checkpoint и прошёл свежую self-acceptance 683/683 + native 13/13, но остаётся без независимого exact-range gate; keyboard/DPI/file-picker и clean-context UX-gates не закрыты |
| AI workflow | `FACT` | Активный стек использует Codex `gpt-5.6-sol`; общие правила, runbook, verification protocol и шаблон полного этапного prompt находятся в репозитории |
| Дизайн и ассеты | `PARTIAL` | Есть собственная visual system, упрощённая Library v2, наборы вариантов и diagnostic fixtures; полный inspector/grid идут позже |

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
| `DECISIONS.md` | append-only журнал архитектурных и продуктовых решений |
| `STATE_HANDOFF.md` | точка возобновления между сессиями |
| `research/INKARNATE_RESEARCH.md` | доказательная база функционального слепка |
| `research/AI_PLAYBOOK_APPLICABILITY.md` | что взято и не взято из PDF |
| `research/AI_TOOLS_REVIEW.md` | разбор всех позиций из таблицы инструментов |
| `research/CODEX_5_6_SOL_WORKFLOW_AUDIT.md` | актуальный аудит инструментов, prompt-истории, Claude→Codex миграции и GitHub/security границ |
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

На branch `stage/10d-object-inspectors` функциональный срез 10D и связанные security fixes прошли
свежую self-acceptance (`FACT` локального checkpoint): Release 0/0, 683/683 tests, native 13/13,
7 PNG/30 checks. Независимый exact `BASE..HEAD` gate остаётся `PARTIAL`; следующее действие —
свежая Codex-проверка неизменяемого снимка. Только затем начинать 10E Grid geometry одним крупным
prompt из `prompts/PROMPT_TEMPLATE_CODEX.md`; не смешивать checkpoint с 10E/10F/Stage 9B–9E.
