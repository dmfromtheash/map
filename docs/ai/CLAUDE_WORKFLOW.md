# Map в Claude: выбор модели и вспомогательные скрипты

Правила — `AGENTS.md`; состояние — `docs/STATE.md`. Этот файл — только про модели Claude и
скрипты `tools/ai/`. Обновлён 2026-10-23 (DEC-054).

## Модели

Машинный источник профилей — [claude-models.json](claude-models.json) (проверен по
[официальному списку](https://platform.claude.com/docs/en/models/overview) 2026-10-23).

| Выбор | Профиль | Подача задачи |
|---|---|---|
| Opus | Claude Opus 5.5 (`claude-opus-5-5`) | конкретный результат, небольшой план, инварианты, связный проход через подсистемы |
| Fable | Claude Fable 5.1 (`claude-fable-5-1`) | цель всего этапа и наблюдаемые критерии; способ исследования модель выбирает сама |

Это подача задачи (**PROPOSED**), а не измеренное преимущество на Map. Имя модели в промпте её не
переключает — сверять выбор в интерфейсе Claude Code. Не заменять модель другой молча.

## Промпт следующей сессии

Готовится только по просьбе владельца («промпт для Fable/Opus»). Подготовка не запускает работу.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/ai/New-ClaudeTask.ps1 -Model Fable
powershell -NoProfile -ExecutionPolicy Bypass -File tools/ai/New-ClaudeTask.ps1 -Model Opus -Stage R09
powershell -NoProfile -ExecutionPolicy Bypass -File tools/ai/New-ClaudeTask.ps1 -Model Fable -Task 'Исправь …'
```

Без `-Stage`/`-Task` берётся «Следующий шаг» из `docs/STATE.md`. `-Stage` — идентификатор этапа
дорожной карты (`R09`, `R07b`). Готовый промпт прочитать и дополнить последними словами владельца.
Актуальный промпт сессии — один файл в `docs/ai/`; прежний — в `docs/archive/prompts/`.

## Скрипты

`pwsh` на ПК нет — всё запускается `powershell -NoProfile -ExecutionPolicy Bypass -File …`
(файлы с UTF-8 BOM).

| Скрипт | Что делает |
|---|---|
| `tools/ai/Get-MapContext.ps1 -AsJson` | только читает: папка, ветка, HEAD, изменения, следующий шаг из `STATE.md`, предупреждения |
| `tools/ai/Check-Scope.ps1 -Allowed …` | показывает изменённые файлы вне списка карточки этапа |
| `tools/ai/New-ClaudeTask.ps1` | печатает промпт сессии (см. выше) |
| `tools/ai/Test-ClaudeSetup.ps1` | проверяет наличие входных файлов, профилей моделей, навыков, `.mcp.json` |
| `tools/ai/Get-RepositoryStatus.ps1 -AsJson` | разовый опрос GitHub по архивному списку кандидатов (сеть) |
| `tools/ai/start-map.ps1` | ярлык владельца: перепубликует Release при новых исходниках и запускает Map |
| `tools/ai/publish-local-r2r.ps1` | ручная перепубликация ярлыка (Map закрыт) |
| `tools/ai/live/*` | живые проверки: `mapwin.ps1` (снимок окна), `mapinput.ps1`, RenderDoc, профилировщик VS |

## Навыки и справка

- Навыки проекта — `.claude/skills/` (`map-local-feature`, `map-linked-geometry`,
  `map-visual-compare`, `map-claude-prompt`); подключать по смыслу задачи, не все сразу.
- Microsoft Learn MCP настроен в `.mcp.json` для справки по .NET; сверять ответы с
  TargetFramework и пакетами Map. Содержимое карт ему не отправлять.
