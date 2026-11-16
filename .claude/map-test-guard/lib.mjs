// Shared helpers of the Map test guard (.claude/map-test-guard). No npm dependencies.
// Paths: the guard lives in <repo>/.claude/map-test-guard; worktrees of the repo live in
// <repo>/.worktrees/<name> and are classified as their own repo root.
import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { existsSync, readFileSync } from "node:fs";
import { isAbsolute, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

export const GUARD_DIR = fileURLToPath(new URL(".", import.meta.url));
export const ROOT = resolve(GUARD_DIR, "..", "..");

const norm = (p) => resolve(p).split("\\").join("/").replace(/\/+$/, "");

/** Repo root that owns `abs` (ROOT or ROOT/.worktrees/<name>), or undefined when outside. */
export function ownerRoot(abs, root = ROOT) {
  const a = norm(abs), r = norm(root);
  const al = a.toLowerCase(), rl = r.toLowerCase();
  if (al !== rl && !al.startsWith(rl + "/")) return undefined;
  const rest = a.slice(r.length + 1).split("/");
  if (rest[0].toLowerCase() === ".worktrees" && rest[1]) return `${r}/.worktrees/${rest[1]}`;
  return r;
}

/** Path relative to its repo root (forward slashes), or undefined when outside the repo. */
export function repoRelative(path, cwd = ROOT, root = ROOT) {
  if (!path) return undefined;
  const abs = norm(isAbsolute(path) ? path : resolve(cwd, path));
  const owner = ownerRoot(abs, root);
  if (!owner) return undefined;
  return abs.slice(owner.length + 1);
}

export function git(cwd, args, input) {
  const r = spawnSync("git", args, { cwd, encoding: "utf8", input, maxBuffer: 256 * 1024 * 1024 });
  return r.status === 0 ? r.stdout : undefined;
}

/** Receipts are kept outside the repo, one folder per repo root (main checkout or worktree). */
export function receiptsDir(repoRoot) {
  const key = createHash("sha256").update(norm(repoRoot).toLowerCase()).digest("hex").slice(0, 12);
  const base = process.env.MAP_TEST_GUARD_HOME ?? join(process.env.LOCALAPPDATA ?? join(ROOT, ".."), "map-test-guard");
  const name = norm(repoRoot).split("/").pop().replace(/[^\w.-]/g, "_");
  return join(base, "receipts", `${name}-${key}`);
}

/** Files that count as code for the Stop hook and the content hash. */
export const CODE = /^(src|tests)\/(?!.*\/(bin|obj)\/).+(?<!\.md)$/i;

/**
 * Content hash of every tracked or untracked (not ignored) file under src/ and tests/.
 * It does not change when the same content is committed, so a receipt taken before a commit
 * still covers the committed tree.
 */
export function contentHash(repoRoot) {
  const list = git(repoRoot, ["ls-files", "--cached", "--others", "--exclude-standard", "-z", "--", "src", "tests"]);
  if (list === undefined) return undefined;
  const files = [...new Set(list.split("\0").filter(Boolean))].filter((p) => CODE.test(p) && existsSync(join(repoRoot, p))).sort();
  if (files.length === 0) return createHash("sha256").update("empty").digest("hex");
  const ids = git(repoRoot, ["hash-object", "--stdin-paths"], files.join("\n") + "\n");
  if (ids === undefined) return undefined;
  const blobs = ids.trim().split(/\r?\n/);
  const h = createHash("sha256");
  files.forEach((f, i) => h.update(`${f}\0${blobs[i]}\n`));
  return h.digest("hex");
}

/** tests/KnownFailures.txt in the same format Invoke-TestProject.ps1 reads. */
export function readKnownFailures(repoRoot) {
  const file = join(repoRoot, "tests", "KnownFailures.txt");
  if (!existsSync(file)) return [];
  return readFileSync(file, "utf8").replace(/^\uFEFF/, "").split(/\r?\n/).map((l) => l.replace(/#.*$/, "").trim()).filter(Boolean);
}

export function isKnown(name, known) {
  return known.some((p) => (p.endsWith("*") ? name.startsWith(p.slice(0, -1)) : name === p));
}
