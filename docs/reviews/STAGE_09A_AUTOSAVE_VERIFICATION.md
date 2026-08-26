# Stage 9A — проверка autosave и startup recovery

## Итог

**FACT.** Срез 9A выполнен и проверен на коммите `03372d7a0a9a2083ece7886ead456e8a6cdc0dfd`.
Фоновая автокопия сохраняет dirty-документ отдельным валидным `.mapproj`, не меняет путь
основного проекта и не выдаёт его за вручную сохранённый. После аварийного завершения dashboard
предлагает последнюю валидную копию и открывает её dirty, не перезаписывая исходный файл.

**PARTIAL.** Stage 9 целиком не завершён: 9B–9E остаются следующими отдельными срезами.
Расширенный object inspector и полный grid property sheet относятся к Stage 10 по `DEC-010`.

## Проверенный снимок Git

- branch: `stage/09-reliability-cycle`;
- `BASE`: `f29bbfe679e2662b3c2d375bbefeb116bb1a98ab`;
- `HEAD`: `03372d7a0a9a2083ece7886ead456e8a6cdc0dfd`;
- 13 файлов: `A=2`, `M=11`, `D=0`, `R=0`, `+1165/-6`;
- рабочее дерево после коммита чистое;
- tracked `artifacts/`, новые package/project references и изменения solution отсутствуют;
- `git diff --check BASE..HEAD` — exit 0.

## Что изменено

1. Добавлен bounded autosave stream на документ: текущая и одна предыдущая валидная immutable
   generation, атомарно переключаемая через `autosave.json` и его bounded backup.
2. Snapshot проходит обычный полный `.mapproj` write/read-back pipeline. Durable temp после сбоя
   перед replace можно безопасно повысить до recovery-generation только после валидации и проверки
   `documentId`.
3. Startup scan объединяет autosave и interrupted-save, выдаёт не больше одного самого свежего
   кандидата на документ и не предлагает damaged/missing/mismatched snapshot.
4. Autosave и ручное сохранение используют один gate. Timer не перекрывает ручное сохранение,
   autosave не вызывает `MarkSaved`, не меняет `DocumentPath` и не уменьшает pending changes.
5. Dashboard различает автосохранённую и прерванную копию. Restore открывает копию dirty; успешный
   manual save и явное «Не сохранять» очищают recovery только после соответствующего решения.
6. В status bar добавлен отдельный честный статус автокопии. Production interval — 30 секунд;
   UI-test interval — 1 секунда только при явном изолированном recovery root.
7. Добавлен нативный UIA-driver `scripts/drive-stage09-autosave.ps1`, управляющий только двумя
   запущенными им процессами Map и складывающий recovery/evidence внутрь игнорируемых artifacts.

## Автоматические проверки

| Проверка | Результат |
|---|---|
| `dotnet clean Map.slnx -c Release` | exit 0 |
| `dotnet restore Map.slnx --force-evaluate` | exit 0 |
| `dotnet build Map.slnx -c Release --no-restore` | exit 0; 0 warnings, 0 errors |
| `dotnet test Map.slnx -c Release --no-build --no-restore --blame-hang --blame-hang-timeout 2m` | exit 0; **195/195** |
| Core / Persistence / Rendering / Assets / Presentation | 63 / 49 / 16 / 6 / 61 |
| `dotnet format analyzers ... --severity warn` | exit 0 |
| NuGet vulnerable, direct + transitive | exit 0; известных уязвимостей нет |
| NuGet deprecated, direct + transitive | exit 0; только существующий xUnit 2.9.3 Legacy в пяти test-проектах |
| `pre_commit run --all-files` | exit 0; все hooks Passed |
| `scripts/doctor.ps1` | exit 0 |

`dotnet format whitespace` по изменённым C# возвращает только две ранее существовавшие строки
`EditorViewModel.cs:945-946`; обе принадлежат `d657dcf6` и не входят в новые hunks. Новый diff
mixed line endings и trailing whitespace не содержит.

Новые тесты доказывают fallback на предыдущую валидную generation, сохранение такой generation
после третьего autosave, promotion durable temp, отбрасывание mismatched id, порядок очистки при
manual save, честные состояния ViewModel, retry после ошибки, cleanup при undo/clean close и
`DontSave`, а также skip timer tick при занятом manual-save gate.

## Проверка настоящего окна

**FACT.** `scripts/drive-stage09-autosave.ps1` повторно прошёл на чистой Release-сборке. Evidence:
`artifacts/stage09-autosave/ui-smoke-final2/evidence.json`.

- dirty editor показал `Автокопия: 02:53`, сохранив ручной статус
  `Не сохранено: 2 изменения`;
- создан валидный ZIP `.mapproj` revision 1 с обязательными entries;
- процесс, принадлежащий driver-у, был завершён, затем запущен новый Map process;
- dashboard показал `Автосохранённая копия «Stage 9 autosave» ...`;
- restore открыл документ dirty и вернул объект `Прямоугольник`;
- TCP listeners обоих процессов: 0;
- три PNG имеют размер `2576×1408`, просмотрены вручную: наложений, обрезки статусов и сломанной
  recovery-плашки нет;
- после driver процессов Map не осталось.

Дополнительно на том же коде прошли существующие native regression drivers Editor UX и Stage 2:
минимальное окно `960×640`, accessibility/undo/redo, save/reopen и точная геометрия сохранились.

## Не проверено и риски

- **PARTIAL:** не симулировалось физическое отключение питания; проверен bounded process kill после
  durable snapshot и fault injection в persistence tests.
- **PARTIAL:** нет многочасового soak со многими документами; bounded cleanup доказан тестами и
  последовательными generations.
- **PARTIAL:** Stage 3–8 native drivers в этом срезе повторно не запускались, потому что 9A не меняет
  их доменные операции; Stage 2 и полный Editor UX regression были повторены.
- Cleanup recovery при системном запрете удаления остаётся best effort: основной save не объявляется
  неуспешным, но на следующем старте возможен устаревший recovery offer.
- Интервал autosave пока фиксирован и не является пользовательской настройкой — это явно out of
  scope 9A.
- Security/secret scan не запускался по прямому правилу проекта; выполнен предусмотренный NuGet
  audit зависимостей.

## Следующая единственная задача

**PROPOSED:** Stage 9B — именованные checkpoints и безопасный возврат к ним. Не смешивать с
resize/crop, export, migrations или Stage 10 object/grid parity.
