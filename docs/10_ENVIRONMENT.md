# 10. Локальная среда

## Нужно для Map

| Компонент | Назначение |
|---|---|
| Windows x64 на текущем ПК владельца | единственная целевая среда |
| .NET SDK 10.0.400 | build/run и адресные tests |
| Git | история и сохранение изменений |
| Codex с выбранной владельцем моделью | разработка по запросу владельца; модель не фиксируется документом |
| GitHub CLI | только read-only проверка и явно разрешённая синхронизация Project |

Avalonia и Skia восстанавливаются как NuGet-зависимости проекта. Python, Node, pre-commit,
coverage, Stryker, diagnostics tools, ImageMagick и Accessibility Insights не являются частью
обычной разработки Map.

## Обычные команды

```powershell
dotnet build .\Map.slnx -c Release
dotnet run --project .\src\Map.App\Map.App.csproj -c Release
```

Сборка ограничена `win-x64`, является framework-dependent (`SelfContained=false`) и использует
установленный .NET runtime.
Нативные PDB Skia/HarfBuzz удаляются из output как ненужные для запуска.

Другой ПК, чистая Windows, VM/Sandbox, installer, updater, portable/release-пакет, подпись и
публикация не входят в текущую среду. Для них требуется новое прямое решение владельца.

Новый инструмент или пакет добавляется только тогда, когда без него нельзя выполнить конкретную
задачу владельца.

## GitHub сейчас

`gh` авторизован как `dmfromtheash`; private `dmfromtheash/map` подключён как `origin`. Токен пока
не имеет `read:project`, поэтому Project настраивается отдельно. Связка и границы внешних действий
описаны в `docs/GITHUB_PROJECTS.md`.
