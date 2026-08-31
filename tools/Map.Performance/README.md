# Map.Performance

Локальный deterministic runner Stage 13. Проект намеренно не входит в `Map.slnx` и не запускается
обычной сборкой приложения. Generated JSON записывается в ignored `tmp/performance` либо в явно
переданный `--output`.

```powershell
dotnet run --project .\tools\Map.Performance\Map.Performance.csproj -c Release -- `
  --profile stage13a --object-count 50000 --seed 13013 --warmup 1 --iterations 5
```

Доступные профили текущего среза: `stage13a`, `small-render`, `hit-test`, `marquee`,
`object-tree`, `spatial-query`, `native-stage13a`. Перед/после сравниваются с одинаковыми `seed`,
`object-count` и fixture hash. CPU-render timing не выдаётся за native input-to-present: профиль
`native-stage13a` открывает отдельное настоящее off-screen окно без активации и системного ввода,
считает реализованные строки и измеряет путь от interaction-start до завершения Skia scene pass.
