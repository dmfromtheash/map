# GitHub для Map

## FACT — что есть сейчас

- Локальный репозиторий: `C:\Projects\Map`.
- `origin` настроен на `https://github.com/dmfromtheash/map.git`.
- Репозиторий `dmfromtheash/map` существует и переведён в **private** до первого push.
- На момент подключения repository был пустым, без remote branches и tags.
- `.github/` отсутствует.
- GitHub CLI авторизован как `dmfromtheash` по HTTPS.
- Существующие `3-puti`, `ensi-bot`, `nri-archive` и другие репозитории принадлежат другим
  продуктам и не должны использоваться как remote для Map.
- Текущий token не имеет `read:project`, поэтому список GitHub Projects недоступен.
- GitHub Project и issues Map ещё не созданы; token scope ещё не менялся.

## PROPOSED — правильная связь

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

## Как использовать в stage

1. До начала кода найти существующую issue текущего stage и перевести её в `In progress`.
2. В issue держать outcome, scope и короткие acceptance-критерии; не копировать туда весь prompt.
3. Ветка называется по stage, например `stage/12a-named-checkpoints`.
4. Локальные commits относятся только к этому stage и упоминают issue, если она уже существует.
5. `Done` ставится только после рабочего пользовательского сценария и локального commit.
6. `PARTIAL` или `BLOCKED` отражается в issue и Project без ложного закрытия.

## Оставшийся GitHub setup

Владелец 2026-08-31 прямо разрешил необходимые commits и push в `dmfromtheash/map`. Осталось:

- расширить scope авторизации: `gh auth refresh -s project`;
- создать user Project `Map`, его поля/views и issues;
- связать Project с repository.

Когда владелец разрешит setup, ожидаемая последовательность команд будет такой:

```powershell
gh auth refresh -s project
git push -u origin <явно выбранная владельцем ветка>
gh project create --owner dmfromtheash --title Map
gh project link <project-number> --owner dmfromtheash --repo Map
```

Первым `main` должен стать cleanup checkpoint поверх product HEAD, а не исторически старый local
`main` и не грязный checkout. Это разрешение не распространяется на публикацию мусорных веток.

## Справка

- GitHub Projects: https://docs.github.com/issues/planning-and-tracking-with-projects
- Quickstart: https://docs.github.com/issues/planning-and-tracking-with-projects/learning-about-projects/quickstart-for-projects
- GitHub CLI Projects: https://cli.github.com/manual/gh_project
