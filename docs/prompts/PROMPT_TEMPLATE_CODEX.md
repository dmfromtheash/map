# Stage NN — <полный вертикальный результат>

> Этот файл — исполняемый контракт для Codex `gpt-5.6-sol`, а не общий список пожеланий. Заполни все поля; удали подсказки в угловых скобках. Один prompt закрывает полный roadmap-этап или крупный однородный блок этапа, но не смешивает соседние stages.

## Execution profile

- Model: `gpt-5.6-sol`.
- Reasoning: `<high для реализации | xhigh для review/security/архитектуры>`.
- Role: `<implementation | fresh independent review | review-fix>`.
- Autonomy: read-only checks and reversible in-scope changes are authorized. Ask only before a new product/architecture decision, dependency/license/account/secret, external or irreversible action, material scope expansion, or destructive handling of owner data.
- Git authority: `<no commit | one stage commit after all gates>`; no push/PR/release/tag.

## Source of truth and baseline

Read fully, in order:

1. `AGENTS.md`;
2. `CODEX.md`;
3. `docs/00_START_HERE.md`;
4. `docs/STATE_HANDOFF.md`;
5. Stage NN in `docs/06_DEVELOPMENT_ROADMAP.md`;
6. accepted decisions: `<DEC-...>`;
7. relevant specifications/research/reviews: `<paths>`;
8. `docs/07_CODEX_RUNBOOK.md`;
9. `docs/11_CODEX_VERIFICATION_PROTOCOL.md`.

Expected branch: `<stage/NN-name>`
Accepted BASE: `<full hash or discovery instruction>`
Before edits report actual branch, HEAD, worktree, existing user changes and discrepancies. Never clean/reset owner changes.

## Outcome

<One observable user result that is true only when the whole block is complete.>

## Current facts and gaps

- `FACT`: <existing capability/evidence>.
- `PARTIAL`: <existing but incomplete surface>.
- Gap closed by this stage: <exact end-to-end gap>.

## In scope

- <complete functional responsibility 1>;
- <complete functional responsibility 2>;
- tests, native driver/evidence and documentation needed to accept the outcome;
- expected project areas: `<directories; not a permission to touch unrelated code>`.

## Out of scope

- <neighboring roadmap stage>;
- <architecture/product/licensing changes not accepted>;
- cleanup/refactor unrelated to acceptance;
- push/PR/release/publication.

## Invariants

- Domain stays independent of Avalonia/Skia; UI is not document truth; rendering does not mutate document.
- Every edit is a testable undo/redo command; document/layer/object/asset IDs stay stable.
- Persistence remains atomic and migration never overwrites the only old copy.
- Startup/edit/save/export remain offline.
- Preserve current format/version/contracts unless an accepted DEC explicitly changes them.
- Use only original/approved assets and clean-room observations.
- <stage-specific invariant>.

## Internal checkpoints

These are progress gates inside one prompt, not separate stop points:

1. <contract/model + failing tests>;
2. <core command/logic>;
3. <persistence/rendering integration>;
4. <presentation/native UI>;
5. <adversarial paths and evidence>;
6. <full self-check and report>.

If a checkpoint exposes an out-of-scope product decision, stop only that branch, record `BLOCKED`, and continue all independent in-scope work.

## Acceptance criteria

1. <observable happy path>.
2. <undo/redo or state integrity>.
3. <save/reopen/export semantic result where relevant>.
4. <render/presentation/native UI result where relevant>.
5. <failure/adversarial behavior with precise expected error/atomicity>.
6. <backward compatibility/regression>.
7. Full stage gates pass with no unaccounted files or warnings.
8. Anything not demonstrated is reported `PARTIAL`/`BLOCKED`, never implied complete.

## Failure and adversarial scenarios

- <most dangerous realistic input/state>;
- <boundary/empty/huge/corrupt/locked/cancelled case relevant to this stage>;
- <repeat/undo/reopen/restart case>;
- <proof that the regression test fails without the fix, if this is a review-fix>.

## Risk and tool matrix

| Risk / claim | Tool | Required evidence | Required? | Valid skip condition |
|---|---|---|---:|---|
| compile and regression | `dotnet build/test/format` | exit codes, counts, 0 warnings/errors | yes | none for code stage |
| repository boundary | Git inventory + `pre-commit` | BASE..HEAD, A/M/D/R, hook PASS | yes before commit | commit not authorized; still report worktree diff |
| dependency graph | NuGet audit | vulnerable/deprecated output and package changes | `<yes/no>` | no dependency change and not final full gate |
| critical branch assertions | coverage + Stryker | branch/file coverage; survivors disposition | `<yes/no>` | no new critical branching/command/persistence logic |
| file/import/archive boundary | targeted adversarial tests | corruption/limits/path/roundtrip evidence | `<yes/no>` | no file/parser boundary |
| performance budget | benchmark + counters/trace/gcdump | methodology, median/P95, allocations/trace | `<yes/no>` | no performance claim/budget |
| native behavior | Release EXE + project UIA driver | process identity, actions/checks, evidence JSON | `<yes/no>` | no UI/input change |
| visual fidelity | screenshots + vision + ImageMagick | numbered PNG review, fixed-state RMSE/diff | `<yes/no>` | no visual change or no accepted comparable reference |
| accessibility | UIA + Accessibility Insights | labels/roles/focus/tab/contrast | `<yes/no>` | no changed standard controls |
| security/supply chain | Codex Security or targeted review | trust boundary/source-to-sink/tool authority | `<yes/no>` | no new boundary/tool/dependency and no owner request |

Do not delete rows. Set `Required?` and justify every skipped tool in the final report.

## Required automatic verification

```powershell
<exact commands for this repository/stage; start from Gate B in docs/11_CODEX_VERIFICATION_PROTOCOL.md>
```

Add targeted tests/drivers for each acceptance criterion. Do not substitute a broad green suite for a missing stage-specific check.

## Required native UI verification

<Exact Release EXE launch, process identity, user actions, UIA driver/checks, screenshots and vision review; or `N/A — no UI/canvas/input change` with evidence that presentation behavior is untouched.>

## Git and stop condition

- Preserve pre-existing user changes and authored files.
- Do not use `--amend`, rebase, reset, history rewrite or cleanup of unrelated files.
- If commit is authorized, create one commit only after every required gate, report full `BASE` and `HEAD`, then stop.
- Never push, create PR/release/tag or publish without a new direct owner request.

## Required report

Use the exact report structure from `docs/07_CODEX_RUNBOOK.md`. Include acceptance-by-acceptance status, changed file purpose, command/exit/count evidence, native UI evidence, every tool used/skipped with reason, `FACT/PARTIAL/PROPOSED/BLOCKED`, residual risks, scope check and the one next roadmap stage/fix not implemented.
