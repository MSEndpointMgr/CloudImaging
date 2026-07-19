/**
 * k6 performance test: 50 concurrent imaging sessions (SC-003).
 * Verifies Device Gateway API handles 50 simultaneous session registrations
 * and maintains < 5 s response time at P95.
 *
 * Run: k6 run session-concurrency.k6.js -e BASE_URL=https://<your-gateway-url>
 */

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend } from 'k6/metrics';

const errorRate      = new Rate('error_rate');
const sessionLatency = new Trend('session_create_latency', true);

export const options = {
  scenarios: {
    concurrentSessions: {
      executor:    'ramping-vus',
      startVUs:    0,
      stages: [
        { duration: '30s', target: 50 },   // ramp up to 50 concurrent
        { duration: '60s', target: 50 },   // hold at 50 (SC-003)
        { duration: '15s', target: 0  },   // ramp down
      ],
    },
  },
  thresholds: {
    // SC-003: P95 session creation < 5 s
    'session_create_latency': ['p(95)<5000'],
    'error_rate':             ['rate<0.01'],   // < 1% error rate
  },
};

const BASE_URL = __ENV.BASE_URL || 'http://localhost:7071';

export default function () {
  const payload = JSON.stringify({
    serialNumber: `SN-PERF-${__VU}-${__ITER}`,
    manufacturer: 'Dell',
    model:        'Latitude 5540',
  });

  const start    = Date.now();
  const response = http.post(`${BASE_URL}/api/v1/sessions`, payload, {
    headers: { 'Content-Type': 'application/json' },
  });
  sessionLatency.add(Date.now() - start);

  const success = check(response, {
    'session created (201)': r => r.status === 201,
    'has sessionId':         r => JSON.parse(r.body).sessionId !== undefined,
    'has passcode':          r => JSON.parse(r.body).passcode !== undefined,
    'has deviceSessionToken':r => JSON.parse(r.body).deviceSessionToken !== undefined,
  });

  errorRate.add(!success);
  sleep(0.5);
}
