import { describe, it, expect, vi, beforeEach } from 'vitest';
import express, { type NextFunction, type Response } from 'express';
import request from 'supertest';
import { AxiosError, AxiosHeaders, type InternalAxiosRequestConfig } from 'axios';
import type { AuthenticatedRequest } from '@/middleware/auth.js';

const operatorApi = vi.hoisted(() => ({
  drivers: vi.fn(),
}));

vi.mock('@/services/operatorApiClient.js', () => ({ operatorApiClient: operatorApi }));

const { driversRouter } = await import('@/routes/drivers.js');
const { errorHandler } = await import('@/middleware/errorHandler.js');

const modelId = '8f0c3c5e-6c1f-4c55-9a43-2f5d8c1e7a10';
const operator = { roles: ['CloudImaging.Operator'], preferred_username: 'olle@contoso.com', oid: 'oid-olle' };
const technician = { roles: ['CloudImaging.Technician'], preferred_username: 'tina@contoso.com', oid: 'oid-tina' };
const actor = { upn: 'olle@contoso.com', objectId: 'oid-olle' };

function appFor(claims: Record<string, unknown>) {
  const app = express();
  app.use(express.json({ limit: '1mb' }));
  app.use((req: AuthenticatedRequest, _res: Response, next: NextFunction) => { req.user = claims as AuthenticatedRequest['user']; next(); });
  app.use('/api/drivers', driversRouter);
  app.use(errorHandler);
  return app;
}

function upstreamError(status: number, data: unknown, headers: Record<string, string> = {}): AxiosError {
  const config = { headers: new AxiosHeaders() } as InternalAxiosRequestConfig;
  return new AxiosError('Request failed', 'ERR_BAD_REQUEST', config, undefined, { status, statusText: '', headers, config, data });
}

describe('Portal backend: driver routes (onboarding, models, sync now)', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    operatorApi.drivers.mockResolvedValue({ status: 200, data: { ok: true } });
  });

  it('3: reads the vendor model list with vendor and search, Operator only', async () => {
    const res = await request(appFor(operator)).get('/api/drivers/catalog/models?vendor=Hp&search=840');

    expect(res.status).toBe(200);
    expect(operatorApi.drivers).toHaveBeenCalledWith('GET', 'catalog/models', { query: { vendor: 'Hp', search: '840' } });
    expect((await request(appFor(technician)).get('/api/drivers/catalog/models?vendor=Hp')).status).toBe(403);
  });

  it('4: previews only the selection fields', async () => {
    await request(appFor(operator)).post('/api/drivers/models/preview').send({ vendor: 'Hp', catalogModelKeys: ['8C26_X64'], releases: ['Windows11_24H2'], actor: { upn: 'mallory@contoso.com' }, extra: 1 });

    expect(operatorApi.drivers).toHaveBeenCalledWith('POST', 'models/preview', { body: { vendor: 'Hp', catalogModelKeys: ['8C26_X64'], releases: ['Windows11_24H2'] } });
  });

  it('5: onboards with the actor from the token and discards the browser actor', async () => {
    operatorApi.drivers.mockResolvedValue({ status: 201, data: { modelIds: [modelId] } });

    const res = await request(appFor(operator)).post('/api/drivers/models').send({ vendor: 'Hp', catalogModelKeys: ['8C26_X64'], releases: ['Windows11_24H2'], actor: { upn: 'mallory@contoso.com' } });

    expect(res.status).toBe(201);
    expect(res.body).toEqual({ modelIds: [modelId] });
    expect(operatorApi.drivers).toHaveBeenCalledWith('POST', 'models', { body: { vendor: 'Hp', catalogModelKeys: ['8C26_X64'], releases: ['Windows11_24H2'], actor } });
  });

  it('6 and 7: models and the model page are readable by Technicians, with the release query', async () => {
    const app = appFor(technician);

    expect((await request(app).get('/api/drivers/models')).status).toBe(200);
    expect((await request(app).get(`/api/drivers/models/${modelId}?release=Windows11_24H2`)).status).toBe(200);
    expect(operatorApi.drivers).toHaveBeenCalledWith('GET', 'models');
    expect(operatorApi.drivers).toHaveBeenCalledWith('GET', `models/${modelId}`, { query: { release: 'Windows11_24H2' } });
  });

  it('7: a model id that is not a GUID is 400 without an upstream call', async () => {
    const res = await request(appFor(technician)).get('/api/drivers/models/not-a-guid');

    expect(res.status).toBe(400);
    expect(operatorApi.drivers).not.toHaveBeenCalled();
  });

  it('8: adds a release with only the release and the token actor', async () => {
    await request(appFor(operator)).post(`/api/drivers/models/${modelId}/releases`).send({ release: 'Windows11_25H2', actor: { upn: 'mallory@contoso.com' } });

    expect(operatorApi.drivers).toHaveBeenCalledWith('POST', `models/${modelId}/releases`, { body: { release: 'Windows11_25H2', actor } });
  });

  it('43: completes onboarding with the token actor and relays 409 for an active model', async () => {
    operatorApi.drivers.mockRejectedValueOnce(upstreamError(409, { type: 'https://cloudimaging.io/errors/drivers', title: 'Conflict', status: 409, detail: 'Onboarding of this model is already complete.' }));

    const res = await request(appFor(operator)).post(`/api/drivers/models/${modelId}/complete`).send({ actor: { upn: 'mallory@contoso.com' } });

    expect(operatorApi.drivers).toHaveBeenCalledWith('POST', `models/${modelId}/complete`, { body: { actor } });
    expect(res.status).toBe(409);
    expect(res.body.detail).toBe('Onboarding of this model is already complete.');
  });

  it('26: Sync now relays 429 with availableAt and Retry-After', async () => {
    operatorApi.drivers.mockRejectedValueOnce(upstreamError(429, { status: 429, detail: 'Drivers were checked less than an hour ago.', availableAt: '2026-10-25T10:00:00+00:00' }, { 'retry-after': '1800' }));

    const res = await request(appFor(operator)).post('/api/drivers/sync/runs').send({});

    expect(operatorApi.drivers).toHaveBeenCalledWith('POST', 'sync/runs', { body: { actor } });
    expect(res.status).toBe(429);
    expect(res.body.availableAt).toBe('2026-10-25T10:00:00+00:00');
    expect(res.headers['retry-after']).toBe('1800');
  });

  it('26: Sync now answers 202 with the run', async () => {
    operatorApi.drivers.mockResolvedValue({ status: 202, data: { runId: modelId } });

    const res = await request(appFor(operator)).post('/api/drivers/sync/runs').send({});

    expect(res.status).toBe(202);
    expect(res.body).toEqual({ runId: modelId });
  });

  it('27: lists runs for Technicians', async () => {
    operatorApi.drivers.mockResolvedValue({ status: 200, data: [] });

    expect((await request(appFor(technician)).get('/api/drivers/sync/runs')).status).toBe(200);
    expect(operatorApi.drivers).toHaveBeenCalledWith('GET', 'sync/runs');
  });

  it('relays the unreachable catalog 503 with its detail and catalogUrl, but masks other 5xx text', async () => {
    operatorApi.drivers.mockRejectedValueOnce(upstreamError(503, { type: 'https://cloudimaging.io/errors/drivers', status: 503, detail: "Can't reach catalog at https://hpia.hpcloud.hp.com. Make sure Imaging Core can reach it, then try again.", catalogUrl: 'https://hpia.hpcloud.hp.com/ref/platformList.cab' }));
    operatorApi.drivers.mockRejectedValueOnce(upstreamError(500, { detail: 'System.Exception at Core.Internal' }));

    const unreachable = await request(appFor(operator)).get('/api/drivers/catalog/models?vendor=Hp');
    const fault = await request(appFor(operator)).get('/api/drivers/catalog/models?vendor=Hp');

    expect(unreachable.status).toBe(503);
    expect(unreachable.body.detail).toBe("Can't reach catalog at https://hpia.hpcloud.hp.com. Make sure Imaging Core can reach it, then try again.");
    expect(unreachable.body.catalogUrl).toBe('https://hpia.hpcloud.hp.com/ref/platformList.cab');
    expect(fault.status).toBe(500);
    expect(fault.body.detail).not.toContain('System.Exception');
  });

  it('writes are refused for Technicians without an upstream call', async () => {
    const app = appFor(technician);

    expect((await request(app).post('/api/drivers/models').send({})).status).toBe(403);
    expect((await request(app).post(`/api/drivers/models/${modelId}/complete`).send({})).status).toBe(403);
    expect((await request(app).post('/api/drivers/sync/runs').send({})).status).toBe(403);
    expect((await request(app).post('/api/drivers/rules').send({})).status).toBe(403);
    expect((await request(app).put(`/api/drivers/models/${modelId}/releases/Windows11_24H2/overrides/sp100`).send({ action: 'Skip' })).status).toBe(403);
    expect((await request(app).get('/api/drivers/rules/suggestions')).status).toBe(403);
    expect(operatorApi.drivers).not.toHaveBeenCalled();
  });
});

describe('Portal backend: driver routes (rules, overrides, categories, build now)', () => {
  const ruleId = '1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed';

  beforeEach(() => {
    vi.clearAllMocks();
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    operatorApi.drivers.mockResolvedValue({ status: 200, data: { ok: true } });
  });

  it('12: sets an override with only the action and the token actor', async () => {
    const res = await request(appFor(operator)).put(`/api/drivers/models/${modelId}/releases/Windows11_24H2/overrides/sp140001`).send({ action: 'Skip', actor: { upn: 'mallory@contoso.com' }, extra: 1 });

    expect(res.status).toBe(200);
    expect(operatorApi.drivers).toHaveBeenCalledWith('PUT', `models/${modelId}/releases/Windows11_24H2/overrides/sp140001`, { body: { action: 'Skip', actor } });
  });

  it('12: relays the 400 for an action the component does not allow', async () => {
    operatorApi.drivers.mockRejectedValueOnce(upstreamError(400, { status: 400, detail: 'Specialize is not allowed for this component.' }));

    const res = await request(appFor(operator)).put(`/api/drivers/models/${modelId}/releases/Windows11_24H2/overrides/sp140001`).send({ action: 'Specialize' });

    expect(res.status).toBe(400);
    expect(res.body.detail).toBe('Specialize is not allowed for this component.');
  });

  it('12: a bad release or lineage key is 400 without an upstream call', async () => {
    const app = appFor(operator);

    expect((await request(app).put(`/api/drivers/models/${modelId}/releases/Windows12/overrides/sp1`).send({ action: 'Skip' })).status).toBe(400);
    expect((await request(app).put(`/api/drivers/models/${modelId}/releases/Windows11_24H2/overrides/sp%201`).send({ action: 'Skip' })).status).toBe(400);
    expect(operatorApi.drivers).not.toHaveBeenCalled();
  });

  it('13: undoes an override with changedBy from the token', async () => {
    await request(appFor(operator)).delete(`/api/drivers/models/${modelId}/releases/Windows11_24H2/overrides/sp140001?changedBy=mallory`);

    expect(operatorApi.drivers).toHaveBeenCalledWith('DELETE', `models/${modelId}/releases/Windows11_24H2/overrides/sp140001`, { query: { changedBy: 'olle@contoso.com' } });
  });

  it('17 and 22: rules and categories are readable by Technicians', async () => {
    const app = appFor(technician);

    expect((await request(app).get('/api/drivers/rules')).status).toBe(200);
    expect((await request(app).get('/api/drivers/categories?vendor=Hp')).status).toBe(200);
    expect(operatorApi.drivers).toHaveBeenCalledWith('GET', 'rules');
    expect(operatorApi.drivers).toHaveBeenCalledWith('GET', 'categories', { query: { vendor: 'Hp' } });
  });

  it('18: suggestions are for Operators', async () => {
    expect((await request(appFor(operator)).get('/api/drivers/rules/suggestions')).status).toBe(200);
    expect(operatorApi.drivers).toHaveBeenCalledWith('GET', 'rules/suggestions');
  });

  it('19 and 20: create and update forward only the rule fields with the token actor', async () => {
    operatorApi.drivers.mockResolvedValueOnce({ status: 201, data: { ruleId } });
    const rule = { name: 'Skip HP Services Scan', scope: 'Hp', match: 'NamePattern', value: 'HP Services Scan*', action: 'Skip' };

    const created = await request(appFor(operator)).post('/api/drivers/rules').send({ ...rule, actor: { upn: 'mallory@contoso.com' }, ruleId: 'x' });
    await request(appFor(operator)).put(`/api/drivers/rules/${ruleId}`).send(rule);

    expect(created.status).toBe(201);
    expect(operatorApi.drivers).toHaveBeenCalledWith('POST', 'rules', { body: { ...rule, actor } });
    expect(operatorApi.drivers).toHaveBeenCalledWith('PUT', `rules/${ruleId}`, { body: { ...rule, actor } });
  });

  it('19: relays the 409 for a name in use', async () => {
    operatorApi.drivers.mockRejectedValueOnce(upstreamError(409, { status: 409, detail: 'A rule named "Audio" already exists.' }));

    const res = await request(appFor(operator)).post('/api/drivers/rules').send({ name: 'Audio' });

    expect(res.status).toBe(409);
    expect(res.body.detail).toBe('A rule named "Audio" already exists.');
  });

  it('21: deletes a rule with changedBy and relays 204', async () => {
    operatorApi.drivers.mockResolvedValueOnce({ status: 204, data: '' });

    const res = await request(appFor(operator)).delete(`/api/drivers/rules/${ruleId}`);

    expect(res.status).toBe(204);
    expect(operatorApi.drivers).toHaveBeenCalledWith('DELETE', `rules/${ruleId}`, { query: { changedBy: 'olle@contoso.com' } });
    expect((await request(appFor(operator)).delete('/api/drivers/rules/not-a-guid')).status).toBe(400);
  });

  it('23: replaces critical categories for a known vendor only', async () => {
    const app = appFor(operator);

    await request(app).put('/api/drivers/critical-categories/Hp').send({ categories: ['Driver - Network'], extra: true });
    const bad = await request(app).put('/api/drivers/critical-categories/Acme').send({ categories: [] });

    expect(operatorApi.drivers).toHaveBeenCalledWith('PUT', 'critical-categories/Hp', { body: { categories: ['Driver - Network'], actor } });
    expect(bad.status).toBe(400);
    expect(operatorApi.drivers).toHaveBeenCalledTimes(1);
  });

  it('14: approves only the lineage keys with the token actor, and relays the 409 for a component that is not waiting', async () => {
    operatorApi.drivers.mockResolvedValueOnce({ status: 200, data: { ok: true } });
    operatorApi.drivers.mockRejectedValueOnce(upstreamError(409, { status: 409, detail: 'Intel Wireless LAN Driver is not waiting for approval.' }));
    const path = `/api/drivers/models/${modelId}/releases/Windows11_24H2/approvals`;

    await request(appFor(operator)).post(path).send({ lineageKeys: ['sp500'], actor: { upn: 'mallory@contoso.com' }, approvals: [{ lineageKey: 'x', action: 'Offline' }] });
    const conflict = await request(appFor(operator)).post(path).send({ lineageKeys: ['sp100'] });

    expect(operatorApi.drivers).toHaveBeenCalledWith('POST', `models/${modelId}/releases/Windows11_24H2/approvals`, { body: { lineageKeys: ['sp500'], actor } });
    expect(conflict.status).toBe(409);
    expect(conflict.body.detail).toBe('Intel Wireless LAN Driver is not waiting for approval.');
    expect((await request(appFor(technician)).post(path).send({ lineageKeys: ['sp500'] })).status).toBe(403);
  });

  it('33: Build now relays 202 and the 409 when nothing is waiting', async () => {
    operatorApi.drivers.mockResolvedValueOnce({ status: 202, data: '' });
    operatorApi.drivers.mockRejectedValueOnce(upstreamError(409, { status: 409, detail: 'No changes are waiting.' }));

    const accepted = await request(appFor(operator)).post(`/api/drivers/models/${modelId}/releases/Windows11_24H2/build`).send({});
    const nothing = await request(appFor(operator)).post(`/api/drivers/models/${modelId}/releases/Windows11_24H2/build`).send({});

    expect(accepted.status).toBe(202);
    expect(operatorApi.drivers).toHaveBeenCalledWith('POST', `models/${modelId}/releases/Windows11_24H2/build`, { body: { actor } });
    expect(nothing.status).toBe(409);
    expect(nothing.body.detail).toBe('No changes are waiting.');
  });
});

describe('Portal backend: driver routes (vendors, sync settings, run items, checks)', () => {
  const runId = '1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed';

  beforeEach(() => {
    vi.clearAllMocks();
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    operatorApi.drivers.mockResolvedValue({ status: 200, data: { ok: true } });
  });

  it('1: lists vendors for Technicians', async () => {
    expect((await request(appFor(technician)).get('/api/drivers/vendors')).status).toBe(200);
    expect(operatorApi.drivers).toHaveBeenCalledWith('GET', 'vendors');
  });

  it('2: switches a vendor with only syncEnabled and the token actor, and refuses an unknown vendor', async () => {
    await request(appFor(operator)).put('/api/drivers/vendors/Hp').send({ syncEnabled: false, actor: { upn: 'mallory@contoso.com' } });
    const bad = await request(appFor(operator)).put('/api/drivers/vendors/Surface').send({ syncEnabled: true });

    expect(operatorApi.drivers).toHaveBeenCalledWith('PUT', 'vendors/Hp', { body: { syncEnabled: false, actor } });
    expect(bad.status).toBe(400);
    expect(operatorApi.drivers).toHaveBeenCalledTimes(1);
  });

  it('2: relays the 400 for Lenovo', async () => {
    operatorApi.drivers.mockRejectedValueOnce(upstreamError(400, { status: 400, detail: 'Lenovo is not supported yet.' }));

    const res = await request(appFor(operator)).put('/api/drivers/vendors/Lenovo').send({ syncEnabled: true });

    expect(res.status).toBe(400);
    expect(res.body.detail).toBe('Lenovo is not supported yet.');
  });

  it('24 and 25: reads settings as a Technician and saves only the schedule fields with the token actor', async () => {
    const settings = { schedule: 'SelectedDays', weekdays: 31, repeatWeeks: 2, localTime: '02:00', timeZone: 'Europe/Berlin', waitForApprovalOfNewComponents: true, waitForTesting: true };

    expect((await request(appFor(technician)).get('/api/drivers/sync/settings')).status).toBe(200);
    await request(appFor(operator)).put('/api/drivers/sync/settings').send({ ...settings, lastScheduledSlotUtc: 'x', actor: { upn: 'mallory@contoso.com' } });

    expect(operatorApi.drivers).toHaveBeenCalledWith('GET', 'sync/settings');
    expect(operatorApi.drivers).toHaveBeenCalledWith('PUT', 'sync/settings', { body: { ...settings, actor } });
    expect((await request(appFor(technician)).put('/api/drivers/sync/settings').send(settings)).status).toBe(403);
  });

  it('25: relays field errors', async () => {
    operatorApi.drivers.mockRejectedValueOnce(upstreamError(400, { status: 400, detail: 'Choose a time zone from the list.', errors: [{ field: 'timeZone', detail: 'Choose a time zone from the list.' }] }));

    const res = await request(appFor(operator)).put('/api/drivers/sync/settings').send({ timeZone: 'Europe/Stockholm' });

    expect(res.status).toBe(400);
    expect(res.body.detail).toBe('Choose a time zone from the list.');
  });

  it('28: lists a run\'s items, and a run id that is not a GUID is 400', async () => {
    expect((await request(appFor(technician)).get(`/api/drivers/sync/runs/${runId}/items`)).status).toBe(200);
    expect(operatorApi.drivers).toHaveBeenCalledWith('GET', `sync/runs/${runId}/items`);
    expect((await request(appFor(technician)).get('/api/drivers/sync/runs/x/items')).status).toBe(400);
  });

  it('reads a run\'s progress as a Technician, and a run id that is not a GUID is 400', async () => {
    expect((await request(appFor(technician)).get(`/api/drivers/sync/runs/${runId}/progress`)).status).toBe(200);
    expect(operatorApi.drivers).toHaveBeenCalledWith('GET', `sync/runs/${runId}/progress`);
    expect((await request(appFor(technician)).get('/api/drivers/sync/runs/x/progress')).status).toBe(400);
  });

  it('the synchronization report is for Readers, Operators and Administrators, and forwards only the range', async () => {
    const reader = { roles: ['CloudImaging.Reader'], preferred_username: 'rita@contoso.com', oid: 'oid-rita' };
    const range = '?from=2026-10-01T00:00:00.000Z&to=2026-10-26T23:59:59.999Z&extra=1';

    expect((await request(appFor(reader)).get(`/api/drivers/reports/sync${range}`)).status).toBe(200);
    expect((await request(appFor(operator)).get(`/api/drivers/reports/sync${range}`)).status).toBe(200);
    expect(operatorApi.drivers).toHaveBeenCalledWith('GET', 'reports/sync', { query: { from: '2026-10-01T00:00:00.000Z', to: '2026-10-26T23:59:59.999Z' } });
    expect((await request(appFor(technician)).get(`/api/drivers/reports/sync${range}`)).status).toBe(403);
    expect(operatorApi.drivers).toHaveBeenCalledTimes(2);
  });

  it.each([
    [''],
    ['?from=2026-10-01T00:00:00Z'],
    ['?from=yesterday&to=2026-10-26T00:00:00Z'],
    ['?from=2026-10-01&to=2026-10-26'],
  ])('the synchronization report refuses %s without an upstream call', async (query) => {
    const res = await request(appFor(operator)).get(`/api/drivers/reports/sync${query}`);

    expect(res.status).toBe(400);
    expect(operatorApi.drivers).not.toHaveBeenCalled();
  });

  it.each([
    ['44', `/api/drivers/models/${modelId}/check`],
    ['45', '/api/drivers/models/check'],
  ])('%s: relays 409 while running and 429 with availableAt and Retry-After', async (_route, path) => {
    operatorApi.drivers.mockRejectedValueOnce(upstreamError(409, { status: 409, detail: 'A driver sync is running. Try again when it has finished.', availableAt: '2026-10-25T10:00:00+00:00' }));
    operatorApi.drivers.mockRejectedValueOnce(upstreamError(429, { status: 429, detail: 'Try again later.', availableAt: '2026-10-25T10:00:00+00:00' }, { 'retry-after': '900' }));

    const running = await request(appFor(operator)).post(path).send({});
    const tooSoon = await request(appFor(operator)).post(path).send({});

    expect(running.status).toBe(409);
    expect(running.body.availableAt).toBe('2026-10-25T10:00:00+00:00');
    expect(tooSoon.status).toBe(429);
    expect(tooSoon.body.availableAt).toBe('2026-10-25T10:00:00+00:00');
    expect(tooSoon.headers['retry-after']).toBe('900');
  });

  it('26: relays 409 while running with availableAt', async () => {
    operatorApi.drivers.mockRejectedValueOnce(upstreamError(409, { status: 409, detail: 'A driver sync is running. Try again when it has finished.', availableAt: '2026-10-25T10:00:00+00:00' }));

    const res = await request(appFor(operator)).post('/api/drivers/sync/runs').send({});

    expect(res.status).toBe(409);
    expect(res.body.availableAt).toBe('2026-10-25T10:00:00+00:00');
  });
});

describe('Portal backend: driver routes (storage, downloads, roll back, removal)', () => {
  const pair = `models/${modelId}/releases/Windows11_24H2`;

  beforeEach(() => {
    vi.clearAllMocks();
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    operatorApi.drivers.mockResolvedValue({ status: 200, data: { ok: true } });
  });

  it('16: rolls back with the token actor and relays the 409 without a previous set', async () => {
    operatorApi.drivers.mockRejectedValueOnce(upstreamError(409, { status: 409, detail: 'No previous driver set to roll back to.' }));

    const res = await request(appFor(operator)).post(`/api/drivers/${pair}/rollback`).send({ actor: { upn: 'mallory@contoso.com' } });

    expect(operatorApi.drivers).toHaveBeenCalledWith('POST', `${pair}/rollback`, { body: { actor } });
    expect(res.status).toBe(409);
    expect(res.body.detail).toBe('No previous driver set to roll back to.');
  });

  it('releases the set in testing with the token actor, relays the 409, and refuses Technicians', async () => {
    operatorApi.drivers.mockRejectedValueOnce(upstreamError(409, { status: 409, detail: 'No driver set in testing to release.' }));

    const res = await request(appFor(operator)).post(`/api/drivers/${pair}/testing/release`).send({ actor: { upn: 'mallory@contoso.com' } });
    const tech = await request(appFor(technician)).post(`/api/drivers/${pair}/testing/release`).send({});
    const bad = await request(appFor(operator)).post(`/api/drivers/models/${modelId}/releases/Windows12/testing/release`).send({});

    expect(operatorApi.drivers).toHaveBeenCalledWith('POST', `${pair}/testing/release`, { body: { actor } });
    expect(operatorApi.drivers).toHaveBeenCalledTimes(1);
    expect(res.status).toBe(409);
    expect(res.body.detail).toBe('No driver set in testing to release.');
    expect(tech.status).toBe(403);
    expect(bad.status).toBe(400);
  });

  it('10: reads the removal impact with an optional release, and refuses an unknown release', async () => {
    const app = appFor(operator);

    expect((await request(app).get(`/api/drivers/models/${modelId}/removal-impact`)).status).toBe(200);
    expect((await request(app).get(`/api/drivers/models/${modelId}/removal-impact?release=Windows11_24H2`)).status).toBe(200);
    expect((await request(app).get(`/api/drivers/models/${modelId}/removal-impact?release=Windows12`)).status).toBe(400);
    expect(operatorApi.drivers).toHaveBeenCalledWith('GET', `models/${modelId}/removal-impact`, { query: { release: undefined } });
    expect(operatorApi.drivers).toHaveBeenCalledWith('GET', `models/${modelId}/removal-impact`, { query: { release: 'Windows11_24H2' } });
    expect(operatorApi.drivers).toHaveBeenCalledTimes(2);
  });

  it('11, 9 and 15: removals send changedBy from the token and relay 204 and 409', async () => {
    operatorApi.drivers.mockResolvedValueOnce({ status: 204, data: '' });
    operatorApi.drivers.mockRejectedValueOnce(upstreamError(409, { status: 409, detail: 'Remove the model instead.' }));
    operatorApi.drivers.mockRejectedValueOnce(upstreamError(409, { status: 409, detail: 'Only a component the vendor no longer publishes can be removed.' }));
    const app = appFor(operator);

    const model = await request(app).delete(`/api/drivers/models/${modelId}?changedBy=mallory`);
    const release = await request(app).delete(`/api/drivers/${pair}`);
    const component = await request(app).delete(`/api/drivers/${pair}/components/sp100`);

    expect(model.status).toBe(204);
    expect(release.status).toBe(409);
    expect(release.body.detail).toBe('Remove the model instead.');
    expect(component.status).toBe(409);
    expect(operatorApi.drivers).toHaveBeenCalledWith('DELETE', `models/${modelId}`, { query: { changedBy: 'olle@contoso.com' } });
    expect(operatorApi.drivers).toHaveBeenCalledWith('DELETE', pair, { query: { changedBy: 'olle@contoso.com' } });
    expect(operatorApi.drivers).toHaveBeenCalledWith('DELETE', `${pair}/components/sp100`, { query: { changedBy: 'olle@contoso.com' } });
  });

  it('removals and roll back are refused for Technicians and bad segments without an upstream call', async () => {
    const tech = appFor(technician);
    const ops = appFor(operator);

    expect((await request(tech).delete(`/api/drivers/models/${modelId}`)).status).toBe(403);
    expect((await request(tech).post(`/api/drivers/${pair}/rollback`).send({})).status).toBe(403);
    expect((await request(tech).get(`/api/drivers/models/${modelId}/removal-impact`)).status).toBe(403);
    expect((await request(tech).put('/api/drivers/storage/settings').send({ previousVersionsToKeep: 2 })).status).toBe(403);
    expect((await request(ops).delete('/api/drivers/models/not-a-guid')).status).toBe(400);
    expect((await request(ops).delete(`/api/drivers/models/${modelId}/releases/Windows12`)).status).toBe(400);
    expect((await request(ops).delete(`/api/drivers/${pair}/components/sp%20100`)).status).toBe(400);
    expect(operatorApi.drivers).not.toHaveBeenCalled();
  });

  it('29, 31 and 32: storage, a model\'s storage and downloads are readable by Technicians', async () => {
    const app = appFor(technician);

    expect((await request(app).get('/api/drivers/storage')).status).toBe(200);
    expect((await request(app).get(`/api/drivers/storage/models/${modelId}`)).status).toBe(200);
    expect((await request(app).get('/api/drivers/downloads')).status).toBe(200);
    expect((await request(app).get('/api/drivers/storage/models/x')).status).toBe(400);
    expect(operatorApi.drivers).toHaveBeenCalledWith('GET', 'storage');
    expect(operatorApi.drivers).toHaveBeenCalledWith('GET', `storage/models/${modelId}`);
    expect(operatorApi.drivers).toHaveBeenCalledWith('GET', 'downloads');
  });

  it('46: saves only the number with the token actor and relays the 400', async () => {
    operatorApi.drivers.mockResolvedValueOnce({ status: 200, data: { previousVersionsToKeep: 2 } });
    operatorApi.drivers.mockRejectedValueOnce(upstreamError(400, { status: 400, detail: 'Choose a number from the list.' }));

    const ok = await request(appFor(operator)).put('/api/drivers/storage/settings').send({ previousVersionsToKeep: 2, actor: { upn: 'mallory@contoso.com' }, extra: 1 });
    const bad = await request(appFor(operator)).put('/api/drivers/storage/settings').send({ previousVersionsToKeep: 9 });

    expect(ok.status).toBe(200);
    expect(operatorApi.drivers).toHaveBeenCalledWith('PUT', 'storage/settings', { body: { previousVersionsToKeep: 2, actor } });
    expect(bad.status).toBe(400);
    expect(bad.body.detail).toBe('Choose a number from the list.');
  });

  it('30: previews for Technicians with only the image and session IDs, and checks their shape first', async () => {
    const sessionId = '1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed';
    const app = appFor(technician);

    const ok = await request(app).post('/api/drivers/assignment-preview').send({ osImageId: modelId, sessionIds: [sessionId], extra: 1 });
    const notGuid = await request(app).post('/api/drivers/assignment-preview').send({ osImageId: modelId, sessionIds: ['x'] });
    const tooMany = await request(app).post('/api/drivers/assignment-preview').send({ osImageId: modelId, sessionIds: Array.from({ length: 201 }, () => sessionId) });
    const noImage = await request(app).post('/api/drivers/assignment-preview').send({ sessionIds: [] });

    expect(ok.status).toBe(200);
    expect(operatorApi.drivers).toHaveBeenCalledWith('POST', 'assignment-preview', { body: { osImageId: modelId, sessionIds: [sessionId] } });
    expect(notGuid.status).toBe(400);
    expect(tooMany.status).toBe(400);
    expect(noImage.status).toBe(400);
    expect(operatorApi.drivers).toHaveBeenCalledTimes(1);
  });

  it('34: reads versions for Technicians, and passes check=true only for Operators', async () => {
    const path = `/api/drivers/${pair}/components/sp100/versions?check=true`;

    expect((await request(appFor(technician)).get(path)).status).toBe(200);
    expect((await request(appFor(operator)).get(path)).status).toBe(200);

    expect(operatorApi.drivers).toHaveBeenNthCalledWith(1, 'GET', `${pair}/components/sp100/versions`, { query: { check: undefined } });
    expect(operatorApi.drivers).toHaveBeenNthCalledWith(2, 'GET', `${pair}/components/sp100/versions`, { query: { check: 'true' } });
  });

  it('35 and 36: roll back with only version and until and the token actor, relay the 409, and undo with changedBy', async () => {
    operatorApi.drivers.mockRejectedValueOnce(upstreamError(409, { status: 409, detail: 'Version 1.0 could not be downloaded again from HP: hpia answered 404.' }));
    const app = appFor(operator);

    const conflict = await request(app).put(`/api/drivers/${pair}/components/sp100/rollback`).send({ version: '1.0', until: 'Undone', actor: { upn: 'mallory@contoso.com' }, extra: 1 });
    await request(app).delete(`/api/drivers/${pair}/components/sp100/rollback`);

    expect(operatorApi.drivers).toHaveBeenCalledWith('PUT', `${pair}/components/sp100/rollback`, { body: { version: '1.0', until: 'Undone', actor } });
    expect(conflict.status).toBe(409);
    expect(conflict.body.detail).toBe('Version 1.0 could not be downloaded again from HP: hpia answered 404.');
    expect(operatorApi.drivers).toHaveBeenCalledWith('DELETE', `${pair}/components/sp100/rollback`, { query: { changedBy: 'olle@contoso.com' } });
    expect((await request(appFor(technician)).put(`/api/drivers/${pair}/components/sp100/rollback`).send({})).status).toBe(403);
  });

  it('37, 37a and 37b: stage, commit only the block ids, and abandon, each as the token actor', async () => {
    const uploadId = '1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed';
    const app = appFor(operator);

    await request(app).post('/api/drivers/custom-components/uploads/start').send({ fileName: 'intel.zip', sizeBytes: 1024, extra: 1, actor: { upn: 'mallory@contoso.com' } });
    await request(app).post(`/api/drivers/custom-components/uploads/${uploadId}/complete`).send({ blockIds: ['MDAwMDAw'], extra: 1 });
    const badBlocks = await request(app).post(`/api/drivers/custom-components/uploads/${uploadId}/complete`).send({ blockIds: ['<script>'] });
    const badId = await request(app).post('/api/drivers/custom-components/uploads/x/complete').send({ blockIds: ['MDAwMDAw'] });
    await request(app).post(`/api/drivers/custom-components/uploads/${uploadId}/abandon`).send({});

    expect(operatorApi.drivers).toHaveBeenCalledWith('POST', 'custom-components/uploads/start', { body: { fileName: 'intel.zip', sizeBytes: 1024, actor } });
    expect(operatorApi.drivers).toHaveBeenCalledWith('POST', `custom-components/uploads/${uploadId}/complete`, { body: { blockIds: ['MDAwMDAw'], actor } });
    expect(operatorApi.drivers).toHaveBeenCalledWith('POST', `custom-components/uploads/${uploadId}/abandon`, { body: { actor } });
    expect(badBlocks.status).toBe(400);
    expect(badId.status).toBe(400);
    expect(operatorApi.drivers).toHaveBeenCalledTimes(3);
  });

  it('38 to 42: forward only the custom component fields with the token actor, relay field errors and the 409', async () => {
    const customId = '1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed';
    operatorApi.drivers.mockRejectedValueOnce(upstreamError(400, { status: 400, detail: 'Enter the silent install command.', errors: [{ field: 'installCommand', detail: 'Enter the silent install command.' }, { other: 'dropped' }] }));
    operatorApi.drivers.mockRejectedValueOnce(upstreamError(409, { status: 409, detail: 'Intel (pre-release) already replaces this component on HP EliteBook 840 G11.' }));
    const app = appFor(operator);
    const fields = { uploadId: customId, name: 'Intel', version: '24.0', action: 'Specialize', category: 'Driver - Network', installCommand: null, targets: [{ modelId, replacesLineageKey: 'sp100', extra: 1 }] };

    const invalid = await request(app).post('/api/drivers/custom-components').send({ ...fields, actor: { upn: 'mallory@contoso.com' }, blobName: 'x' });
    const conflict = await request(app).put(`/api/drivers/custom-components/${customId}`).send(fields);
    await request(app).delete(`/api/drivers/custom-components/${customId}`);
    await request(app).post(`/api/drivers/custom-components/${customId}/notices/${modelId}/dismiss`).send({});

    const forwarded = { ...fields, targets: [{ modelId, replacesLineageKey: 'sp100' }], actor };
    expect(operatorApi.drivers).toHaveBeenCalledWith('POST', 'custom-components', { body: forwarded });
    expect(invalid.status).toBe(400);
    expect(invalid.body.errors).toEqual([{ field: 'installCommand', detail: 'Enter the silent install command.' }]);
    expect(conflict.status).toBe(409);
    expect(operatorApi.drivers).toHaveBeenCalledWith('DELETE', `custom-components/${customId}`, { query: { changedBy: 'olle@contoso.com' } });
    expect(operatorApi.drivers).toHaveBeenCalledWith('POST', `custom-components/${customId}/notices/${modelId}/dismiss`, { body: { actor } });
    expect((await request(appFor(technician)).get('/api/drivers/custom-components')).status).toBe(200);
    expect((await request(appFor(technician)).post('/api/drivers/custom-components').send(fields)).status).toBe(403);
  });
});
