# CLAUDE.md — вход для Claude Code

@AGENTS.md

Все правила — в `AGENTS.md` (подключён выше, повторно читать не нужно). Здесь только особенности
Claude Code.

- Начало сессии: `docs/STATE.md` и `tools/ai/Get-MapContext.ps1` (см. `AGENTS.md §Вход в сессию`).
  Затем чек-лист `docs/00_STAGE_PROTOCOL.md`.
- Claude — главный исполнитель и единственный, кто делает визуал.
- Список шагов задачи — TodoWrite. Правки — Edit/Write, поиск — Grep/Glob, чтение — Read
  (нужный диапазон строк, а не файл целиком повторно).
- Codex отключён владельцем 2026-10-30 (`AGENTS.md`, DEC-075): не запускать и не предлагать.
- Навыки проекта `.claude/skills/`: `map-local-feature` (новая функция), `map-linked-geometry`
  (реки, дороги, области, гексы), `map-visual-compare` (сравнение холста с образцом),
  `map-claude-prompt` (промпт следующей сессии по просьбе владельца).
- Память `memory/`: только то, чего нет в репозитории (слова владельца, отвергнутые подходы,
  подводные камни). Состояние проекта хранится в `docs/STATE.md`, а не в памяти.
- `pwsh` на ПК нет: скрипты запускать `powershell -NoProfile -ExecutionPolicy Bypass -File …`.
- Выбор модели и шаблон задачи — `docs/ai/CLAUDE_WORKFLOW.md`, `docs/ai/PROMPT_CLAUDE.md`.
