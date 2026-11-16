// Self-test of the Map test guard (DEC-116 proof). Runs without dotnet and without builds:
//   node .claude/map-test-guard/selftest.mjs
// 1. PreToolUse decisions for synthetic hook inputs (module and real hook process).
// 2. Fail-safe: a broken guard asks for tests/ edits.
// 3. Mutation proof: a copy of the guard with the Assert-count rule removed must fail case 1.
// 4. Stop hook in a temporary git repo: block without receipt, pass with a covering receipt.
// 5. Receipt/baseline logic: command preparation, verdicts on synthetic .trx, real .trx from %TEMP%\map-tr if present.
// Everything temporary is created in the OS temp folder and deleted at the end.
import { spawnSync } from "node:child_process";
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import { GUARD_DIR, ROOT, contentHash, receiptsDir } from "./lib.mjs";
import { decide } from "./guard-tests.mjs";

const results = [];
function expect(name, actual, wanted) {
  const ok = actual === wanted;
  results.push(ok);
  console.log(`${ok ? "OK  " : "FAIL"} ${name}: ${actual}${ok ? "" : ` (ожидалось ${wanted})`}`);
}
const kind = (v) => v?.decision ?? "allow";
const T = (p) => join(ROOT, p);
const edit = (path, before, after) => ({ tool_name: "Edit", cwd: ROOT, tool_input: { file_path: T(path), old_string: before, new_string: after } });
const shell = (tool, command) => ({ tool_name: tool, cwd: ROOT, tool_input: { command } });

const tmp = mkdtempSync(join(tmpdir(), "map-test-guard-selftest-"));
process.env.MAP_TEST_GUARD_HOME = join(tmp, "home");
try {
  // ------------------------------------------------------------ 1. PreToolUse decisions
  console.log("== PreToolUse: правки файлов");
  const test = "tests/Map.Core.Tests/SampleTests.cs";
  expect("убран Assert", kind(decide(edit(test, "Assert.Equal(3, a);\n        Assert.True(b);", "Assert.Equal(3, a);"))), "ask");
  expect("добавлен Skip", kind(decide(edit(test, "[Fact]\npublic void A()", '[Fact(Skip = "flaky")]\npublic void A()'))), "ask");
  expect("поднят порог в Assert", kind(decide(edit(test, "Assert.True(frameMs < 16.7);", "Assert.True(frameMs < 33.4);"))), "ask");
  expect("поднят бюджет", kind(decide(edit(test, "var budget = TimeSpan.FromMilliseconds(50);", "var budget = TimeSpan.FromMilliseconds(500);"))), "ask");
  expect("правка tests/KnownFailures.txt", kind(decide(edit("tests/KnownFailures.txt", "# x", "# x\nMap.Core.Tests.A.B"))), "ask");
  expect("безобидная правка src/", kind(decide(edit("src/Map.Core/Document.cs", "int a = 1;", "int a = 2;"))), "allow");
  expect("убран [Fact] метод", kind(decide(edit(test, "[Fact]\npublic void A() { Assert.True(x); }\n[Fact]\npublic void B() { Assert.True(y); }", "[Fact]\npublic void A() { Assert.True(x); }"))), "ask");
  expect("убран InlineData", kind(decide(edit(test, "[InlineData(1)]\n[InlineData(2)]", "[InlineData(1)]"))), "ask");
  expect("условный атрибут [EnvironmentFact]", kind(decide(edit(test, "[Fact]", "[EnvironmentFact]"))), "ask");
  expect("ранний return по файлу", kind(decide(edit(test, "var p = Path();", "var p = Path();\n        if (!File.Exists(p)) return;"))), "ask");
  expect("цикл повторов", kind(decide(edit(test, "Run();", "for (var attempt = 0; attempt < 3; attempt++) Run();"))), "ask");
  expect("перенос в [Trait]/[Collection]", kind(decide(edit(test, "[Fact]", '[Trait("Category", "Slow")]\n[Fact]'))), "ask");
  expect("новая проверка добавлена", kind(decide(edit(test, "Assert.True(a);", "Assert.True(a);\n        Assert.Equal(2, b);"))), "allow");
  expect("сменено ожидаемое значение (строка)", kind(decide(edit(test, 'Assert.Equal("old", name);', 'Assert.Equal("new", name);'))), "note");
  expect("порог ужесточён (число меньше)", kind(decide(edit(test, "Assert.True(ms < 33);", "Assert.True(ms < 16);"))), "note");
  expect("RetryClock в коде продукта — не повтор", kind(decide(edit(test, "var c = new FakeClock();", "var c = new RetryClock();"))), "allow");
  expect("Write нового теста без пропусков", kind(decide({ tool_name: "Write", cwd: ROOT, tool_input: { file_path: T("tests/Map.Core.Tests/BrandNewSelfTest.cs"), content: "[Fact]\npublic void A() { Assert.True(true); }" } })), "allow");
  expect("Write нового теста со Skip", kind(decide({ tool_name: "Write", cwd: ROOT, tool_input: { file_path: T("tests/Map.Core.Tests/BrandNewSelfTest.cs"), content: '[Fact(Skip = "later")]\npublic void A() {}' } })), "ask");
  expect("MultiEdit: одна из правок убирает Assert", kind(decide({ tool_name: "MultiEdit", cwd: ROOT, tool_input: { file_path: T(test), edits: [{ old_string: "a", new_string: "b" }, { old_string: "Assert.True(x);", new_string: "" }] } })), "ask");
  expect("тест в worktree .worktrees/X", kind(decide(edit(".worktrees/X/tests/Map.Core.Tests/A.cs", "Assert.True(x);", ""))), "ask");
  expect("tools/ci/Invoke-TestProject.ps1", kind(decide(edit("tools/ci/Invoke-TestProject.ps1", "a", "b"))), "ask");
  expect("tools/ai/Test-Integrity.ps1", kind(decide(edit("tools/ai/Test-Integrity.ps1", "a", "b"))), "ask");
  expect("tools/ai/test-integrity-baseline.txt", kind(decide(edit("tools/ai/test-integrity-baseline.txt", "a", "b"))), "ask");
  expect("Directory.Build.props", kind(decide(edit("Directory.Build.props", "a", "b"))), "ask");
  expect("тестовый csproj", kind(decide(edit("tests/Map.Core.Tests/Map.Core.Tests.csproj", "a", "b"))), "ask");
  expect(".github/workflows/build.yml", kind(decide(edit(".github/workflows/build.yml", "a", "b"))), "ask");
  expect(".claude/settings.local.json", kind(decide(edit(".claude/settings.local.json", "a", "b"))), "ask");
  expect("сама защита", kind(decide(edit(".claude/map-test-guard/guard-tests.mjs", "a", "b"))), "ask");
  expect("src/*.csproj (не тестовый)", kind(decide(edit("src/Map.Core/Map.Core.csproj", "a", "b"))), "allow");
  expect("docs/", kind(decide(edit("docs/STATE.md", "a", "b"))), "allow");

  console.log("== PreToolUse: команды оболочки");
  expect("sed -i в tests/", kind(decide(shell("Bash", "sed -i 's/Assert.True/\\/\\//' tests/Map.Core.Tests/A.cs"))), "ask");
  expect("Set-Content в tests\\", kind(decide(shell("PowerShell", "Set-Content -Path tests\\Map.Core.Tests\\A.cs -Value ''"))), "ask");
  expect("Out-File в tests/", kind(decide(shell("PowerShell", "'x' | Out-File tests/Map.Core.Tests/A.cs"))), "ask");
  expect("перенаправление > в tests/", kind(decide(shell("Bash", "echo '' > tests/Map.Core.Tests/A.cs"))), "ask");
  expect("перенаправление >> в KnownFailures", kind(decide(shell("Bash", "echo Map.X.Y >> tests/KnownFailures.txt"))), "ask");
  expect("Remove-Item теста", kind(decide(shell("PowerShell", "Remove-Item tests/Map.Core.Tests/A.cs"))), "ask");
  expect("git checkout -- tests", kind(decide(shell("Bash", "git checkout HEAD -- tests/Map.Core.Tests/A.cs"))), "ask");
  expect("git apply (пути не видны)", kind(decide(shell("Bash", "git apply fix.patch"))), "ask");
  expect("[IO.File]::WriteAllText в tests", kind(decide(shell("PowerShell", "[IO.File]::WriteAllText('tests/Map.Core.Tests/A.cs', '')"))), "ask");
  expect("dotnet test tests/... 2>&1", kind(decide(shell("PowerShell", "dotnet test tests/Map.Core.Tests/Map.Core.Tests.csproj -c Release 2>&1 | Select-Object -Last 5"))), "allow");
  expect("grep по tests/", kind(decide(shell("Bash", "grep -rn 'Assert.' tests/ | head"))), "allow");
  expect("git diff -- tests > файл в scratch", kind(decide(shell("Bash", "git diff -- tests > C:/Users/x/scratch/d.txt"))), "allow");
  expect("Get-Content теста", kind(decide(shell("PowerShell", "Get-Content tests/Map.Core.Tests/A.cs -TotalCount 20"))), "allow");
  expect("запись в src/ через Set-Content", kind(decide(shell("PowerShell", "Set-Content src/Map.Core/A.cs 'x'"))), "allow");

  // ------------------------------------------------------------ 2. real hook process + fail-safe
  console.log("== Настоящий процесс хука (stdin → stdout) и отказоустойчивость");
  const hook = (input, env = {}) =>
    spawnSync(process.execPath, [join(GUARD_DIR, "pre-tool-use.mjs")], { input, encoding: "utf8", env: { ...process.env, ...env } }).stdout;
  const parsed = (out) => (out ? JSON.parse(out).hookSpecificOutput?.permissionDecision ?? "context" : "allow");
  const removeAssert = JSON.stringify(edit(test, "Assert.True(x);", ""));
  expect("процесс: убран Assert → ask", parsed(hook(removeAssert)), "ask");
  expect("процесс: src → ничего", parsed(hook(JSON.stringify(edit("src/Map.Core/A.cs", "a", "b")))), "allow");
  expect("сбой защиты + tests/ → ask", parsed(hook(JSON.stringify(edit(test, "a", "b")), { MAP_TEST_GUARD_FORCE_ERROR: "1" })), "ask");
  expect("сбой защиты + src/ → ничего", parsed(hook(JSON.stringify(edit("src/Map.Core/A.cs", "a", "b")), { MAP_TEST_GUARD_FORCE_ERROR: "1" })), "allow");
  expect("битый JSON с tests/ → ask", parsed(hook('{"tool_name":"Edit","tool_input":{"file_path":"tests/A.cs"')), "ask");

  // ------------------------------------------------------------ 3. mutation proof
  console.log("== Доказательство «сломал защиту → тест красный»");
  const mutant = join(tmp, "mutant");
  mkdirSync(mutant);
  for (const f of ["lib.mjs", "guard-tests.mjs"]) cpSync(join(GUARD_DIR, f), join(mutant, f));
  const src = readFileSync(join(mutant, "guard-tests.mjs"), "utf8");
  const broken = src.replace(/^\s*fewer\(ASSERT,.*$/m, "");
  expect("мутация применена", broken !== src, true);
  writeFileSync(join(mutant, "guard-tests.mjs"), broken);
  const m = await import(pathToFileURL(join(mutant, "guard-tests.mjs")).href);
  expect("мутант пропускает удаление Assert (значит, случай 1 ловит поломку)", kind(m.decide(edit(test, "Assert.True(x);\nAssert.True(y);", "Assert.True(x);"))), "allow");

  // ------------------------------------------------------------ 4. Stop hook in a temp repo
  console.log("== Stop: временный git-репозиторий");
  const { check } = await import("./stop-check.mjs");
  const repo = join(tmp, "repo");
  mkdirSync(join(repo, "src"), { recursive: true });
  mkdirSync(join(repo, "docs"));
  writeFileSync(join(repo, "src", "A.cs"), "class A {}\n");
  writeFileSync(join(repo, "docs", "n.md"), "x\n");
  const g = (...a) => spawnSync("git", a, { cwd: repo, encoding: "utf8", env: { ...process.env, GIT_AUTHOR_DATE: "2026-01-01T00:00:00Z", GIT_COMMITTER_DATE: "2026-01-01T00:00:00Z" } });
  g("init", "-q"); g("add", "."); g("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-qm", "init");
  const transcript = join(tmp, "transcript.jsonl");
  writeFileSync(transcript, "{}\n");
  const until = Date.now() + 60; while (Date.now() < until) {}
  const ev = { cwd: repo, transcript_path: transcript, stop_hook_active: false };
  expect("нет изменений → пропускает", kind(check(ev, repo)), "allow");
  writeFileSync(join(repo, "docs", "n.md"), "y\n");
  expect("правка только docs → пропускает", kind(check(ev, repo)), "allow");
  writeFileSync(join(repo, "src", "A.cs"), "class A { int x; }\n");
  expect("правка src без квитанции → block", kind(check(ev, repo)), "block");
  expect("второй стоп (stop_hook_active) → пропускает", kind(check({ ...ev, stop_hook_active: true }, repo)), "allow");
  const dir = receiptsDir(repo);
  mkdirSync(dir, { recursive: true });
  const fake = (name, extra) => writeFileSync(join(dir, name), JSON.stringify({ tool: "map-test-guard", ran: true, contentChangedDuringRun: false, reports: [{ retry: false }], counts: { total: 3 }, git: { contentHash: contentHash(repo) }, ...extra }));
  fake("2026-01-01-a.json", { counts: { total: 0 } });
  expect("квитанция с 0 тестов не считается → block", kind(check(ev, repo)), "block");
  fake("2026-01-01-b.json", { ran: false });
  expect("квитанция только разбора (без прогона) не считается → block", kind(check(ev, repo)), "block");
  fake("2026-01-01-c.json", {});
  expect("квитанция на текущее содержимое → пропускает", kind(check(ev, repo)), "allow");
  g("add", "."); g("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-qm", "c2");
  expect("после коммита того же содержимого → всё ещё покрыто", kind(check(ev, repo)), "allow");
  writeFileSync(join(repo, "src", "A.cs"), "class A { int y; }\n");
  expect("новая правка после квитанции → block", kind(check(ev, repo)), "block");
  expect("cwd вне репозитория-владельца → пропускает", kind(check({ ...ev, cwd: tmpdir() }, repo)), "allow");

  // ------------------------------------------------------------ 5. receipt / baseline logic
  console.log("== Квитанция и база");
  const { prepareCommand, summarize, verdictOf, findTrx } = await import("./receipt.mjs");
  const { compareRuns } = await import("./baseline.mjs");
  const R = "C:\\r";
  expect("dotnet test: добавлены logger и папка", prepareCommand(["dotnet", "test", "tests/X/X.csproj", "-c", "Release"], R), "dotnet test tests/X/X.csproj -c Release --logger trx --results-directory C:\\r");
  expect("dotnet test: настройки после -- остаются в конце", prepareCommand(["dotnet", "test", "x.csproj", "--", "xUnit.ParallelizeTestCollections=false"], R), "dotnet test x.csproj --logger trx --results-directory C:\\r -- xUnit.ParallelizeTestCollections=false");
  expect("Invoke-TestProject: добавлен -ResultsDir", prepareCommand(["powershell", "-File", "tools/ci/Invoke-TestProject.ps1", "-Project", "Map.Core.Tests"], R), "powershell -File tools/ci/Invoke-TestProject.ps1 -Project Map.Core.Tests -ResultsDir C:\\r");
  let threw = false;
  try { prepareCommand(["npm", "test"], R); } catch { threw = true; }
  expect("неизвестная команда без {results} → ошибка", threw, true);

  const trxDir = join(tmp, "trx");
  mkdirSync(join(trxDir, "retry"), { recursive: true });
  const trx = (results) => `<?xml version="1.0"?><TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><TestDefinitions>${results.map((r, i) => `<UnitTest name="${r[0]}" id="id${i}"><TestMethod className="N.C" name="${r[0].replace(/\(.*$/, "")}" /></UnitTest>`).join("")}</TestDefinitions><Results>${results.map((r, i) => `<UnitTestResult testId="id${i}" testName="N.C.${r[0]}" outcome="${r[1]}"${r[2] ? `><Output><ErrorInfo><Message>${r[2]}</Message></ErrorInfo></Output></UnitTestResult>` : " />"}`).join("")}</Results></TestRun>`;
  writeFileSync(join(trxDir, "zero.trx"), trx([]));
  const zero = summarize(findTrx([join(trxDir, "zero.trx")]));
  expect("отчёт с 0 тестов → zero-tests", verdictOf(zero, 0, true), "zero-tests");
  expect("нет отчёта → no-report", verdictOf(summarize([]), 0, true), "no-report");
  writeFileSync(join(trxDir, "run.trx"), trx([["A", "Passed"], ["B(x: 1)", "Failed"], ["C", "NotExecuted", "defect: slow"], ["D", "Timeout"]]));
  writeFileSync(join(trxDir, "retry", "run.trx"), trx([["B(x: 1)", "Passed"]]));
  const run = summarize(findTrx([trxDir]));
  expect("папка: retry не добавляет тестов (всего)", run.counts.total, 4 + 0);
  expect("Timeout считается не «прошёл»", run.failures.length, 2);
  expect("прошёл при повторе — отмечен", run.passedOnRetry.join(","), "N.C.B");
  expect("причина пропуска прочитана", run.skipped[0]?.reason, "defect: slow");
  expect("падения → failed", verdictOf(run, 0, true), "failed");
  writeFileSync(join(trxDir, "ok.trx"), trx([["A", "Passed"]]));
  const ok = summarize(findTrx([join(trxDir, "ok.trx")]));
  expect("всё прошло, exit 0 → passed", verdictOf(ok, 0, true), "passed");
  expect("всё прошло, exit 1 → exit-nonzero", verdictOf(ok, 1, true), "exit-nonzero");
  expect("всё прошло, но без прогона → parsed-only", verdictOf(ok, null, false), "parsed-only");
  const cmp = compareRuns({ failures: ["a", "b"], reports: [{ retry: false }], counts: { total: 5 } }, { failures: ["b", "c"], reports: [{ retry: false }], counts: { total: 5 } });
  expect("база: новые/старые/исправлено", `${cmp.regressions}|${cmp.preexisting}|${cmp.fixed}|${cmp.reliable}`, "c|b|a|true");
  const cmp0 = compareRuns({ failures: [], reports: [], counts: { total: 0 } }, { failures: ["c"], reports: [{ retry: false }], counts: { total: 5 } });
  expect("база без отчёта → недостоверно", cmp0.reliable, false);

  console.log("== История нестабильных тестов (синтетика)");
  const { analyse } = await import("./test-history.mjs");
  const ob = (test, outcome, state, source, time, retry = false) => ({ test, outcome, state, source, time, retry });
  const hist = analyse([
    ob("R", "fail", "s1", "run1", "1"), ob("R", "pass", "s1", "run1", "1", true),
    ob("F", "pass", "s1", "run1", "1"), ob("F", "fail", "s2", "run2", "2"), ob("F", "pass", "s3", "run3", "3"),
    ob("M", "fail", "s1", "run1", "1"), ob("M", "pass", "s1", "run4", "4"),
    ob("S", "pass", "s1", "run1", "1"), ob("S", "fail", "s2", "run2", "2"), ob("S", "fail", "s3", "run3", "3"),
  ]);
  const h = (t) => hist.find((x) => x.test === t);
  expect("история: прошёл только при повторе", h("R")?.kinds.some((k) => k.startsWith("прошёл только при повторе")), true);
  expect("история: +−+ по прогонам — нестабилен", h("F")?.flaky, true);
  expect("история: один коммит, разные исходы — нестабилен", h("M")?.kinds.some((k) => k.startsWith("одно состояние")), true);
  expect("история: сломался и падает дальше — не «нестабилен»", h("S")?.flaky, false);

  const real = join(process.env.TEMP ?? tmpdir(), "map-tr", "Map.Presentation.Tests.trx");
  if (existsSync(real)) {
    const { readTrx } = await import("./trx.mjs");
    const r = readTrx(real);
    expect(`настоящий .trx: разбор совпадает с Counters (${r.counters.total}/${r.counters.passed}/${r.counters.failed})`, r.countersMismatch, false);
  } else console.log("SKIP настоящий .trx не найден — разбор настоящего отчёта не проверен");
} finally {
  rmSync(tmp, { recursive: true, force: true });
}
const failed = results.filter((x) => !x).length;
console.log(`\nИТОГ: проверок ${results.length}, провалено ${failed}`);
process.exitCode = failed ? 1 : 0;
