# Stage 1 — технический spike нативного ядра

Изолированная проверка стека **.NET 10 + Avalonia 12.1 + Skia** (DEC-002) на самых рискованных
свойствах будущего редактора карт. Это **не** MVP и не заготовка production-кода: здесь нет
dashboard, финального дизайна, каталога ассетов и формата `.mapproj` v1.

Всё изображённое на холсте — самостоятельно сгенерированные геометрические fixtures
(квадрат / круг / треугольник) с собственной палитрой. Сторонние ассеты не используются.

## Структура

| Проект | Назначение | Зависит от |
|---|---|---|
| `src/Map.Spike.Core` | документ, геометрия, viewport, spatial index, команды, тайлы, маска | — |
| `src/Map.Spike.Persistence` | контейнер `.mapspike`, атомарное сохранение, recovery journal, fault injection | Core |
| `src/Map.Spike.Rendering` | обход сцены, culling, кэш тайлов, кэш геометрии, Skia-адаптер, hit-test | Core, SkiaSharp |
| `src/Map.Spike.Export` | offscreen 4096×4096 transparent PNG | Core, Rendering |
| `src/Map.Spike.App` | настоящее Avalonia-окно с custom Skia canvas | все |
| `src/Map.Spike.Benchmarks` | консольный benchmark + помощник аварийного завершения процесса | Core, Persistence, Rendering, Export |
| `tests/Map.Spike.Tests` | xUnit: команды, hit-test, save/reopen, fault/recovery, рендер, export | все |

`Map.Spike.Core` не ссылается ни на Avalonia, ни на SkiaSharp — это проверяется чтением
`dotnet list spike/Map.Spike.slnx reference` и содержимым `Map.Spike.Core.csproj`.

## Сборка и проверки

Из корня репозитория:

```powershell
dotnet restore spike/Map.Spike.slnx --force-evaluate
dotnet build spike/Map.Spike.slnx -c Release --no-restore
dotnet test spike/Map.Spike.slnx -c Release --no-build
```

Одной командой с чистого состояния (тоже из корня):

```powershell
dotnet test spike/Map.Spike.slnx -c Release
```

## Запуск окна

```powershell
dotnet run --project spike/src/Map.Spike.App -c Release
```

Или собранный exe: `spike/src/Map.Spike.App/bin/Release/net10.0/Map.Spike.App.exe`.

### Клавиши и мышь

| Ввод | Действие |
|---|---|
| `1` / `2` / `3` | инструмент: выбор / кисть / стирание маски |
| колесо | зум вокруг курсора |
| средняя кнопка или `Alt`+перетаскивание | панорамирование |
| `Z` | вписать холст в окно |
| `M` | включить/выключить маску |
| `O` | включить/выключить красный overlay режима маски |
| `Ctrl+Z` / `Ctrl+Y` | undo / redo |
| `Ctrl+S` | атомарное сохранение |
| `Ctrl+R` | повторное открытие с диска |
| `E` | экспорт PNG 4096×4096 |
| `F` | кадровый benchmark в окне (60 прогревочных + 300 измеряемых кадров) |
| `F5` | пересобрать сцену из 10 000 объектов |

Выходные файлы: `%LOCALAPPDATA%\Map\Spike\`
(`spike-session.mapspike`, `export-4096.png`, `crash.log` при необрабатываемом исключении).
Recovery journal: `%LOCALAPPDATA%\Map\Recovery\<documentId>\journal.json`.

## Benchmark

```powershell
dotnet run --project spike/src/Map.Spike.Benchmarks -c Release -- --out artifacts/benchmark
```

Пишет `artifacts/benchmark/benchmark-report.md` c моделью среды, warmup, median и P95.

**Важно:** консольный benchmark рисует в **программный** raster-surface Skia без GPU-контекста.
Его кадровые числа не характеризуют работу окна. Реальный GPU-путь измеряется только клавишей `F`
в приложении, и отчёт обязан приводить оба числа отдельно.

## Проверка отсутствия сетевого слушателя

```powershell
$p = Start-Process spike\src\Map.Spike.App\bin\Release\net10.0\Map.Spike.App.exe -PassThru
Start-Sleep 8
Get-NetTCPConnection -State Listen | Where-Object { $_.OwningProcess -eq $p.Id }
$p.Kill()
```

Пустой результат означает, что процесс не слушает ни один TCP-порт.

## Ручной smoke в настоящем окне

```powershell
powershell -ExecutionPolicy Bypass -File .\spike\tools\drive-window.ps1 -OutDir .\artifacts\spike-smoke
```

Скрипт запускает собранный exe, отправляет **настоящий** ввод Win32 (`keybd_event`, `mouse_event`)
и сохраняет снимки окна через `PrintWindow`. Он ничего не утверждает — доказательством являются
сохранённые PNG и напечатанные факты о процессе.

Две особенности, из-за которых наивная версия скрипта молча ничего не делала:

- `SetForegroundWindow` из фонового процесса запрещён Windows; нужен `AttachThreadInput` к потоку
  текущего активного окна, иначе ввод уходит в консоль;
- `SendKeys` преобразует символ через `VkKeyScan` для **активной раскладки**, поэтому при русской
  раскладке латинские буквы теряются, а цифры проходят. Скрипт шлёт virtual-key коды напрямую.

## Аварийное завершение во время сохранения

Помощник убивает собственный процесс в заданной фазе сохранения:

```powershell
dotnet run --project spike/src/Map.Spike.Benchmarks -c Release -- crash-during-save `
  --target C:\Temp\killed.mapspike --journal C:\Temp\journal --phase DuringReplace
```

Автоматически это делают тесты `ProcessKillRecoveryTests`.

## Что spike сознательно не делает

- не рисует растровые слои с обрезкой по границе холста — штрих может выходить за рамку;
- не сглаживает края объектов (`DrawVertices` без MSAA) — это осознанный размен ради бюджета кадра;
- не хранит viewport, выделение и режим инструмента в документе;
- не реализует миграции, checkpoints, linked-ассеты и предпросмотр внутри контейнера;
- формат `.mapspike` намеренно отличается расширением от принятого `.mapproj` (DEC-005),
  чтобы артефакт spike нельзя было принять за настоящий файл проекта.
