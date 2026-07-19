/**
 * k6 performance test: Operator API boot image query/SAS generation (SC-015).
 *
 * Run: k6 run operator-api-boot-image-perf.k6.js -e BASE_URL=https://<url> -e TOKEN=<bearer>
 */

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Trend } from 'k6/metrics';

const listLatency = new Trend('boot_image_list_latency', true);
const sasLatency  = new Trend('boot_image_sas_latency',  true);

export const options = {
  vus:      20,
  duration: '60s',
  thresholds: {
    // SC-015: boot image query + SAS P95 < 2 s
    'boot_image_list_latency': ['p(95)<2000'],
    'boot_image_sas_latency':  ['p(95)<2000'],
  },
};

const BASE_URL = __ENV.BASE_URL || 'http://localhost:7072';
const TOKEN    = __ENV.TOKEN    || 'test-token';
const HEADERS  = { Authorization: `Bearer ${TOKEN}` };

export default function () {
  const t1     = Date.now();
  const listRes = http.get(`${BASE_URL}/api/boot-images`, { headers: HEADERS });
  listLatency.add(Date.now() - t1);

  check(listRes, { 'list OK': r => r.status === 200 });

  const images = JSON.parse(listRes.body);
  if (Array.isArray(images) && images.length > 0) {
    const id  = images[0].bootImageId;
    const t2  = Date.now();
    const sas = http.post(`${BASE_URL}/api/boot-images/${id}/sas`, null, { headers: HEADERS });
    sasLatency.add(Date.now() - t2);
    check(sas, { 'SAS OK': r => r.status === 200 });
  }

  sleep(0.3);
}
