# Источники и проверка

Дата актуальной проверки: **28.08.2026**.

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

Bounded account walkthrough тестовой карты владельца 26.08.2026 проверил через видимые controls
keyboard profile, Text, Line/roads/portals, Shape boolean/contours, Brush/Mask/Land, stamp
Many/Area/Density/Auto, multi-selection/group, каталог и variant sets, а также четыре Grid
geometry. DevTools/network/storage и извлечение ассетов не использовались. Результаты:
`INKARNATE_ACCOUNT_WALKTHROUGH_20260826.md` и `ASSET_CATALOG_AND_VARIANT_SETS.md`.

## Публичные open-source ориентиры clean-room прохода

| Источник | Что использовано / не использовано |
|---|---|
| [Clipper2](https://github.com/AngusJohnson/Clipper2) | Подтверждает публичную C# библиотеку clipping/offsetting/triangulation и BSL-1.0; пакет не добавлен, нужен отдельный dependency decision |
| [Azgaar Fantasy Map Generator](https://github.com/Azgaar/Fantasy-Map-Generator) | Только публичный архитектурный ориентир separation state/editors/renderers; web stack, код, UI и assets не переносятся |
| [Tiled — Editing Tilesets](https://github.com/mapeditor/tiled/blob/master/docs/manual/editing-tilesets.rst) | Публичное подтверждение image-collection sets, stable per-item metadata/alignment/probability; код, UI, formats и assets не переносятся |
| [Tiled — Tileset implementation](https://github.com/mapeditor/tiled/blob/master/src/libtiled/tileset.cpp) | Публичное подтверждение разделения remove-from-set и delete-content; используется только как data-model cross-check, dependency не добавлена |

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

### Активный Codex workflow

- [GPT-5.6 Sol model](https://developers.openai.com/api/docs/models/gpt-5.6-sol) — активный coding model, контекст, reasoning и поддерживаемые tools
- [GPT-5.6 prompting guide](https://developers.openai.com/api/docs/guides/latest-model?model=gpt-5.6) — outcome-first структура, tool guidance и autonomy boundary
- [AGENTS.md](https://learn.chatgpt.com/docs/agent-configuration/agents-md) — репозиторные инструкции Codex
- [Subagents](https://learn.chatgpt.com/docs/agent-configuration/subagents) — независимые параллельные подзадачи, только когда они действительно разделимы
- [Long-running work](https://learn.chatgpt.com/docs/long-running-work) — checkpoints и устойчивое состояние длинного этапа
- [Sandboxing](https://learn.chatgpt.com/docs/sandboxing) и [approvals/security](https://learn.chatgpt.com/docs/agent-approvals-security) — одна явная граница полномочий вместо повторяющихся запретов
- [OpenAI: Codex just got better for developers](https://www.youtube.com/watch?v=eiQgljOrkWU) — официальный video overview с главой GPT-5.6 Sol; transcript через Browser не был доступен, поэтому скрытые детали из видео не приписывались
- [Практический 6-project Codex vs Claude experiment](https://github.com/AdrielRod/codex-vs-claude-code) — неофициальный воспроизводимый источник для сравнения поведения, не benchmark продукта
- [Hacker News discussion](https://news.ycombinator.com/item?id=46391391) — смешанные пользовательские оценки; только secondary/anecdotal evidence

### Проверочные инструменты

- [pre-commit](https://pre-commit.com/)
- [Lychee](https://github.com/lycheeverse/lychee)
- [NuGet Audit](https://learn.microsoft.com/en-us/nuget/concepts/auditing-packages)
- [dotnet format](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-format)
- [dotnet-coverage](https://learn.microsoft.com/en-us/dotnet/core/additional-tools/dotnet-coverage)
- [dotnet-counters](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-counters)
- [dotnet-gcdump](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-gcdump)
- [Stryker.NET](https://stryker-mutator.io/docs/stryker-net/introduction/)
- [Avalonia Headless testing](https://docs.avaloniaui.net/docs/testing/setting-up-the-headless-platform)
- [WinAppDriver](https://github.com/microsoft/WinAppDriver)
- [FsCheck](https://fscheck.github.io/FsCheck/)
- [ArchUnitNET](https://github.com/TNG/ArchUnitNET)

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

## Публичные editor/geometry/release references, проверено 29.08.2026

Exact commits, лицензии и admission-решения закреплены в `external-sources.json`; подробный вывод —
`PUBLIC_REUSE_RESEARCH_20260829.md`.

- [Clipper2](https://github.com/AngusJohnson/Clipper2) и
  [NetTopologySuite](https://github.com/NetTopologySuite/NetTopologySuite) — polygon/topology
  candidates; не добавлены в production graph.
- [Mapsui](https://github.com/Mapsui/Mapsui),
  [Avalonia.Samples](https://github.com/AvaloniaUI/Avalonia.Samples) и
  [SkiaSharp](https://github.com/mono/SkiaSharp) — официальные/публичные .NET/Avalonia/Skia
  references для viewport, tests и будущего font-shaping probe.
- [Tiled](https://github.com/mapeditor/tiled),
  [MapTool](https://github.com/RPTools/maptool), [Pinta](https://github.com/PintaProject/Pinta),
  [PixiEditor](https://github.com/PixiEditor/PixiEditor),
  [LDtk](https://github.com/deepnight/ldtk) и
  [Azgaar Fantasy Map Generator](https://github.com/Azgaar/Fantasy-Map-Generator) — editor/grid
  reference only с разными license/architecture restrictions; direct code/assets не переносились.
- [Velopack](https://github.com/velopack/velopack),
  [official documentation](https://docs.velopack.io/) и
  [self-hosting/local source](https://docs.velopack.io/distributing/self-hosting) — Stage 11
  updater candidate; Flow/cloud не является обязательным.
- [Nerdbank.GitVersioning](https://github.com/dotnet/Nerdbank.GitVersioning) и
  [MinVer](https://github.com/adamralph/minver) — Git-derived versioning candidates, отложены до
  принятого tag/release flow.
- [SVG.NET](https://github.com/vvvv/SVG) — изучен и отклонён для текущего Skia pipeline.

## Что не является доказательством

- рекламный текст без наблюдаемого сценария;
- число stars без чтения назначения и лицензии;
- результат AI-инструмента без проверки;
- название видео без просмотра;
- зелёная unit-сборка без desktop/file/export smoke;
- старый пост, если официальная текущая документация ему противоречит.
