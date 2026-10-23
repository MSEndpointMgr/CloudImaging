import { describe, it, expect, vi, beforeAll, afterAll, beforeEach } from 'vitest';
import express, { type NextFunction, type Response, type Router } from 'express';
import request from 'supertest';
import type { AuthenticatedRequest } from '@/middleware/auth.js';

/**
 * Route-by-role enforcement matrix for every authenticated Portal backend route, driven by the
 * "Accepted after" and "Accepted before" columns of specs/004-operator-role/contracts/portal-server-routes.md.
 * Every router is mounted at its app.ts path behind an injected-claims auth stand-in.
 */

const operatorApi = vi.hoisted(() => {
  const resolved = (value: unknown = {}) => vi.fn().mockResolvedValue(value);
  return {
    getSessions: resolved([]),
    getSession: resolved(),
    coupleSession: resolved(),
    assignSession: resolved(),
    bulkAssign: resolved(),
    getSessionHistory: resolved([]),
    cancelSession: resolved(undefined),
    listPreFlightOverrides: resolved([]),
    approvePreFlightOverride: resolved(),
    revokePreFlightOverride: resolved(undefined),
    getImages: resolved([]),
    createImage: resolved(),
    updateImage: resolved(),
    deleteImage: resolved(undefined),
    startOsImageUpload: resolved(),
    publishOsImageUpload: resolved(),
    abandonOsImageUpload: resolved(undefined),
    getUploadJob: resolved(),
    getBootImages: resolved([]),
    startBootImageUpload: resolved(),
    publishBootImageUpload: resolved(),
    deleteBootImage: resolved(undefined),
    promoteBootImage: resolved(),
    demoteBootImage: resolved(),
    getConfiguration: resolved({ updateCheckEnabled: false }),
    putConfiguration: resolved(undefined),
    getPartitioningScheme: resolved(),
    putPartitioningScheme: resolved(undefined),
    getRecoveryImages: resolved([]),
    deleteRecoveryImage: resolved(undefined),
    startRecoveryImageUpload: resolved(),
    publishRecoveryImageUpload: resolved(),
    getSessionLogs: resolved([]),
    getSessionLogDownloadUrl: resolved(),
    getBootMediaCertMetadata: resolved(),
    getBranding: resolved(),
    putBranding: resolved(undefined),
    getBrandingLogoSas: resolved(),
    uploadBrandingLogo: resolved(),
    uploadBrandingPortalLogo: resolved(),
    getBrandingLogoContent: resolved({ data: Buffer.from('logo'), contentType: 'image/png' }),
    getBrandingPortalLogoContent: resolved({ data: Buffer.from('logo'), contentType: 'image/png' }),
    deleteBrandingLogo: resolved(),
    deleteBrandingPortalLogo: resolved(),
    getLocations: resolved([]),
    createLocation: resolved(),
    updateLocation: resolved(),
    deleteLocation: resolved(undefined),
    getRegions: resolved(),
    putRegions: resolved(undefined),
    getWifiNetworks: resolved([]),
    createWifiNetwork: resolved(),
    updateWifiNetwork: resolved(),
    deleteWifiNetwork: resolved(undefined),
    setWifiNetworkOrder: resolved(undefined),
    setLocationWifiNetworks: resolved(undefined),
    getUserLocationPreference: resolved(),
    putUserLocationPreference: resolved(),
    listAutopilotRegistrations: resolved([]),
    listHandledAutopilotRegistrations: resolved([]),
    getAutopilotRegistration: resolved(),
    decideAutopilotRegistration: resolved(),
    listAutopilotGroupTags: resolved([]),
    createAutopilotGroupTag: resolved(),
    updateAutopilotGroupTag: resolved(),
    deleteAutopilotGroupTag: resolved(undefined),
    // routes/cert.ts posts generate and rotate through the client's raw axios instance.
    http: { post: resolved({ data: {} }) },
  };
});

const updateCheck = vi.hoisted(() => ({
  getUpdateStatus: vi.fn().mockResolvedValue({ enabled: false, updateAvailable: false }),
}));

vi.mock('@/services/operatorApiClient.js', () => ({ operatorApiClient: operatorApi }));
vi.mock('@/services/updateCheck.js', () => updateCheck);

const { sessionsRouter } = await import('@/routes/sessions.js');
const { imagesRouter } = await import('@/routes/images.js');
const { brandingRouter } = await import('@/routes/branding.js');
const { bootImagesRouter } = await import('@/routes/boot-images.js');
const { certRouter } = await import('@/routes/cert.js');
const { portalConfigRouter } = await import('@/routes/portal-config.js');
const { partitioningSchemeRouter } = await import('@/routes/partitioning-scheme.js');
const { recoveryImagesRouter } = await import('@/routes/recovery-images.js');
const { sessionHistoryRouter } = await import('@/routes/session-history.js');
const { locationsRouter } = await import('@/routes/locations.js');
const { regionsRouter } = await import('@/routes/regions.js');
const { wifiNetworksRouter } = await import('@/routes/wifi-networks.js');
const { userPreferencesRouter } = await import('@/routes/user-preferences.js');
const { uploadJobsRouter } = await import('@/routes/upload-jobs.js');
const { updateCheckRouter } = await import('@/routes/update-check.js');
const { autopilotRouter } = await import('@/routes/autopilot.js');
const { preFlightOverridesRouter } = await import('@/routes/preflight-overrides.js');

const MOUNTS: [string, Router][] = [
  ['/api/sessions', sessionsRouter],
  ['/api/images', imagesRouter],
  ['/api/branding', brandingRouter],
  ['/api/boot-images', bootImagesRouter],
  ['/api/cert', certRouter],
  ['/api/portal-config', portalConfigRouter],
  ['/api/partitioning-scheme', partitioningSchemeRouter],
  ['/api/recovery-images', recoveryImagesRouter],
  ['/api/session-history', sessionHistoryRouter],
  ['/api/locations', locationsRouter],
  ['/api/regions', regionsRouter],
  ['/api/wifi-networks', wifiNetworksRouter],
  ['/api/user-preferences', userPreferencesRouter],
  ['/api/upload-jobs', uploadJobsRouter],
  ['/api/update-check', updateCheckRouter],
  ['/api/autopilot', autopilotRouter],
  ['/api/preflight-overrides', preFlightOverridesRouter],
];

type Role = 'Adm' | 'Ops' | 'Tech' | 'Rdr' | 'Appr';
type Method = 'get' | 'post' | 'put' | 'patch' | 'delete';

const ROLE_VALUES: Record<Role, string> = {
  Adm: 'CloudImaging.Administrator',
  Ops: 'CloudImaging.Operator',
  Tech: 'CloudImaging.Technician',
  Rdr: 'CloudImaging.Reader',
  Appr: 'CloudImaging.AutopilotApprover',
};

// Guard roles in the order the 403 detail lists them ("Guard after" column).
const PA = ['CloudImaging.PortalAccess'];
const PA_RDR = ['CloudImaging.PortalAccess', 'CloudImaging.Reader'];
const ADM = ['CloudImaging.Administrator'];
const OPS = ['CloudImaging.Operator', 'CloudImaging.Administrator'];
const AP_VIEW = ['CloudImaging.AutopilotApprover', 'CloudImaging.Administrator', 'CloudImaging.Operator', 'CloudImaging.Technician'];
const AP_AUDIT = ['CloudImaging.AutopilotApprover', 'CloudImaging.Administrator', 'CloudImaging.Operator', 'CloudImaging.Reader'];
const AP_DECIDE = ['CloudImaging.AutopilotApprover', 'CloudImaging.Administrator', 'CloudImaging.Operator'];
const HISTORY = ['CloudImaging.Reader', 'CloudImaging.Operator', 'CloudImaging.Administrator'];

// "Accepted after" sets.
const PA_USERS: Role[] = ['Adm', 'Ops', 'Tech'];
const PA_RDR_USERS: Role[] = ['Adm', 'Ops', 'Tech', 'Rdr'];
const ADM_USERS: Role[] = ['Adm'];
const OPS_USERS: Role[] = ['Adm', 'Ops'];

// "Accepted before" sets; null for a route this feature adds.
const PA_BEFORE: Role[] = ['Adm', 'Tech'];
const PA_RDR_BEFORE: Role[] = ['Adm', 'Tech', 'Rdr'];
const ADM_BEFORE: Role[] = ['Adm'];

type Row = [method: Method, path: string, guard: string[], accepted: Role[], before: Role[] | null];

const ROUTES: Row[] = [
  ['get', '/api/sessions', PA, PA_USERS, PA_BEFORE],
  ['get', '/api/sessions/:sessionId', PA, PA_USERS, PA_BEFORE],
  ['get', '/api/sessions/:sessionId/logs', PA, PA_USERS, PA_BEFORE],
  ['get', '/api/sessions/:sessionId/logs/:fileName/download-url', PA, PA_USERS, PA_BEFORE],
  ['post', '/api/sessions/couple', PA, PA_USERS, PA_BEFORE],
  ['post', '/api/sessions/:sessionId/assign', PA, PA_USERS, PA_BEFORE],
  ['post', '/api/sessions/bulk-assign', PA, PA_USERS, PA_BEFORE],
  ['delete', '/api/sessions/:sessionId', PA, PA_USERS, PA_BEFORE],
  ['post', '/api/sessions/:sessionId/preflight-override', ADM, ADM_USERS, ADM_BEFORE],

  ['get', '/api/images', PA_RDR, PA_RDR_USERS, PA_RDR_BEFORE],
  ['post', '/api/images', OPS, OPS_USERS, ADM_BEFORE],
  ['post', '/api/images/upload/start', OPS, OPS_USERS, ADM_BEFORE],
  ['post', '/api/images/upload/:uploadId/publish', OPS, OPS_USERS, ADM_BEFORE],
  ['post', '/api/images/upload/:uploadId/abandon', OPS, OPS_USERS, ADM_BEFORE],
  ['patch', '/api/images/:imageId', OPS, OPS_USERS, ADM_BEFORE],
  ['delete', '/api/images/:imageId', OPS, OPS_USERS, ADM_BEFORE],

  ['get', '/api/boot-images', PA_RDR, PA_RDR_USERS, PA_RDR_BEFORE],
  ['post', '/api/boot-images/upload/start', OPS, OPS_USERS, ADM_BEFORE],
  ['post', '/api/boot-images/upload/:token/publish', OPS, OPS_USERS, ADM_BEFORE],
  ['post', '/api/boot-images/:id/promote', OPS, OPS_USERS, ADM_BEFORE],
  ['post', '/api/boot-images/:id/demote', OPS, OPS_USERS, ADM_BEFORE],
  ['delete', '/api/boot-images/:id', OPS, OPS_USERS, ADM_BEFORE],

  ['get', '/api/recovery-images', PA_RDR, PA_RDR_USERS, PA_RDR_BEFORE],
  ['post', '/api/recovery-images/upload/start', OPS, OPS_USERS, ADM_BEFORE],
  ['post', '/api/recovery-images/upload/:uploadId/publish', OPS, OPS_USERS, ADM_BEFORE],
  ['delete', '/api/recovery-images/:id', OPS, OPS_USERS, ADM_BEFORE],

  ['get', '/api/upload-jobs/:uploadId', OPS, OPS_USERS, ADM_BEFORE],

  ['get', '/api/locations', PA, PA_USERS, PA_BEFORE],
  ['post', '/api/locations', OPS, OPS_USERS, ADM_BEFORE],
  ['put', '/api/locations/:locationId', OPS, OPS_USERS, ADM_BEFORE],
  ['delete', '/api/locations/:locationId', OPS, OPS_USERS, ADM_BEFORE],
  ['put', '/api/locations/:locationId/wifi-networks', OPS, OPS_USERS, ADM_BEFORE],
  ['get', '/api/regions', OPS, OPS_USERS, ADM_BEFORE],
  ['put', '/api/regions', OPS, OPS_USERS, ADM_BEFORE],

  ['get', '/api/wifi-networks', OPS, OPS_USERS, ADM_BEFORE],
  ['post', '/api/wifi-networks', OPS, OPS_USERS, ADM_BEFORE],
  ['put', '/api/wifi-networks/order', OPS, OPS_USERS, ADM_BEFORE],
  ['put', '/api/wifi-networks/:networkId', OPS, OPS_USERS, ADM_BEFORE],
  ['delete', '/api/wifi-networks/:networkId', OPS, OPS_USERS, ADM_BEFORE],

  ['get', '/api/autopilot/registrations', AP_VIEW, ['Appr', 'Adm', 'Ops', 'Tech'], ['Appr', 'Adm', 'Tech']],
  ['get', '/api/autopilot/registrations/:requestId', AP_VIEW, ['Appr', 'Adm', 'Ops', 'Tech'], ['Appr', 'Adm', 'Tech']],
  ['get', '/api/autopilot/history', AP_AUDIT, ['Appr', 'Adm', 'Ops', 'Rdr'], ['Appr', 'Adm', 'Rdr']],
  ['post', '/api/autopilot/registrations/:requestId/approve', AP_DECIDE, ['Appr', 'Adm', 'Ops'], ['Appr', 'Adm']],
  ['post', '/api/autopilot/registrations/:requestId/reject', AP_DECIDE, ['Appr', 'Adm', 'Ops'], ['Appr', 'Adm']],
  ['post', '/api/autopilot/registrations/:requestId/retry', AP_DECIDE, ['Appr', 'Adm', 'Ops'], ['Appr', 'Adm']],
  ['get', '/api/autopilot/group-tags', AP_DECIDE, ['Appr', 'Adm', 'Ops'], ['Appr', 'Adm']],
  ['post', '/api/autopilot/group-tags', ADM, ADM_USERS, ADM_BEFORE],
  ['put', '/api/autopilot/group-tags/:tagId', ADM, ADM_USERS, ADM_BEFORE],
  ['delete', '/api/autopilot/group-tags/:tagId', ADM, ADM_USERS, ADM_BEFORE],

  ['get', '/api/session-history', HISTORY, ['Adm', 'Ops', 'Rdr'], PA_RDR_BEFORE],
  ['get', '/api/session-history/completed-timestamps', PA_RDR, PA_RDR_USERS, null],

  ['get', '/api/branding', PA, PA_USERS, PA_BEFORE],
  ['get', '/api/branding/logo/sas', PA, PA_USERS, PA_BEFORE],
  ['get', '/api/branding/logo/content', PA, PA_USERS, PA_BEFORE],
  ['get', '/api/branding/portal-logo/content', PA, PA_USERS, PA_BEFORE],
  ['put', '/api/branding', ADM, ADM_USERS, ADM_BEFORE],
  ['put', '/api/branding/logo', ADM, ADM_USERS, ADM_BEFORE],
  ['put', '/api/branding/portal-logo', ADM, ADM_USERS, ADM_BEFORE],
  ['delete', '/api/branding/logo', ADM, ADM_USERS, ADM_BEFORE],
  ['delete', '/api/branding/portal-logo', ADM, ADM_USERS, ADM_BEFORE],

  ['get', '/api/portal-config', ADM, ADM_USERS, ADM_BEFORE],
  ['put', '/api/portal-config', ADM, ADM_USERS, ADM_BEFORE],
  ['get', '/api/cert/active', ADM, ADM_USERS, ADM_BEFORE],
  ['post', '/api/cert/generate', ADM, ADM_USERS, ADM_BEFORE],
  ['post', '/api/cert/rotate', ADM, ADM_USERS, ADM_BEFORE],
  ['get', '/api/partitioning-scheme', PA, PA_USERS, PA_BEFORE],
  ['put', '/api/partitioning-scheme', ADM, ADM_USERS, ADM_BEFORE],
  ['get', '/api/preflight-overrides', PA, PA_USERS, PA_BEFORE],
  ['delete', '/api/preflight-overrides', ADM, ADM_USERS, ADM_BEFORE],
  ['get', '/api/update-check', ADM, ADM_USERS, ADM_BEFORE],
  ['post', '/api/update-check/refresh', ADM, ADM_USERS, ADM_BEFORE],

  ['get', '/api/user-preferences', PA, PA_USERS, PA_BEFORE],
  ['put', '/api/user-preferences', PA, PA_USERS, PA_BEFORE],
];

// SC-003: every existing route except the narrowed one keeps its pre-feature result for these roles.
const NARROWED = 'GET /api/session-history';
const EXISTING_ROLE_TOKENS: Role[] = ['Adm', 'Tech', 'Rdr', 'Appr'];
const UNCHANGED_FOR_EXISTING_ROLES = ROUTES.filter(([method, path, , , before]) => before !== null && `${method.toUpperCase()} ${path}` !== NARROWED);

const TOKENS: [name: string, roles: Role[]][] = [
  ['Administrator', ['Adm']],
  ['Operator', ['Ops']],
  ['Technician', ['Tech']],
  ['Reader', ['Rdr']],
  ['AutopilotApprover', ['Appr']],
  ['Operator plus Technician', ['Ops', 'Tech']],
];

/** Every GUID-validated parameter gets a value that fails validation, so accepted requests stop at 400. */
function urlFor(path: string): string {
  return path.replace(':fileName', 'client.log').replace(/:\w+/g, 'not-a-guid');
}

function appFor(roles: Role[]) {
  const claims = { roles: roles.map(r => ROLE_VALUES[r]), preferred_username: 'user@contoso.com', oid: 'oid-user' };
  const app = express();
  app.use(express.json());
  app.use((req: AuthenticatedRequest, _res: Response, next: NextFunction) => { req.user = claims as AuthenticatedRequest['user']; next(); });
  for (const [prefix, router] of MOUNTS) app.use(prefix, router);
  return app;
}

function send(app: express.Express, method: Method, path: string) {
  const call = request(app)[method](urlFor(path));
  return method === 'get' || method === 'delete' ? call : call.send({});
}

function backendMocks(): ReturnType<typeof vi.fn>[] {
  const { http, ...methods } = operatorApi;
  return [...Object.values(methods), http.post, updateCheck.getUpdateStatus];
}

/** Lists "METHOD /api/prefix/path" for every route the mounted routers register. */
function registeredRoutes(): string[] {
  const found: string[] = [];
  for (const [prefix, router] of MOUNTS) {
    for (const layer of router.stack) {
      const route = layer.route as { path: string; methods: Record<string, boolean> } | undefined;
      if (!route) continue;
      for (const method of Object.keys(route.methods)) {
        found.push(`${method.toUpperCase()} ${prefix}${route.path === '/' ? '' : route.path}`);
      }
    }
  }
  return found;
}

describe('Portal backend: route-by-role matrix', () => {
  beforeAll(() => {
    vi.stubGlobal('fetch', vi.fn(() => { throw new Error('Unexpected network call from a unit test.'); }));
  });

  afterAll(() => {
    vi.unstubAllGlobals();
  });

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('lists every route the 17 routers register exactly once', () => {
    const table = ROUTES.map(([method, path]) => `${method.toUpperCase()} ${path}`);
    expect(MOUNTS).toHaveLength(17);
    expect(ROUTES).toHaveLength(73);
    expect(new Set(table).size).toBe(table.length);
    expect([...registeredRoutes()].sort()).toEqual([...table].sort());
  });

  describe.each(TOKENS)('%s token', (_name, tokenRoles) => {
    const app = appFor(tokenRoles);

    it.each(ROUTES)('%s %s', async (method, path, guard, accepted) => {
      const res = await send(app, method, path);

      if (tokenRoles.some(r => accepted.includes(r))) {
        expect(res.status).not.toBe(401);
        expect(res.status).not.toBe(403);
        expect(res.status).toBeLessThan(500);
        if (path.includes(':')) expect(res.status).toBe(400);
        return;
      }

      expect(res.status).toBe(403);
      expect(res.body.detail).toBe(`This operation requires one of the following roles: ${guard.join(', ')}.`);
      for (const mock of backendMocks()) expect(mock).not.toHaveBeenCalled();
    });
  });

  describe('existing roles keep their access', () => {
    it('covers every existing route except the narrowed one', () => {
      expect(UNCHANGED_FOR_EXISTING_ROLES).toHaveLength(71);
    });

    describe.each(EXISTING_ROLE_TOKENS)('%s token', (role) => {
      const app = appFor([role]);

      it.each(UNCHANGED_FOR_EXISTING_ROLES)('%s %s matches Accepted before', async (method, path, _guard, _accepted, before) => {
        const res = await send(app, method, path);
        const refused = res.status === 403;
        expect(refused).toBe(!before!.includes(role));
      });
    });

    it('narrows GET /api/session-history for Technician only', () => {
      const row = ROUTES.find(([method, path]) => `${method.toUpperCase()} ${path}` === NARROWED)!;
      const [, , , accepted, before] = row;
      const changed = EXISTING_ROLE_TOKENS.filter(r => accepted.includes(r) !== before!.includes(r));
      expect(changed).toEqual(['Tech']);
    });
  });
});
