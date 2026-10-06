import { describe, it, expect, vi, beforeEach } from 'vitest';
import express, { type NextFunction, type Response } from 'express';
import request from 'supertest';
import type { AuthenticatedRequest } from '@/middleware/auth.js';

const operatorApi = vi.hoisted(() => ({
  listAutopilotRegistrations: vi.fn(),
  listHandledAutopilotRegistrations: vi.fn(),
  getAutopilotRegistration: vi.fn(),
  decideAutopilotRegistration: vi.fn(),
  listAutopilotGroupTags: vi.fn(),
  createAutopilotGroupTag: vi.fn(),
  updateAutopilotGroupTag: vi.fn(),
  deleteAutopilotGroupTag: vi.fn(),
}));

vi.mock('@/services/operatorApiClient.js', () => ({ operatorApiClient: operatorApi }));

const { autopilotRouter, decidingUser } = await import('@/routes/autopilot.js');

const REQUEST_ID = '2b0f4c7e-1d3a-4c55-9a8e-6f1e2d3c4b5a';
const TAG_ID = '9d8c7b6a-5f4e-4d3c-8b2a-1f0e9d8c7b6a';

type Claims = Record<string, unknown>;

/** Mounts the router behind a stand-in for the auth middleware that injects the given claims. */
function appFor(claims: Claims) {
  const app = express();
  app.use(express.json());
  app.use((req: AuthenticatedRequest, _res: Response, next: NextFunction) => { req.user = claims as AuthenticatedRequest['user']; next(); });
  app.use('/api/autopilot', autopilotRouter);
  return app;
}

const approver: Claims = { roles: ['CloudImaging.AutopilotApprover'], preferred_username: 'approver@contoso.com', oid: 'oid-approver' };
const admin: Claims = { roles: ['CloudImaging.Administrator'], preferred_username: 'admin@contoso.com', oid: 'oid-admin' };
const technician: Claims = { roles: ['CloudImaging.Technician'], preferred_username: 'tech@contoso.com' };
const reader: Claims = { roles: ['CloudImaging.Reader'], preferred_username: 'reader@contoso.com' };

describe('Portal backend: Autopilot registration routes', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    operatorApi.listAutopilotRegistrations.mockResolvedValue([]);
    operatorApi.listHandledAutopilotRegistrations.mockResolvedValue([]);
    operatorApi.getAutopilotRegistration.mockResolvedValue({ requestId: REQUEST_ID });
    operatorApi.decideAutopilotRegistration.mockResolvedValue({ requestId: REQUEST_ID, state: 'Importing' });
    operatorApi.listAutopilotGroupTags.mockResolvedValue([]);
    operatorApi.createAutopilotGroupTag.mockResolvedValue({ tagId: TAG_ID });
    operatorApi.deleteAutopilotGroupTag.mockResolvedValue(undefined);
  });

  it.each([
    ['Approver', approver, 200],
    ['Administrator', admin, 200],
    ['Technician', technician, 200],
    ['Reader', reader, 403],
  ])('shows the queue to %s with status %i', async (_name, claims, status) => {
    const res = await request(appFor(claims)).get('/api/autopilot/registrations');
    expect(res.status).toBe(status);
    if (status === 200) expect(operatorApi.listAutopilotRegistrations).toHaveBeenCalledWith();
  });

  it.each([
    ['Approver', approver, 200],
    ['Administrator', admin, 200],
    ['Reader', reader, 200],
    ['Technician', technician, 403],
  ])('shows handled requests to %s with status %i', async (_name, claims, status) => {
    const res = await request(appFor(claims)).get('/api/autopilot/history?from=2026-09-01T00:00:00.000Z&to=2026-10-01T23:59:59.999Z');
    expect(res.status).toBe(status);
    if (status === 200) expect(operatorApi.listHandledAutopilotRegistrations).toHaveBeenCalledWith('2026-09-01T00:00:00.000Z', '2026-10-01T23:59:59.999Z');
  });

  it.each(['yesterday', '2026-13-45', "2026-01-01' or 1 eq 1"])('rejects the history date %s', async (from) => {
    const res = await request(appFor(admin)).get(`/api/autopilot/history?from=${encodeURIComponent(from)}`);
    expect(res.status).toBe(400);
    expect(operatorApi.listHandledAutopilotRegistrations).not.toHaveBeenCalled();
  });

  it.each([
    ['Approver', approver, 200],
    ['Administrator', admin, 200],
    ['Technician', technician, 403],
    ['Reader', reader, 403],
  ])('lets %s approve with status %i', async (_name, claims, status) => {
    const res = await request(appFor(claims)).post(`/api/autopilot/registrations/${REQUEST_ID}/approve`).send({ groupTagDefinitionId: TAG_ID });
    expect(res.status).toBe(status);
  });

  it('records the decision under the caller identity from the token, not the request body', async () => {
    await request(appFor(approver))
      .post(`/api/autopilot/registrations/${REQUEST_ID}/approve`)
      .send({ groupTagDefinitionId: TAG_ID, decidedByUpn: 'someone-else@contoso.com' });

    expect(operatorApi.decideAutopilotRegistration).toHaveBeenCalledWith(REQUEST_ID, 'approve', {
      decidedByUpn: 'approver@contoso.com',
      decidedByObjectId: 'oid-approver',
      groupTagDefinitionId: TAG_ID,
    });
  });

  it('rejects a group tag id that is not a GUID', async () => {
    const res = await request(appFor(approver)).post(`/api/autopilot/registrations/${REQUEST_ID}/approve`).send({ groupTagDefinitionId: 'EMEA' });
    expect(res.status).toBe(400);
    expect(operatorApi.decideAutopilotRegistration).not.toHaveBeenCalled();
  });

  it('trims the reject reason and caps its length', async () => {
    await request(appFor(admin)).post(`/api/autopilot/registrations/${REQUEST_ID}/reject`).send({ reason: '  Not a company device  ' });
    expect(operatorApi.decideAutopilotRegistration).toHaveBeenCalledWith(REQUEST_ID, 'reject', expect.objectContaining({ reason: 'Not a company device' }));

    const tooLong = await request(appFor(admin)).post(`/api/autopilot/registrations/${REQUEST_ID}/reject`).send({ reason: 'x'.repeat(501) });
    expect(tooLong.status).toBe(400);
  });

  it('refuses a decision when the token names nobody', async () => {
    const res = await request(appFor({ roles: ['CloudImaging.AutopilotApprover'] })).post(`/api/autopilot/registrations/${REQUEST_ID}/retry`);
    expect(res.status).toBe(401);
  });

  it('rejects a request id that is not a GUID', async () => {
    const res = await request(appFor(approver)).get('/api/autopilot/registrations/not-a-guid');
    expect(res.status).toBe(400);
  });

  it.each([
    ['Approver', approver, 200, 403],
    ['Administrator', admin, 200, 201],
    ['Technician', technician, 403, 403],
  ])('group tags: %s reads with %i and creates with %i', async (_name, claims, readStatus, writeStatus) => {
    const app = appFor(claims);
    expect((await request(app).get('/api/autopilot/group-tags')).status).toBe(readStatus);
    expect((await request(app).post('/api/autopilot/group-tags').send({ name: 'EMEA', kind: 'Static', value: 'EMEA' })).status).toBe(writeStatus);
  });

  it('forwards only group tag fields to the Operator API', async () => {
    await request(appFor(admin)).post('/api/autopilot/group-tags').send({ name: 'Site', kind: 'Template', value: '{CountryCode}-STD', description: 'Per site', tagId: TAG_ID, createdBy: 'x' });
    expect(operatorApi.createAutopilotGroupTag).toHaveBeenCalledWith({ name: 'Site', kind: 'Template', value: '{CountryCode}-STD', description: 'Per site' });
  });
});

describe('Portal backend: decidingUser', () => {
  const req = (claims: Claims) => ({ user: claims }) as unknown as AuthenticatedRequest;

  it('prefers preferred_username, then upn, unique_name, email', () => {
    expect(decidingUser(req({ upn: 'u@c.com', email: 'e@c.com' }))?.decidedByUpn).toBe('u@c.com');
    expect(decidingUser(req({ email: 'e@c.com' }))?.decidedByUpn).toBe('e@c.com');
  });

  it('falls back to the object id when no name claim is present', () => {
    expect(decidingUser(req({ oid: 'oid-1' }))).toEqual({ decidedByUpn: 'oid-1', decidedByObjectId: 'oid-1' });
  });

  it('returns null for a token without any identity claim', () => {
    expect(decidingUser(req({}))).toBeNull();
  });
});
