// Baseline comparison for Map: is a failure new, or was it already there?
//
//   node .claude/map-test-guard/baseline.mjs [--base REV] [--keep] -- <test command ...>
//   node .claude/map-test-guard/baseline.mjs --compare-trx <base.trx|dir> <current.trx|dir>   (no run)
//
// Runs the SAME command (same filter) on REV (default HEAD: "did my uncommitted change break it?";
// --base origin/main for branch-vs-main) in a separate worktree, then on the current tree — one after
// the other, never in parallel. Prints NEW / OLD / FIXED by full test name (theory cases separately).
// "Old" is claimed only from this side-by-side evidence. Worktree: %LOCALAPPDATA%\map-test-baseline\worktree
// (outside C:\Projects); it is removed after the run unless --keep (it holds a full build, ~1–2 GB).
import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, rmSync } from "node:fs";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { ROOT } from "./lib.mjs";
import { findTrx, formatReceipt, runWithReceipt, summarize } from "./receipt.mjs";

const WORKTREE = join(process.env.LOCALAPPDATA ?? join(ROOT, ".."), "map-test-baseline", "worktree");

function git(args, cwd) {
  const r = spawnSync("git", args, { cwd, encoding: "utf8" });
  if (r.status !== 0) throw new Error(`git ${args.join(" ")}: ${(r.stderr || r.stdout).trim()}`);
  return r.stdout.trim();
}

export function parse(argv) {
  const o = { base: "HEAD", keep: false, command: [], compareTrx: undefined };
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === "--") { o.command = argv.slice(i + 1); break; }
    if (argv[i] === "--base") o.base = argv[++i];
    else if (argv[i] === "--keep") o.keep = true;
    else if (argv[i] === "--compare-trx") { o.compareTrx = [argv[++i], argv[++i]]; }
    else throw new Error(`Unknown option ${argv[i]}`);
  }
  if (!o.compareTrx && !o.command.length)
    throw new Error("Usage: baseline.mjs [--base REV] [--keep] -- <test command ...> | --compare-trx <base> <current>");
  return o;
}

/** A run is comparable only when it wrote a report with tests in it. */
const reliable = (r) => r.reports.some((x) => !x.retry) && r.counts.total > 0;

export function compareRuns(base, current) {
  const b = new Set(base.failures), c = new Set(current.failures);
  return {
    regressions: current.failures.filter((f) => !b.has(f)),
    preexisting: current.failures.filter((f) => b.has(f)),
    fixed: base.failures.filter((f) => !c.has(f)),
    reliable: reliable(base) && reliable(current),
    totals: { base: base.counts.total, current: current.counts.total },
  };
}

function show(title, list, max = 25) {
  console.log(`${title}: ${list.length}`);
  for (const f of list.slice(0, max)) console.log(`  - ${f}`);
  if (list.length > max) console.log(`  … ещё ${list.length - max}`);
}

export function printComparison(c) {
  console.log("\nСРАВНЕНИЕ (одна команда, по очереди, одна машина):");
  if (!c.reliable) console.log("  НЕДОСТОВЕРНО: в одном из прогонов нет отчёта .trx или 0 тестов — выводы «старое/новое» делать нельзя.");
  if (c.totals.base !== c.totals.current)
    console.log(`  Внимание: число тестов разное (база ${c.totals.base}, сейчас ${c.totals.current}) — набор тестов изменился.`);
  show("НОВЫЕ падения (регрессии или новые тесты)", c.regressions);
  show("СТАРЫЕ (падали и в базе — подтверждено прогоном базы)", c.preexisting);
  show("ИСПРАВЛЕНО (падали в базе, теперь нет)", c.fixed);
  return c.reliable && c.regressions.length === 0 ? 0 : 1;
}

function prepareWorktree(commit) {
  if (!existsSync(join(WORKTREE, ".git"))) {
    mkdirSync(resolve(WORKTREE, ".."), { recursive: true });
    git(["worktree", "add", "--detach", WORKTREE, commit], ROOT);
  } else {
    git(["checkout", "--detach", "--force", commit], WORKTREE);
  }
}

function removeWorktree() {
  spawnSync("git", ["worktree", "remove", "--force", WORKTREE], { cwd: ROOT, encoding: "utf8" });
  rmSync(WORKTREE, { recursive: true, force: true });
  spawnSync("git", ["worktree", "prune"], { cwd: ROOT, encoding: "utf8" });
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  let o;
  try {
    o = parse(process.argv.slice(2));
    if (o.compareTrx) {
      const base = summarize(findTrx([o.compareTrx[0]]));
      const current = summarize(findTrx([o.compareTrx[1]]));
      console.log(`База: ${o.compareTrx[0]} — ${base.counts.total} тестов, упало ${base.failures.length}`);
      console.log(`Сейчас: ${o.compareTrx[1]} — ${current.counts.total} тестов, упало ${current.failures.length}`);
      console.log("(только сравнение готовых отчётов: что это одна команда и один фильтр — не проверено)");
      process.exitCode = printComparison(compareRuns(base, current));
    } else {
      const commit = git(["rev-parse", o.base], process.cwd());
      prepareWorktree(commit);
      console.log(`База: ${o.base} = ${commit.slice(0, 9)} в ${WORKTREE}`);
      const base = await runWithReceipt({ label: `baseline-${commit.slice(0, 9)}`, cwd: WORKTREE, command: o.command });
      console.log(formatReceipt(base, 10));
      const current = await runWithReceipt({ label: "current", cwd: process.cwd(), command: o.command });
      console.log(formatReceipt(current, 10));
      process.exitCode = printComparison(compareRuns(base, current));
    }
  } catch (error) {
    console.error(error instanceof Error ? error.message : String(error));
    process.exitCode = 2;
  } finally {
    if (o && !o.compareTrx && !o.keep && existsSync(WORKTREE)) removeWorktree();
  }
}
