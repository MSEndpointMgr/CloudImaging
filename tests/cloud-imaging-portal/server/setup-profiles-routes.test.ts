import { describe, it, expect, vi, beforeEach } from 'vitest';
import express, { type NextFunction, type Response } from 'express';
import request from 'supertest';
import { AxiosError, AxiosHeaders, type InternalAxiosRequestConfig } from 'axios';
import type { AuthenticatedRequest } from '@/middleware/auth.js';

const operatorApi = vi.hoisted(() => ({
  getSetupProfiles: vi.fn(),
  getSetupProfile: vi.fn(),
  createSetupProfile: vi.fn(),
  updateSetupProfile: vi.fn(),
  deleteSetupProfile: vi.fn(),
  restoreSetupProfileVersion: vi.fn(),
  getSetupProfileVersions: vi.fn(),
  getSetupProfileVersion: vi.fn(),
  getSetupProfileHistory: vi.fn(),
  uploadSetupProfileContent: vi.fn(),
  getSetupProfileContent: vi.fn(),
  getWifiNetworks: vi.fn(),
  createWifiNetwork: vi.fn(),
}));

vi.mock('@/services/operatorApiClient.js', () => ({ operatorApiClient: operatorApi }));

const { setupProfilesRouter } = await import('@/routes/setup-profiles.js');
const { wifiNetworksRouter } = await import('@/routes/wifi-networks.js');
const { errorHandler } = await import('@/middleware/errorHandler.js');

const profileId = '8f0c3c5e-6c1f-4c55-9a43-2f5d8c1e7a10';
const locationId = '1b0c3c5e-6c1f-4c55-9a43-2f5d8c1e7a10';
const operator = { roles: ['CloudImaging.Operator'], preferred_username: 'olle@contoso.com', oid: 'oid-olle' };

/** Mounts the body parsers the way app.ts does, so the size limits are tested as deployed. */
function appFor(claims: Record<string, unknown>) {
  const app = express();
  app.use('/api/setup-profiles/content', express.raw({ limit: '25mb' }));
  app.use('/api/setup-profiles', express.json({ limit: '4mb' }));
  app.use(express.json({ limit: '1mb' }));
  app.use((req: AuthenticatedRequest, _res: Response, next: NextFunction) => { req.user = claims as AuthenticatedRequest['user']; next(); });
  app.use('/api/setup-profiles', setupProfilesRouter);
  app.use('/api/wifi-networks', wifiNetworksRouter);
  app.use(errorHandler);
  return app;
}

function upstreamError(status: number, data: unknown): AxiosError {
  const config = { headers: new AxiosHeaders() } as InternalAxiosRequestConfig;
  return new AxiosError('Request failed', 'ERR_BAD_REQUEST', config, undefined, { status, statusText: '', headers: {}, config, data });
}

const targeting = { allDevices: false, locationIds: [locationId] };
const content = { failureHandling: 'CompleteWithWarnings', registryValues: [], scripts: [], answerFile: null };

describe('Portal backend: setup profile routes', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    operatorApi.getSetupProfiles.mockResolvedValue([]);
    operatorApi.getSetupProfile.mockResolvedValue({ profileId });
    operatorApi.createSetupProfile.mockResolvedValue({ profileId });
    operatorApi.updateSetupProfile.mockResolvedValue({ profileId });
    operatorApi.deleteSetupProfile.mockResolvedValue(undefined);
  });

  it('lists profiles', async () => {
    const res = await request(appFor(operator)).get('/api/setup-profiles');

    expect(res.status).toBe(200);
    expect(operatorApi.getSetupProfiles).toHaveBeenCalledOnce();
  });

  it('reads one profile by id', async () => {
    const res = await request(appFor(operator)).get(`/api/setup-profiles/${profileId}`);

    expect(res.status).toBe(200);
    expect(operatorApi.getSetupProfile).toHaveBeenCalledWith(profileId);
  });

  it('creates with the actor from the token and drops actor fields sent by the browser', async () => {
    const res = await request(appFor(operator)).post('/api/setup-profiles').send({
      name: 'Stockholm', targeting, duplicatedFrom: null, content,
      actor: { upn: 'mallory@contoso.com' }, createdBy: 'mallory', changedBy: 'mallory', lastModifiedBy: 'mallory',
    });

    expect(res.status).toBe(201);
    expect(operatorApi.createSetupProfile).toHaveBeenCalledWith({
      name: 'Stockholm', targeting, duplicatedFrom: null, content, actor: { upn: 'olle@contoso.com', objectId: 'oid-olle' },
    });
  });

  it('saves with the actor from the token and drops actor fields sent by the browser', async () => {
    const res = await request(appFor(operator)).put(`/api/setup-profiles/${profileId}`).send({
      baseVersion: 7, name: 'Stockholm', targeting, actor: { upn: 'mallory@contoso.com' }, lastModifiedBy: 'mallory',
    });

    expect(res.status).toBe(200);
    expect(operatorApi.updateSetupProfile).toHaveBeenCalledWith(profileId, {
      baseVersion: 7, name: 'Stockholm', targeting, actor: { upn: 'olle@contoso.com', objectId: 'oid-olle' },
    });
  });

  it('deletes with changedBy from the token', async () => {
    const res = await request(appFor(operator)).delete(`/api/setup-profiles/${profileId}?changedBy=mallory`);

    expect(res.status).toBe(204);
    expect(operatorApi.deleteSetupProfile).toHaveBeenCalledWith(profileId, 'olle@contoso.com');
  });

  it('restores with the actor from the token', async () => {
    operatorApi.restoreSetupProfileVersion.mockResolvedValue({ profileId });

    const res = await request(appFor(operator)).post(`/api/setup-profiles/${profileId}/restore`).send({ version: 5, baseVersion: 7, actor: { upn: 'mallory@contoso.com' } });

    expect(res.status).toBe(200);
    expect(operatorApi.restoreSetupProfileVersion).toHaveBeenCalledWith(profileId, { version: 5, baseVersion: 7, actor: { upn: 'olle@contoso.com', objectId: 'oid-olle' } });
  });

  it('reads versions, one version and history', async () => {
    operatorApi.getSetupProfileVersions.mockResolvedValue([]);
    operatorApi.getSetupProfileVersion.mockResolvedValue({ version: 999999 });
    operatorApi.getSetupProfileHistory.mockResolvedValue([]);
    const app = appFor(operator);

    expect((await request(app).get(`/api/setup-profiles/${profileId}/versions`)).status).toBe(200);
    expect((await request(app).get(`/api/setup-profiles/${profileId}/versions/999999`)).status).toBe(200);
    expect((await request(app).get(`/api/setup-profiles/${profileId}/history`)).status).toBe(200);
    expect(operatorApi.getSetupProfileVersion).toHaveBeenCalledWith(profileId, 999999);
  });

  it('checks the version after the role guard', async () => {
    const res = await request(appFor({ roles: ['CloudImaging.Reader'] })).get(`/api/setup-profiles/${profileId}/versions/0`);

    expect(res.status).toBe(403);
  });

  it('accepts a 3 MB body on setup profiles and still refuses 2 MB on another router', async () => {
    const big = 'x'.repeat(3 * 1024 * 1024);
    const accepted = await request(appFor(operator)).put(`/api/setup-profiles/${profileId}`).send({ baseVersion: 1, name: 'Big', targeting, content: { ...content, padding: big } });
    const refused = await request(appFor(operator)).post('/api/wifi-networks').send({ ssid: 'x'.repeat(2 * 1024 * 1024) });

    expect(accepted.status).toBe(200);
    expect(refused.status).toBe(413);
    expect(operatorApi.createWifiNetwork).not.toHaveBeenCalled();
  });

  it.each([
    ['get', '/api/setup-profiles/not-a-guid'],
    ['put', '/api/setup-profiles/not-a-guid'],
    ['delete', '/api/setup-profiles/not-a-guid'],
    ['get', `/api/setup-profiles/${profileId}/versions/0`],
    ['get', `/api/setup-profiles/${profileId}/versions/1000000`],
    ['get', `/api/setup-profiles/${profileId}/versions/abc`],
  ] as const)('refuses %s %s with 400 without an upstream call', async (method, path) => {
    const res = await request(appFor(operator))[method](path).send({});

    expect(res.status).toBe(400);
    for (const mock of Object.values(operatorApi)) expect(mock).not.toHaveBeenCalled();
  });

  it.each([400, 404, 409])('relays an upstream %i detail', async (status) => {
    operatorApi.updateSetupProfile.mockRejectedValue(upstreamError(status, { title: 'Refused', detail: 'This profile was changed by ops2@contoso.com at 2026-10-23 10:41. Reload to see their changes.' }));

    const res = await request(appFor(operator)).put(`/api/setup-profiles/${profileId}`).send({ baseVersion: 7, name: 'Stockholm', targeting });

    expect(res.status).toBe(status);
    expect(res.body.detail).toBe('This profile was changed by ops2@contoso.com at 2026-10-23 10:41. Reload to see their changes.');
  });

  it('truncates a long upstream detail to 500 characters', async () => {
    operatorApi.createSetupProfile.mockRejectedValue(upstreamError(400, { detail: 'a'.repeat(900) }));

    const res = await request(appFor(operator)).post('/api/setup-profiles').send({ name: 'x', targeting, content });

    expect(res.status).toBe(400);
    expect(res.body.detail.length).toBe(501);
  });

  it('relays at most 50 upstream errors with only code, line and message', async () => {
    const errors = Array.from({ length: 60 }, (_, i) => ({ code: 'plainTextPassword', line: i + 1, message: 'm'.repeat(400), secret: 'x' }));
    operatorApi.createSetupProfile.mockRejectedValue(upstreamError(400, { detail: 'The answer file has 60 problems.', errors, internal: 'stack' }));

    const res = await request(appFor(operator)).post('/api/setup-profiles').send({ name: 'x', targeting, content });

    expect(res.status).toBe(400);
    expect(res.body.errors).toHaveLength(50);
    expect(Object.keys(res.body.errors[0] as object).sort()).toEqual(['code', 'line', 'message']);
    expect(res.body.errors[0].message).toHaveLength(300);
    expect(res.body).not.toHaveProperty('internal');
  });

  describe('script and answer file texts', () => {
    const sha = 'a'.repeat(64);
    const technician = { roles: ['CloudImaging.Technician'], preferred_username: 'tina@contoso.com', oid: 'oid-tina' };

    it('relays the raw bytes, the kind and the uploading user from the token, and nothing else the browser sent', async () => {
      operatorApi.uploadSetupProfileContent.mockResolvedValue({ sha256: sha, sizeBytes: 9 });
      const bytes = Buffer.from('\uFEFFexit 0 \u00e4', 'utf8');

      const res = await request(appFor(operator))
        .post('/api/setup-profiles/content?kind=answerFile&actor=mallory&uploadedBy=mallory@contoso.com')
        .set('Content-Type', 'application/octet-stream')
        .send(bytes);

      expect(res.status).toBe(200);
      expect(res.body).toEqual({ sha256: sha, sizeBytes: 9 });
      const [kind, body, uploadedBy] = operatorApi.uploadSetupProfileContent.mock.calls[0] as [string, Buffer, string];
      expect(kind).toBe('answerFile');
      expect(Buffer.compare(body, bytes)).toBe(0);
      expect(uploadedBy).toBe('olle@contoso.com');
    });

    it('passes an unknown kind through for Imaging Core to refuse', async () => {
      operatorApi.uploadSetupProfileContent.mockRejectedValue(upstreamError(400, { detail: 'Upload a script or an answer file.' }));

      const res = await request(appFor(operator)).post('/api/setup-profiles/content?kind=driver').set('Content-Type', 'application/octet-stream').send(Buffer.from('x'));

      expect(res.status).toBe(400);
      expect(operatorApi.uploadSetupProfileContent).toHaveBeenCalledWith('driver', expect.any(Buffer), 'olle@contoso.com');
    });

    it('accepts 24 MB plus a BOM and refuses more than 25 MB without an upstream call', async () => {
      operatorApi.uploadSetupProfileContent.mockResolvedValue({ sha256: sha, sizeBytes: 25_165_824 });
      const app = appFor(operator);

      const accepted = await request(app).post('/api/setup-profiles/content?kind=script').set('Content-Type', 'application/octet-stream').send(Buffer.alloc(25_165_824 + 3, 0x78));
      const refused = await request(app).post('/api/setup-profiles/content?kind=script').set('Content-Type', 'application/octet-stream').send(Buffer.alloc(25 * 1024 * 1024 + 1, 0x78));

      expect(accepted.status).toBe(200);
      expect(refused.status).toBe(413);
      expect(operatorApi.uploadSetupProfileContent).toHaveBeenCalledOnce();
    });

    it('relays the 413 Imaging Core gives a script over 24 MB', async () => {
      operatorApi.uploadSetupProfileContent.mockRejectedValue(upstreamError(413, { detail: 'This script is 24.5 MB. A script can be at most 24 MB.' }));

      const res = await request(appFor(operator)).post('/api/setup-profiles/content?kind=script').set('Content-Type', 'application/octet-stream').send(Buffer.from('x'));

      expect(res.status).toBe(413);
      expect(res.body.detail).toBe('This script is 24.5 MB. A script can be at most 24 MB.');
    });

    it('refuses a JSON body on the upload route', async () => {
      const res = await request(appFor(operator)).post('/api/setup-profiles/content?kind=script').send({ text: 'exit 0' });

      expect(res.status).toBe(400);
      expect(operatorApi.uploadSetupProfileContent).not.toHaveBeenCalled();
    });

    it('keeps the 4 MB JSON limit on the rest of the router', async () => {
      const res = await request(appFor(operator)).put(`/api/setup-profiles/${profileId}`).send({ baseVersion: 1, name: 'x'.repeat(5 * 1024 * 1024), targeting });

      expect(res.status).toBe(413);
    });

    it('reads a text with its media type for a technician', async () => {
      operatorApi.getSetupProfileContent.mockResolvedValue({ data: Buffer.from('Write-Output h\u00e4j', 'utf8'), contentType: 'text/plain; charset=utf-8' });

      const res = await request(appFor(technician)).get(`/api/setup-profiles/${profileId}/content/${sha}`);

      expect(res.status).toBe(200);
      expect(res.headers['content-type']).toBe('text/plain; charset=utf-8');
      expect(res.text).toBe('Write-Output h\u00e4j');
      expect(operatorApi.getSetupProfileContent).toHaveBeenCalledWith(profileId, sha);
    });

    it.each(['A'.repeat(64), 'a'.repeat(63), `${'a'.repeat(63)}g`, '..%2F000001.json'])('refuses the hash %s with 400 without an upstream call', async (bad) => {
      const res = await request(appFor(operator)).get(`/api/setup-profiles/${profileId}/content/${bad}`);

      expect(res.status).toBe(400);
      expect(operatorApi.getSetupProfileContent).not.toHaveBeenCalled();
    });

    it('relays a 404 for a text the profile does not use', async () => {
      operatorApi.getSetupProfileContent.mockRejectedValue(upstreamError(404, ''));

      expect((await request(appFor(operator)).get(`/api/setup-profiles/${profileId}/content/${sha}`)).status).toBe(404);
    });

    it('lets a technician read but not upload', async () => {
      const res = await request(appFor(technician)).post('/api/setup-profiles/content?kind=script').set('Content-Type', 'application/octet-stream').send(Buffer.from('x'));

      expect(res.status).toBe(403);
      expect(operatorApi.uploadSetupProfileContent).not.toHaveBeenCalled();
    });
  });
});
