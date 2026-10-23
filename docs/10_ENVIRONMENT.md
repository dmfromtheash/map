# 10. Локальная среда

Обновлено 2026-10-23. Новый инструмент или пакет добавляется только по решению владельца
(`docs/00_STAGE_REFERENCE.md §H, §K`).

## Нужно для Map

| Компонент | Назначение |
|---|---|
| Windows 11 x64, 16 ГБ ОЗУ, экран 2560×1440 (+ второй монитор для тестовых окон) | единственная целевая среда |
| .NET SDK 10.0.400 | сборка, запуск, тесты |
| Git, GitHub CLI (`gh`) | история; push, CI (`gh run watch`), релизы — по `AGENTS.md §Git` |
| Windows PowerShell 5.1 | скрипты `tools/` (`pwsh` / PowerShell 7 **не установлен**; `.ps1` с BOM) |
| Python 3 (`py -3`) | генераторы арта `tools/HexArt`, разбор кадров `tools/ai/live/analyze_frames.py` |
| Claude Code | главный исполнитель, весь визуал |
| Codex CLI (GPT-6.1 Sol, GPT-6 Astra) | второй исполнитель по «да» владельца на задачу |
| RenderDoc 1.46, Microsoft PIX 2603.25 | захват GPU-кадра: `tools/ai/live/renderdoc-capture.ps1` (DEC-050) |
| Visual Studio 2026 Community | профилировщик CPU/GPU/памяти: `tools/ai/live/vs-profile.ps1` (DEC-051) |
| dotnet-trace, dotnet-counters, dotnet-gcdump, dotnet-dump | профили CPU/памяти, дампы |
| FlaUI (`tools/Map.LiveCheck`) | живая проверка Release-exe через UI Automation; вне `Map.slnx` |
| GitHub Actions (`.github/workflows`) | `build.yml` — сборка и 5 тестовых проектов после push; `release.yml` — zip по тегу `v*` |

NuGet-зависимости продукта: Avalonia, SkiaSharp, NetTopologySuite 2.6.0 (DEC-051),
Microsoft.Data.Sqlite 10.0.13 (MBTiles, DEC-051). Coverage, Stryker, pre-commit, ImageMagick,
Accessibility Insights — не часть обычной разработки.

## Команды

```powershell
dotnet build .\Map.slnx -c Release -m:1 -nr:false -p:UseSharedCompilation=false
dotnet run --project .\src\Map.App\Map.App.csproj -c Release
```

Флаги `-m:1 -nr:false -p:UseSharedCompilation=false` берегут ОЗУ и не оставляют фоновых
сборщиков. Тесты, `tools/Map.Performance` и `tools/Map.LiveCheck` не входят в `Map.slnx` —
собирать отдельно (`docs/00_STAGE_REFERENCE.md §M`).

Локальная сборка — framework-dependent `win-x64`. Скачиваемая сборка (DEC-050):
`tools/release/publish-release.ps1` — самодостаточный zip с ReadyToRun. Installer, updater и подпись
не входят в среду.

## GitHub

Репозиторий `dmfromtheash/map` публичный (DEC-050), `main` — fast-forward продуктовой ветки,
релизы на странице Releases. Состояние токена и прав проверять перед GitHub-задачей, не переносить
из старых отчётов.
