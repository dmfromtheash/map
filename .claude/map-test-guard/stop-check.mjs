// Stop hook logic for Map: before the agent ends a turn, code under src/ or tests/ that changed in this
// session must be covered by a test-run receipt whose content hash equals the current src/+tests/ content.
// Blocks once per stop (stop_hook_active lets the second stop through), so it cannot loop.
// Changed-in-session = uncommitted files with mtime after the session start, or files in commits made
// since the session start. Other people's older uncommitted files do not trigger it.
import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { join, resolve } from "node:path";
import { CODE, ROOT, contentHash, git, ownerRoot, receiptsDir } from "./lib.mjs";

function changedInSession(repoRoot, sessionStart) {
  const status = git(repoRoot, ["status", "--porcelain", "--untracked-files=all", "--", "src", "tests"]) ?? "";
  const dirty = status
    .split("\n")
    .filter(Boolean)
    .map((line) => line.slice(3).replace(/^.* -> /, "").replace(/^"|"$/g, ""))
    .filter((p) => CODE.test(p) && existsSync(join(repoRoot, p)) && statSync(join(repoRoot, p)).mtimeMs > sessionStart);
  const since = Math.floor(sessionStart / 1000);
  const committed = (git(repoRoot, ["log", `--since=@${since}`, "--format=", "--name-only", "HEAD", "--", "src", "tests"]) ?? "")
    .split("\n")
    .map((l) => l.trim())
    .filter((p) => p && CODE.test(p));
  return [...new Set([...dirty, ...committed])];
}

export function coveringReceipt(repoRoot, hash) {
  const dir = receiptsDir(repoRoot);
  if (!existsSync(dir)) return undefined;
  for (const name of readdirSync(dir).filter((n) => n.endsWith(".json")).sort().reverse()) {
    try {
      const r = JSON.parse(readFileSync(join(dir, name), "utf8"));
      const hasTests = r.reports?.some((x) => !x.retry) && r.counts?.total > 0;
      if (r.tool === "map-test-guard" && r.ran && hasTests && !r.contentChangedDuringRun && r.git?.contentHash === hash) return join(dir, name);
    } catch {}
  }
  return undefined;
}

export function check(event, root = ROOT, now = Date.now()) {
  if (event.stop_hook_active) return undefined;
  const cwd = resolve(event.cwd ?? process.cwd());
  const top = git(cwd, ["rev-parse", "--show-toplevel"])?.trim();
  if (!top) return undefined;
  const repoRoot = resolve(top);
  const owner = ownerRoot(repoRoot, root);
  if (!owner || resolve(owner).toLowerCase() !== repoRoot.toLowerCase()) return undefined;
  let sessionStart = now - 12 * 3600 * 1000;
  if (event.transcript_path && existsSync(event.transcript_path)) {
    const s = statSync(event.transcript_path);
    if (s.birthtimeMs > 0) sessionStart = s.birthtimeMs;
  }
  const changed = changedInSession(repoRoot, sessionStart);
  if (changed.length === 0) return undefined;
  const hash = contentHash(repoRoot);
  if (hash && coveringReceipt(repoRoot, hash)) return undefined;
  const sample = changed.slice(0, 3).join(", ");
  const reason =
    `Проверка тестов Map: в этой сессии изменён код в src/ или tests/ (${changed.length}: ${sample}${changed.length > 3 ? ", …" : ""}), ` +
    "а квитанции прогона тестов для текущего содержимого нет. Прежде чем завершить: запусти тесты затронутого проекта по фильтру через " +
    "`node .claude/map-test-guard/receipt.mjs -- dotnet test tests/<Проект>/<Проект>.csproj -c Release --filter \"FullyQualifiedName~<Класс>\"` " +
    "и опиши итог по квитанции (прошло/упало/пропущено). Падения — новые ли: `node .claude/map-test-guard/baseline.mjs -- <та же команда>`. " +
    "Если тесты сознательно не запускались (например, ПК занят замером FPS или правка только сборочная), прямо скажи владельцу, что не проверено и почему.";
  return { decision: "block", reason };
}

export function run(raw, root = ROOT) {
  const event = JSON.parse(raw || "{}");
  const verdict = check(event, root);
  return verdict ? JSON.stringify(verdict) : "";
}
