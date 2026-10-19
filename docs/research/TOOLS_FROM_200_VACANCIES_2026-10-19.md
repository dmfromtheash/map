# Инструменты из 200 ИТ-вакансий и их пригодность для Map — 2026-10-19

Статус: исследование по поручению владельца («провести ресёрч 200 вакансий ИТ-направления,
выписать все инструменты, проанализировать и взять те, что подойдут проекту в данный момент»).
Документ ничего не меняет в коде и не добавляет пакетов: по docs/10 и DEC-016 новый инструмент
появляется только по прямому решению владельца. Раздел 4 — список кандидатов на такое решение.

## 1. Метод и ограничения

- 200 вакансий = 5 направлений × 40, собраны 2026-10-19 пятью параллельными агентами по открытым
  страницам работодателей и досок (hh.ru, career.habr.com, jobs.dou.ua, djinni.co, arc.dev,
  Lever/Greenhouse/Ashby-борды, builtin, wellfound, hitmarker, esri.com, страницы студий).
  Каждая строка имеет URL; инструменты выписаны по тексту объявления (включая «будет плюсом»).
- Направления: «.NET desktop» (WPF/Avalonia/WinForms), «Gamedev / графика / tools»,
  «GIS / картография», «.NET backend + DevOps + QA automation», «Арт-пайплайн + UI-редакторы на
  канвасе». Два последних взяты намеренно: редактор карт — это и инструмент художника, и редактор
  на холсте, и их требования ближе к Map, чем общие.
- Даты: 2025–2026, большинство — осень 2026; часть уже в архиве (помечено в таблицах).
- Не отдали содержимое: LinkedIn, Indeed, Glassdoor, getmatch.ru, geekjob.ru; Habr Career — 410 для
  архивных страниц; hh.ru API — 403. Поэтому зарубежные вакансии частично взяты с зеркал-агрегаторов.
- Таблицы вакансий (по 40 строк) и сводная таблица лежат в
  `artifacts/jobs-research-2026-10-19/` (папка вне Git): `jobs_dotnet_desktop.md`,
  `jobs_gamedev_graphics.md`, `jobs_gis_mapping.md`, `jobs_backend_devops_qa.md`,
  `jobs_art_ui_tools.md`, `agg_table.md`.
- Числа в сводной таблице — суммы упоминаний по пяти нормализованным таблицам; одна вакансия,
  назвавшая и «.NET 8», и «.NET 10», даёт две единицы в строке «.NET». Это верхняя оценка,
  достаточная для ранжирования, но не точный счёт вакансий.

## 2. Сводная таблица: что чаще всего требуют (из 200)

| Инструмент/практика | Всего | .NET desktop | Gamedev | GIS | Backend/DevOps/QA | Арт/UI |
|---|---|---|---|---|---|---|
| .NET (8/9/10) | 94 | 50 | 2 | 6 | 36 | 0 |
| C# | 93 | 39 | 12 | 7 | 30 | 5 |
| Git / GitHub / GitLab | 68 | 33 | 6 | 12 | 15 | 2 |
| C++ | 59 | 9 | 35 | 10 | 0 | 5 |
| Тесты unit/integration (фреймворк не назван) | 56 | 23 | 8 | 6 | 16 | 3 |
| AI-ассистенты (Copilot, Claude Code, Cursor, Codex, MCP) | 51 | 15 | 11 | 8 | 9 | 8 |
| Agile/Scrum | 45 | 18 | 2 | 6 | 17 | 2 |
| AWS | 45 | 2 | 2 | 10 | 31 | 0 |
| CI/CD (практика) | 44 | 8 | 4 | 8 | 22 | 2 |
| WPF | 42 | 35 | 4 | 0 | 2 | 1 |
| REST API | 42 | 14 | 0 | 10 | 15 | 3 |
| JavaScript | 42 | 2 | 3 | 23 | 5 | 9 |
| PostgreSQL | 41 | 5 | 0 | 21 | 15 | 0 |
| Python | 41 | 5 | 9 | 18 | 5 | 4 |
| React | 37 | 4 | 1 | 12 | 7 | 13 |
| TypeScript | 37 | 0 | 0 | 15 | 7 | 15 |
| SQL | 36 | 15 | 0 | 13 | 8 | 0 |
| Azure | 35 | 2 | 1 | 6 | 26 | 0 |
| Шейдеры (HLSL/GLSL) | 35 | 0 | 22 | 3 | 0 | 10 |
| MS SQL Server | 32 | 8 | 0 | 5 | 19 | 0 |
| Профилирование (практика, инструмент не назван) | 30 | 5 | 6 | 10 | 2 | 7 |
| Docker | 26 | 3 | 1 | 7 | 15 | 0 |
| Code review | 25 | 5 | 4 | 7 | 8 | 1 |
| Unity | 24 | 2 | 11 | 0 | 1 | 10 |
| Kubernetes | 23 | 2 | 2 | 3 | 15 | 1 |
| Async / многопоточность | 22 | 16 | 6 | 0 | 0 | 0 |
| WebGL | 22 | 0 | 2 | 7 | 0 | 13 |
| PostGIS | 22 | 0 | 0 | 22 | 0 | 0 |
| ООП | 21 | 12 | 3 | 0 | 6 | 0 |
| Entity Framework | 21 | 8 | 0 | 0 | 13 | 0 |
| DirectX 11/12 | 20 | 1 | 15 | 2 | 0 | 2 |
| IaC (Terraform/Bicep) | 19 | 1 | 0 | 0 | 18 | 0 |
| ASP.NET Core | 18 | 10 | 0 | 0 | 8 | 0 |
| Паттерны проектирования | 18 | 10 | 0 | 0 | 6 | 2 |
| SOLID | 17 | 9 | 1 | 0 | 7 | 0 |
| Azure DevOps | 17 | 2 | 0 | 0 | 15 | 0 |
| Unreal Engine | 17 | 0 | 13 | 1 | 0 | 3 |
| Пространственные индексы (R-tree/GiST/H3/quadtree) | 16 | 1 | 0 | 13 | 1 | 1 |
| OpenGL | 16 | 1 | 9 | 3 | 0 | 3 |
| Vulkan | 16 | 0 | 15 | 0 | 0 | 1 |
| MVVM | 15 | 15 | 0 | 0 | 0 | 0 |
| WinForms | 15 | 12 | 1 | 0 | 2 | 0 |
| Linux | 15 | 6 | 0 | 6 | 3 | 0 |
| ArcGIS | 15 | 2 | 0 | 11 | 0 | 2 |
| Photoshop | 15 | 0 | 2 | 0 | 0 | 13 |
| Kafka | 14 | 1 | 0 | 5 | 8 | 0 |
| Avalonia | 13 | 13 | 0 | 0 | 0 | 0 |
| Собственный движок (in-house) | 13 | 0 | 13 | 0 | 0 | 0 |
| GitHub Actions | 12 | 2 | 0 | 0 | 10 | 0 |
| XAML | 11 | 9 | 2 | 0 | 0 | 0 |
| WebGPU | 11 | 0 | 2 | 3 | 0 | 6 |
| PixiJS | 11 | 0 | 1 | 0 | 0 | 10 |
| Атласы спрайтов / draw calls | 11 | 0 | 0 | 0 | 0 | 11 |
| Visual Studio | 10 | 9 | 0 | 0 | 1 | 0 |
| Jenkins | 10 | 2 | 1 | 0 | 7 | 0 |
| Jira | 10 | 2 | 1 | 2 | 3 | 2 |
| Blender | 10 | 0 | 4 | 0 | 0 | 6 |
| Canvas 2D | 10 | 0 | 2 | 0 | 0 | 8 |
| Vector Tiles / MVT | 10 | 0 | 0 | 10 | 0 | 0 |
| QGIS | 10 | 0 | 0 | 9 | 0 | 1 |
| xUnit | 9 | 3 | 0 | 0 | 6 | 0 |
| Figma | 9 | 1 | 0 | 0 | 0 | 8 |
| GDAL/OGR, MapLibre GL, Mapbox GL, Spine, PowerShell | по 9 | | | | | |
| Leaflet, OGC WMS/WFS, Maya, Metal | по 8 | | | | | |
| NUnit, Grafana, Геометрические операции (буферы/пересечения) | по 7 | | | | | |
| RenderDoc, PIX, Playwright, TeamCity, GitLab CI, GeoServer, Undo/redo, CRDT | по 5 | | | | | |
| Dear ImGui, MSTest, Qt, Nsight, Three.js, Scene graph | по 3–4 | | | | | |
| FlaUI, WinAppDriver, Appium, SonarQube, Storybook, SkiaSharp/Skia, dotTrace/dotMemory, Native AOT | по 2–3 | | | | | |
| Serilog, NSubstitute, Aseprite, Godot, CommunityToolkit.Mvvm, Prism, ReactiveUI | по 1 | | | | | |
| Moq, FluentAssertions, Verify, BenchmarkDotNet, OpenTelemetry, Sentry, Roslyn-анализаторы, StyleCop, ReSharper, Rider, MSIX, WiX, Velopack, Inno Setup, Tracy, Superluminal, Perforce, Krita, Procreate, Clip Studio, ImageMagick, Konva, Chromatic | 0 | | | | | |

Наблюдения, важные для Map:

1. Стек Map (C#, .NET 10, Avalonia, Skia, xUnit, Git) совпадает с ядром рынка. Avalonia — уже
   второй desktop-фреймворк после WPF (13 из 40 desktop-вакансий, все новые вакансии hh.ru осени
   2026 — про миграцию WPF/WinForms → Avalonia); .NET 10 прямо требуют 5 из 40.
2. AI-ассистенты стали требованием в 51 упоминании из 200 (Claude Code, Copilot, Cursor, Codex,
   MCP, «умение направлять агентов и верифицировать их код»). Map уже так и работает (Codex,
   Claude Code, REA, windows-mcp, `tools/ai`, `.claude/skills`).
3. Рынок требует практики, а не бренды: «unit/integration tests» — 56 упоминаний, при этом xUnit 9,
   NUnit 7, Moq/FluentAssertions/Verify — 0; «профилирование» — 30, при этом RenderDoc/PIX — по 5,
   dotTrace — 2, Tracy — 0; «CI/CD» — 44, GitHub Actions — 12; «code review» — 25, SonarQube — 2,
   Roslyn-анализаторы — 0.
4. Для редакторов на холсте вакансии называют именно то, что Map уже строит: undo/redo,
   слои и кэши слоёв, scene graph и quadtree/пространственный индекс, hit-testing и ручки
   выделения, атласы спрайтов и экономия draw calls, «60 FPS под нагрузкой», profiling CPU/GPU.
5. Из GIS в Map переносятся концепции, а не продукты: пространственные индексы (16), векторные и
   растровые тайлы (10 + MBTiles/XYZ), геометрические операции, упрощение линий. Сами продукты
   (PostGIS, GDAL, QGIS, MapLibre) — для серверных и веб-карт с реальными координатами.
6. Половина самых частых инструментов (AWS, Azure, Docker, Kubernetes, PostgreSQL, Kafka, EF,
   Terraform, React, TypeScript) относится к серверам и вебу и к личному офлайн-приложению на одном
   ПК неприменима по DEC-001/DEC-007/DEC-016.

## 3. Что в Map уже совпадает с рынком (менять не нужно)

| Требование рынка | Как закрыто в Map |
|---|---|
| C#, современный .NET, Avalonia, MVVM, XAML/код UI | .NET 10 / C# 14, Avalonia 12.1.1, собственный `ViewModelBase` |
| Skia, шейдеры | SkiaSharp 3.119.4, SkSL через `SKRuntimeEffect` (шейдер обрывов) |
| xUnit, unit/integration tests | 6 тестовых проектов на xUnit, адресные прогоны |
| Профилирование CPU/памяти | `dotnet-trace`, `dotnet-counters`, `dotnet-gcdump`, `MAP_FRAME_TRACE` + `analyze_frames.py`, счётчики `tools/Map.Performance` |
| Пространственный индекс, упрощение линий, булевы операции путей | `ObjectSpatialIndex`, Дуглас–Пекер в `RiverGeometry`, `SKPathOp` в рендере рек/дорог |
| Атласы, экономия draw calls, кэши слоёв | готовые картинки переходов, уменьшенные копии тайлов, `LayerImageCache`, `RenderImageCache`, бюджет загрузок на кадр |
| Undo/redo, слои, именованные версии | история команд, draft Undo, schema v13 |
| AI-ассистенты и MCP | Codex + Claude Code, REA, windows-mcp, ETW MCP, `tools/ai/*.ps1`, `.claude/skills` |
| Git, code review | Git/GitHub, `/code-review` перед коммитом (практика 2026-10-18) |
| Python/PowerShell как языки пайплайна | генератор кистей на Python, скрипты `tools/ai` |
| R2R/AOT как плюс | `publish-local-r2r.ps1` |

## 4. Кандидаты, подходящие проекту сейчас (по решению владельца)

Отобраны по трём критериям: инструмент встречается в вакансиях профильных направлений; он закрывает
открытую задачу текущей фазы (скорость гексов и зума, живые проверки, «Release 0 ошибок» после
каждого этапа, 100k+ объектов, 131072²); он не нарушает DEC-001/007/016 и docs/10 (офлайн, один ПК,
без телеметрии, минимальная обвязка).

### 4.1. RenderDoc (и PIX для Windows) — захват GPU-кадра

- Рынок: RenderDoc 5, PIX 5, Nsight 3 из 40 gamedev-вакансий; «CPU/GPU profiling» — 30 из 200.
- Зачем сейчас: открытые вопросы скорости (протяжка r3 в гексах, зум с загрузкой текстур,
  шейдер обрывов, «сотни вызовов через ANGLE, поток ждёт `SKCanvas.Flush`») сегодня диагностируются
  по `dotnet-trace`, то есть по CPU-стороне. RenderDoc снимает кадр Direct3D 11 через ANGLE и
  показывает число draw call, загрузки текстур за кадр, стоимость шейдеров и лишние копии —
  это прямой ответ на гипотезы в handoff.
- Стоимость: бесплатная программа, нуль изменений в коде и пакетах; запуск Map из RenderDoc
  с переменными изолированного профиля. Ограничение docs/10 («новый инструмент — только когда
  без него нельзя») — решение владельца.

### 4.2. Встроенные анализаторы .NET SDK (без пакетов)

- Рынок: явных Roslyn-анализаторов 0, но «code review / SOLID / clean code» — 50 упоминаний,
  SonarQube — 2. Это самая дешёвая форма того же требования.
- Зачем сейчас: `AnalysisLevel=latest-recommended` + `EnforceCodeStyleInBuild` в
  `Directory.Build.props` включают правила производительности CA18xx (лишние аллокации, LINQ в
  горячих путях, `Span`/`ArrayPool`), ровно те, что ищут при работе над 60 FPS. Поставляется с SDK
  10.0.400, пакетов не добавляет, что согласуется с docs/10.
- Стоимость: первый прогон даст предупреждения; вводить как warning (не error), чтобы не ломать
  «Release 0 ошибок». Решение владельца, поскольку меняет вывод сборки.

### 4.3. GitHub Actions: сборка Release + unit-тесты

- Рынок: «CI/CD» 44 из 200, GitHub Actions 12, Azure DevOps 17 (не нужен — репозиторий на GitHub).
- Зачем сейчас: владелец требует commit + diff + push после каждого этапа; «Release 0 ошибок» и
  зелёные Core/Rendering/Persistence/Presentation/Assets-тесты сейчас подтверждаются только на
  шумном ПК. Один файл `.github/workflows/build.yml` на `windows-latest` с `actions/setup-dotnet`
  (SDK из `global.json`) даёт независимую проверку каждой ветки `codex/*`. Нативные смоуки
  `Map.Performance` в CI не идут (нужно окно и GPU) — остаются локальными.
- Стоимость: нуль пакетов; бесплатные минуты GitHub для приватного репозитория ограничены
  (2000 мин/мес), сборка ~5–8 мин. Требует решения владельца: docs/10 ограничивает GitHub
  read-only действиями, а workflow — это запись в репозиторий.

### 4.4. FlaUI (UIA3) для чёрного ящика поверх Release-exe

- Рынок: для тестирования WPF/WinForms-приложений просят FlaUI 2, WinAppDriver 2, Appium 2,
  Windows UI Automation; Playwright 5 — только веб.
- Зачем сейчас: в handoff повторяется «NOT CHECKED: живой проход владельцем». Смоуки Map работают
  внутри процесса (`Map.Performance`), а живая проверка — вручную через windows-mcp с правилом
  «сначала App, потом клики». Avalonia публикует UIA-пиры, поэтому FlaUI может воспроизводимо
  прогонять сценарий по настоящему `Map.App.exe` (открыть библиотеку, новую карту, вкладки
  панелей, протяжку) и снимать окно.
- Стоимость: один пакет в `tools/` (не в продукте и не в `Map.slnx`), BSD/MIT-лицензия. Дублирует
  часть `Native*Smoke`, поэтому — по решению владельца, если ручные живые проходы остаются узким местом.

### 4.5. Профилировщик Visual Studio (CPU / GPU Usage / память) — как второе мнение

- Рынок: Visual Studio 10 из 40 desktop-вакансий (Rider — 0), «профилировщики .NET» 5,
  dotTrace/dotMemory 2.
- Зачем: уже установлен вместе с SDK у большинства; «GPU Usage» показывает время GPU-кадра рядом
  с CPU, чего нет у `dotnet-trace`. dotTrace/dotMemory — платные, рынок их почти не называет;
  без явной нужды не брать.

### 4.6. NetTopologySuite — только когда появится задача

- Рынок: GIS-вакансии на C# (ИНПАД, Rheinmetall, ЦРТ) называют именно NetTopologySuite; «геометрические
  операции» — 7, «пространственные индексы» — 16.
- Условие: если объединение/вычитание областей и регионов, буферы дорог и рек или упрощение
  многоугольников упрутся в устойчивость `SKPathOp` (самопересечения, вырожденные контуры).
  Чистый C#, BSD, без нативных зависимостей; даёт `STRtree`, `Buffer`, `Union`, `Simplify`,
  `IsValid`. Сейчас собственные реализации справляются — не добавлять.

### 4.7. MBTiles / XYZ-пирамида как контейнер экспорта — позже

- Рынок: MBTiles/XYZ 3, Vector Tiles 10, «импорт в Foundry/Roll20» — открытая задача гексов.
- Условие: если понадобится отдавать гигантскую карту вьюерам/VTT одним файлом, пирамида тайлов
  (DEC-023/024) уже есть; MBTiles — это SQLite с таблицей тайлов, стандарт для MapLibre/Leaflet.
  Без запроса владельца не делать.

## 5. Что рынок требует, но Map не подходит (и почему)

| Группа | Инструменты | Причина |
|---|---|---|
| Серверы и облако | AWS, Azure, Docker, Kubernetes, Terraform/Bicep, Kafka, RabbitMQ, PostgreSQL/PostGIS, EF Core, ASP.NET Core, микросервисы | DEC-001/016: офлайн-программа на одном ПК, без сервера |
| Телеметрия | OpenTelemetry, Sentry, Grafana, Prometheus, Application Insights, Serilog | docs/04 «диагностика без телеметрии»: локальный ограниченный журнал уже есть |
| Веб-рендер и фронтенд | React, TypeScript, WebGL, WebGPU, PixiJS, Canvas 2D, Three.js, WebAssembly | DEC-002, docs/04 «почему не Electron/Tauri» |
| Другие движки и UI | Unity, Unreal, Godot, Qt, Dear ImGui, WPF, WinForms, MAUI | DEC-002: Avalonia/Skia; Qt и Godot — задокументированный резерв |
| GIS-продукты | ArcGIS, QGIS, GDAL/OGR, MapLibre, Mapbox, Leaflet, GeoServer, OGC WMS/WFS, проекции/EPSG | фэнтези-карта без реальных координат; переносятся только концепции (п. 2, 4.6, 4.7) |
| Низкоуровневые API | DirectX 12, Vulkan, Metal, HLSL/GLSL напрямую | Skia через Avalonia закрывает GPU-путь; шейдеры — SkSL |
| Анимация и 3D-арт | Spine, After Effects, Blender, Maya, Houdini, Substance, VFX | карта статична; арт владельца — ручная 2D-живопись |
| Коллаборация | CRDT (Y.js/Automerge), WebSocket, multiplayer | одиночный пользователь |
| Тест-инструменты веба | Playwright, Selenium, Storybook, Chromatic | нет веб-UI |
| Процессные | Jira, Confluence, Azure DevOps, Scrum-церемонии | один владелец, `docs/GITHUB_PROJECTS.md` и handoff |
| Установщики | MSIX, WiX, Velopack, Inno Setup | 0 упоминаний в выборке и вне среды по docs/10 |
| Photoshop, Figma | 15 и 9 упоминаний | инструменты владельца-художника, не разработки; прототипы панелей уже делаются как Artifact-макеты |

Отдельно: BenchmarkDotNet, Stryker, coverage, FluentAssertions, Verify в 200 вакансиях не
встретились ни разу — текущий отказ Map от них (DEC-016) рынком не опровергается.

## 6. Предлагаемый порядок, если владелец одобрит

1. RenderDoc-захват двух сценариев: протяжка r3 на 1024² и зум с загрузкой текстур; результат —
   число draw call и загрузок за кадр в отчёт `artifacts/`. Нуль изменений кода.
2. Встроенные анализаторы как warning в `Directory.Build.props`; разбор предупреждений в горячих
   путях `Map.Rendering`.
3. `.github/workflows/build.yml`: Release-сборка `Map.slnx` + пять unit-проектов на `windows-latest`.
4. FlaUI-сценарий «живого прохода» в `tools/` — только если ручные живые проверки остаются узким местом.
5. NetTopologySuite и MBTiles — по появлении конкретной задачи (п. 4.6, 4.7).

## 7. Топ-15 по каждому направлению (из 40)

- **.NET desktop:** C# 39, .NET 35, WPF 35, Git 23, async/многопоточность 16, SQL 15, MVVM 14,
  REST 14, unit-тесты 14, Avalonia 13, .NET Framework 12, WinForms 12, ООП 12, Agile 10,
  ASP.NET Core 10. AI-инструменты 9, Visual Studio 9, CI/CD 8, профилировщики 5, SkiaSharp 2, Rider 0.
- **Gamedev / графика / tools:** C++ 35, DirectX 15, Vulkan 15, Unreal 13, собственный движок 13,
  C# 12, Unity 11, HLSL 10, OpenGL 9, Python 9, GLSL 8, автотесты 8, Metal 7, multithreading 6,
  Git 6; RenderDoc 5, PIX 5, Dear ImGui 4, WPF 4, Nsight 3, Skia 1 (Google), Tracy/Perforce 0.
- **GIS / карты:** JavaScript 23, PostGIS 22, PostgreSQL 21, Python 18, TypeScript 15, SQL 13,
  Git 12, React 12, ArcGIS 11, AWS 10, C++ 10, REST 10, Vector Tiles 10, профилирование 10,
  GDAL 9; QGIS 9, MapLibre 8, Mapbox 8, Leaflet 8, пространственные индексы 8, AI-ассистенты 8,
  NetTopologySuite 3.
- **Backend / DevOps / QA:** .NET 36, C# 30, CI/CD 22, MS SQL 19, Azure 18, Azure DevOps 15,
  Docker 15, Kubernetes 15, PostgreSQL 15, AWS 14, EF 13, REST 13, микросервисы 11, Git 10,
  GitHub Actions 10; xUnit 6, NUnit 6, Playwright 5, MSTest 4, FlaUI 2, WinAppDriver 2, SonarQube 2.
- **Арт / UI-редакторы:** TypeScript 15, Photoshop 13, WebGL 13, React 12, PixiJS 10,
  шейдеры 10, Unity 10, атласы/draw calls 9, JavaScript 9, Spine 9, GenAI-инструменты 8,
  Canvas 2D 8, Figma 8, After Effects 7, профилирование 7; CRDT 5, undo/redo 3, scene graph 3,
  Krita/Procreate/ImageMagick 0.
