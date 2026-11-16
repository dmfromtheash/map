// Entry point of the Stop hook. If the check itself fails, it blocks ONCE with the error (the second
// stop has stop_hook_active=true and passes), so a broken guard is visible instead of silently allowing.
let raw = "";
process.stdin.setEncoding("utf8");
process.stdin.on("data", (d) => (raw += d));
process.stdin.on("end", async () => {
  let out = "";
  try {
    const stop = await import("./stop-check.mjs");
    out = stop.run(raw);
  } catch (error) {
    if (!/"stop_hook_active"\s*:\s*true/.test(raw)) {
      const reason = `Проверка тестов Map не сработала (${error instanceof Error ? error.message : String(error)}). Если в сессии менялся код в src/ или tests/, скажи владельцу, какие тесты запускались (квитанция) или что не проверено.`;
      out = JSON.stringify({ decision: "block", reason });
    }
  }
  if (out) process.stdout.write(out);
});
