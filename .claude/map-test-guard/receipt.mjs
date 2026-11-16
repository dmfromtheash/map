// Test-run receipt for Map: runs a test command and records what really happened, from .trx files.
//
//   node .claude/map-test-guard/receipt.mjs [--label NAME] [--cwd DIR] -- <command ...>
//   node .claude/map-test-guard/receipt.mjs --parse-only <file.trx|dir> ...   (read existing reports, no run)
//
// The command gets its own results folder: `{results}` in the command is replaced by it; otherwise
// `dotnet test` gets `--logger trx --results-directory <dir>` and tools/ci/Invoke-TestProject.ps1 gets
// `-ResultsDir <dir>`. Any other command must contain `{results}`.
// "passed" only when at least one .trx was written, it holds > 0 tests, nothing failed and exit code is 0.
// Receipts live in %LOCALAPPDATA%\map-test-guard\receipts\<repo>-<hash>\ (outside the repo); the newest 30 are kept.
import { spawn } from "node:child_process";
import { createWriteStream, existsSync, mkdirSync, readdirSync, rmSync, statSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { contentHash, git, isKnown, readKnownFailures, receiptsDir } from "./lib.mjs";
import { readTrx } from "./trx.mjs";

const KEEP = 30;

export function parseArguments(argv) {
  const o = { label: "run", cwd: process.cwd(), command: [], parseOnly: undefined };
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === "--") { o.command = argv.slice(i + 1); break; }
    if (argv[i] === "--label") o.label = argv[++i];
    else if (argv[i] === "--cwd") o.cwd = resolve(argv[++i]);
    else if (argv[i] === "--parse-only") { o.parseOnly = argv.slice(i + 1); break; }
    else throw new Error(`Unknown option ${argv[i]}`);
  }
  if (!o.parseOnly && o.command.length === 0)
    throw new Error("Usage: receipt.mjs [--label NAME] [--cwd DIR] -- <command ...> | --parse-only <trx|dir>...");
  return o;
}

// The command runs through the shell (cmd on Windows): a filter like "A|B" must be quoted or cmd pipes it
// (2026-11-16: `--filter FullyQualifiedName~A|FullyQualifiedName~B` ran "B" as a program, exit 255, no .trx).
const quote = (s) => (/[\s"|&<>^]/.test(s) ? `"${s.replace(/"/g, '\\"')}"` : s);

/** Adds the results folder to the command (see header). Throws when it cannot know where .trx goes. */
export function prepareCommand(command, resultsDir) {
  const text = command.map(quote).join(" ");
  if (text.includes("{results}")) return text.replaceAll("{results}", quote(resultsDir));
  if (/Invoke-TestProject\.ps1/i.test(text)) {
    if (/-ResultsDir\b/i.test(text)) throw new Error("Уберите -ResultsDir или поставьте {results}: квитанция сама задаёт папку отчётов.");
    return `${text} -ResultsDir ${quote(resultsDir)}`;
  }
  if (/\bdotnet(\.exe)?\s+test\b/i.test(text)) {
    if (/--results-directory\b|(^|\s)-r\s/i.test(text)) throw new Error("Уберите --results-directory или поставьте {results}: квитанция сама задаёт папку отчётов.");
    const extra = /--logger\s+"?trx/i.test(text) ? "" : " --logger trx";
    const sep = text.match(/\s--\s/); // MSBuild-style run settings after "--" must stay last
    const add = `${extra} --results-directory ${quote(resultsDir)}`;
    return sep ? text.slice(0, sep.index) + add + text.slice(sep.index) : text + add;
  }
  throw new Error("Не знаю, куда команда пишет .trx. Поставьте {results} там, где нужна папка отчётов.");
}

/**
 * .trx files under the given files/folders. A file inside a `retry` folder below a given folder is a retry
 * of Invoke-TestProject.ps1; a .trx named directly is always a main report.
 */
export function findTrx(paths) {
  const out = [];
  const walk = (p, inRetry) => {
    if (!existsSync(p)) return;
    if (statSync(p).isDirectory()) for (const n of readdirSync(p)) walk(join(p, n), inRetry || n.toLowerCase() === "retry");
    else if (p.toLowerCase().endsWith(".trx")) out.push({ file: p, retry: inRetry });
  };
  paths.forEach((p) => walk(resolve(p), false));
  return out.sort((x, y) => x.file.localeCompare(y.file));
}

/** Sums .trx reports; files inside a `retry` folder (Invoke-TestProject.ps1) are retries, not new tests. */
export function summarize(files, known = []) {
  const reports = files.map((f) => ({ ...readTrx(f.file), retry: f.retry }));
  const isRetry = (r) => r.retry;
  const main = reports.filter((r) => !isRetry(r));
  const retries = reports.filter(isRetry);
  const counts = { total: 0, passed: 0, failed: 0, skipped: 0, other: 0 };
  for (const r of main) for (const k of Object.keys(counts)) counts[k] += r.counts[k];
  const failures = [...new Set(main.flatMap((r) => r.failures.map((f) => f.name)))].sort();
  const failedMethods = [...new Set(main.flatMap((r) => r.failures.map((f) => f.method)))];
  const retryFailed = new Set(retries.flatMap((r) => r.failures.map((f) => f.method)));
  const retryPassed = new Set(retries.flatMap((r) => r.passed.map((f) => f.method)));
  const passedOnRetry = failedMethods.filter((m) => retryPassed.has(m) && !retryFailed.has(m));
  return {
    reports: reports.map((r) => ({ file: r.file, retry: isRetry(r), counts: r.counts, counters: r.counters, countersMismatch: r.countersMismatch })),
    counts,
    failures,
    knownFailures: failedMethods.filter((m) => isKnown(m, known)),
    passedOnRetry,
    skipped: main.flatMap((r) => r.skipped.map((s) => ({ name: s.name, reason: s.reason ?? "" }))),
  };
}

export function verdictOf(s, exitCode, ran) {
  if (s.reports.filter((r) => !r.retry).length === 0) return "no-report";
  if (s.counts.total === 0) return "zero-tests";
  if (s.counts.failed + s.counts.other > 0) return "failed";
  if (!ran) return "parsed-only";
  if (exitCode !== 0) return "exit-nonzero";
  return "passed";
}

function prune(dir) {
  const jsons = readdirSync(dir).filter((n) => n.endsWith(".json")).sort();
  for (const name of jsons.slice(0, Math.max(0, jsons.length - KEEP))) {
    const stem = name.slice(0, -5);
    for (const p of [`${stem}.json`, `${stem}.log`, `${stem}-results`]) rmSync(join(dir, p), { recursive: true, force: true });
  }
}

export async function runWithReceipt({ label, cwd, command, parseOnly }) {
  const repoRoot = resolve((git(cwd, ["rev-parse", "--show-toplevel"]) ?? cwd).trim());
  const dir = receiptsDir(repoRoot);
  mkdirSync(dir, { recursive: true });
  const stamp = new Date().toISOString().replace(/[:.]/g, "-");
  const base = join(dir, `${stamp}-${label.replace(/[^\w.-]/g, "_")}`);
  const resultsDir = `${base}-results`;
  const hashBefore = contentHash(repoRoot);
  const receipt = {
    schema: 1,
    tool: "map-test-guard",
    label,
    startedAt: new Date().toISOString(),
    cwd,
    repoRoot,
    platform: `${process.platform}-${process.arch}`,
    git: {
      head: git(repoRoot, ["rev-parse", "HEAD"])?.trim(),
      branch: git(repoRoot, ["rev-parse", "--abbrev-ref", "HEAD"])?.trim(),
      dirtyFiles: (git(repoRoot, ["status", "--porcelain", "--untracked-files=all", "--", "src", "tests"]) ?? "").split("\n").filter(Boolean),
      contentHash: hashBefore,
    },
  };
  let exitCode = null;
  let files;
  const started = Date.now();
  if (parseOnly) {
    receipt.command = `(parse-only) ${parseOnly.join(" ")}`;
    files = findTrx(parseOnly);
  } else {
    mkdirSync(resultsDir, { recursive: true });
    receipt.command = prepareCommand(command, resultsDir);
    receipt.resultsDir = resultsDir;
    receipt.log = `${base}.log`;
    const log = createWriteStream(receipt.log);
    const child = spawn(receipt.command, { cwd, shell: true, stdio: ["ignore", "pipe", "pipe"], env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: "1" } });
    for (const stream of [child.stdout, child.stderr]) stream.on("data", (chunk) => log.write(chunk));
    exitCode = await new Promise((done) => child.once("close", (c) => done(c)));
    await new Promise((done) => log.end(done));
    files = findTrx([resultsDir]);
  }
  const s = summarize(files, readKnownFailures(repoRoot));
  const hashAfter = contentHash(repoRoot);
  Object.assign(receipt, {
    finishedAt: new Date().toISOString(),
    durationSeconds: Math.round((Date.now() - started) / 1000),
    exitCode,
    ran: !parseOnly,
    contentChangedDuringRun: hashBefore !== hashAfter,
    ...s,
  });
  receipt.verdict = verdictOf(s, exitCode, !parseOnly);
  writeFileSync(`${base}.json`, JSON.stringify(receipt, null, 2));
  receipt.file = `${base}.json`;
  prune(dir);
  return receipt;
}

const VERDICT = {
  passed: "ПРОШЛО",
  failed: "ЕСТЬ ПАДЕНИЯ",
  "no-report": "НЕТ ОТЧЁТА .trx — тесты не запускались или прогон оборвался. Это не «прошло».",
  "zero-tests": "0 ТЕСТОВ в отчёте — фильтр ничего не выбрал или проект пустой. Это не «прошло».",
  "exit-nonzero": "КОД ВЫХОДА НЕ 0 при отсутствии падений в .trx (сборка? другой проект?) — не «прошло».",
  "parsed-only": "только разбор готовых отчётов, прогона не было — не квитанция прогона",
};

export function formatReceipt(r, maxFailures = 25) {
  const c = r.counts;
  const lines = [
    `КВИТАНЦИЯ ${r.label}: ${VERDICT[r.verdict] ?? r.verdict}${r.ran ? ` (exit=${r.exitCode}, ${r.durationSeconds} с)` : ""}`,
    `  ревизия ${r.git.head?.slice(0, 9)} (${r.git.branch}), изменённых файлов в src/tests: ${r.git.dirtyFiles.length}, содержимое ${r.git.contentHash?.slice(0, 12)}`,
    `  команда: ${r.command}`,
  ];
  if (r.contentChangedDuringRun) lines.push("  ВНИМАНИЕ: файлы src/tests менялись во время прогона — квитанция относится к состоянию на старте.");
  const main = r.reports.filter((x) => !x.retry);
  lines.push(`  отчётов .trx: ${main.length}${r.reports.length > main.length ? ` (+ повторов: ${r.reports.length - main.length})` : ""}`);
  if (main.length)
    lines.push(`  тесты: прошло ${c.passed}, упало ${c.failed}${c.other ? `, прочих исходов (ошибка/таймаут/прервано) ${c.other}` : ""}, пропущено ${c.skipped}, всего ${c.total}`);
  for (const x of r.reports.filter((x) => x.countersMismatch))
    lines.push(`  ВНИМАНИЕ: счётчики в ${x.file} (${x.counters.total}/${x.counters.passed}/${x.counters.failed}) не совпадают с разбором результатов`);
  if (r.failures.length) {
    lines.push(`  упавшие (${r.failures.length}):`);
    for (const f of r.failures.slice(0, maxFailures)) lines.push(`    - ${f}`);
    if (r.failures.length > maxFailures) lines.push(`    … ещё ${r.failures.length - maxFailures}, см. ${r.file}`);
  }
  if (r.knownFailures.length) lines.push(`  из них в tests/KnownFailures.txt: ${r.knownFailures.join(", ")}`);
  if (r.passedOnRetry.length) lines.push(`  прошли при повторе (нестабильные, надо чинить): ${r.passedOnRetry.join(", ")}`);
  if (c.skipped) lines.push(`  пропущенные (${c.skipped}) считаются непроверенными`);
  lines.push(`  квитанция: ${r.file}`);
  if (r.log) lines.push(`  полный журнал: ${r.log}`);
  return lines.join("\n");
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const receipt = await runWithReceipt(parseArguments(process.argv.slice(2)));
    console.log(formatReceipt(receipt));
    process.exitCode = receipt.verdict === "passed" ? 0 : 1;
  } catch (error) {
    console.error(error instanceof Error ? error.message : String(error));
    process.exitCode = 2;
  }
}

