/*
 * One generator, two servers, everything else held still.
 *
 * D-249 excluded four candidates by removing pieces of Graticula. It never removed
 * Graticula. This script is driven twice -- once at the dev server's /rest/info and once
 * at a bare Kestrel answering the same 182 bytes on the same machine over the same
 * protocol -- so the only difference between the two runs is the server.
 *
 * It reuses connections and reports the handshake timings, for the same reason flood.js
 * does: if tls and connect are not ~0 after the warm-up, the run measures handshakes
 * rather than the pipeline, and this repository has already published one measurement
 * that did exactly that.
 */

import http from 'k6/http';
import { Trend, Counter } from 'k6/metrics';

const URL = __ENV.URL;
const VUS = parseInt(__ENV.VUS || '16', 10);
const DURATION = __ENV.DURATION || '20s';
const WARM = __ENV.WARM || '6s';
const LABEL = __ENV.LABEL || 'run';
const OUT = __ENV.OUT || `${LABEL}.json`;

const lat = new Trend('lat', true);
const tlsMs = new Trend('tls_ms', true);
const connectMs = new Trend('connect_ms', true);
const waitMs = new Trend('wait_ms', true);
const ok = new Counter('ok');
const bad = new Counter('bad');

export const options = {
  insecureSkipTLSVerify: true,
  noConnectionReuse: false,
  discardResponseBodies: false,
  scenarios: {
    warm: {
      executor: 'constant-vus',
      vus: VUS,
      duration: WARM,
      startTime: '0s',
      exec: 'warm',
      gracefulStop: '2s',
    },
    load: {
      executor: 'constant-vus',
      vus: VUS,
      duration: DURATION,
      // Two seconds after the warm-up ends, so a connection still being established is
      // never counted against the server.
      startTime: `${parseFloat(WARM) + 2}s`,
      exec: 'measure',
      gracefulStop: '10s',
    },
  },
};

export function warm() {
  http.get(URL, { timeout: '60s' });
}

export function measure() {
  const answer = http.get(URL, { timeout: '60s' });

  lat.add(answer.timings.duration);
  tlsMs.add(answer.timings.tls_handshaking);
  connectMs.add(answer.timings.connecting);
  waitMs.add(answer.timings.waiting);

  if (answer.status === 200) {
    ok.add(1);
  } else {
    bad.add(1);
  }
}

export function handleSummary(data) {
  const m = data.metrics;
  const n = (name, field) => {
    const x = m[name];
    if (!x) return null;
    const v = x.values[field];
    return v === undefined ? null : v;
  };

  const seconds = parseFloat(DURATION);
  const okCount = n('ok', 'count') || 0;
  const badCount = n('bad', 'count') || 0;
  const rps = (okCount + badCount) / seconds;

  const f = (x, d) => (x === null ? '-' : x.toFixed(d === undefined ? 2 : d));

  const report = {
    label: LABEL,
    url: URL,
    vus: VUS,
    seconds,
    ok: okCount,
    bad: badCount,
    reqPerSecond: rps,
    medianMs: n('lat', 'med'),
    p95Ms: n('lat', 'p(95)'),
    // `waiting` is time-to-first-byte: the server's own answer time with the connection
    // already open. If median latency is much larger than this, the cost is not the
    // server thinking.
    waitMedianMs: n('wait_ms', 'med'),
    tlsMedianMs: n('tls_ms', 'med'),
    connectMedianMs: n('connect_ms', 'med'),
  };

  const line =
    `${LABEL.padEnd(18)} vus=${String(VUS).padStart(3)}  ` +
    `${f(rps, 1).padStart(9)} req/s  med ${f(report.medianMs).padStart(7)} ms  ` +
    `ttfb ${f(report.waitMedianMs).padStart(7)} ms  p95 ${f(report.p95Ms).padStart(7)} ms  ` +
    `tls ${f(report.tlsMedianMs, 3)}  connect ${f(report.connectMedianMs, 3)}  ` +
    `bad ${badCount}`;

  const out = {};
  out.stdout = line + '\n';
  out[OUT] = JSON.stringify(report, null, 1);
  return out;
}
