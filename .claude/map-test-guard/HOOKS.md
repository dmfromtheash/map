# Защита тестов Map — подключение

Файлы (Node 22, без npm-пакетов):

| файл | что делает |
|---|---|
| `pre-tool-use.mjs` → `guard-tests.mjs` | хук PreToolUse: спрашивает владельца, если правка ослабляет тест или трогает настройки прогона |
| `stop.mjs` → `stop-check.mjs` | хук Stop: не даёт закончить ход, если код в `src/`/`tests/` менялся в сессии, а квитанции прогона на текущее содержимое нет |
| `receipt.mjs` | прогон тестов с квитанцией по `.trx` (`node .claude/map-test-guard/receipt.mjs -- dotnet test …`) |
| `baseline.mjs` | та же команда на `HEAD` в отдельной копии `%LOCALAPPDATA%\map-test-baseline\worktree` → новые / старые / исправленные падения |
| `skip-report.mjs` | пропущенные тесты по причинам; причины без `defect:` помечены (DEC-116) |
| `test-history.mjs` | нестабильные тесты по отчётам CI (`gh run download`, только чтение) и локальным квитанциям |
| `selftest.mjs` | самопроверка всех решений, без dotnet и сборки |

Квитанции лежат вне репозитория: `%LOCALAPPDATA%\map-test-guard\receipts\<папка>-<хеш>\`, хранятся последние 30.

## 1. Блок для `C:\Projects\Map\.claude\settings.local.json` (добавить ключ `hooks`, остальное не трогать)

```json
  "hooks": {
    "PreToolUse": [
      {
        "matcher": "Edit|Write|MultiEdit|NotebookEdit|Bash|PowerShell",
        "hooks": [
          {
            "type": "command",
            "command": "\"C:/Users/Leshe/tools/node/node.exe\" \"C:/Projects/Map/.claude/map-test-guard/pre-tool-use.mjs\"",
            "timeout": 20
          }
        ]
      }
    ],
    "Stop": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "\"C:/Users/Leshe/tools/node/node.exe\" \"C:/Projects/Map/.claude/map-test-guard/stop.mjs\"",
            "timeout": 30
          }
        ]
      }
    ]
  }
```

Пути указывают на основную папку `C:\Projects\Map`: блок начинает работать после того, как ветка
`claude/test-guard` влита и файлы `.claude/map-test-guard/` есть в основной папке. Правки в
`C:\Projects\Map\.worktrees\<имя>\…` защита распознаёт сама (путь относительно корня копии).

Проверка после подключения: `node .claude/map-test-guard/selftest.mjs` → `ИТОГ: проверок N, провалено 0`.

## 2. Предлагаемое дополнение к `AGENTS.md` §Проверка (после пункта «Честные тесты (DEC-116)»)

```diff
 - **Честные тесты (DEC-116):** новый сторож — с доказательством «сломал код → красный»; тихий проход (ни одной
   проверки, ранний `return`, `Skip` без `"defect: …"`) запрещён; ослабление теста — только с `test-change:` и причиной
   в коммите. Перед коммитом тестов: `powershell -NoProfile -ExecutionPolicy Bypass -File tools/ai/Test-Integrity.ps1`.
+- **Защита тестов при правке (владелец, 2026-11-16):** хуки `.claude/map-test-guard/` спрашивают владельца, если правка
+  убирает `Assert`/тест/`InlineData`, добавляет `Skip`, условный `*Fact`, ранний `return`, повтор, поднимает число в
+  проверке или пороге, переносит тест в `[Trait]`/`[Collection]`, трогает `tests/KnownFailures.txt`, тестовый csproj,
+  `Directory.*`, `*.runsettings`, `tools/ci/*`, `Test-Integrity.ps1`, `.github/workflows/*`. Обходить их (оболочкой,
+  `git apply`) нельзя. Сменил ожидаемое значение — объяснить по поведению, а не «так выдаёт код».
+- **Прогон с квитанцией:** тесты запускать через
+  `node .claude/map-test-guard/receipt.mjs -- dotnet test tests/<Проект>/<Проект>.csproj -c Release --filter "…"` и
+  отчитываться по квитанции (прошло/упало/пропущено, ревизия). Нет `.trx` или 0 тестов — это не «прошло».
+  «Старое падение» / «не регрессия» — только по `node .claude/map-test-guard/baseline.mjs -- <та же команда>`, иначе
+  писать «не атрибутировано». Пропуски — непроверенное (`skip-report.mjs`). Нестабильные тесты —
+  `test-history.mjs`. Хук Stop напоминает о прогоне, если код менялся; не запускали — прямо сказать, что не проверено.
```
