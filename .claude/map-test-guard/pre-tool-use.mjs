// Entry point of the PreToolUse hook. The guard module is loaded dynamically so that even a broken
// guard (syntax error, missing file) still ASKS for edits that touch tests/ or protected files.
let raw = "";
process.stdin.setEncoding("utf8");
process.stdin.on("data", (d) => (raw += d));
process.stdin.on("end", async () => {
  let out = "";
  try {
    const guard = await import("./guard-tests.mjs");
    out = guard.run(raw);
  } catch (error) {
    if (/tests[\\/]|KnownFailures|\.runsettings|Directory\.(Build|Packages)|Test-Integrity|\.github[\\/]workflows|tools[\\/]ci|map-test-guard|settings(\.local)?\.json/i.test(raw)) {
      const text = `Защита тестов Map сломана (${error instanceof Error ? error.message : String(error)}), а правка касается тестов или их настроек. Проверьте правку сами.`;
      out = JSON.stringify({ hookSpecificOutput: { hookEventName: "PreToolUse", permissionDecision: "ask", permissionDecisionReason: text } });
    }
  }
  if (out) process.stdout.write(out);
});
