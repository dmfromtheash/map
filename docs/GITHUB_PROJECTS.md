# GitHub для Map — заморожено

## Решение владельца от 2026-08-31

**GitHub не трогать.** Это решение отменяет все более ранние операционные разрешения на setup,
push и обновление issues/Project. До нового отдельного прямого запроса запрещены:

- push/fetch/pull и создание или удаление remote-веток;
- PR, issue, comments, close/reopen, labels и milestones;
- GitHub Project, его поля, views и cards;
- `gh auth refresh`, изменение token scopes и повторная device-code авторизация;
- tag и любые другие remote-записи.

Локальная разработка, локальные ветки и прямо запрошенные локальные commits продолжаются. Наличие
`origin` не является разрешением обращаться к нему.

## HISTORICAL — снимок на момент запрета 2026-08-31

Список ниже фиксирует состояние того дня, а не результат текущей проверки сети или токена.
Более позднее разовое разрешение на commit/push, записанное в `CODEX.md`, уже исполнено;
оно не возобновляет setup Project и не даёт постоянного разрешения на внешние действия.
Прямой текущий запрос владельца и `AGENTS.md` определяют допустимость новой проверки.

- Актуальный product-worktree:
  `C:\Users\Leshe\.codex\worktrees\stage10f-fonts-bindings\Map`.
- `C:\Projects\Map` — старый cleanup-checkout, не база новой функции.
- `origin` настроен на `https://github.com/dmfromtheash/map.git`.
- Репозиторий `dmfromtheash/map` существует и переведён в **private** до первого push.
- `main`, `stage/12a-named-checkpoints` и `stage/12b-resize-crop-region` уже были отправлены до
  текущего запрета; default branch — `main`.
- `.github/` отсутствует.
- GitHub CLI авторизован как `dmfromtheash` по HTTPS.
- Для новых commits Map используется repo-local адрес
  `222780331+dmfromtheash@users.noreply.github.com`, чтобы сохранить включённую GitHub-защиту
  приватного email без переписывания истории.
- Существующие `3-puti`, `ensi-bot`, `nri-archive` и другие репозитории принадлежат другим
  продуктам и не должны использоваться как remote для Map.
- Последнее известное состояние token не давало доступ к GitHub Projects; перепроверять или
  расширять scope сейчас запрещено.
- Созданы issues `#1`–`#4` для Stage 12A–12D.
- GitHub Project `Map` не был подтверждён как созданный; setup остановлен.

## Исторически предложенная связь — не выполнять

Используется одна простая цепочка:

```text
C:\Projects\Map
    -> origin: private dmfromtheash/Map
    -> user-owned GitHub Project "Map"
    -> одна issue/card на один функциональный срез
    -> branch и commits связываются с этой issue
```

Отдельный private repository и `origin` уже настроены. Нельзя дополнительно привязывать Map к
чужому продукту только потому, что тот уже существует в аккаунте.

Git остаётся источником истины для кода и истории. `docs/06_DEVELOPMENT_ROADMAP.md` определяет
порядок продукта, `docs/STATE_HANDOFF.md` — актуальный HEAD и состояние, а GitHub Project только
показывает задачи и их статус.

## Минимальный Project

Название: `Map`.

Поля:

| Поле | Значения | Зачем |
|---|---|---|
| `Status` | Backlog, Ready, In progress, Blocked, Done | текущее состояние задачи |
| `Stage` | 12A, 12B, 12C, 12D, Bug, UX | к какому срезу относится |
| `Priority` | P0, P1, P2 | что важнее для владельца |
| `Type` | Feature, Bug, Decision | вид работы |

Views:

- `Сейчас`: `Status = In progress` или `Blocked`;
- `Дорожная карта`: группировка по `Stage`;
- `Готово`: `Status = Done`.

Одна карточка соответствует пользовательскому результату, а не каждому файлу, тесту или
промежуточной проверке. Для текущего цикла достаточно четырёх основных issues: Stage 12A–12D.

## Как использовать после возможного будущего разрешения

1. До начала кода найти существующую issue текущего stage и перевести её в `In progress`.
2. В issue держать outcome, scope и короткие acceptance-критерии; не копировать туда весь prompt.
3. Ветка называется по stage, например `stage/12a-named-checkpoints`.
4. Локальные commits относятся только к этому stage и упоминают issue, если она уже существует.
5. `Done` ставится только после рабочего пользовательского сценария и локального commit.
6. `PARTIAL` или `BLOCKED` отражается в issue и Project без ложного закрытия.

## Остаток

Ничего не настраивать и не проверять. Если владелец когда-нибудь отдельно вернёт GitHub в scope,
сначала заново прочитать это решение, уточнить точное разрешённое действие и проверить фактическое
состояние read-only. Старые команды setup не являются действующим планом.

## Справка

- GitHub Projects: https://docs.github.com/issues/planning-and-tracking-with-projects
- Quickstart: https://docs.github.com/issues/planning-and-tracking-with-projects/learning-about-projects/quickstart-for-projects
- GitHub CLI Projects: https://cli.github.com/manual/gh_project
