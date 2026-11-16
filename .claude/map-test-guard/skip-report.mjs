// Skip accounting for Map: which tests were skipped (NotExecuted in .trx) and why.
//   node .claude/map-test-guard/skip-report.mjs [--save NAME] [--compare previous.json] <file.trx|dir|receipt.json>...
// A reason without "defect:" breaks DEC-116 and is flagged. With --compare, new skips versus the saved
// report give exit 1 (they need the same justification as a new Skip in code). Saved reports go to the
// receipts folder of the current repo (outside the repo).
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { git, receiptsDir } from "./lib.mjs";
import { findTrx, summarize } from "./receipt.mjs";

export function collectSkips(inputs) {
  const skips = [];
  const trx = [];
  for (const p of inputs) {
    if (p.toLowerCase().endsWith(".json")) skips.push(...(JSON.parse(readFileSync(p, "utf8")).skipped ?? []));
    else trx.push(p);
  }
  if (trx.length) skips.push(...summarize(findTrx(trx)).skipped);
  return skips.map((s) => (typeof s === "string" ? { name: s, reason: "" } : s));
}

export function byReason(skips) {
  const groups = new Map();
  for (const s of skips) {
    const reason = s.reason || "без причины";
    groups.set(reason, [...(groups.get(reason) ?? []), s.name]);
  }
  return [...groups.entries()].map(([reason, names]) => ({ reason, count: names.length, defect: /^defect:/.test(reason), names })).sort((a, b) => b.count - a.count);
}

function main(argv) {
  let save, compare;
  const inputs = [];
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === "--save") save = argv[++i];
    else if (argv[i] === "--compare") compare = argv[++i];
    else inputs.push(argv[i]);
  }
  if (!inputs.length) { console.error("usage: skip-report.mjs [--save NAME] [--compare previous.json] <trx|dir|receipt.json>..."); return 2; }
  const skips = collectSkips(inputs);
  const groups = byReason(skips);
  console.log(`пропущено: ${skips.length}`);
  for (const g of groups) console.log(`  ${String(g.count).padStart(4)}  ${g.defect ? "" : "[БЕЗ defect:, DEC-116] "}${g.reason}`);
  if (!skips.length) console.log("  пропусков нет (или отчёт не тот)");
  let exit = 0;
  if (compare) {
    const before = new Set(JSON.parse(readFileSync(compare, "utf8")).skips.map((s) => s.name));
    const now = new Set(skips.map((s) => s.name));
    const added = skips.filter((s) => !before.has(s.name));
    const gone = [...before].filter((n) => !now.has(n));
    console.log(`по сравнению с ${compare}: новых пропусков ${added.length}, ушло ${gone.length}`);
    for (const s of added.slice(0, 30)) console.log(`  + ${s.name}  [${s.reason || "без причины"}]`);
    if (added.length) exit = 1;
  }
  if (save) {
    const top = git(process.cwd(), ["rev-parse", "--show-toplevel"])?.trim() ?? process.cwd();
    const dir = receiptsDir(resolve(top));
    mkdirSync(dir, { recursive: true });
    const path = join(dir, `skips-${save.replace(/[^\w.-]/g, "_")}.json`);
    writeFileSync(path, JSON.stringify({ schema: 1, createdAt: new Date().toISOString(), inputs, groups: groups.map(({ names, ...g }) => g), skips }, null, 1));
    console.log(`сохранено: ${path}`);
  }
  return exit;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url) && existsSync(process.argv[1]))
  process.exitCode = main(process.argv.slice(2));
