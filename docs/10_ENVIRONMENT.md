# 10. Среда и инструменты

## Установлено и проверено 25.08.2026

| Инструмент | Версия | Назначение |
|---|---:|---|
| Windows | 10.0.26200, x64 | целевая рабочая система |
| .NET SDK | 10.0.400 | сборка, тесты, publish |
| Avalonia Templates | 12.1.1 | официальный стартовый шаблон; приложение ещё не создано |
| Claude Code | 2.1.245 | исполнитель Opus в консоли |
| Git | 2.54.0 | локальный контроль версий |
| ripgrep | 15.2.0 | быстрый поиск по проекту |
| Python | 3.13.13 | только исследовательские/служебные скрипты, не runtime приложения |
| Node/npm | 22.22.3 / 10.9.8 | не нужны приложению; остаются системными инструментами |
| winget | 1.29.290 | официальная установка Windows SDK/tools |
| Claude project plugins | Superpowers 6.3.0 / Interfaces 1.6.2 | TDD/debugging/verification и отдельный UI/accessibility review |
| pre-commit | 4.6.2 | менеджер быстрых Git-гейтов |
| Gitleaks | 8.30.1 | поиск известных форматов секретов |
| Lychee | 0.24.2 | проверка Markdown-ссылок и anchors |
| dotnet-coverage | 18.10.0 local tool | фактическое покрытие .NET-кода |
| dotnet-stryker | 4.16.0 local tool | мутационная проверка силы тестов |

Исследовательские Python-пакеты: PyMuPDF 1.28.0, pypdf 6.16.2, pdfplumber 0.11.10, pypdfium2 5.13.0, openpyxl 3.1.5, yt-dlp 2026.7.4. Они использовались для чтения источников и не входят в продукт.

## Намеренно не установлено

- Qt/CMake/C++ toolchain — резерв только при провале Avalonia spike;
- Godot — резерв, не текущая архитектура;
- Rust/Tauri, Electron — не соответствуют выбранному нативному контуру;
- FFmpeg — пока не нужен продукту;
- Ollama/local LLM — карта не требует встроенного ИИ;
- дизайнерские клонеры/экстракторы — риск прав и не нужны собственному дизайну;
- Cloudflare/Vercel/серверы — офлайн-программа.

## Проверка

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\doctor.ps1
dotnet tool restore
dotnet tool list
python -m pre_commit --version
gitleaks version
lychee --version
```

Успех doctor означает только готовность среды, не готовность приложения.

После первой установки Gitleaks/Lychee через WinGet нужно открыть новую консоль, чтобы обновлённый `PATH` применился. До этого они вызываются по фактическому пути WinGet package. Git-hook устанавливается один раз на checkout: `python -m pre_commit install`.

## Добавление нового инструмента

Перед установкой записать:

1. какая конкретная задача текущего этапа без него не решается;
2. официальный источник и актуальная версия;
3. лицензия и влияние на распространение `.exe`;
4. runtime/build/dev-only;
5. размер и security/telemetry/network последствия;
6. как удалить/откатить;
7. как проверить, что он действительно использован.

## Первый scaffold после принятия DEC

Создаёт Opus только в Stage 1. Не выполнять заранее:

```powershell
dotnet new sln -n Map
dotnet new avalonia.mvvm -n Map.App -o src/Map.App -f net10.0
```

Точная структура и пакеты из технического задания важнее примера команды.
