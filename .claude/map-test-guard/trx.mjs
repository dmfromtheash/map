// Minimal .trx reader (VSTest TRX written by `dotnet test --logger trx`). No XML package:
// TRX is machine-written by XmlWriter, attribute values have <, >, &, " escaped.
import { readFileSync } from "node:fs";

const ENTITY = { amp: "&", lt: "<", gt: ">", quot: '"', apos: "'" };
const decode = (s) => s.replace(/&(#x[0-9a-f]+|#\d+|\w+);/gi, (m, e) =>
  e[0] === "#" ? String.fromCodePoint(e[1].toLowerCase() === "x" ? parseInt(e.slice(2), 16) : Number(e.slice(1))) : (ENTITY[e] ?? m));

function attrs(tagText) {
  const out = {};
  for (const m of tagText.matchAll(/([\w:.-]+)="([^"]*)"/g)) out[m[1]] = decode(m[2]);
  return out;
}

/**
 * Counts every UnitTestResult by outcome. `skipped` = NotExecuted; anything not Passed/Failed/NotExecuted
 * (Error, Timeout, Aborted, Inconclusive, ...) is `other` and is listed with the failures — it is not a pass.
 */
export function parseTrx(text) {
  const methods = new Map();
  for (const m of text.matchAll(/<UnitTest\b([^>]*)>([\s\S]*?)<\/UnitTest>/g)) {
    const def = attrs(m[1]);
    const tm = m[2].match(/<TestMethod\b([^>]*)\/?>/);
    if (tm) { const a = attrs(tm[1]); methods.set(def.id, `${a.className}.${a.name}`); }
  }
  const counts = { total: 0, passed: 0, failed: 0, skipped: 0, other: 0 };
  const failures = [];
  const skipped = [];
  const passed = [];
  for (const m of text.matchAll(/<UnitTestResult\b([^>]*?)(?:\/>|>([\s\S]*?)<\/UnitTestResult>)/g)) {
    const r = attrs(m[1]);
    counts.total++;
    const entry = { name: r.testName, method: methods.get(r.testId) ?? (r.testName ?? "").replace(/\(.*$/, ""), outcome: r.outcome };
    if (r.outcome === "Passed") { counts.passed++; passed.push(entry); }
    else if (r.outcome === "Failed") { counts.failed++; failures.push(entry); }
    else if (r.outcome === "NotExecuted") {
      counts.skipped++;
      const msg = m[2]?.match(/<Message>([\s\S]*?)<\/Message>/)?.[1];
      skipped.push({ ...entry, reason: msg ? decode(msg).trim() : undefined });
    }
    else { counts.other++; failures.push(entry); }
  }
  const c = text.match(/<Counters\b([^>]*)\/?>/);
  const counters = c ? Object.fromEntries(Object.entries(attrs(c[1])).map(([k, v]) => [k, Number(v)])) : undefined;
  const summary = text.match(/<ResultSummary\b[^>]*\boutcome="([^"]*)"/)?.[1];
  const mismatch = counters && (counters.total !== counts.total || counters.passed !== counts.passed || counters.failed !== counts.failed);
  const started = text.match(/<Times\b[^>]*\bstart="([^"]*)"/)?.[1];
  return { counts, failures, skipped, passed, started, counters, summaryOutcome: summary, countersMismatch: Boolean(mismatch) };
}

export function readTrx(file) {
  return { file, ...parseTrx(readFileSync(file, "utf8").replace(/^﻿/, "")) };
}
