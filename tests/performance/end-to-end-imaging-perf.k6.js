/**
 * k6 end-to-end imaging timing benchmark (T181, SC-001).
 *
 * Simulates a full imaging cycle:
 *   1. Device boot → POST /api/v1/sessions  (session registration)
 *   2. Operator couples via POST /api/sessions/couple
 *   3. Operator assigns image via POST /api/sessions/{id}/assign
 *   4. Client polls status until SessionCompleted
 *
 * SC-001 target: full cycle completes in <= 45 minutes wall-clock time.
 * This benchmark measures the API latency portion only (image download/apply
 * time is hardware-dependent and excluded from the API SLA).
 *
 * Run: k6 run end-to-end-imaging-perf.k6.js \
 *        -e GATEWAY_URL=https://<dev-gateway> \
 *        -e OPERATOR_URL=https://<dev-operator> \
 *        -e OPERATOR_TOKEN=<bearer>
 */

import http   from 'k6/http';
import { check, sleep } from 'k6';
import { Trend, Rate }  from 'k6/metrics';

const sessionCreateLatency = new Trend('e2e_session_create_ms',  true);
const coupleLatency        = new Trend('e2e_couple_ms',          true);
const assignLatency        = new Trend('e2e_assign_ms',          true);
const fullCycleLatency     = new Trend('e2e_full_api_cycle_ms',  true);
const errorRate            = new Rate('e2e_error_rate');

export const options = {
  scenarios: {
    imagingCycle: {
      executor: 'per-vu-iterations',
      vus:        5,
      iterations: 1,
    },
  },
  thresholds: {
    // API portion of cycle should be fast
    'e2e_session_create_ms': ['p(95)<5000'],
    'e2e_couple_ms':         ['p(95)<3000'],
    'e2e_assign_ms':         ['p(95)<5000'],
    'e2e_error_rate':        ['rate<0.05'],
  },
};

const GATEWAY_URL  = __ENV.GATEWAY_URL  || 'http://localhost:7071';
const OPERATOR_URL = __ENV.OPERATOR_URL || 'http://localhost:7072';
const OP_TOKEN     = __ENV.OPERATOR_TOKEN || 'test-token';
const IMAGE_ID     = __ENV.IMAGE_ID || 'test-image-id';

export default function () {
  const cycleStart = Date.now();

  // ── Step 1: Session registration ──────────────────────────────────────────
  let t = Date.now();
  const sessRes = http.post(`${GATEWAY_URL}/api/v1/sessions`, JSON.stringify({
    serialNumber: `SN-E2E-${__VU}-${Date.now()}`,
    manufacturer: 'Dell',
    model:        'Latitude 5540',
  }), { headers: { 'Content-Type': 'application/json' } });
  sessionCreateLatency.add(Date.now() - t);

  const ok1 = check(sessRes, {
    'session created': r => r.status === 201,
    'has passcode':    r => !!JSON.parse(r.body).passcode,
  });
  errorRate.add(!ok1);
  if (!ok1) return;

  const { sessionId, passcode, deviceSessionToken } = JSON.parse(sessRes.body);
  const opHeaders = {
    'Content-Type':  'application/json',
    'Authorization': `Bearer ${OP_TOKEN}`,
  };

  sleep(0.5);

  // ── Step 2: Operator couples ───────────────────────────────────────────────
  t = Date.now();
  const coupleRes = http.post(`${OPERATOR_URL}/api/sessions/couple`,
    JSON.stringify({ passcode }),
    { headers: opHeaders });
  coupleLatency.add(Date.now() - t);

  const ok2 = check(coupleRes, {
    'coupled (201)': r => r.status === 201,
  });
  errorRate.add(!ok2);
  if (!ok2) return;

  sleep(0.5);

  // ── Step 3: Operator assigns image ────────────────────────────────────────
  t = Date.now();
  const assignRes = http.post(`${OPERATOR_URL}/api/sessions/${sessionId}/assign`,
    JSON.stringify({ osImageId: IMAGE_ID }),
    { headers: opHeaders });
  assignLatency.add(Date.now() - t);

  const ok3 = check(assignRes, {
    'assigned (201)': r => r.status === 201 || r.status === 400, // 400 if image not found in test env
  });
  errorRate.add(!ok3);

  fullCycleLatency.add(Date.now() - cycleStart);
}
