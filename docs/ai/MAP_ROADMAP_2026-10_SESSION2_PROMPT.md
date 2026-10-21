# Промпт сессии 2 — дорожная карта Map 2026-10, два исполнителя (Claude Code + Codex/Astra)

Скопируй этот файл в новую сессию Claude Code (или напиши: «Выполняй `docs/ai/MAP_ROADMAP_2026-10_SESSION2_PROMPT.md`»).
Общие правила цикла — в `docs/ai/MAP_ROADMAP_2026-10_PROMPT.md`; этот файл задаёт точку старта второй сессии.

---

Ты — интегратор дорожной карты продукта **Map** (офлайн-редактор фэнтези-карт, C#/.NET 10, Avalonia, SkiaSharp).
Второй исполнитель — **Codex (модель `gpt-6-astra`, уровень `xhigh`)**, ты запускаешь его сам через
`codex exec` (владелец разрешил запускать Codex без вопросов на этапы дорожной карты). Вопросов владельцу
не задавать; всё, что можно решить самому, решать и записывать как ASSUMPTION в PROGRESS.

## 1. Где и как

- Продуктовая папка (только для тебя): `C:\Users\Leshe\.codex\worktrees\stage10f-fonts-bindings\Map`, ветка
  `codex/stage-18-editable-areas`. Папку `C:\Projects\Map` не трогать.
- Папка Codex: `C:\Users\Leshe\.codex\worktrees\roadmap-codex\Map` (отдельный git-worktree). На каждый этап —
  новая ветка `codex/roadmap-R##` от текущего продуктового HEAD: `git checkout -b codex/roadmap-R## codex/stage-18-editable-areas`
  в папке Codex. Codex коммитит локально и не пушит; ты переносишь его коммиты `git cherry-pick` (слияние `git merge`
  блокирует классификатор auto-режима), прогоняешь комбинацию и сам правишь сводные документы.
- Запуск Codex (промпт через файл, иначе зависает на stdin; `-s read-only` игнорируется его конфигом):
  ```
  codex exec -m gpt-6-astra -c model_reasoning_effort=xhigh -s workspace-write -C "<папка Codex>" -o "<файл отчёта>" - < "<файл промпта>"
  ```
  в фоне; результат читать из файла отчёта. Если Codex упал с «Selected model is at capacity», его работа лежит
  незакоммиченной или как `wip(R##)` в его папке — запусти короткий промпт «продолжи этап R##: осмотри git status,
  доделай, проверь, закоммить, отчитайся».
- Прочитать перед первым действием: `AGENTS.md`, `CLAUDE.md`, `docs/00_STAGE_PROTOCOL.md` (чек-листы A–G; §J —
  unit-тестов с давними падениями больше нет, любое падение Rendering/Persistence/Presentation = регрессия;
  `PlacementWorkflowTests.Stage18_temporary_preview_memory_refusal…` и `HexWorkshopRenderingTests.Trees_sway_and_only_trees`
  ложно падают только под параллельной сборкой — перегонять одиночно), `docs/ROADMAP_2026-10_PRODUCT.md`,
  `docs/ROADMAP_2026-10_PROGRESS.md` (источник правды), три верхние секции `docs/STATE_HANDOFF.md`.
- Память машины 16 ГБ: не запускать два полных прогона тестов/смоуков одновременно с Codex; A/B-замеры — когда Codex не собирает.
- Каждую новую функцию проверять **живым запуском** в изолированном профиле (`Map.LiveCheck`, снимки PrintWindow),
  открывать снимки и смотреть визуал и отклик — это требование владельца. REA — при надобности.
- После каждого этапа: commit → `git show --stat` → push ветки → push `main` (`git push origin codex/stage-18-editable-areas:main`)
  → `gh run list --commit <sha>` → `gh run watch <id> --exit-status` → при изменении кода `tools/ai/publish-local-r2r.ps1`
  (при закрытом Map). Push — по одной команде за вызов.

## 2. Состояние на старте (2026-10-21, ночь)

- Закрыто **6 из 44**: R00, R01, R02, R03, R04, R06 (✅, CI зелёный, HEAD a7bce73). R05 — PARTIAL (пп. 2 и 6 сделаны,
  п. 3 ускорен 254 → ~100 мс; осталось: фоновая растеризация плиток объектов, ползунок обрывов ≥ 55 FPS, прогрев;
  второй монитор — только владелец). Схема файла — **v15** (переправы дорог); следующий свободный номер — **v16**.
- **R19 (экспорт для Obsidian) у Codex в работе**: ветка `codex/roadmap-R19` в папке Codex, уже есть `wip(R19)` коммит
  (2ba37f0) и незакоммиченные файлы. Отчёт, если Codex успел его дописать:
  `C:\Users\Leshe\AppData\Local\Temp\claude\C--Projects-Map\c2f4a4a8-2f90-4ad5-8b8b-0686cfa9f0ea\scratchpad\codex-r19-result.txt`
  (папка временная — если её нет, смотри `git log`/`git status` в папке Codex).
- Все 1714 unit-тестов зелёные; смоуки `native-hex-workshop`, `native-player-package`, `native-export-wins`,
  `native-library-wins`, `native-crossings`, `native-stage19` зелёные. Остатки семи устаревших смоуков объяснены в §J протокола.

## 3. Первые действия

1. `powershell -NoProfile -ExecutionPolicy Bypass -File tools/ai/Get-MapContext.ps1 -AsJson`; `git status`; прочитать PROGRESS.
2. **Доделать R19:** в папке Codex `git log --oneline -3`, `git status --short`. Если есть финальный коммит и отчёт — перенести
   `git cherry-pick <sha>`, собрать Release, прогнать 5 тестовых проектов + `tools/ci/Test-KnownFailures.ps1`, смоук
   `native-player-package` и новый смоук R19, посмотреть снимки, внести записи (PROGRESS, верх handoff, CHANGELOG),
   commit/push/CI → R19 ✅. Если коммита нет или остался `wip` — запустить Codex с промптом-продолжением (см. §1), а самому
   начать п. 3.
3. **Claude — R07** (подписи вдоль пути + пресеты подписей + имена; зависит от R06 ✅; схема **v16** — забронировать
   в PROGRESS). «Готово, когда» — в дорожной карте.
4. **Codex после R19 — R13** (печать постером для обеих категорий, экспорт слоями; зависит от R03 ✅; схемы нет).
   Промпт для Codex — по образцу прошлых: только его папка и ветка, без push и сводных документов, схему не менять,
   изолированный профиль, живой проход со снимками, отчёт с таблицей G1 и готовым текстом для PROGRESS/handoff/CHANGELOG.
5. Дальше по очереди выполнимых этапов (столбец «Зависит»): Claude — остаток R05, затем R08/R10; Codex — R16 (от R02),
   R14 (от R01). Один исполнитель — один активный этап; «Исп.» и статус писать в таблицу PROGRESS сразу.
6. В конце сессии: отчёт владельцу простыми словами (что добавили, что проверено, что нет, сколько этапов из 44),
   память — только невыводимое из репозитория.

## 4. Решения владельца, которых нет (работать по допущениям)

Новые гекс-границы не блокируют; папка библиотеки по умолчанию `Документы\Map`; арт мостов/домов — процедурные
заглушки; макеты 🎨 — делать временный вариант с пометкой PARTIAL и прикладывать снимки; `~/.codex/config.toml`
`sandbox_mode` остаётся как есть (предложение владельцу — `workspace-write`).
