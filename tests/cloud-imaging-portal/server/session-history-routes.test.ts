import { describe, it, expect, vi, beforeEach } from 'vitest';
import express, { type NextFunction, type Response } from 'express';
import request from 'supertest';
import type { AuthenticatedRequest } from '@/middleware/auth.js';

const operatorApi = vi.hoisted(() => ({
  getSessionHistory: vi.fn(),
}));

vi.mock('@/services/operatorApiClient.js', () => ({ operatorApiClient: operatorApi }));

const { sessionHistoryRouter } = await import('@/routes/session-history.js');

type Claims = Record<string, unknown>;

function appFor(claims: Claims) {
  const app = express();
  app.use(express.json());
  app.use((req: AuthenticatedRequest, _res: Response, next: NextFunction) => { req.user = claims as AuthenticatedRequest['user']; next(); });
  app.use('/api/session-history', sessionHistoryRouter);
  return app;
}

const admin: Claims = { roles: ['CloudImaging.Administrator'] };
const operator: Claims = { roles: ['CloudImaging.Operator'] };
const technician: Claims = { roles: ['CloudImaging.Technician'] };
const reader: Claims = { roles: ['CloudImaging.Reader'] };
const approver: Claims = { roles: ['CloudImaging.AutopilotApprover'] };

const HISTORY_DETAIL = 'This operation requires one of the following roles: CloudImaging.Reader, CloudImaging.Operator, CloudImaging.Administrator.';
const TIMESTAMPS_DETAIL = 'This operation requires one of the following roles: CloudImaging.PortalAccess, CloudImaging.Reader.';
const FROM = '2026-10-08T00:00:00.000Z';
const TO = '2026-10-22T00:00:00.000Z';

function record(finalState: string, terminalAt: string) {
  return {
    sessionId: '0f8fad5b-d9cb-469f-a165-70867728950e',
    finalState,
    deviceSerialNumber: 'SN-001',
    deviceManufacturer: 'Contoso',
    deviceModel: 'Laptop 7',
    locationId: '7c9e6679-7425-40de-944b-e07fc1f90ae7',
    locationName: 'Stockholm',
    preFlightAuthorizationResult: 'Authorized',
    preFlightChecks: [{ checkName: 'SecureBoot', passed: true }],
    architecture: 'X64',
    assignedOsImageId: 'a3bb189e-8bf9-3888-9912-ace4e6543002',
    failedStepName: finalState === 'SessionFailed' ? 'ApplyImage' : null,
    errorDetail: finalState === 'SessionFailed' ? 'Disk write failed.' : null,
    createdAt: '2026-10-20T08:00:00.000Z',
    terminalAt,
  };
}

const HISTORY = [
  record('SessionCompleted', '2026-10-20T09:00:00.000Z'),
  record('SessionFailed', '2026-10-20T10:00:00.000Z'),
  record('SessionUnauthorized', '2026-10-21T07:00:00.000Z'),
  record('SessionCompleted', '2026-10-21T11:30:00.000Z'),
];

describe('Portal backend: session history routes', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    operatorApi.getSessionHistory.mockResolvedValue(HISTORY);
  });

  describe('GET /api/session-history', () => {
    it('refuses Technician, naming Reader, Operator and Administrator', async () => {
      const res = await request(appFor(technician)).get('/api/session-history');
      expect(res.status).toBe(403);
      expect(res.body.detail).toBe(HISTORY_DETAIL);
      expect(operatorApi.getSessionHistory).not.toHaveBeenCalled();
    });

    it('refuses AutopilotApprover', async () => {
      const res = await request(appFor(approver)).get('/api/session-history');
      expect(res.status).toBe(403);
      expect(res.body.detail).toBe(HISTORY_DETAIL);
    });

    it.each([
      ['Reader', reader],
      ['Operator', operator],
      ['Administrator', admin],
    ])('returns the full records to %s', async (_name, claims) => {
      const res = await request(appFor(claims)).get('/api/session-history').query({ from: FROM, to: TO });
      expect(res.status).toBe(200);
      expect(res.body).toEqual(HISTORY);
      expect(operatorApi.getSessionHistory).toHaveBeenCalledWith(FROM, TO);
    });

    it('forwards a failure from the Operator API to the error handler', async () => {
      operatorApi.getSessionHistory.mockRejectedValue(new Error('upstream down'));
      const res = await request(appFor(reader)).get('/api/session-history');
      expect(res.status).toBe(500);
    });
  });

  describe('GET /api/session-history/completed-timestamps', () => {
    it.each([
      ['Administrator', admin],
      ['Operator', operator],
      ['Technician', technician],
      ['Reader', reader],
    ])('returns completion times to %s', async (_name, claims) => {
      const res = await request(appFor(claims)).get('/api/session-history/completed-timestamps');
      expect(res.status).toBe(200);
      expect(res.body).toEqual(['2026-10-20T09:00:00.000Z', '2026-10-21T11:30:00.000Z']);
    });

    it('refuses AutopilotApprover', async () => {
      const res = await request(appFor(approver)).get('/api/session-history/completed-timestamps');
      expect(res.status).toBe(403);
      expect(res.body.detail).toBe(TIMESTAMPS_DETAIL);
      expect(operatorApi.getSessionHistory).not.toHaveBeenCalled();
    });

    it('forwards from and to unchanged', async () => {
      await request(appFor(technician)).get('/api/session-history/completed-timestamps').query({ from: FROM, to: TO });
      expect(operatorApi.getSessionHistory).toHaveBeenCalledWith(FROM, TO);
    });

    it('forwards no range when none is given', async () => {
      await request(appFor(technician)).get('/api/session-history/completed-timestamps');
      expect(operatorApi.getSessionHistory).toHaveBeenCalledWith(undefined, undefined);
    });

    it('returns only strings, with no device, location, pre-flight, image or error detail', async () => {
      const res = await request(appFor(technician)).get('/api/session-history/completed-timestamps');
      expect(Array.isArray(res.body)).toBe(true);
      for (const item of res.body as unknown[]) expect(typeof item).toBe('string');
      const body = res.text;
      for (const leaked of ['SN-001', 'Contoso', 'Laptop 7', 'Stockholm', 'SecureBoot', 'a3bb189e', 'Disk write failed', 'ApplyImage', 'sessionId']) {
        expect(body).not.toContain(leaked);
      }
    });

    it('returns an empty array when the Operator API returns no list', async () => {
      operatorApi.getSessionHistory.mockResolvedValue(null);
      const res = await request(appFor(reader)).get('/api/session-history/completed-timestamps');
      expect(res.status).toBe(200);
      expect(res.body).toEqual([]);
    });

    it('forwards a failure from the Operator API to the error handler', async () => {
      operatorApi.getSessionHistory.mockRejectedValue(new Error('upstream down'));
      const res = await request(appFor(technician)).get('/api/session-history/completed-timestamps');
      expect(res.status).toBe(500);
    });

    it('is registered before any parameterised path in the router', () => {
      const paths = sessionHistoryRouter.stack
        .map(layer => (layer.route as { path: string } | undefined)?.path)
        .filter((p): p is string => typeof p === 'string');
      const ownIndex = paths.indexOf('/completed-timestamps');
      expect(ownIndex).toBeGreaterThanOrEqual(0);
      const firstParameterised = paths.findIndex(p => p.includes(':'));
      if (firstParameterised >= 0) expect(ownIndex).toBeLessThan(firstParameterised);
    });
  });
});
