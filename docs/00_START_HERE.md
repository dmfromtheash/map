# 00. START HERE

## Что это за пакет

Это исходная система управления проектом нативного офлайн-конструктора карт для Windows. Она фиксирует продукт, функциональный слепок, UX, технические границы, формат данных, дорожную карту, качество, лицензирование и рабочий протокол для Claude Code Opus.

## Состояние на 27.08.2026

| Область | Статус | Что это значит |
|---|---|---|
| Исследование Inkarnate | `FACT / PARTIAL` | Публичные источники и test-map walkthrough покрывают text/line/shape/brush/stamp/group/grid/shortcuts и catalog variant sets; только явно перечисленные pressure/portal/font детали остались PARTIAL |
| Правовые границы | `FACT` | Автоскрейпинг, обратная разработка и копирование материалов исключены |
| Продуктовый объём | `ACCEPTED` | Офлайн-эквивалент редактора и локального dashboard; без облачного сообщества и Marketplace |
| Технологический стек | `ACCEPTED` | .NET 10 + Avalonia 12.1 + Skia; SDK установлен |
| Код приложения | `PARTIAL` | Stages 1–8, Stage 9A, Stage 10 input slice и 10B Library/variant sets реализованы; Codex проверил Claude-диапазон и закрыл два пропуска background-driver, но keyboard/DPI/file-picker и clean-context UX-gate остаются незавершёнными |
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
| `07_OPUS_RUNBOOK.md` | как тимлид ставит и принимает задачи Opus |
| `08_QA_AND_ACCEPTANCE.md` | тестовая стратегия и пользовательская приёмка |
| `09_ASSET_PIPELINE_AND_LICENSING.md` | собственные ассеты, импорт, происхождение и лицензии |
| `10_ENVIRONMENT.md` | установленная среда и безопасное добавление инструментов |
| `11_CLAUDE_VERIFICATION_PROTOCOL.md` | обязательные Git-гейты и независимая практическая проверка каждого коммита Opus |
| `DECISIONS.md` | append-only журнал архитектурных и продуктовых решений |
| `STATE_HANDOFF.md` | точка возобновления между сессиями |
| `research/INKARNATE_RESEARCH.md` | доказательная база функционального слепка |
| `research/AI_PLAYBOOK_APPLICABILITY.md` | что взято и не взято из PDF |
| `research/AI_TOOLS_REVIEW.md` | разбор всех позиций из таблицы инструментов |
| `research/SOURCES.md` | первичные источники и дата проверки |
| `prompts/STAGE_01_TECHNICAL_SPIKE.md` | готовое следующее задание для Opus после утверждения решений |
| `prompts/STAGE_09_RELIABILITY_CYCLE.md` | принятый контракт текущего цикла надёжности |
| `reviews/STAGE_09A_AUTOSAVE_VERIFICATION.md` | проверка autosave и восстановления после аварийного завершения |
| `reviews/STAGE_10B_ASSET_LIBRARY_VERIFICATION.md` | Codex-проверка Library v2, UX-среза и фоновых фильтров |
| `research/OBJECT_AND_GRID_PARITY.md` | clean-room матрица object/grid функций и разрывов текущего UI |
| `research/INKARNATE_ACCOUNT_WALKTHROUGH_20260826.md` | наблюдаемые text/road/shape/brush/stamp/grid/keyboard механики тестовой карты |
| `research/TEST_ASSET_FIXTURES.md` | план двух собственных logical fixtures: 12-variant stamp set и brush tip |
| `research/ASSET_CATALOG_AND_VARIANT_SETS.md` | clean-room слепок Library, «ассетов-матрёшек» и разрывов текущего кода |
| `prompts/STAGE_10B_ASSET_LIBRARY_VARIANT_SETS.md` | готовый крупный контракт Opus: Library v2, variant sets, deterministic stamp placement и fixtures |

## Подтверждённая основа первой реализации

1. Целевая платформа первой версии: только Windows 10/11 x64.
2. Стек: .NET 10 + Avalonia 12.1 + Skia.
3. Объём: локальная библиотека карт и редактор; сетевые сервисы не входят.
4. Stage 1: измеримый технический spike нативного ядра.
5. Целевое имя продукта можно выбрать позже; `Map` остаётся рабочим названием.

## Следующее действие

Codex проверил точный Claude-диапазон `dfc2d1d..172f1b4` и исправил два пропуска фонового
драйвера: media type и exact tag теперь реально выбираются без активации окна. Свежий прогон:
22 PNG, 99 проверок, 323 штампа / 12 SHA. Подробности —
`reviews/STAGE_10B_ASSET_LIBRARY_VERIFICATION.md`. Stage 10B остаётся `PARTIAL` только по явно
перечисленным keyboard/DPI/file-picker и clean-context UX-gates. Следующий функциональный срез —
10C multi-selection/groups; не смешивать его с `IsometricSquares`, полным inspector или Stage 9B.
