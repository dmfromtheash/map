// PreToolUse hook for Map (Edit|Write|MultiEdit|Bash|PowerShell). Complements tools/ai/Test-Integrity.ps1
// (DEC-116): that script judges commits, this hook stops a weakening edit before it is written.
//  - ask:   fewer Assert/tests/InlineData cases, a new Skip / conditional [..Fact] attribute / Assert.Skip,
//           a new early `if (..Exists/Env/OS..) return;`, a new retry, a raised number in an Assert/threshold
//           line, a test moved by [Trait]/[Collection], any edit of test-run configuration, and any edit of
//           this guard or the Claude settings. Shell commands that write, move or delete such files.
//  - note:  a changed expected value in Assert.* is allowed; the agent is told to justify it.
//  - allow: everything else (new test files without skips, edits that keep or add checks, src/ edits).
// The owner answers "ask" prompts; the agent cannot. Fail-safe: if the guard itself fails and the input
// mentions tests/ or a protected file, it ASKS instead of allowing.
import { existsSync, readFileSync } from "node:fs";
import { isAbsolute, resolve } from "node:path";
import { ROOT, repoRelative } from "./lib.mjs";

const SELF = /^(\.claude\/(map-test-guard\/|settings(\.local)?\.json$))/i;
const CONFIG =
  /(^|\/)([^/]+\.runsettings|xunit\.runner\.json|Directory\.(Build|Packages)\.(props|targets)|global\.json)$|^[^/]+\.slnx$|^tests\/.*\.csproj$|^tests\/KnownFailures\.txt$|^tools\/ci\/|^tools\/ai\/(Test-Integrity\.ps1|test-integrity-baseline\.txt)$|^\.github\/workflows\//i;
const TEST_FILE = /^tests\//i;

const ASSERT = /\bAssert\.\w+\s*[(<]/g;
const TEST_CASE = /\[\s*\w*(Fact|Theory)\b/g;
const INLINE_CASE = /\[\s*(InlineData|MemberData|ClassData)\s*\(/g;
const SKIP = /\bSkip\s*=/g;
const CONDITIONAL_ATTR = /\[\s*(?!Fact\b|Theory\b)\w+(Fact|Theory)\b|\bAssert\.Skip\w*\s*\(|\bSkip\.(If|IfNot|Unless|When)\s*\(|\bExplicit\s*=\s*true/g;
const EARLY_RETURN = /^\s*if \(.*(GetEnvironmentVariable|File\.Exists|Directory\.Exists|OperatingSystem\.Is).*\)\s*return\s*;/gm; // same rule as Test-Integrity.ps1
const RETRY = /\[\s*Retry\w*(Fact|Theory)\b|\bfor\s*\(\s*(var|int)\s+(attempt|retry|retries|tries|try)\w*\b|\bwhile\s*\([^)\n]*\b(attempt|retr|tries)\w*|\bRetry(Async)?\s*\(|\bPolly\b/gi;
const MOVE = /\[\s*(Trait|Collection)\s*\(/;
const THRESHOLD = /(Assert|Budget|budget|Timeout|TimeSpan|Milliseconds|Seconds|Ratio|ratio|<=|>=|Max|Limit|Within|Delay|Tolerance|tolerance|precision|Epsilon)/;
const EXPECT = /\bAssert\.(Equal|NotEqual|InRange|True|False|StrictEqual|Same)\s*\(/;
const NUM = /\d+(\.\d+)?/g;

const count = (text, re) => (text.match(re) ?? []).length;
const lines = (text) => text.split(/\r?\n/).map((l) => l.trim()).filter(Boolean);

function classify(rel) {
  if (rel === undefined) return undefined;
  if (SELF.test(rel)) return "self";
  if (CONFIG.test(rel)) return "config";
  if (TEST_FILE.test(rel)) return "test";
  return undefined;
}

/** Numbers in `after` larger than in a `before` line of the same shape (digits ignored). */
function raisedNumbers(before, after) {
  const out = [];
  const old = lines(before).filter((l) => THRESHOLD.test(l));
  const oldSet = new Set(lines(before));
  for (const line of lines(after)) {
    if (!THRESHOLD.test(line) || oldSet.has(line)) continue;
    const shape = line.replace(NUM, "#");
    for (const o of old) {
      if (o.replace(NUM, "#") !== shape) continue;
      const a = (o.match(NUM) ?? []).map(Number), b = (line.match(NUM) ?? []).map(Number);
      if (b.some((v, k) => k < a.length && v > a[k])) { out.push(`${o} → ${line}`); break; }
    }
  }
  return out;
}

export function compare(before, after, isNew) {
  const reasons = [];
  const more = (re, what) => {
    const c0 = count(before, re), c1 = count(after, re);
    if (c1 > c0) reasons.push(`${what}: ${c0} → ${c1}`);
  };
  const fewer = (re, what) => {
    const c0 = count(before, re), c1 = count(after, re);
    if (c1 < c0) reasons.push(`${what}: ${c0} → ${c1}`);
  };
  if (!isNew) {
    fewer(ASSERT, "проверок Assert.* стало меньше");
    fewer(TEST_CASE, "тестов [Fact]/[Theory] стало меньше");
    fewer(INLINE_CASE, "случаев теории (InlineData/MemberData) стало меньше");
  }
  more(SKIP, "добавлен Skip = (в Map разрешён только Skip = \"defect: …\" с обоснованием, DEC-116)");
  more(CONDITIONAL_ATTR, "добавлен условный пропуск теста (свой атрибут *Fact/*Theory, Assert.Skip, Explicit)");
  more(EARLY_RETURN, "добавлен ранний return по окружению/файлу/ОС — тихий проход (DEC-116)");
  more(RETRY, "добавлен повтор (retry) — он прячет нестабильность");
  const oldLines = new Set(lines(before));
  const moved = lines(after).filter((l) => MOVE.test(l) && !oldLines.has(l));
  if (moved.length) reasons.push(`тест перенесён в другую группу/коллекцию: ${moved.join("; ")}`);
  for (const r of raisedNumbers(before, after)) reasons.push(`порог/число в проверке увеличено: ${r}`);
  const oldExpect = lines(before).filter((l) => EXPECT.test(l));
  const newExpect = lines(after).filter((l) => EXPECT.test(l));
  const changedExpectations =
    oldExpect.length > 0 && newExpect.some((l) => !oldExpect.includes(l)) && oldExpect.some((l) => !newExpect.includes(l));
  return { reasons, changedExpectations };
}

function fileEdits(tool, input, cwd) {
  if (tool === "Edit") return [{ path: input.file_path, before: input.old_string ?? "", after: input.new_string ?? "" }];
  if (tool === "MultiEdit")
    return (input.edits ?? []).map((e) => ({ path: input.file_path, before: e.old_string ?? "", after: e.new_string ?? "" }));
  if (tool === "Write") {
    const abs = input.file_path && (isAbsolute(input.file_path) ? input.file_path : resolve(cwd, input.file_path));
    const exists = abs && existsSync(abs);
    return [{ path: input.file_path, before: exists ? readFileSync(abs, "utf8") : undefined, after: input.content ?? "" }];
  }
  if (tool === "NotebookEdit") return [{ path: input.notebook_path, before: "", after: "" }];
  return [];
}

// ---------------------------------------------------------------- shell commands
export const PROTECTED_IN_TEXT =
  /(^|[\s"'=(/\\])tests[\\/]|\.runsettings\b|xunit\.runner\.json|Directory\.(Build|Packages)\.(props|targets)|global\.json|\.slnx\b|KnownFailures\.txt|tools[\\/]ci[\\/]|Test-Integrity\.ps1|test-integrity-baseline|\.github[\\/]workflows|map-test-guard|settings(\.local)?\.json/i;
const SHELL_WRITE =
  /(\bsed\s+(-[a-zA-Z]*\s+)*-[a-zA-Z]*i|\bperl\s+-[a-z]*i|\btee\b|Tee-Object|Set-Content|Add-Content|Out-File|Clear-Content|Remove-Item|Move-Item|Rename-Item|Copy-Item|New-Item|\b(rm|mv|cp|del|erase|ren|rmdir|rd)\s|\bgit\s+(rm|mv|restore|checkout\s+(\S+\s+)?--)|Write(All)?(Text|Lines|Bytes)|Append(All)?(Text|Lines)|\[IO\.File\]::(Delete|Move|Copy|Replace)|File\.(Delete|Move|Copy|Replace)\s*\(|writeFile|appendFile|unlink|rmSync|renameSync|copyFile|truncate|\bopen\s*\([^)]*['"][wa]\+?['"])/i;
const PATCHERS = /\bgit\s+(apply|am)\b|(^|[\s;&|])patch\s+-|\bgit\s+(stash\s+(pop|apply)|cherry-pick|revert)\b/i;
const REDIRECT = /(?:^|[^\d&>=])>{1,2}\s*["']?([^\s|&;"'<>()]+)/g;

export function shellReasons(command) {
  const reasons = [];
  for (const m of command.matchAll(REDIRECT)) {
    const target = m[1];
    if (/^\$null$|^\/dev\/null$|^nul$/i.test(target)) continue;
    if (PROTECTED_IN_TEXT.test(" " + target)) reasons.push(`команда перезаписывает защищённый файл через перенаправление > (${target})`);
  }
  if (SHELL_WRITE.test(command) && PROTECTED_IN_TEXT.test(command))
    reasons.push("команда оболочки пишет, переносит или удаляет файлы тестов/настроек тестов — так обходят проверку правок");
  if (PATCHERS.test(command))
    reasons.push("команда накладывает патч/чужие изменения (git apply/am/patch/cherry-pick/stash pop) — хук не видит, задеты ли тесты");
  return reasons;
}

// ---------------------------------------------------------------- decision
export function decide(event, root = ROOT) {
  const tool = event.tool_name;
  const input = event.tool_input ?? {};
  const cwd = event.cwd ?? root;
  if (tool === "Bash" || tool === "PowerShell") {
    const reasons = shellReasons(String(input.command ?? ""));
    return reasons.length ? { decision: "ask", reasons } : undefined;
  }
  const reasons = [];
  const notes = [];
  for (const edit of fileEdits(tool, input, cwd)) {
    const rel = repoRelative(edit.path, cwd, root);
    const kind = classify(rel);
    if (!kind) continue;
    if (kind === "self") { reasons.push(`${rel}: это сама защита тестов или настройки Claude`); continue; }
    if (kind === "config") { reasons.push(`${rel}: настройки сборки/запуска тестов, список известных падений или CI — от них зависит, что вообще проверяется`); continue; }
    const result = compare(edit.before ?? "", edit.after, edit.before === undefined);
    for (const r of result.reasons) reasons.push(`${rel}: ${r}`);
    if (result.changedExpectations) notes.push(rel);
  }
  if (reasons.length) return { decision: "ask", reasons };
  if (notes.length)
    return {
      decision: "note",
      note:
        `Защита тестов Map: ты меняешь ожидаемое значение в Assert (${notes.join(", ")}). В отчёте объясни, почему новое значение верно ` +
        "по поведению или спецификации, а не потому что так выдаёт код. Если тест противоречит задаче — сообщи, не подгоняй. " +
        "Ослабление проверки требует `test-change:` с причиной в сообщении коммита (DEC-116).",
    };
  return undefined;
}

export function render(verdict) {
  if (!verdict) return "";
  if (verdict.decision === "ask") {
    const text =
      "Защита тестов Map: агент хочет ослабить проверку или изменить то, что решает, какие тесты считаются.\n- " +
      verdict.reasons.join("\n- ") +
      "\nРазрешайте, только если это и есть задача (например, вы просили переписать тест). Ослабление — с `test-change:` в коммите (DEC-116).";
    return JSON.stringify({ hookSpecificOutput: { hookEventName: "PreToolUse", permissionDecision: "ask", permissionDecisionReason: text } });
  }
  return JSON.stringify({ hookSpecificOutput: { hookEventName: "PreToolUse", additionalContext: verdict.note } });
}

/** Whole hook: raw stdin text → stdout text. Never throws; on its own error it asks if tests may be touched. */
export function run(raw, root = ROOT) {
  try {
    if (process.env.MAP_TEST_GUARD_FORCE_ERROR) throw new Error("forced error (self-test)");
    return render(decide(JSON.parse(raw || "{}"), root));
  } catch (error) {
    if (!PROTECTED_IN_TEXT.test(raw ?? "") && !/tests[\\/]/i.test(raw ?? "")) return "";
    const text = `Защита тестов Map не смогла проверить правку (${error instanceof Error ? error.message : String(error)}), а она касается тестов или их настроек. Проверьте правку сами.`;
    return JSON.stringify({ hookSpecificOutput: { hookEventName: "PreToolUse", permissionDecision: "ask", permissionDecisionReason: text } });
  }
}
