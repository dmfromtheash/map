# Переносимые заготовки ассетов

Обычный `StampDerivedAssetStore` только читает `<каталог>/derived/v1/<sha>.mapderived`.
Отсутствующие, устаревшие или повреждённые данные восстанавливаются в рабочем потоке;
обычный рендер не записывает portable-файлы в библиотеку. Машинный кэш остаётся в профиле.

Явная подготовка (БЕЗ ОКНА, операция записи в указанные каталоги):

```powershell
dotnet run --project tools/Map.Performance -c Release --no-build -- --profile derived-build --catalogs C:\MyLibrary\catalog --dry-run --output C:\Checks\derived-plan.json
dotnet run --project tools/Map.Performance -c Release --no-build -- --profile derived-build --catalogs C:\MyLibrary\catalog --output C:\Checks\derived-result.json
```

Запускать запись только для каталогов, которые разрешено изменять. В задаче USER
`D:\MapLibrary` остаётся источником чтения; эти команды туда не запускались.
Файл содержит цвет обзора, вспомогательную маску32×32 и точные premultiplied mip
до1024px; маска пока не участвует в отсечении объектов. Заголовок и блоки проверяются
по версии, границам и SHA256. Пути источников относительные. Первое доверие на машине
требует SHA оригинала и всех ступеней; подтверждение размера/mtime хранится только
в машинном кэше и входит в его дисковый лимит. Изменение mtime повторяет проверку байтов.
Повреждённый portable исключается до изменения файла, запросы восстанавливают локальный
кэш. Запись идёт во временный файл с атомарным переименованием. Явный builder требует
отдельного экземпляра стора, не обслуживающего холст.

ЖИВОЙ A/B использует обычный профиль `native-million-assets`:

```powershell
$env:MAP_DRIVE_LIBRARY_ROOTS='D:\MapLibrary'
$env:MAP_PERF_PORTABLE_FIXTURE='plain' # второй вариант: portable
$env:MAP_MEASURE_GPU_FLUSH='1'
dotnet run --project tools/Map.Performance -c Release --no-build -- --profile native-million-assets --object-count 100 --output C:\Checks\portable-live.json
```

Режим fixture импортирует первые100 SHA-сортированных исходников владельца в отдельный
временный каталог профиля. `portable` явно готовит заготовки только там. В обоих режимах
измеряемый машинный кэш сначала пуст; подготовка fixture идёт до открытия измеряемого
редактора и записывается отдельно. Маршрут, seed и картинки одинаковы. Профиль после
проверки удаляется; окно/GPU и итоговые пиксели проверяются отдельно от тестов reader/writer.
Импорт прогревает файловый кэш Windows: этот A/B не является проверкой холодного диска ОС.
