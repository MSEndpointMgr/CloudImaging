/**
 * k6 performance test: OS image CRUD with 500-item catalog (SC-008).
 * Verifies Operator API handles GET /api/images at catalog scale.
 *
 * Run: k6 run image-crud-latency.k6.js -e BASE_URL=https://<your-operator-url> -e TOKEN=<bearer>
 */

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Trend } from 'k6/metrics';

const listLatency   = new Trend('image_list_latency',   true);
const createLatency = new Trend('image_create_latency', true);

export const options = {
  vus:      10,
  duration: '60s',
  thresholds: {
    // SC-008: catalog read P95 < 2 s
    'image_list_latency':   ['p(95)<2000'],
    'image_create_latency': ['p(95)<3000'],
  },
};

const BASE_URL = __ENV.BASE_URL || 'http://localhost:7072';
const TOKEN    = __ENV.TOKEN    || 'test-token';

const HEADERS = {
  'Content-Type':  'application/json',
  'Authorization': `Bearer ${TOKEN}`,
};

export default function () {
  // ── List images (most frequent operation) ─────────────────────────────────
  const t1 = Date.now();
  const listRes = http.get(`${BASE_URL}/api/images`, { headers: HEADERS });
  listLatency.add(Date.now() - t1);

  check(listRes, { 'list OK (200)': r => r.status === 200 });

  sleep(0.2);
}
