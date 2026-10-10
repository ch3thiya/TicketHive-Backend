// End-of-test report. k6 calls handleSummary(data) once with every metric;
// this turns it into:
//   - a short console report (what the demo audience sees)
//   - results/<run>.md   for the GitHub Actions job summary
//   - results/<run>.json the raw k6 data, kept as evidence
//
// `invariants` are correctness rules a test checks after the load (for
// example "no oversell"). They are written to results/<run>-verdict.json,
// which run.sh reads to fail the run when one is broken, because k6
// thresholds can only judge individual metrics, not a rule that combines them.

const fmtMs = (v) => (v === undefined ? '-' : `${v.toFixed(1)} ms`);
const fmtPct = (v) => (v === undefined ? '-' : `${(v * 100).toFixed(2)} %`);

function endpointRows(data) {
  return Object.entries(data.metrics)
    .filter(([name]) => name.startsWith('http_req_duration{name:'))
    .map(([name, metric]) => {
      const endpoint = name.slice('http_req_duration{name:'.length, -1);
      const failed = data.metrics[`http_req_failed{name:${endpoint}}`];
      return {
        endpoint,
        count: metric.values.count,
        avg: metric.values.avg,
        p95: metric.values['p(95)'],
        p99: metric.values['p(99)'],
        max: metric.values.max,
        errorRate: failed?.values.rate,
      };
    });
}

function thresholdRows(data) {
  const rows = [];
  for (const [metric, m] of Object.entries(data.metrics)) {
    for (const [rule, result] of Object.entries(m.thresholds || {})) {
      rows.push({ metric, rule, ok: result.ok });
    }
  }
  return rows;
}

export function buildSummary(data, { testName, invariants = [] }) {
  const profile = __ENV.PROFILE || 'smoke';
  const target = __ENV.TARGET || 'gateway';
  const prefix = __ENV.RESULTS_PREFIX || `../results/${testName}-${profile}`;
  const endpoints = endpointRows(data);
  const thresholds = thresholdRows(data);
  const checks = data.metrics.checks?.values;
  const maxVus = data.metrics.vus_max?.values.max;
  const rps = data.metrics.http_reqs?.values.rate;
  const verdict = {
    test: testName,
    profile,
    thresholdsPassed: thresholds.every((t) => t.ok),
    invariants,
    passed: thresholds.every((t) => t.ok) && invariants.every((i) => i.ok),
  };

  const line = (ok) => (ok ? 'PASS' : 'FAIL');
  const text = [
    '',
    `=== ${testName} | profile: ${profile} | via: ${target} | peak VUs: ${maxVus ?? '-'} | ${rps?.toFixed(1) ?? '-'} req/s ===`,
    '',
    'Endpoint                count      avg       p95       p99   errors',
    ...endpoints.map(
      (e) =>
        `${e.endpoint.padEnd(20)} ${String(e.count).padStart(8)} ${fmtMs(e.avg).padStart(9)} ${fmtMs(e.p95).padStart(9)} ${fmtMs(e.p99).padStart(9)} ${fmtPct(e.errorRate).padStart(8)}`,
    ),
    '',
    `Checks passed: ${checks ? fmtPct(checks.rate) : '-'}`,
    '',
    'Thresholds:',
    ...thresholds.map((t) => `  ${line(t.ok)}  ${t.metric}  ${t.rule}`),
    ...(invariants.length ? ['', 'Invariants:', ...invariants.map((i) => `  ${line(i.ok)}  ${i.name}: ${i.detail}`)] : []),
    '',
    `RESULT: ${line(verdict.passed)}`,
    '',
  ].join('\n');

  const markdown = [
    `### Load test: ${testName} (${profile}, via ${target}) — ${line(verdict.passed)}`,
    '',
    `Peak VUs: ${maxVus ?? '-'} · Throughput: ${rps?.toFixed(1) ?? '-'} req/s · Checks passed: ${checks ? fmtPct(checks.rate) : '-'}`,
    '',
    '| Endpoint | Requests | Avg | p95 | p99 | Errors |',
    '|---|---:|---:|---:|---:|---:|',
    ...endpoints.map((e) => `| ${e.endpoint} | ${e.count} | ${fmtMs(e.avg)} | ${fmtMs(e.p95)} | ${fmtMs(e.p99)} | ${fmtPct(e.errorRate)} |`),
    '',
    '| Threshold | Rule | Result |',
    '|---|---|---|',
    ...thresholds.map((t) => `| ${t.metric} | \`${t.rule}\` | ${line(t.ok)} |`),
    ...invariants.map((i) => `| ${i.name} | ${i.detail} | ${line(i.ok)} |`),
    '',
  ].join('\n');

  return {
    stdout: text,
    [`${prefix}.md`]: markdown,
    [`${prefix}.json`]: JSON.stringify(data, null, 2),
    [`${prefix}-verdict.json`]: JSON.stringify(verdict, null, 2),
  };
}
