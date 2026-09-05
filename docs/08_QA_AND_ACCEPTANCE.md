# 08. Минимальная проверка качества

Map используется одним владельцем на одном ПК, поэтому проверка ориентирована на его реальный
сценарий, а не на универсальную матрицу.

## По умолчанию

- Production-код компилируется одной Release-сборкой.
- Изменённая рискованная логика получает один адресный regression test.
- Видимое изменение один раз проверяется в настоящем окне Map на текущем ПК.
- Сохранение проверяется реальным save/reopen, если оно затронуто.
- Зелёная проверка не повторяется без нового изменения.

### Историческая проверка Stage 12D

Файловый этап проверяется двумя адресными фильтрами `FullyQualifiedName~Stage12D`, одной Release-
сборкой решения и одним профилем `native-stage12d`. Native smoke использует настоящее скрытое
own-process окно (`ShowActivated=false`, `ShowInTaskbar=false`), только собственный visual tree и
уникальный temporary profile; системный ввод, foreground activation и реальные данные владельца
не используются. Обязательные утверждения включают неизменность source bytes/timestamp,
create-new запись, полный v2 read-back, разные UI-состояния и отсутствие путей/content в логе.

## Не является обязательным

- весь набор тестов;
- чистый checkout и отдельная независимая сессия;
- coverage, mutation, benchmarks и fault-матрицы;
- DPI/accessibility/device matrix;
- SBOM и antivirus-аудит;
- постоянные screenshots, traces и отчёты в `artifacts/`.
- VM/Sandbox, чистая Windows и другой компьютер;
- installer, updater, portable/release-пакет, подпись и публикация.

Эти проверки выполняются только если владелец отдельно просит конкретную из них или если задача
непосредственно исправляет соответствующий риск.

## Явно запрошенный performance/stress stage

Для подготовленного Stage 13 владелец отдельно разрешил performance и stress-проверки. Это узкое
исключение не расширяет обычную acceptance-практику.

- До изменений выполняется один baseline-run на текущем принятом HEAD; после законченного среза —
  один сопоставимый run на неизменной fixture и той же машине.
- Fixture детерминирована: фиксированный seed, известные counts/types/visible candidates и hash
  scene contract. Результат содержит branch, HEAD, runtime, renderer path, warmup и P50/P95/max.
- Отдельно измеряются CPU render time, allocations/GC, input-to-present native latency, число
  candidates/draw calls/realized UI rows и cold/warm caches. Одно число FPS не заменяет их.
- Основные профили: 10K/50K/100K; small viewport и full overview; simple/mixed objects; hit-test,
  marquee, selection/drag, object list/search, one-object edit, save/reopen и sparse tile export.
- 50K является acceptance-профилем. 100K остаётся stress/file-boundary профилем и не превращается
  в обещание одинаковой плавности без отдельного решения владельца.
- Большой raster проверяется материализованными sparse tiles. Тест не выделяет bitmap размером со
  весь 65K/131K/262K canvas и не вызывает умышленный memory exhaustion.
- Native UI smoke использует только собственное скрытое окно процесса с `ShowActivated=false` и
  `ShowInTaskbar=false`; запрещены `SendInput`, foreground activation и переключение чужих окон.
- Generated traces/JSON/скриншоты хранятся во временной или ignored папке и после вывода не
  превращаются в постоянное evidence-дерево. В репозитории остаётся только компактный повторяемый
  runner и необходимые regression fixtures/tests.
- Полный suite, coverage, mutation и повторение неизменных зелёных
  прогонов по-прежнему запрещены.

Порог считается доказанным только когда нативный сценарий и функциональная корректность зелёные
одновременно. Быстрый кадр с потерей объектов, неверным z-order, сломанным selection, Undo/Redo,
save/reopen или export является `REJECTED`, а не оптимизацией.

Принятый Stage 13 runner содержит профили `stage13a`, `stage13b`, `stage13c`, `stage13d` и
`native-stage13a`…`native-stage13d`. Для Stage 13D public acceptance fixture — mixed-50K на
131 072 × 131 072; 100K измеряется только как stress index/save-reopen. 262K существует только
как synthetic test/probe и не меняет `CanvasDimensionPolicy.Maximum` без отдельного решения
владельца. Актуальные команды и числа записываются в `STATE_HANDOFF`, generated JSON остаётся в
ignored `tmp/performance`.

## Отчёт

Указать: что изменено; что проверено сейчас; что сознательно не проверялось. Затем обязательно
закончить разделом `ИТОГИ` по формату из `AGENTS.md`: простыми словами сообщить, что готово, что
предлагается делать дальше и каков оценочный процент реализации всего текущего scope проекта.

Отдельно убедиться, что новый stage не начат от более старого product HEAD и не потерял функции
предыдущих stages. Это проверка Git-границы и списка production-файлов, а не повод повторять весь
test suite.
