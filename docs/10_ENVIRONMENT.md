# 10. Локальная среда

## Нужно для Map

| Компонент | Назначение |
|---|---|
| Windows x64 на текущем ПК владельца | единственная целевая среда |
| .NET SDK 10.0.400 | build/run и адресные tests |
| Git | история и сохранение изменений |
| Codex с выбранной владельцем моделью | разработка по запросу владельца; модель не фиксируется документом |
| GitHub CLI | только read-only проверка и явно разрешённая синхронизация Project |
| RenderDoc 1.46, Microsoft PIX 2603.25 | захват GPU-кадра (DEC-050): `tools/ai/live/renderdoc-capture.ps1` |
| dotnet-trace, dotnet-counters, dotnet-gcdump, dotnet-dump | профили CPU/памяти и разбор дампов |
| GitHub Actions (`.github/workflows`) | независимая Release-сборка и unit-тесты после каждого push; релиз по тегу |
| FlaUI (`tools/Map.LiveCheck`) | живой проход по Release-exe через UI Automation; вне `Map.slnx` |

Avalonia и Skia восстанавливаются как NuGet-зависимости проекта. Python (скрипты арта и разбора кадров), Node, pre-commit,
coverage, Stryker, ImageMagick и Accessibility Insights не являются частью обычной разработки Map;
инструменты DEC-050 используются по сценариям из `docs/STATE_HANDOFF.md`.

## Обычные команды

```powershell
dotnet build .\Map.slnx -c Release
dotnet run --project .\src\Map.App\Map.App.csproj -c Release
```

Сборка ограничена `win-x64`, является framework-dependent (`SelfContained=false`) и использует
установленный .NET runtime.
Нативные PDB Skia/HarfBuzz удаляются из output как ненужные для запуска.

Скачиваемая сборка (DEC-050): `tools/release/publish-release.ps1` собирает самодостаточный
win-x64 zip с ReadyToRun; тот же скрипт запускает `release.yml` по тегу `v*`. Installer, updater и
подпись кода по-прежнему не входят в среду.

Новый инструмент или пакет добавляется только тогда, когда без него нельзя выполнить конкретную
задачу владельца.

## GitHub сейчас

По DEC-050 репозиторий `dmfromtheash/map` публичный, `main` указывает на последнюю ветку, релизы
лежат на странице Releases; push и создание релизов разрешены в рамках поручений владельца.

Состояние remote, аккаунта и разрешений меняется; проверять его read-only перед относящейся
к GitHub задачей, не переносить сведения о токене из старых отчётов. Связка и границы внешних
действий описаны в `docs/GITHUB_PROJECTS.md`; текущая документационная уборка внешних действий
не выполняет и разрешений не меняет.
