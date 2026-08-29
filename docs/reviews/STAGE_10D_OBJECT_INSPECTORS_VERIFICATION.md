# Stage 10D — Object inspectors: self-acceptance и security fixes

## Итог

`FACT`: функциональный срез Stage 10D завершён и закреплён одним локальным checkpoint `HEAD`
branch `stage/10d-object-inspectors` поверх `4b50ba0a707b45ace00da607650ee4d5c1a952bb`.

`PARTIAL`: независимая проверка неизменяемого `BASE..HEAD` и clean-context acceptance пока не
выполнены. Этот отчёт — свежая self-acceptance checkpoint snapshot, а не подмена следующего
независимого gate.

## Что завершено

- Stamp inspector: scale/reference size, exact dimensions, flip X/Y, opacity, sublayer, shadow,
  blend, blur, hue/saturation/brightness/contrast.
- Text inspector: content, weight, size, fill/outline, shadow, curve, spacing, line height,
  alignment, flips, blend/blur и явный font fallback.
- Line inspector: colour/width/opacity, solid/dotted/dashed, offset/caps, shadow, blend/blur,
  asset stroke, spacing и colour adjustments.
- Shape inspector: fill colour/opacity, asset texture, transform texture, stroke и общие эффекты.
- Brush inspector: colour, size, opacity, softness, spacing, scatter, angle, random flips,
  size/opacity jitter, eraser и именованный asset brush tip.
- Все document edits проходят одной undoable command на законченное действие; brush tip остаётся
  transient tool state и не создаёт ложную запись history.
- Новые appearance fields проходят save/reopen, strict enum/blend/finite validation и optional
  SHA normalization.

## Автоматическая проверка

- Release build: 0 warnings, 0 errors.
- Полный suite после финальных security fixes: **683/683**:
  - Core 200;
  - Persistence 115;
  - Rendering 26;
  - Assets 133;
  - Presentation 209.
- Отдельный targeted gate по Stage 10D, Library и resource boundaries: **233/233**.
- Stage10D coverage run (`Stage10D|Stage10LibraryStampTests`): 29 Core, 12 Persistence,
  6 Rendering и 90 Presentation tests; Cobertura:
  `artifacts/stage10d-coverage/stage10d-library.cobertura.xml`.
- Выбранные Stage10D ranges: EditorViewModel 64,49% lines, SceneRenderer 41,84%,
  MapProjectStore 69,11%; небольшие domain contracts `BlendModes`, `ColorAdjustment`,
  `ShadowPreset`, `StampStyle`, `VectorStrokeStyle`, `ShapeFillStyle`, `FilterSettings` — 100%.
  Низкий процент целого renderer/view-model не выдаётся за пробел именно 10D: эти классы содержат
  много функций других этапов.
- Свежий security-targeted coverage после production-правок: 218 tests,
  `artifacts/stage10d-coverage/stage10d-security-final.cobertura.xml`; aggregate line coverage:
  `AssetLibraryDocument` 70,54%, `RecentMapsStore` 79,87%, `MapProjectStore` 61,20%,
  `AppearanceContract` 100%.

### Mutation tests

- appearance contract: 93,55% (`artifacts/stage10d-stryker-appearance-redgreen`); после отчёта
  добавлен отдельный non-finite `OffsetToPercent` regression test;
- linked path policy: 72,46%
  (`artifacts/stage10d-stryker-linked-path-final2`);
- `.mapproj` read budget: 74,24%
  (`artifacts/stage10d-stryker-project-budget-final`);
- image policy: последний запечатанный report до финальных null/empty/exact-scale assertions —
  27,27% (`artifacts/stage10d-stryker-image-policy-final`). Его нельзя интерпретировать как
  высокий confidence score; поэтому image boundary дополнительно закрыт прямыми boundary,
  truncated-decode, exact-byte, oversize-file и pre-decode-eviction тестами.
- catalog linked path span: 77,27% (`artifacts/stage10d-stryker-catalog-path-final3`), 17 killed;
  3 survivors дублируются последующими full-path/portable-segment guards, 2 exception mutants не
  достижимы текущими fixtures;
- recent reparse span: 37,50% (`artifacts/stage10d-stryker-recent-reparse-final2`), 3 killed;
  2 survivors эквивалентны итоговому `File.Exists == false` для отсутствующего файла, 3 reparse/
  IO mutants не получили coverage в Stryker sandbox; обычный symlink fixture проходит вне него;
- blur admission span: 100% (`artifacts/stage10d-stryker-blur-admission-final2`), 13/13 killed.

## Настоящее окно

Финальный background native driver после security fixes и scoped formatting:

- script: `scripts/drive-stage10d-object-inspectors.ps1`;
- Release EXE: `src/Map.App/bin/Release/net10.0/Map.App.exe`;
- результат: **PASS, 13/13 шагов, 7 PNG, 30/30 checks**;
- evidence: `artifacts/stage10d-object-inspectors/ui-smoke-20260828-234008/report.json`;
- размер каждого screenshot: 2500×1460;
- shape ↔ brush normalized RMSE: **0,0711899** (threshold 0,02), отдельный diff PNG сохранён;
- save archive содержит shape texture/shadow, line pattern, text weight/flip, stamp flip;
- reopen снова показывает Shape и Text inspector;
- после clean shutdown процессов `Map.App` не осталось.

Vision-review выполнен на финальном run `ui-smoke-20260828-234008` для всех 7 PNG: Library asset,
Shape texture/shadow, dashed Line, flipped Text, Stamp, полный Brush surface и имя tip видимы;
reopened Text показывает bold/center и явный Arial fallback. Overlap не обнаружен, длинные панели
имеют ожидаемый scroll. В `04-text-inspector.png` верхние geometry controls вне кадра из-за
сохранённого scroll offset; `07-reopened-inspectors.png` показывает их полностью. Это same-task
review, не независимый clean-context UX gate.

## Security fixes, выполненные после Stage 10D

1. Linked fallback отклоняет root/drive/UNC/device/ADS до probe; `.`/`..` сохраняются для
   совместимости, но канонический fallback не выходит из папки проекта. Reparse chain проверяется.
2. Catalog original читается один bounded раз; metadata и SHA относятся к exact bytes, те же bytes
   встраиваются или декодируются. Stage10D больше не делает verify-path → повторный `ReadAllBytes`.
3. Image policy: 64 MiB encoded, 32 MiPixels, 128 MiB decoded/image; brush tip 1024×1024;
   production cache освобождает место до native decode и учитывает `encoded + decoded`, а не
   только расчётный RGBA.
4. Optional thumbnail имеет отдельный предел 8 MiB / 2048 по оси / 4 MiPixels, проходит полный
   decode и отбрасывается без отказа здоровому original во всех placement paths; Library cover
   проверяется и Avalonia-декодируется из одного byte array.
5. `.mapproj` budget выполняется metadata-only до inflate; exact allocation/read + sentinel;
   ratio gate покрывает и малые entries с additive slack; preview проверяет PNG/IHDR/dimensions/
   pixels до UI decode.
6. Persistence передаёт уже проверенные arrays документу через internal ownership transfer и не
   держит вторую полную clone-копию asset payload.
7. Старый v1 за новым memory envelope получает `MapProjectResourceLimitException` и гарантию
   «файл не изменён», а не ложный corruption verdict. Решение принято в DEC-015.
8. До `JsonSerializer` streaming semantic gate считает все materialized DTO arrays, aggregate
   scene/composition objects, strings и tokens. End-to-end fixture с 100 001 коротким `{}`
   отклоняется до DTO allocation и доказывает, что `.mapproj` не изменён.
9. Catalog boundary отклоняет null-элементы и небезопасные `originals/`/`thumbnails/` paths ещё
   при загрузке, а повреждённый optional thumbnail path не блокирует SHA-проверенный original.
   Все null-элементы вложенных scene DTO collections теперь дают `MapProjectCorruptedException`,
   а не выходят из контролируемой parser boundary через `NullReferenceException`.
10. Catalog JSON до materialization ограничен размером, tokens, строками и cardinality; имена
    полей сравниваются так же case-insensitive, как serializer, а duplicate aliases делят общий
    budget. Legacy v1 migration перестроена с O(n²) на линейные HashSet-indexes.
11. PNG/JPEG/WebP проверяются по фактическому codec/header и обязаны совпадать с media type и
    extension; GIF/BMP и format smuggling отклоняются до document/render decode.
12. До `ZipArchive` читается bounded EOCD/central directory: ограничены compressed file,
    entry count, длина/сумма имён; ZIP64 и multi-disk не нужны внутри v1 budgets и отклоняются.
13. Recent maps index на старте читает не больше 1 MiB и 10 cards; UNC/device/DOS paths не
    вызывают `File.Exists`, длинные title нормализуются только для карточки, публикация атомарна.
14. Editor повторно использует уже принадлежащие документу immutable asset references, а
    aggregate payload/entry/full-container admission выполняется до resolver allocation и command.
15. Catalog `linkedPath` принимает только обычный локальный абсолютный drive path и отклоняет
    UNC/device/extended/ADS/небезопасные сегменты до чтения; recent cards не следуют через reparse
    ancestor; inspector blur ограничен 0..32 до преобразования в Skia `float`.

## Репозиторные ворота

- Codex Security diff scan immutable pre-fix snapshot: scan
  `eca97115-ae7e-4b2b-9b99-2f3c39814421`, digest
  `codex-security-snapshot/v1:sha256:9c2bbb9cea63879527397cdeb20489fbacdd05a7b1aecd702c5875e5de4e230f`;
  usage 26 307 641 tokens, 5 threads. Найдены 1 medium и 2 low findings: catalog linked path,
  recent reparse probe и unbounded vector blur. Все три исправлены в текущем scope и закрыты
  regression tests; два same-user race кандидата подавлены после source-to-sink validation.

- `scripts/doctor.ps1`: PASS; .NET 10.0.400, Codex, `rg`, ImageMagick и подписанный Microsoft
  Accessibility Insights найдены.
- `scripts/check-stage-prompts.ps1 -RequireActive`: PASS после добавления единого полного
  `docs/prompts/STAGE_10D_OBJECT_INSPECTORS.md`; 1 active prompt.
- `pre-commit install` и `pre-commit run --all-files`: PASS, включая offline lychee, line endings,
  JSON/YAML/merge/symlink/submodule/large-file gates.
- NuGet vulnerable audit: 0 известных vulnerable packages во всех проектах.
- Deprecated audit: production projects чисты; xUnit 2.9.3 в пяти test projects помечен NuGet как
  `Legacy`. Миграция на xUnit v3 не смешивается со Stage 10D и остаётся отдельным technical debt.
- Статический аудит репозиторных scripts/hooks не нашёл вредоносного инструмента. Единственный
  recursive `Remove-Item` ограничен свежей временной папкой evidence-driver и восстанавливает
  временно скрытый каталог; это допустимая, но всё же destructive операция внутри artifacts.
- В локальном checkout нет `origin` или другого Git remote, а в подключённом GitHub-аккаунте
  репозиторий Map не найден. Поэтому удалённую структуру, Actions и branch protection проверить
  нельзя до явного подключения владельцем; remote не создавался и ничего не публиковалось.
- `git diff --check`: PASS. Полный `dotnet format --severity info` возвращает 143 historical
  diagnostics, включая 37 whitespace diagnostics в нетронутых файлах; на новых строках текущего
  diff — 0 diagnostics. Массовая перепись сознательно не выполнена.

## Известные остаточные риски

- Windows pathname reparse check и последующее открытие не связаны одним directory handle.
  SHA exact bytes закрывает тихую подмену содержимого; узкий same-user namespace race остаётся
  до отдельного Windows handle-walk решения.
- `encoded + width × height × 4` остаётся conservative admission weight, но не включает весь
  Skia/Avalonia/GPU overhead; фактический peak относится к Stage 11 profiling и не обещает точный
  working set.
- Script хранится как UTF-8 BOM и не использует недоступный Windows PowerShell 5.1
  `System.Math.Clamp`, поэтому одинаково разбирается `powershell` и `pwsh`.
- Keyboard/DPI/file-picker и независимый clean-context UX gate, перенесённые из 10B/10C, этим
  срезом не закрывались.

## Точная следующая задача

После review был создан отдельный test-only fix commit
`8de17de27546783bdd6247cb4df094d9df505711`: silent-pass symbolic-link fixture заменена
исполняемой Windows junction; production-код не менялся. Новая отдельная задача Codex
`gpt-5.6-sol`, reasoning `xhigh`, независимо проверила неизменяемый диапазон
`2af5bd3495851f032897435fc957f5705a692bbe..8de17de27546783bdd6247cb4df094d9df505711`
по текущему verification budget: exact regression `1/1` и Map.Presentation.Tests `209/209` приняты
как `FACT`. Full Stage 10D solution/native/security gates не повторяются без нового changed-risk
trigger. Текущий research/release foundation candidate должен получить отдельный локальный checkpoint
и свежую независимую проверку точного `8de17de..checkpoint HEAD`; Stage 10E до неё не начинать.
