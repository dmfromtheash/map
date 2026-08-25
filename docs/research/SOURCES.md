# Источники и проверка

Дата актуальной проверки: **26.08.2026**.

При конфликте источников приоритет: более свежая официальная документация/updates → официальный tutorial → наблюдаемое ручное поведение → вторичный обзор. Субтитры — вспомогательный источник и не доказывают неясную деталь сами по себе.

## Локальные источники владельца

| Источник | Использование |
|---|---|
| `C:\Users\Leshe\Downloads\AI Playbook v2.0.pdf` | процесс постановки, PRD, итерации, проверки, handoff; конкретные web/cloud советы отфильтрованы |
| `C:\Users\Leshe\Desktop\ИИ инструменты\AI-инструменты_из_Saved_Messages_06-08-2026.xlsx` | полный список инструментов, каждый пункт переоценён под Map |
| `C:\Projects\FreelancePortfolio\AGENTS.md` | общие правила малых проверяемых изменений |
| `C:\Projects\FreelancePortfolio\chatgpt\PROJECT_INSTRUCTIONS.md` | разделение ролей owner/lead/Opus |
| `C:\Projects\FreelancePortfolio\docs\project\07_OPUS5_RUNBOOK.md` | структура ограниченного задания и handoff |
| `C:\Projects\FreelancePortfolio\docs\project\05_DEVELOPMENT_ROADMAP.md` | stage gates; web stack не переносился |
| `C:\Projects\FreelancePortfolio\docs\DECISIONS.md` | append-only журнал решений |

## Inkarnate — первичные источники

| Источник | Что подтверждает |
|---|---|
| [Homepage](https://inkarnate.com/) | публичное позиционирование, типы карт, asset library, line/shape, clone, планы |
| [FAQ](https://inkarnate.com/faq) | World/Region, City/Village, Battlemap/Interior, Scene/Isometric; историческое online requirement |
| [Updates](https://inkarnate.com/updates/) | текущие изменения dashboard/editor/Windows app и даты; приоритет над старым video |
| [Terms](https://inkarnate.com/terms) | договорные ограничения исследования и права на materials/assets |
| [Privacy](https://inkarnate.com/privacy) | сетевой/account контекст конкурента; не переносится в offline product |
| [2.0 Editor Overview, 2:35:49](https://www.youtube.com/watch?v=QifZDEvGvBo) | полный UI/tool/layer/history/catalog walkthrough и Q&A |
| [Text Tool Overview](https://www.youtube.com/watch?v=ErwB0XeMJe4) | text properties и inheritance |
| [Line & Shape Tool Overview](https://www.youtube.com/watch?v=GzeGhwbpbqw) | nodes, portals, fill/stroke/boolean behavior |
| [Stamp Tool Overview](https://www.youtube.com/watch?v=tfzd8Tisq3E) | catalog, transform, random/scatter, shadows |
| [Layers Overview](https://www.youtube.com/watch?v=Rf5bVBKFnZ0) | layer types, object tree, masks/effects |
| [Brush Tool Overview](https://www.youtube.com/watch?v=IJl_R3E4tHo) | brush presets/dynamics/material/shape |
| [Land Tool Overview](https://www.youtube.com/watch?v=fo0v-4jSwcU) | add/subtract, shapes, move area, land style |

Фокусный повторный проход 26.08.2026 сопоставил актуальный публичный `Stamp Tool Overview`,
предоставленный владельцем снимок выбранного stamp и текущую реализацию Map. Результат:
`OBJECT_AND_GRID_PARITY.md`.

### Известная временная оговорка

Главный video опубликован 10.12.2025. Update от 12.01.2026 о retiring Path и более поздние изменения dashboard/Windows app считаются актуальнее. Неясные Q&A ведущего помечены как открытые, потому что в нескольких местах он сам исправлял ответ.

## Предлагаемый native stack — первичные источники

### Avalonia

- [Avalonia 12 release](https://avaloniaui.net/blog/avalonia-12/)
- [Avalonia 12.1 release](https://avaloniaui.net/blog/release-12-1)
- [Windows platform guide](https://docs.avaloniaui.net/docs/platform-specific-guides/windows)
- [Custom rendering](https://docs.avaloniaui.net/docs/graphics-animation/custom-rendering)
- [Licensing/free tools](https://docs.avaloniaui.net/tools)

### .NET

- [.NET 10 download](https://dotnet.microsoft.com/en-us/download)
- [Single-file deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)

### Сравнивавшиеся альтернативы

- [Qt Graphics View](https://doc.qt.io/qt-6/graphicsview.html)
- [Qt licensing](https://doc.qt.io/qt-6/licensing.html)
- [Qt LGPL obligations](https://www.qt.io/development/open-source-lgpl-obligations)
- [Godot 2D introduction](https://docs.godotengine.org/en/latest/tutorials/2d/introduction_to_2d.html)
- [Godot Windows/project export](https://docs.godotengine.org/en/stable/tutorials/export/exporting_projects.html)

## AI/process candidates — официальные или исходные страницы

- [Claude Code common workflows — Work with images](https://code.claude.com/docs/en/common-workflows#work-with-images)
- [Claude Code computer use](https://code.claude.com/docs/en/computer-use) — CLI preview только macOS; Windows Desktop не равен запрошенной консольной версии
- [Anthropic official skills](https://github.com/anthropics/skills)
- [Chrome DevTools MCP](https://github.com/ChromeDevTools/chrome-devtools-mcp)
- [Repomix](https://github.com/yamadashy/repomix)
- [ImageMagick compare](https://imagemagick.org/script/compare.php)
- [Accessibility Insights for Windows](https://accessibilityinsights.io/docs/windows/overview/)
- [Accessibility Insights for Windows source](https://github.com/microsoft/accessibility-insights-windows)
- [Microsoft OmniParser](https://github.com/microsoft/OmniParser)
- [Windows-MCP](https://github.com/CursorTouch/Windows-MCP) — изучен как широкий desktop-control MCP, не установлен
- [UIAutomation MCP](https://github.com/locomorange/uiautomation-mcp) — изучен как UIA MCP, не установлен
- [Qwen-Image-Layered](https://github.com/QwenLM/Qwen-Image-Layered)
- [Microsoft TRELLIS.2](https://github.com/microsoft/TRELLIS.2)
- [Kanka](https://kanka.io/)
- [Dungeon Scrawl](https://www.dungeonscrawl.com/)

Остальные URLs сохраняются в исходной книге и перечислены по названию/вердикту в `AI_TOOLS_REVIEW.md`; они не использованы как основания архитектуры.

## Что не является доказательством

- рекламный текст без наблюдаемого сценария;
- число stars без чтения назначения и лицензии;
- результат AI-инструмента без проверки;
- название видео без просмотра;
- зелёная unit-сборка без desktop/file/export smoke;
- старый пост, если официальная текущая документация ему противоречит.
