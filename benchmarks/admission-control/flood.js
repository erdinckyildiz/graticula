/*
 * ADR-046 condition 1, re-measured with a generator that is not 480 Python threads.
 *
 * WHAT THIS MEASURES, AND AGAINST WHAT
 * ------------------------------------
 * ADR-046 ("admission control bounds the queue, not the wait") refuses a request on
 * arrival when more than `PerSourceConcurrency x QueueWaitersPerPermit` callers are
 * already waiting for one data source's permits — 24 x 4 = 96 on the defaults. Its
 * condition 1 asks two things of a measurement:
 *
 *   1. a saturated source refuses a meaningful share at concurrency 240; and
 *   2. the admitted requests' median stops growing with concurrency.
 *
 * The 2026-08-23 run discharged only the first, and only on a cold server. Warm, at
 * concurrency 480, nothing was refused and the median kept growing — and the reason
 * could not be established, because the generator was 480 Python threads doing a TLS
 * handshake per request on the same machine as the server. Two explanations survived
 * that run and have different repairs:
 *
 *   (a) the generator could not keep 480 requests in flight, so the queue was reached
 *       in bursts rather than held — the counter was empty in 22 of 24 samples; or
 *   (b) part of the latency is queued upstream of admission control, in Kestrel or the
 *       thread pool, where nothing in ConnectionBudget can reach it.
 *
 * D-145 records that gap. Q-140 is the owner's question about it, answered 2026-09-09
 * by choosing a better generator rather than a second host. This script is that
 * generator, and it is written to separate (a) from (b) rather than to produce a
 * throughput number:
 *
 *   * it reuses connections, so `tls_ms` and `connect_ms` below are the evidence that a
 *     handshake is not being paid per request — the cost the Python probe could not
 *     avoid. If those are not ~0 after warm-up, this run is as untrustworthy as that one;
 *   * it is Go rather than Python, so 480 concurrent callers are 480 goroutines and not
 *     480 OS threads contending for one interpreter lock;
 *   * it samples the server's own queue counter throughout the measured window
 *     (`/admin/health`, `admissionControl.waitingForSource`), which is how (a) is
 *     answered: a queue held at its bound refutes it, a queue found empty confirms it;
 *   * it drives a control path, `/rest/info`, which passes through Kestrel and the whole
 *     request pipeline but takes no data-source permit. If the control's latency grows
 *     with the flood, the growth is upstream of admission control, which is (b).
 *
 * It still runs on the machine under test. That cost is not removed by this script and
 * Q-140 does not claim it is: what is removed is the GIL and the per-request handshake.
 *
 * WHY BOTH A CLOSED AND AN OPEN LOOP
 * ----------------------------------
 * `MODE=vus` is closed-loop — N callers, each with one request outstanding — which is
 * what the Python probe did and what makes this comparable to the ADR's tables. It has
 * one property that must be read carefully: a refusal costs almost nothing, so a refused
 * caller comes straight back, and the refused *share* is therefore much higher than the
 * share of offered work that could not be served. It reports what a client experiences.
 *
 * `MODE=rate` is open-loop — R requests per second offered regardless of what the server
 * does — which is what an arrival process actually looks like and what Little's law is
 * about. It reports what a deployment sheds. Neither is the truth on its own.
 *
 * USAGE
 * -----
 *   k6 run -e URL=https://127.0.0.1:8459 -e USER=ci -e PASSWORD=... \
 *          -e LAYER=hosted/ci_many -e MODE=vus -e VUS=240 -e DURATION=60s \
 *          -e LABEL=vus-240 -e OUT=measured-vus-240.json flood.js
 *
 *   k6 run ... -e MODE=rate -e RATE=2000 -e DURATION=60s -e LABEL=rate-2000 flood.js
 *
 * Every run warms for WARM (default 10s) at the same level before the measured window
 * opens, and nothing in the warm-up is counted: the connections, the thread pool and the
 * server's per-layer shape cache are all cold on the first pass, and measuring a cold
 * pool is how this repository produced five wrong harnesses (D-30).
 */

import http from 'k6/http';
import exec from 'k6/execution';
import { Counter, Rate, Trend } from 'k6/metrics';

const URL = (__ENV.URL || 'https://127.0.0.1:8459').replace(/\/+$/, '');
const USER = __ENV.USER || 'ci';
const PASSWORD = __ENV.PASSWORD || '';
const LAYER = __ENV.LAYER || 'hosted/ci_many';
const ROWS = __ENV.ROWS || '200';
const MODE = __ENV.MODE || 'vus';
const VUS = parseInt(__ENV.VUS || '240', 10);
const RATE = parseInt(__ENV.RATE || '1000', 10);
const DURATION = __ENV.DURATION || '60s';
const WARM = __ENV.WARM || '10s';
const LABEL = __ENV.LABEL || `${MODE}-${MODE === 'vus' ? VUS : RATE}`;
const OUT = __ENV.OUT || `measured-${LABEL}.json`;
const SAMPLE_HZ = parseInt(__ENV.SAMPLE_HZ || '10', 10);

// Seconds per reporting window. The whole point of the run is whether shedding is a
// property of the first moments or of the load, so the measured window is cut into
// buckets and each one's refusal share is reported separately.
const WINDOW = parseInt(__ENV.WINDOW || '15', 10);
const WINDOWS = 24;

const seconds = (s) => (s.endsWith('ms') ? parseFloat(s) / 1000 : parseFloat(s));
const WARM_S = seconds(WARM);
const RUN_S = seconds(DURATION);

// The measured window starts after the warm-up plus a two-second gap, so that a
// connection still being established is never counted against the server.
const MEASURE_AT = WARM_S + 2;

// TARGET replaces the query outright. It exists for one measurement: flooding a path
// that takes no data-source permit — `/rest/info` — says what the request pipeline can
// do with the data source removed from the picture. Without that number, "the queue is
// upstream of admission control" is an inference from a control sampled at 4 req/s
// rather than a ceiling anybody has measured.
const QUERY = __ENV.TARGET
  ? `${URL}${__ENV.TARGET}`
  : `${URL}/rest/services/${LAYER}/FeatureServer/0/query` +
    `?where=1%3D1&outFields=*&returnGeometry=true&resultRecordCount=${ROWS}&f=json`;
const HEALTH = `${URL}/admin/health`;
const CONTROL = `${URL}/rest/info?f=json`;

// --- what is reported -------------------------------------------------------------
const okCount = new Counter('req_ok');
const refusedCount = new Counter('req_refused');
const otherCount = new Counter('req_other');
const errorCount = new Counter('req_error');
const bytes = new Counter('body_bytes');

const latOk = new Trend('lat_ok', true);
const latRefused = new Trend('lat_refused', true);
const latAll = new Trend('lat_all', true);

// The evidence that this generator does not pay a handshake per request. If these are
// not near zero the run says nothing the Python probe did not already say.
const tlsMs = new Trend('tls_ms', true);
const connectMs = new Trend('connect_ms', true);
const blockedMs = new Trend('blocked_ms', true);

// The server's own queue counter, sampled through the measured window.
const qSource = new Trend('q_source');
const qWorker = new Trend('q_worker');
const qNonEmpty = new Rate('q_nonempty');
const qAtDepth = new Rate('q_at_depth');
const qSamples = new Counter('q_samples');
const healthMs = new Trend('health_ms', true);

// The server's own counters, cumulative, sampled beside the queue. The difference
// between their first and last sample is what separates *this server is out of CPU*
// from *this server is queueing with CPU to spare*, which is the whole question about
// where the latency is. Read from `/admin/health`'s `runtime` block rather than from
// outside, because a process counter taken from the client cannot attribute CPU.
const rtCpuMs = new Trend('rt_cpu_ms');
const rtPauseMs = new Trend('rt_gc_pause_ms');
const rtAllocMb = new Trend('rt_alloc_mb');
const rtUptimeMs = new Trend('rt_uptime_ms');
const rtCores = new Trend('rt_cores');

// The control path: through Kestrel and the pipeline, no data-source permit.
const controlMs = new Trend('control_ms', true);
const controlFail = new Counter('control_fail');

// Per-window, so cold and sustained can be told apart rather than argued about.
const okW = [];
const refW = [];
const latW = [];
const qW = [];

for (let i = 0; i < WINDOWS; i++) {
  okW.push(new Counter(`w${i}_ok`));
  refW.push(new Counter(`w${i}_refused`));
  latW.push(new Trend(`w${i}_lat_ok`, true));
  qW.push(new Trend(`w${i}_q_source`));
}

// --- scenarios --------------------------------------------------------------------
const loadScenario =
  MODE === 'rate'
    ? {
        executor: 'constant-arrival-rate',
        rate: RATE,
        timeUnit: '1s',
        duration: DURATION,
        // Generous, because a VU that is waiting on the server must never be the reason
        // the offered rate falls short. k6 says so in the summary if it is.
        preAllocatedVUs: Math.max(200, Math.min(4000, RATE * 2)),
        maxVUs: Math.max(400, Math.min(6000, RATE * 4)),
        startTime: `${MEASURE_AT}s`,
        exec: 'measure',
        gracefulStop: '30s',
      }
    : {
        executor: 'constant-vus',
        vus: VUS,
        duration: DURATION,
        startTime: `${MEASURE_AT}s`,
        exec: 'measure',
        gracefulStop: '30s',
      };

export const options = {
  insecureSkipTLSVerify: true,
  discardResponseBodies: false,
  noConnectionReuse: false,
  scenarios: {
    warm: {
      executor: 'constant-vus',
      vus: MODE === 'rate' ? Math.min(200, Math.max(24, Math.round(RATE / 10))) : VUS,
      duration: WARM,
      startTime: '0s',
      exec: 'warm',
      gracefulStop: '2s',
    },
    load: loadScenario,
    sampler: {
      executor: 'constant-arrival-rate',
      rate: SAMPLE_HZ,
      timeUnit: '1s',
      duration: DURATION,
      preAllocatedVUs: 8,
      maxVUs: 32,
      startTime: `${MEASURE_AT}s`,
      exec: 'sample',
      gracefulStop: '10s',
    },
    control: {
      executor: 'constant-arrival-rate',
      rate: 4,
      timeUnit: '1s',
      duration: DURATION,
      preAllocatedVUs: 4,
      maxVUs: 16,
      startTime: `${MEASURE_AT}s`,
      exec: 'control',
      gracefulStop: '10s',
    },
  },
};

export function setup() {
  const answer = http.post(
    `${URL}/rest/auth/login`,
    JSON.stringify({ name: USER, password: PASSWORD }),
    { headers: { 'Content-Type': 'application/json' }, timeout: '30s' }
  );

  if (answer.status !== 200) {
    throw new Error(`login failed: ${answer.status} ${String(answer.body).slice(0, 200)}`);
  }

  const token = JSON.parse(answer.body).token;
  const auth = { headers: { Authorization: `Bearer ${token}` }, timeout: '120s' };

  // Read the bound out of the server rather than assuming the defaults, so that a
  // deployment which moved `QueueWaitersPerPermit` is reported against its own number.
  const health = http.get(HEALTH, auth);
  let admission = null;

  if (health.status === 200) {
    admission = JSON.parse(health.body).admissionControl;
  }

  // One unloaded request, so the summary has a floor to compare the loaded medians to.
  // Without it "the median grew" has no denominator.
  http.get(QUERY, auth);
  const idle = http.get(QUERY, auth);

  return {
    token,
    admission,
    idleMs: idle.timings.duration,
    idleStatus: idle.status,
    idleBytes: idle.body ? idle.body.length : 0,
  };
}

function authOf(data) {
  return { headers: { Authorization: `Bearer ${data.token}` }, timeout: '120s' };
}

export function warm(data) {
  http.get(QUERY, authOf(data));
}

function windowOf() {
  const elapsed = exec.instance.currentTestRunDuration / 1000 - MEASURE_AT;
  const i = Math.floor(elapsed / WINDOW);
  return i >= 0 && i < WINDOWS ? i : -1;
}

export function measure(data) {
  const answer = http.get(QUERY, authOf(data));
  const w = windowOf();

  latAll.add(answer.timings.duration);
  blockedMs.add(answer.timings.blocked);
  connectMs.add(answer.timings.connecting);
  tlsMs.add(answer.timings.tls_handshaking);

  if (answer.status === 200) {
    okCount.add(1);
    latOk.add(answer.timings.duration);
    bytes.add(answer.body ? answer.body.length : 0);

    if (w >= 0) {
      okW[w].add(1);
      latW[w].add(answer.timings.duration);
    }
  } else if (answer.status === 503) {
    refusedCount.add(1);
    latRefused.add(answer.timings.duration);

    if (w >= 0) {
      refW[w].add(1);
    }
  } else if (answer.status === 0) {
    // A transport failure is not a refusal and must never be counted as one: a
    // generator that reports its own broken sockets as load shedding is the fault
    // this whole re-measurement exists to avoid.
    errorCount.add(1);
  } else {
    otherCount.add(1);
  }
}

export function sample(data) {
  const answer = http.get(HEALTH, authOf(data));
  healthMs.add(answer.timings.duration);

  if (answer.status !== 200) {
    return;
  }

  const body = JSON.parse(answer.body);
  const admission = body.admissionControl;
  const runtime = body.runtime;

  if (runtime) {
    rtCpuMs.add(runtime.cpuMilliseconds);
    rtPauseMs.add(runtime.gcPauseMilliseconds);
    rtAllocMb.add(runtime.allocatedBytes / 1048576);
    rtUptimeMs.add(runtime.uptimeMilliseconds);
    rtCores.add(runtime.cores);
  }

  const waiting = admission.waitingForSource;
  const depth = admission.queueDepth;
  const w = windowOf();

  qSamples.add(1);
  qSource.add(waiting);
  qWorker.add(admission.waitingForWorker);
  qNonEmpty.add(waiting > 0);
  qAtDepth.add(depth > 0 && waiting >= depth);

  if (w >= 0) {
    qW[w].add(waiting);
  }
}

export function control() {
  // Anonymous on purpose: `/rest/info` is the cheapest thing the pipeline will answer,
  // so what it measures is Kestrel, the socket and the middleware — everything upstream
  // of the point where a permit is asked for. It does not authenticate, and that is
  // stated rather than hidden, because per-request authentication is one of the three
  // shared costs D-30 could not separate.
  const answer = http.get(CONTROL, { timeout: '120s' });

  if (answer.status === 200) {
    controlMs.add(answer.timings.duration);
  } else {
    controlFail.add(1);
  }
}

// Two cumulative counters, differenced over the same window and divided. Returns null
// rather than zero when the window is too short to have moved either one, because a
// zero that means "not measured" is the kind of number this repository keeps having to
// take back.
function deltaRatio(metrics, over, under) {
  const a = metrics[over];
  const b = metrics[under];

  if (!a || !b) {
    return null;
  }

  const span = b.values.max - b.values.min;
  return span > 0 ? (a.values.max - a.values.min) / span : null;
}

function num(metrics, name, field) {
  const m = metrics[name];

  if (!m) {
    return null;
  }

  const v = m.values[field];
  return v === undefined ? null : v;
}

export function handleSummary(data) {
  const m = data.metrics;
  const ok = num(m, 'req_ok', 'count') || 0;
  const refused = num(m, 'req_refused', 'count') || 0;
  const other = num(m, 'req_other', 'count') || 0;
  const failed = num(m, 'req_error', 'count') || 0;
  const total = ok + refused + other + failed;
  const wall = RUN_S;

  const windows = [];

  for (let i = 0; i < WINDOWS; i++) {
    const wOk = num(m, `w${i}_ok`, 'count') || 0;
    const wRef = num(m, `w${i}_refused`, 'count') || 0;

    if (wOk + wRef === 0) {
      continue;
    }

    windows.push({
      window: i,
      fromSeconds: i * WINDOW,
      ok: wOk,
      refused: wRef,
      refusedShare: wRef / (wOk + wRef),
      medianOkMs: num(m, `w${i}_lat_ok`, 'med'),
      p95OkMs: num(m, `w${i}_lat_ok`, 'p(95)'),
      queueMedian: num(m, `w${i}_q_source`, 'med'),
      queueMax: num(m, `w${i}_q_source`, 'max'),
    });
  }

  const report = {
    label: LABEL,
    when: new Date().toISOString(),
    generator: 'k6',
    mode: MODE,
    offered: MODE === 'rate' ? { ratePerSecond: RATE } : { vus: VUS },
    target: { url: URL, layer: LAYER, rows: ROWS, query: QUERY },
    warmSeconds: WARM_S,
    measuredSeconds: wall,
    windowSeconds: WINDOW,
    serverBound: data.setup_data ? data.setup_data.admission : null,
    idle: data.setup_data
      ? {
          status: data.setup_data.idleStatus,
          millis: data.setup_data.idleMs,
          bytes: data.setup_data.idleBytes,
        }
      : null,
    totals: {
      requests: total,
      ok,
      refused,
      otherStatus: other,
      transportErrors: failed,
      refusedShare: total ? refused / total : 0,
      throughputPerSecond: total / wall,
      okPerSecond: ok / wall,
      megabytesPerSecond: (num(m, 'body_bytes', 'count') || 0) / 1048576 / wall,
    },
    admittedLatencyMs: {
      median: num(m, 'lat_ok', 'med'),
      p90: num(m, 'lat_ok', 'p(90)'),
      p95: num(m, 'lat_ok', 'p(95)'),
      max: num(m, 'lat_ok', 'max'),
    },
    refusalLatencyMs: {
      median: num(m, 'lat_refused', 'med'),
      max: num(m, 'lat_refused', 'max'),
    },
    // If these are not ~0 this run has the same defect as the one it replaces.
    connectionReuse: {
      tlsHandshakeMedianMs: num(m, 'tls_ms', 'med'),
      tlsHandshakeAvgMs: num(m, 'tls_ms', 'avg'),
      connectMedianMs: num(m, 'connect_ms', 'med'),
      blockedMedianMs: num(m, 'blocked_ms', 'med'),
    },
    queue: {
      samples: num(m, 'q_samples', 'count') || 0,
      median: num(m, 'q_source', 'med'),
      p90: num(m, 'q_source', 'p(90)'),
      max: num(m, 'q_source', 'max'),
      nonEmptyShare: num(m, 'q_nonempty', 'rate'),
      atDepthShare: num(m, 'q_at_depth', 'rate'),
      workerMedian: num(m, 'q_worker', 'med'),
      workerMax: num(m, 'q_worker', 'max'),
      healthMedianMs: num(m, 'health_ms', 'med'),
    },
    // Little's law, from the client's side: throughput x mean latency is how many
    // requests were outstanding on average. It is the number the old measurement could
    // not produce and could not do without, because *the generator cannot keep N in
    // flight* is only an explanation if N is not reached.
    inFlight: {
      estimate:
        (total / wall) * ((num(m, 'lat_all', 'avg') || 0) / 1000),
      offered: MODE === 'rate' ? null : VUS,
    },
    serverRuntime: {
      // Cumulative counters differenced across the measured window.
      coresUsed: deltaRatio(m, 'rt_cpu_ms', 'rt_uptime_ms'),
      coresAvailable: num(m, 'rt_cores', 'max'),
      gcPausePercent: 100 * (deltaRatio(m, 'rt_gc_pause_ms', 'rt_uptime_ms') || 0),
      allocMegabytesPerSecond: 1000 * (deltaRatio(m, 'rt_alloc_mb', 'rt_uptime_ms') || 0),
    },
    controlPath: {
      path: '/rest/info',
      medianMs: num(m, 'control_ms', 'med'),
      p95Ms: num(m, 'control_ms', 'p(95)'),
      maxMs: num(m, 'control_ms', 'max'),
      failures: num(m, 'control_fail', 'count') || 0,
    },
    windows,
  };

  const f = (x, d) => (x === null || x === undefined ? '-' : x.toFixed(d === undefined ? 1 : d));
  const lines = [];

  lines.push('');
  lines.push(`=== ${LABEL} — ${MODE === 'rate' ? `${RATE} req/s offered` : `${VUS} callers`}, ` +
             `${wall}s measured after ${WARM_S}s warm`);
  lines.push(`    idle single request: ${f(report.idle ? report.idle.millis : null)} ms`);
  lines.push(`    bound: perSource=${report.serverBound ? report.serverBound.perSource : '?'} ` +
             `queueDepth=${report.serverBound ? report.serverBound.queueDepth : '?'}`);
  lines.push(`    ${total} requests: ${ok} ok, ${refused} refused (` +
             `${f(100 * report.totals.refusedShare)}%), ${other} other, ${failed} transport errors`);
  lines.push(`    throughput ${f(report.totals.throughputPerSecond)} req/s ` +
             `(${f(report.totals.okPerSecond)} admitted/s, ${f(report.totals.megabytesPerSecond)} MB/s)`);
  lines.push(`    admitted median ${f(report.admittedLatencyMs.median)} ms, ` +
             `p95 ${f(report.admittedLatencyMs.p95)} ms`);
  lines.push(`    handshake per request: tls ${f(report.connectionReuse.tlsHandshakeMedianMs, 3)} ms ` +
             `median / ${f(report.connectionReuse.tlsHandshakeAvgMs, 3)} ms mean`);
  lines.push(`    queue: ${report.queue.samples} samples, median ${f(report.queue.median, 0)}, ` +
             `max ${f(report.queue.max, 0)}, non-empty in ` +
             `${f(100 * (report.queue.nonEmptyShare || 0))}% of samples, at depth in ` +
             `${f(100 * (report.queue.atDepthShare || 0))}%`);
  lines.push(`    control /rest/info median ${f(report.controlPath.medianMs)} ms, ` +
             `p95 ${f(report.controlPath.p95Ms)} ms`);
  lines.push(`    in flight (Little's law): ${f(report.inFlight.estimate)} ` +
             `of ${report.inFlight.offered === null ? 'n/a' : report.inFlight.offered} offered`);
  lines.push(`    server: ${f(report.serverRuntime.coresUsed, 2)} of ` +
             `${f(report.serverRuntime.coresAvailable, 0)} cores, GC pause ` +
             `${f(report.serverRuntime.gcPausePercent)}%, ` +
             `${f(report.serverRuntime.allocMegabytesPerSecond, 0)} MB/s allocated`);
  lines.push('');
  lines.push('    from  |     ok | refused |  shed% | med ms | p95 ms | queue med | queue max');
  lines.push('    ------+--------+---------+--------+--------+--------+-----------+----------');

  for (const w of windows) {
    lines.push(
      `    ${String(w.fromSeconds).padStart(4)}s | ${String(w.ok).padStart(6)} | ` +
      `${String(w.refused).padStart(7)} | ${f(100 * w.refusedShare).padStart(6)} | ` +
      `${f(w.medianOkMs, 0).padStart(6)} | ${f(w.p95OkMs, 0).padStart(6)} | ` +
      `${f(w.queueMedian, 0).padStart(9)} | ${f(w.queueMax, 0).padStart(9)}`
    );
  }

  lines.push('');

  const out = {};
  out.stdout = lines.join('\n') + '\n';
  out[OUT] = JSON.stringify(report, null, 1);
  return out;
}
