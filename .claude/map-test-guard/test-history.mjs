// Flaky-test history for Map from real reports (read-only, no builds, no test runs):
//   node .claude/map-test-guard/test-history.mjs [--runs N] [--no-ci] [--local DIR|TRX ...] [--json]
//
// Sources:
//  - CI: the newest N (default 20) completed runs of dmfromtheash/map workflow "build"; their test-results-*
//    artifacts (.trx from tools/ci/Invoke-TestProject.ps1, with retry/) are downloaded one run at a time into
//    %LOCALAPPDATA%\map-test-history\<run> by `gh run download`, parsed and deleted at once.
//  - local receipts of the guard (receipt.mjs keeps the .trx of its newest 30 runs);
//  - extra local folders/files given with --local (e.g. %TEMP%\map-tr).
// A "code state" is the commit for CI, the src/+tests/ content hash for a receipt, and the folder of the .trx
// for --local (its retry/ subfolder belongs to the same run; other subfolders are separate runs).
// Reported per full test name (theory cases separately):
//  - passed only on retry: failed in the main report, passed in the retry/ report of the same run;
//  - same state, both outcomes: passed and failed on the same commit / content (different runs or retry);
//  - flip-flop: in time order the outcome changed at least twice (pass→fail→pass or fail→pass→fail).
import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, readdirSync, readFileSync, rmSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { ROOT } from "./lib.mjs";
import { findTrx } from "./receipt.mjs";
import { readTrx } from "./trx.mjs";

const REPO = "dmfromtheash/map";
const HOME = join(process.env.LOCALAPPDATA ?? join(ROOT, ".."), "map-test-history");

function gh(args) {
  const r = spawnSync("gh", args, { encoding: "utf8", maxBuffer: 64 * 1024 * 1024 });
  if (r.status !== 0) throw new Error(`gh ${args.join(" ")}: ${(r.stderr || r.stdout).trim()}`);
  return r.stdout;
}

/** One observation set: every main/retry result of the .trx files found under `paths`. */
export function observe(paths, meta) {
  const out = [];
  for (const { file, retry } of findTrx(paths)) {
    const r = readTrx(file);
    const raw = meta.time ?? r.started;
    const time = raw && !Number.isNaN(Date.parse(raw)) ? new Date(raw).toISOString() : raw; // one format for sorting
    const folder = dirname(file).replace(/[\\/]retry$/i, "");
    const state = meta.state ?? `folder:${folder}`;
    const source = meta.source ?? `локально ${folder}`;
    for (const [list, outcome] of [[r.passed, "pass"], [r.failures, "fail"]])
      for (const e of list) out.push({ test: e.name, outcome, retry, time, state, source });
  }
  return out;
}

export function analyse(obs) {
  const byTest = new Map();
  for (const o of obs) byTest.set(o.test, [...(byTest.get(o.test) ?? []), o]);
  const report = [];
  for (const [test, list] of byTest) {
    const fails = list.filter((o) => o.outcome === "fail");
    if (fails.length === 0) continue;
    const passes = list.filter((o) => o.outcome === "pass");
    const kinds = [];
    const retrySources = [...new Set(fails.filter((o) => !o.retry).map((o) => o.source))].filter((src) =>
      list.some((o) => o.source === src && o.retry && o.outcome === "pass"));
    if (retrySources.length) kinds.push(`прошёл только при повторе: ${retrySources.length}`);
    const states = new Map();
    for (const o of list) states.set(o.state, new Set([...(states.get(o.state) ?? []), o.outcome]));
    const mixed = [...states.entries()].filter(([, s]) => s.size === 2).map(([k]) => k);
    if (mixed.length) kinds.push(`одно состояние кода — и прошёл, и упал: ${mixed.length}`);
    // one outcome per source (main report wins over retry), in time order
    const perSource = new Map();
    for (const o of [...list].sort((a, b) => Number(a.retry) - Number(b.retry))) if (!perSource.has(o.source)) perSource.set(o.source, o);
    const seq = [...perSource.values()].sort((a, b) => String(a.time).localeCompare(String(b.time))).map((o) => o.outcome);
    const flips = seq.slice(1).filter((v, i) => v !== seq[i]).length;
    if (flips >= 2) kinds.push(`смена исхода по прогонам подряд: ${flips} раз (${seq.map((v) => (v === "pass" ? "+" : "−")).join("")})`);
    const last = [...fails].sort((a, b) => String(b.time).localeCompare(String(a.time)))[0];
    report.push({ test, flaky: kinds.length > 0, kinds, passes: passes.length, fails: fails.length, lastFail: last.time, lastFailSource: last.source, sequence: seq.join("") });
  }
  return report.sort((a, b) => Number(b.flaky) - Number(a.flaky) || b.fails - a.fails || String(b.lastFail).localeCompare(String(a.lastFail)));
}

function localReceipts() {
  const base = join(process.env.MAP_TEST_GUARD_HOME ?? join(process.env.LOCALAPPDATA ?? join(ROOT, ".."), "map-test-guard"), "receipts");
  if (!existsSync(base)) return [];
  const out = [];
  for (const repo of readdirSync(base))
    for (const name of readdirSync(join(base, repo)).filter((n) => n.endsWith(".json") && !n.startsWith("skips-"))) {
      try {
        const r = JSON.parse(readFileSync(join(base, repo, name), "utf8"));
        if (r.ran && r.resultsDir && existsSync(r.resultsDir))
          out.push({ paths: [r.resultsDir], meta: { state: `local:${r.git?.contentHash?.slice(0, 12)}`, source: `квитанция ${name}`, time: r.startedAt } });
      } catch {}
    }
  return out;
}

function ciRuns(n) {
  const runs = JSON.parse(gh(["run", "list", "-R", REPO, "-w", "build", "-L", String(n), "--json", "databaseId,headSha,status,conclusion,createdAt,headBranch"]));
  return runs.filter((r) => r.status === "completed" && r.conclusion !== "cancelled" && r.conclusion !== "skipped");
}

function main(argv) {
  const o = { runs: 20, ci: true, local: [], json: false };
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === "--runs") o.runs = Number(argv[++i]);
    else if (argv[i] === "--no-ci") o.ci = false;
    else if (argv[i] === "--local") o.local.push(argv[++i]);
    else if (argv[i] === "--json") o.json = true;
    else throw new Error(`Unknown option ${argv[i]}`);
  }
  const obs = [];
  const notes = [];
  if (o.ci) {
    mkdirSync(HOME, { recursive: true });
    for (const run of ciRuns(o.runs)) {
      const dir = join(HOME, String(run.databaseId));
      try {
        rmSync(dir, { recursive: true, force: true });
        gh(["run", "download", String(run.databaseId), "-R", REPO, "-p", "test-results-*", "-D", dir]);
        const before = obs.length;
        for (const artifact of existsSync(dir) ? readdirSync(dir) : [])
          obs.push(...observe([join(dir, artifact)], { state: run.headSha.slice(0, 9), source: `CI ${run.databaseId} ${artifact}`, time: run.createdAt }));
        notes.push(`CI ${run.databaseId} ${run.headSha.slice(0, 9)} ${run.conclusion}: результатов ${obs.length - before}`);
      } catch (error) {
        notes.push(`CI ${run.databaseId}: отчётов нет (${(error instanceof Error ? error.message : String(error)).split("\n")[0].slice(0, 120)})`);
      } finally {
        rmSync(dir, { recursive: true, force: true });
      }
    }
    if (existsSync(HOME) && readdirSync(HOME).length === 0) rmSync(HOME, { recursive: true, force: true });
  }
  for (const r of localReceipts()) { const b = obs.length; obs.push(...observe(r.paths, r.meta)); notes.push(`${r.meta.source}: результатов ${obs.length - b}`); }
  for (const p of o.local) { const b = obs.length; obs.push(...observe([p], {})); notes.push(`локально ${p}: результатов ${obs.length - b}`); }

  const report = analyse(obs);
  if (o.json) { console.log(JSON.stringify({ notes, report }, null, 1)); return 0; }
  console.log("Источники:");
  for (const n of notes) console.log(`  ${n}`);
  const flaky = report.filter((r) => r.flaky);
  console.log(`\nНЕСТАБИЛЬНЫЕ тесты: ${flaky.length}`);
  for (const r of flaky) {
    console.log(`  - ${r.test}`);
    console.log(`      прошёл ${r.passes}, упал ${r.fails}; ${r.kinds.join("; ")}; последнее падение ${r.lastFail} (${r.lastFailSource})`);
  }
  const steady = report.filter((r) => !r.flaky);
  console.log(`\nПадали без признаков нестабильности (каждый раз падение или одна смена исхода): ${steady.length}`);
  for (const r of steady.slice(0, 30)) console.log(`  - ${r.test}: упал ${r.fails}, прошёл ${r.passes}, последнее ${r.lastFail}`);
  if (steady.length > 30) console.log(`  … ещё ${steady.length - 30}`);
  return 0;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { process.exitCode = main(process.argv.slice(2)); }
  catch (error) { console.error(error instanceof Error ? error.message : String(error)); process.exitCode = 2; }
}
