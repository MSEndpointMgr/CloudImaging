/**
 * k6 performance test: Bulk assignment latency for 20+ devices (SC-005).
 *
 * Run: k6 run bulk-assign-latency.k6.js -e BASE_URL=https://<url> -e TOKEN=<bearer>
 */

import http from 'k6/http';
import { check } from 'k6';
import { Trend } from 'k6/metrics';

const bulkLatency = new Trend('bulk_assign_latency', true);

export const options = {
  vus:      5,
  duration: '30s',
  thresholds: {
    // SC-005: bulk assignment initiation < 10 s for 20+ devices
    'bulk_assign_latency': ['p(95)<10000'],
  },
};

const BASE_URL  = __ENV.BASE_URL  || 'http://localhost:7072';
const TOKEN     = __ENV.TOKEN     || 'test-token';
const IMAGE_ID  = __ENV.IMAGE_ID  || 'test-image-id';
const HEADERS   = { 'Content-Type': 'application/json', Authorization: `Bearer ${TOKEN}` };

export default function () {
  // Simulate 20 session IDs
  const sessionIds = Array.from({ length: 20 }, (_, i) => `session-${__VU}-${i}`);

  const t1  = Date.now();
  const res = http.post(`${BASE_URL}/api/sessions/bulk-assign`, JSON.stringify({
    sessionIds,
    osImageId: IMAGE_ID,
  }), { headers: HEADERS });
  bulkLatency.add(Date.now() - t1);

  check(res, { 'bulk assign OK (200)': r => r.status === 200 });
}
