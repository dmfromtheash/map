# 10. Среда и инструменты

## Проверено 28.08.2026

| Инструмент | Фактическая версия / источник | Назначение в Map | Статус |
|---|---:|---|---|
| Windows | `10.0.26200`, x64 | рабочая и целевая native-среда | active |
| Codex desktop/CLI | OpenAI-signed `codex-cli 0.142.4` | основной coding agent и локальная оркестрация | active |
| Codex model | `gpt-5.6-sol` | implementation `high`; review/security/architecture `xhigh` | required by prompt |
| Codex Browser | bundled `26.820.80927` | публичные источники, DOM/screenshots, current research | on demand |
| Codex Computer Use | bundled `26.820.80927` | ручной smoke выбранного настоящего Windows-окна | UI stages |
| GitHub connector | OpenAI curated | read-only проверка remote/repo, issues/PR по прямому запросу | connector; у Map remote не настроен |
| Codex Security | OpenAI curated | explicit/boundary-triggered standard/diff security review | on demand |
| OpenAI Docs skill | system skill | актуальные инструкции по Codex/model/prompting | workflow research |
| .NET SDK | `10.0.400`, Microsoft-signed | restore/build/test/publish/analyzers/NuGet Audit | active |
| Git | `2.54.0.windows.1`, signed | локальный version-control и review boundary | active |
| ripgrep | `15.2.0` via WinGet | быстрый read-only поиск | active; binary unsigned |
| Python | `3.13.13`, PSF-signed | pre-commit и служебные scripts, не app runtime | active |
| Node/npm | `22.22.3 / 10.9.8` | системные/on-demand tools; не app runtime | avoid unless justified |
| pre-commit | `4.6.2` | быстрые Git-gates | active |
| pre-commit-hooks | exact commit `3e8a870...` (`v6.0.0`) | repository file hygiene | active, immutable pin |
| Lychee | exact hook commit `2bba271...`; binary `0.24.2` via WinGet | offline Markdown links/anchors | active; PATH binary unsigned |
| dotnet-coverage | local tool `18.10.0` | targeted execution coverage | on demand |
| Stryker.NET | local tool `4.16.0` | mutation strength critical tests | on demand |
| dotnet-counters/trace/gcdump | local tools `9.0.661903` | CPU/GC/allocation/trace evidence | performance/memory stages |
| ImageMagick | `7.1.2-30 Q16-HDRI` | dimensions/alpha/RMSE/diff PNG | visual stages |
| Accessibility Insights | `1.1.2924.1`, Microsoft-signed | UIA tree, FastPass, Tab/focus/contrast | accessibility gates |
| project-owned UIA drivers | repository scripts | reproducible native Release scenarios | UI stages |

Локальные `.NET tools` закреплены в `dotnet-tools.json` с `rollForward: false`. Release tags внешних pre-commit repositories дополнительно заменены на exact Git commits. Unsigned `rg`/`lychee` не признаны вредоносными по одному этому признаку, но их происхождение, hash/version и канал обновления нужно повторно проверять при upgrade.

Исследовательские Python-пакеты (PDF/YouTube/Office) не входят в runtime или обычный build Map. Они не перечисляются в этапном prompt, пока этап не требует работы с соответствующим источником.

## Базовая проверка среды

```powershell
codex --version
dotnet --version
git --version
rg --version
python --version
python -m pre_commit --version
lychee --version
magick -version
dotnet tool list
powershell -ExecutionPolicy Bypass -File .\scripts\doctor.ps1
```

Успешный doctor доказывает только наличие среды, не готовность этапа.

## Что удалено из активного workflow

- Claude Code/Opus как исполнитель;
- project-only Claude plugins Superpowers и Interfaces;
- `CLAUDE.md`, `.claude/settings.json`, Claude-specific runbook/protocol links;
- Repomix как обычный источник контекста — Codex читает repository напрямую;
- автоматически собираемый Gitleaks hook, который скачивал Go toolchain и обходил историю на commit;
- любые broad Windows-control MCP.

Исторические review/handoff записи могут называть прежнего исполнителя: это evidence прошлого, а не активная инструкция. Пользовательские глобальные Claude plugins не удаляются этой repository-миграцией и не влияют на Codex.

## Намеренно не установлено в Map

- web/Electron/Tauri stack, cloud deployment, servers и auth;
- Qt/CMake/C++ и Godot — только резерв после нового решения;
- local LLM/встроенный AI — продукту не требуется;
- OmniParser/model weights — нет доказанной пользы поверх vision + project driver;
- Appium/WinAppDriver — project UIA drivers уже покрывают текущие сценарии; добавить только при конкретном недостающем E2E;
- FsCheck/ArchUnitNET — полезны для property/architecture tests, но новая dependency требует stage-specific acceptance;
- сторонние desktop MCP — непропорциональный доступ к рабочему столу;
- scraper/cloner/asset extractor — clean-room и licensing risk.

## Security и secrets

Map не требует secret scanner на каждом commit, пока в нём нет accounts/keys/integrations. Это не означает «security не применяется»: import/archive/image parsing, external tools, dependencies, installers/updaters/plugins и network capability являются trust boundaries. Триггеры security review находятся в `docs/11_CODEX_VERIFICATION_PROTOCOL.md`.

NuGet Audit остаётся включённым. Если появляется secret-bearing integration, оформить новый DEC и выбрать scanner/config без тяжёлой автоматической загрузки toolchain на каждый commit.

## Добавление/обновление инструмента

До установки зафиксировать:

1. stage risk/claim, который нельзя доказать существующим средством;
2. официальный source, exact version/commit и hash/signature где доступно;
3. license и влияние на distribution;
4. runtime/build/dev-only;
5. hooks, commands, network, filesystem/desktop authority и telemetry;
6. install size/time и offline impact;
7. rollback/uninstall;
8. конкретный evidence output и условие удаления, если пользы нет.

Наличие плагина/connector в marketplace не является разрешением установить или вызвать его. Внешняя запись, push/PR/release/upload/message требуют отдельного прямого запроса владельца.
