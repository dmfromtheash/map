# 10. Локальная среда

## Нужно для Map

| Компонент | Назначение |
|---|---|
| Windows x64 на текущем ПК владельца | единственная целевая среда |
| .NET SDK 10.0.400 | build/run и адресные tests |
| Git | история и сохранение изменений |
| Codex `gpt-5.6-sol` | разработка по запросу владельца |
| GitHub CLI | только read-only проверка и явно разрешённая синхронизация Project |

Avalonia и Skia восстанавливаются как NuGet-зависимости проекта. Python, Node, pre-commit,
coverage, Stryker, diagnostics tools, ImageMagick, Accessibility Insights, VM и гипервизор не
являются частью обычной разработки Map.

## Обычные команды

```powershell
dotnet build .\Map.slnx -c Release
dotnet run --project .\src\Map.App\Map.App.csproj -c Release
```

Сборка ограничена `win-x64` и использует установленный .NET runtime (`SelfContained=false`).
Нативные PDB Skia/HarfBuzz удаляются из output как ненужные для запуска.

Новый инструмент или пакет добавляется только тогда, когда без него нельзя выполнить конкретную
задачу владельца.

## GitHub сейчас

`gh` авторизован как `dmfromtheash`; private `dmfromtheash/map` подключён как `origin`. Токен пока
не имеет `read:project`, поэтому Project настраивается отдельно. Связка и границы внешних действий
описаны в `docs/GITHUB_PROJECTS.md`.
