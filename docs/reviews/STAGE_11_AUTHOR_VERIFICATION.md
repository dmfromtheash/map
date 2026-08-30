# Stage 11 — author verification

Дата: 2026-08-30.

## RESULT

- `PARTIAL` — author slice: реализация, измерения, portable package, integrity verifier,
  dependency/license inventory, icon integration, автоматические проверки и ограниченный smoke
  exact candidate выполнены. Обязательный native-сценарий не завершён целиком: не доказаны
  pan, Save As/close/reopen, fresh Windows shortcut, точный DPI и полный keyboard-only проход.
- `PARTIAL` — общая RC readiness: author-кандидат намеренно собран из dirty implementation
  snapshot (`cleanSource=false`). Final bundle из clean implementation HEAD, clean Windows x64 VM
  без SDK/runtime/сети и side-by-side rollback относятся только к fresh independent acceptance.
- Это не final RC и не утверждение готовности продукта к публичному распространению.

## BASELINE

- Worktree root:
  `C:\Users\Leshe\.codex\worktrees\stage11-prompt-rewrite\Map`.
- Branch: `stage/11-performance-packaging-rc`.
- `TRANSITION_BASE=2e30e78b0b86a4ab070158b7c773200611c62d58`.
- `START_HEAD=0513c568a4515a274b4b173f302cce27ec3c3fe6`.
- `START_HEAD` — отдельный docs-only commit
  `docs: tighten stage 11 performance packaging contract`; author implementation строилась
  поверх него.
- Pre-commit HEAD остаётся `START_HEAD`; worktree dirty только перечисленным Stage 11 slice.
  Generated packages, JSON evidence, logs и screenshots находятся под ignored `artifacts/`.
- Основной checkout `C:\Projects\Map` и owner-source worktree
  `C:\Users\Leshe\.codex\worktrees\fix-app-icon\Map` не изменялись.

## PREREQUISITES

- `FACT`: Stage 9A autosave/recovery принят.
- `PARTIAL`: Stage 9B named checkpoints не реализован.
- `PARTIAL`: Stage 9C resize/crop/new-from-region не реализован.
- `PARTIAL`: Stage 9D полный пользовательский export и cancellation не реализованы. Stage 11
  измеряет только реально существующий renderer boundary и не имитирует отсутствующий UI.
- `PARTIAL`: Stage 9E migrations/corrupt diagnostics не завершён.
- `FACT`: Stage 10F уже owner-accepted для перехода; его старые acceptance-gates здесь не
  повторялись. Сохраняются известные unrelated formatter baseline, machine-local font variation и
  четыре прежних Accessibility Insights baseline findings без Stage 11 regression evidence.

## CHANGED

- Детерминированный Stage 11 benchmark contract и runner: fixed seed, cold/warm separation,
  raw samples, machine fingerprint, memory/GC evidence и gates для objects, large-map layers,
  catalog, undo/redo, brush/mask, save/open и tiled-renderer boundary.
- Portable release builder расширен до self-contained `win-x64` package с точной source identity,
  immutable output name, SPDX 2.3 SBOM, direct/transitive dependency graph, license notices,
  локальными license files, manifest и полным SHA-256 inventory.
- Добавлен отдельный verifier с path containment, reparse-point refusal, exact inventory,
  commit/runtime/version/source-state/SBOM/hash checks и внешним JSON report.
- Добавлен восьмисценарный adversarial verifier harness: healthy Unicode path, tampered, missing,
  extra, tampered manifest, semantic SBOM corruption, path traversal и locked payload.
- Owner-approved PNG/ICO скопированы без преобразования, привязаны к EXE и Avalonia window;
  проверяются source hashes, dimensions, ICO frames и embedded associated icon.
- Добавлен тест, доказывающий, что autosave возвращает управление до блокирующей persistence
  операции и укладывается в 100 ms на author machine.
- Добавлены provenance/benchmark/provider/release docs. Production dependency, installer, updater,
  network requirement и product export UI не добавлялись.

## PERFORMANCE

Контракт: seed `110011`, ровно 1 warm-up и 5 raw measured samples; cold/first costs записаны
отдельно. Fingerprint: Windows `10.0.26200`, x64, AMD64 Family 25 Model 68, 16 logical CPUs,
15.25 GiB available memory, .NET `10.0.10`, workstation GC, Skia `3.119.0.0`, CPU raster
`SKSurface`; Git commit `0513c568...`, dirty author snapshot.

| Profile / metric | Median | P95 | Max | Gate |
|---|---:|---:|---:|---|
| objects-1k render | 2.694 ms | 3.518 ms | 3.518 ms | PASS, median <= 33.3 ms |
| objects-10k render | 2.673 ms | 2.805 ms | 2.805 ms | PASS, max <= 33.3 ms |
| objects-50k render | 2.836 ms | 3.142 ms | 3.142 ms | PASS, median <= 33.3 ms |
| large-map layers render | 20.106 ms | 21.315 ms | 21.315 ms | PASS, cache 40 <= 64; full-canvas allocation 0 |
| catalog-10k fresh metadata | 150.244 ms | 363.625 ms | 363.625 ms | PASS, max <= 2 s |
| catalog-10k warm search | 1.035 ms | 1.412 ms | 1.412 ms | PASS, P95 <= 100 ms |
| undo-200 round trip | 2.548 ms | 10.756 ms | 10.756 ms | PASS, exact undo/redo geometry |
| brush/mask stress | 2.241 ms | 2.845 ms | 2.845 ms | PASS, exact round trip |
| save-10k warm | 220.822 ms | 304.960 ms | 304.960 ms | informational |
| open-10k warm | 38.502 ms | 47.098 ms | 47.098 ms | PASS with semantic round trip |
| tiled 8K/16K/20K one-tile render | 2.133/2.386/2.013 ms | 2.242/3.859/2.408 ms | 2.242/3.859/2.408 ms | PASS |

Дополнительные факты:

- catalog first cold load: 458.225 ms; index: 15,527,226 bytes; original asset files opened: 0;
- cold save/open: 438.707/36.857 ms; maximum `.mapproj`: 330,199 bytes;
- large-map cache: 102 hits, 40 misses, 0 evictions;
- brush/mask history payload: 8,192,000 bytes при budget 31,457,280;
- tiled boundary: одна 2048-square surface, максимум 16,777,216 bytes; полные 8K/16K/20K
  surfaces не выделялись; cancellation замечен до tile allocation;
- product export не реализован; tiled profile — renderer evidence, а не Stage 9D substitute;
- raw evidence: ignored `artifacts/stage11-author/benchmark-final.json`.

## PACKAGE

- Exact author bundle:
  `artifacts/stage11-author/package candidate - Юникод/Map-0.2.0-win-x64-0513c568a451`.
- Manifest: application `0.2.0`, file version `0.2.0.0`, runtime `win-x64`,
  commit `0513c568a4515a274b4b173f302cce27ec3c3fe6`, `cleanSource=false`.
- Inventory: 236 payload files, 16 verifier-visible metadata/checksum files, 251 checksums,
  27 SPDX packages.
- Separate verifier: `PASS`; report:
  `artifacts/stage11-author/package-verifier.json`.
- `FACT`: actual copied candidate with a modified `Map.Core.dll` was rejected by SHA-256.
- `FACT`: a second build into the same output name was rejected; prior candidate was not
  overwritten.
- `FACT`: locked payload, traversal, missing/extra file, manifest corruption and semantic SBOM
  corruption are negative-test refusals.
- `PARTIAL`: this dirty author package is disposable evidence only; a clean-source final package
  is deliberately not built in author context.

## ICON

- Source PNG SHA-256:
  `8BF8D23D901D005670DE90C34E08DBB0BFA39993C6363E8DE229C64352DED4E1`;
  dimensions `1254x1254`.
- Source ICO SHA-256:
  `7E396A60DFB2B6AF62807445D62FF5C7E19168EA494F923EB150039F3FDCD4AC`.
- ICO frames: `256, 128, 96, 64, 48, 32, 24, 16` px.
- `FACT`: project binds `ApplicationIcon` and
  `AvaloniaIncludeApplicationIconAsWindowIcon=true`.
- `FACT`: Windows extracted an associated embedded icon from exact candidate EXE.
- `PARTIAL`: fresh Windows shortcut at standard sizes was not created/visually checked.

## PUBLIC REFERENCES

- Bounded read-only source: `https://github.com/velopack/velopack.git`, exact commit
  `214f8be7384201826ba03d4bad8f0f646d727613`, local pinned clean mirror
  `C:\Projects\Map-ResearchLab\sources\velopack`, MIT.
- Reviewed: `LICENSE`, Avalonia sample README/Program/MainWindow/MemoryLogger/project,
  `VelopackApp.cs`, `UpdateManager.cs`, `SimpleFileSource.cs` and narrow related tests.
- Extracted decisions: integration changes an early process boundary; auto-apply is enabled by
  default unless opted out; local file sources and downgrade control exist; apply/cleanup use
  provider-owned files/processes and require explicit ownership, signing, uninstall, offline,
  downgrade and rollback contracts.
- Map packaging/verifier/scripts/docs were written independently. No upstream code, asset, brand
  or package was copied/added; Velopack remains `PROPOSED`, not a production dependency.

## VERIFIED AUTOMATICALLY

- `dotnet restore Map.slnx --force-evaluate`: exit 0.
- `dotnet build Map.slnx -c Release --no-restore`: exit 0, 0 warnings, 0 errors.
- Full solution tests: exit 0, 739/739
  (Core 215, Rendering 32, Persistence 129, Presentation 230, Assets 133).
- Affected Presentation tests: exit 0, 230/230.
- Deterministic benchmark: exit 0; 9/9 profiles passed.
- Release verifier harness: exit 0; 8/8 scenarios passed.
- Exact candidate verifier, tamper refusal, immutable-output refusal and icon checker: passed.
- Codex Security targeted diff scan
  `3918d851-2f2c-49d9-ba7e-2b50a8fda68d`: completed, coverage complete, 0 findings. TAC
  advisory status was `unknown`; scan was not blocked. Independent worker was unavailable under
  active runtime policy, so review was sequential.
- Vulnerable direct/transitive audit: exit 0, no known vulnerable packages from nuget.org.
- Deprecated direct/transitive audit: exit 0; existing test-only xUnit `2.9.3` Legacy baseline in
  five test projects; no production package was marked deprecated.
- `scripts/doctor.ps1`: exit 0.
- `scripts/check-stage-prompts.ps1 -RequireActive`: exit 0, 1 active prompt.
- `dotnet format Map.slnx --verify-no-changes --no-restore`: exit 1 only on the documented,
  untouched legacy formatter baseline in Stage 7/8/Core/Rendering files; no Stage 11 changed file
  was reported. Out-of-scope formatting was not rewritten.
- Coverage/Stryker was skipped: the new critical package/verifier branches are PowerShell and are
  covered directly by the 8-case adversarial harness; no new critical production C# branch was
  introduced.
- Final `pre-commit run --all-files` and `git diff --check` are the last pre-commit gates and
  their outcomes are recorded in the task handoff.

## VERIFIED IN NATIVE UI

- Exact executable:
  `...\Map-0.2.0-win-x64-0513c568a451\Map.App.exe`;
  SHA-256 `43ceda460e18dbb3ce0c5ace1485357e46113d5a59ddd813442980d7f1689354`;
  version `0.2.0.0`.
- Exact process evidence: PID `33596`, command line pointed to the package candidate, manifest
  commit matched `0513c568...`, and no TCP/UDP endpoint was owned by that process.
- In the native window an existing representative project opened; rectangle edit, selection,
  undo, redo and zoom from 62% to 112% were observed.
- Only that exact candidate process was stopped; evidence:
  `artifacts/stage11-author/native-process-evidence.json`.
- `PARTIAL`: the Save As dialog was reached, but the Save As/close/reopen sequence was not
  completed. По решению владельца оставшиеся действия не повторялись через управление ПК.

## NOT VERIFIED

- Pan, completed Save As/close/reopen, exact DPI, fresh shortcut icon and complete keyboard-only
  native pass.
- Final package built from clean implementation HEAD.
- Clean Windows x64 VM without SDK/runtime/network, integrity-before-launch, open/edit/save/reopen,
  any actually available export path, package tamper refusal on that VM, old/new side-by-side
  launch and rollback.
- Product export/cancellation (Stage 9D), Stage 9B/9C/9E capabilities, installer/updater,
  code signing, antivirus reputation and public distribution.

## RISKS

- Global formatter and test-only deprecated xUnit baselines are pre-existing and unchanged.
- SHA-256 inventory proves internal consistency against the included list, not publisher
  authenticity; signing remains a separate owner decision.
- The author candidate is dirty by design and cannot be promoted to final RC.
- Required clean-VM and rollback evidence is absent.
- Missing Stage 9B–9E capabilities remain explicit prerequisites and were not added here.
- Velopack adoption remains `PROPOSED`; no provider, AUMID, signing identity, network policy,
  install ownership or downgrade policy is accepted.

## GIT/SCOPE

- Planned single implementation commit subject:
  `feat(stage-11): prepare measured portable release candidate`.
- Pre-commit allowlist: 18 files total — 12 added, 6 modified, 0 deleted/renamed. It contains only
  benchmark generators/contracts, packaging/verifier/icon scripts, app icon assets and project
  binding, one autosave performance test with test fake support, license/provenance/release docs
  and this author report.
- No `artifacts/`, package, raw trace, screenshot, VM evidence, secret, lockfile or unrelated
  owner change belongs to the commit.
- The exact implementation commit hash is necessarily produced after this pre-commit report and
  is recorded in the task handoff; the author must stop immediately after that commit.
- No push, PR, merge, tag, GitHub Release, upload or publication.

## NEXT

Одна fresh independent Stage 11 acceptance-задача должна проверить exact
`START_HEAD..IMPLEMENTATION_HEAD` в чистом worktree, полный file inventory, собрать final bundle
из clean implementation HEAD, независимо проверить manifest/SBOM/licenses/hashes/icon/security
границы и выполнить один clean Windows x64 VM offline/side-by-side/rollback сценарий. Не начинать
следующий roadmap stage и не повторять Stage 10F.
