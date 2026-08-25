# 04. Техническая архитектура

## Предлагаемый стек

| Слой | Выбор | Причина |
|---|---|---|
| Runtime | .NET 10 LTS / C# 14 | официальный Windows SDK, self-contained publish, сильная диагностика и тестирование |
| Desktop UI | Avalonia 12.1 | нативное окно без Chromium, MIT, Windows backend и доступность |
| Canvas | Skia через Avalonia custom rendering | аппаратный Direct3D-путь с программным fallback, offscreen render/export |
| UI state | CommunityToolkit.Mvvm | небольшой MIT toolkit; не смешивать с доменной моделью |
| Tests | xUnit + FluentAssertions только если лицензия/политика подтверждены; иначе xUnit assertions | быстрые unit/integration tests |
| Benchmarks | BenchmarkDotNet + встроенные scenario counters | измерять scene traversal, hit test, save/export |
| Packaging | self-contained `win-x64`; installer выбрать отдельным решением | пользователь получает обычную Windows-программу |

Версии пакетов фиксируются после spike. Никакой runtime-зависимости от Python, Node, браузера или сети у приложения нет.

## Почему не Electron/Tauri

Требование — именно программа, не веб-приложение. WebView увеличивает размер/память и переносит продукт в браузерную модель. Для сложного графического холста это не даёт преимущества, оправдывающего нарушение требования.

## Почему не Qt 6/C++ сейчас

Qt Graphics View зрелый и силён для множества 2D-объектов, но C++ увеличивает стоимость итерации с Opus, сложность memory-safety и поставки. LGPL/GPL-комбинации требуют более строгого контроля динамической линковки и модулей. Qt остаётся резервом, если Avalonia/Skia spike не проходит бюджеты.

## Почему не Godot сейчас

Godot хорош как 2D-движок и умеет экспортировать Windows-приложения, но редактор карт — document-centric desktop tool с док-панелями, доступностью, файловыми диалогами и сложными inspectors. Это возможно, но потребует больше собственной UI-инфраструктуры. Godot остаётся резервом для случая, если приоритет сместится к игровому viewport.

## Модули решения

```text
Map.App              composition root, window lifecycle, DI
Map.Presentation     Avalonia views/view-models, commands, dialogs
Map.Core             document model, geometry, tools, commands, invariants
Map.Rendering        scene traversal, caches, hit-test, Skia adapters
Map.Persistence      .mapproj, migrations, atomic save, recovery journal
Map.Assets           import, validation, catalog, thumbnails, provenance
Map.Export           offscreen tiled export and color pipeline
Map.Platform.Windows Windows paths, file association, installer hooks, diagnostics
Map.Tests.*          unit, integration, rendering golden tests, fault injection
Map.Benchmarks       reproducible performance scenarios
```

Зависимости направлены внутрь:

```text
Presentation ─┐
Rendering ────┼──> Core
Persistence ──┤
Assets ───────┤
Export ───────┘
App связывает адаптеры, но Core не знает о них.
```

## Модель редактирования

- `MapDocument` — единственный источник истины.
- UI создаёт `IEditCommand`; команда валидирует preconditions и выдаёт reversible delta.
- Drag/brush gesture собирает промежуточный preview, а в историю попадает одна завершённая транзакция.
- Renderer получает immutable snapshot/revision и не изменяет документ.
- Долгие операции работают на snapshot с cancellation token и progress.
- Selection, viewport и hover — session state, а не обязательная часть сохранённой сцены.

## Рендеринг

### Координаты

- документ хранит world coordinates в `double`;
- viewport — матрица world→screen;
- raster layers имеют собственную pixel density и tile origin;
- export использует тот же scene evaluator, но другой target и scale.

### Проход

1. определить видимую область;
2. запросить spatial index для Object layers;
3. взять валидные layer/tile caches;
4. применить masks → content → effects в зафиксированном порядке;
5. нарисовать selection/guides отдельным overlay, не попадающим в export.

### Производительность

- spatial index для hit-test и culling;
- tile cache для Brush/Land и дорогих фильтров;
- revision-based invalidation, а не глобальный redraw по любому property;
- LOD при движении viewport и точный pass после idle;
- texture atlas применять только после измерения, не как преждевременную сложность;
- background decoding/thumbnails с ограниченным parallelism;
- память undo/cache имеет бюджеты и telemetry только локально в diagnostics.

## Потоки

- UI thread: ввод, короткие изменения модели, композиция кадра;
- worker pool: decode, thumbnail, save serialization, export tiles, checksums;
- snapshot не блокирует редактирование надолго;
- результат фоновой операции применяется только если document revision/operation token совместим.

## Надёжность

- атомарный save: write temp → validate/read-back manifest → flush → replace → backup;
- recovery journal в `%LOCALAPPDATA%\Map\Recovery`, а не рядом с read-only файлом;
- startup lock только на конкретный открытый документ, с обработкой stale lock;
- crash dump/diagnostic log не содержит содержимое карты или пути без согласия на экспорт отчёта;
- миграция выполняется на копии и сохраняет source backup.

## Безопасность импорта

- лимиты размеров/пикселей и защита от decompression bombs;
- canonical paths внутри ZIP; запрет `..`, абсолютных путей и symlink escape;
- SVG ограничивается безопасным статическим профилем: без script, external URL, embedded HTML;
- неверный asset не исполняется и не ломает каталог;
- шрифты и метаданные проходят отдельную валидацию.

## Диагностика без телеметрии

Локальная страница Diagnostics показывает renderer, GPU/driver, CPU fallback, размеры cache/undo, open document stats, последние save/recovery события. Отправка наружу отсутствует по умолчанию.

## Технические ворота до принятия DEC-002

Spike должен доказать:

1. настоящее нативное окно и отсутствие WebView/server;
2. pan/zoom 60 FPS на 10 000 простых объектов на тестовом ПК;
3. hit-test/select без полного обхода сцены;
4. raster brush tile и одна undo-транзакция;
5. layer mask preview;
6. offscreen export минимум 4K;
7. atomic save + recovery после принудительного завершения;
8. self-contained `win-x64` запуск на машине без отдельно установленного runtime (позже на чистой VM).

Если два прохода оптимизации не дают бюджетов, сравнить Qt spike на том же сценарии и вынести DEC-002 владельцу заново.

