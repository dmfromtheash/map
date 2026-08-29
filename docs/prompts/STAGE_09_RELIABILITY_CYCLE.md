# Stage 9 — надёжность полного цикла

> Status: HISTORICAL CONTRACT — completed/partitioned; do not execute again. New work uses `PROMPT_TEMPLATE_CODEX.md`.

> Разрешено владельцем к запуску 26.08.2026. Первый срез выполняется в
> `stage/09-reliability-cycle` после принятого Editor UX Rebuild.

## Контекст и порядок

Перед каждым срезом прочитать `AGENTS.md`, `docs/00_START_HERE.md`, `docs/STATE_HANDOFF.md`,
Stage 9 в roadmap, `DEC-005`, `DEC-008`, `DEC-009`, `docs/04_TECH_ARCHITECTURE.md`,
`docs/05_PROJECT_FILE_AND_DATA_MODEL.md` и `docs/08_QA_AND_ACCEPTANCE.md`.

Срезы Stage 9 выполняются отдельными коммитами и не смешиваются:

1. 9A — autosave journal и startup recovery;
2. 9B — именованные checkpoints и возврат;
3. 9C — resize/crop/new-from-region;
4. 9D — full export и отмена долгих операций;
5. 9E — migrations и corrupted-file diagnostics.

Расширенный object inspector и полный grid property sheet относятся к Stage 10 по `DEC-010` и
не входят в Stage 9.

## Срез 9A — единственная цель

При наличии несохранённых изменений фоново и без модальных окон сохранять локальный durable
snapshot в `%LOCALAPPDATA%\Map\Recovery\<documentId>`. После аварийного завершения существующий
dashboard должен предложить последнюю валидную копию. Autosave никогда не заменяет основной
`.mapproj`, не меняет `DocumentPath` и не помечает документ как вручную сохранённый.

### In scope

- один bounded autosave snapshot на документ плюс bounded backup предыдущего snapshot;
- метаданные: document id, исходный target path или пустое значение, revision, timestamp;
- atomic write через тот же валидируемый `.mapproj` pipeline;
- scan только полного валидного snapshot с совпадающим document id;
- дедупликация autosave и interrupted-save candidate одного документа по последнему времени;
- throttled background trigger только для dirty document;
- последовательность manual save и autosave через один gate;
- явный, но отдельный от manual-save статус autosave;
- clean manual save очищает старый autosave только после доказанного replace;
- dashboard restore открывает recovered snapshot dirty и не перезаписывает target;
- explicit discard удаляет только recovery-срез выбранного document id;
- unit/integration tests и нативный startup recovery smoke.

### Out of scope

- delta-журнал и несколько поколений autosave;
- пользовательская настройка интервала/budget;
- named checkpoints;
- export, crop/resize, migrations;
- новые зависимости;
- изменение формата основного `.mapproj`.

## Инварианты 9A

1. UI не является источником истины; snapshot берётся из `EditingSession`.
2. Autosave не вызывает `MarkSaved` и не уменьшает `PendingChanges`.
3. Основной файл не открывается на запись.
4. Невалидный, неизвестный или несовпадающий snapshot не предлагается пользователю.
5. Ошибка autosave не закрывает редактор и не переключает manual save в `Saved`.
6. Новый autosave не удаляет последнюю валидную копию до полного write/read-back/replace.
7. После успешного manual save recovery удаляется только после durable target replace.
8. Логи не содержат содержимое документа.

## Acceptance 9A

1. `dirty -> autosave` создаёт открываемый snapshot и recovery metadata.
2. Autosave оставляет `DocumentPath`, `IsDirty`, `PendingChanges` и manual `SaveState` без ложного
   перехода в saved.
3. Повторный autosave заменяет snapshot атомарно и оставляет не более одного `.bak`.
4. Fault до replace сохраняет предыдущий валидный autosave.
5. Corrupt/missing/mismatched snapshot не попадает в dashboard.
6. Manual save очищает autosave только после успеха; failed manual save не очищает его.
7. Startup recovery показывает timestamp и открывает копию dirty без записи в исходный target.
8. Background trigger не запускает autosave для clean document и не перекрывается с manual save.
9. Release build без warnings; все тесты; pre-commit; dependency audit; настоящий Release EXE без
   TCP listener и без WebView/Chromium.

## Verification budget and stop rules

При справочном использовании этого исторического контракта действуют текущие конечные лимиты
`docs/11_CODEX_VERIFICATION_PROTOCOL.md`: один зелёный прогон на неизменившийся snapshot, повтор
только после относящегося изменения, максимум три заранее объявленных flake-прогона и остановка с
`PARTIAL`/`BLOCKED` вместо цикла. Исторические evidence и числа не переписываются.

## Отчёт после среза

Использовать `FACT`, `PARTIAL`, `PROPOSED`, `BLOCKED`. Отдельно записать автоматические проверки,
проверку настоящего окна, непройденные сценарии, точный `BASE..HEAD`, изменённые файлы, риски и
следующий единственный срез 9B.
