# Map.Performance

Локальный deterministic runner Stage 13. Проект намеренно не входит в `Map.slnx` и не запускается
обычной сборкой приложения. Generated JSON записывается в ignored `tmp/performance` либо в явно
переданный `--output`.

```powershell
dotnet run --project .\tools\Map.Performance\Map.Performance.csproj -c Release -- `
  --profile stage13a --object-count 50000 --seed 13013 --warmup 1 --iterations 5
```

Доступные профили текущего среза: `stage13a`, `stage13b-baseline`, `stage13b`,
`stage13c-baseline`, `stage13c`, `stage13d`, `small-render`, `hit-test`, `marquee`,
`object-tree`, `spatial-query` и соответствующие `native-stage13a`…`native-stage13d`.
Перед/после сравниваются с одинаковыми `seed`, `object-count` и fixture hash. CPU-render timing
не выдаётся за native input-to-present: native-профиль открывает отдельное настоящее off-screen
окно без активации и системного ввода, считает реализованные строки и измеряет путь от
interaction-start до завершения Skia scene pass. `stage13d` дополнительно проверяет 131K sparse
canvas, bounded export plan, 50K/100K save-reopen и test-only 262K boundary; 262K не меняет
production policy.
