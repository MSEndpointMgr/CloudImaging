import { Router, Request, Response, NextFunction } from 'express';
import type { AuthenticatedRequest } from '../middleware/auth.js';
import { getUserRoles, requireOperator, requireRole, requireTechnician } from '../middleware/roleGuard.js';
import { requireGuidParams } from '../middleware/validateParams.js';
import { operatorApiClient } from '../services/operatorApiClient.js';
import { decidingUser } from './autopilot.js';

/**
 * Drivers router (`/api/drivers`). Technicians and up read; Operators and Administrators write.
 * Every route validates its path segments before any upstream call, and writes take the acting
 * user from the token only. Route numbers refer to the driver routes contract.
 */
export const DRIVER_VENDORS = ['Hp', 'Dell', 'Lenovo'] as const;

export const WINDOWS_RELEASES = ['Windows10_22H2', 'Windows11_23H2', 'Windows11_24H2', 'Windows11_25H2'] as const;

const LINEAGE_KEY_PATTERN = /^[A-Za-z0-9_.-]{1,80}$/;

function badParameter(res: Response, name: string, detail: string): void {
  res.status(400).json({
    type: 'https://cloudimaging.io/errors/invalid-parameter',
    title: `Invalid ${name}.`,
    status: 400,
    detail,
  });
}

/** 400 unless `:vendor` is Hp, Dell or Lenovo. */
export function requireVendorParam(req: Request, res: Response, next: NextFunction): void {
  const vendor = req.params['vendor'];
  if (typeof vendor !== 'string' || !(DRIVER_VENDORS as readonly string[]).includes(vendor)) {
    badParameter(res, 'vendor', `Route parameter 'vendor' must be one of ${DRIVER_VENDORS.join(', ')}.`);
    return;
  }
  next();
}

/** 400 unless `:release` is a Windows release value. */
export function requireReleaseParam(req: Request, res: Response, next: NextFunction): void {
  const release = req.params['release'];
  if (typeof release !== 'string' || !(WINDOWS_RELEASES as readonly string[]).includes(release)) {
    badParameter(res, 'release', `Route parameter 'release' must be one of ${WINDOWS_RELEASES.join(', ')}.`);
    return;
  }
  next();
}

/** 400 unless `:lineageKey` is 1 to 80 letters, digits, dots, dashes or underscores. */
export function requireLineageKeyParam(req: Request, res: Response, next: NextFunction): void {
  const key = req.params['lineageKey'];
  if (typeof key !== 'string' || !LINEAGE_KEY_PATTERN.test(key)) {
    badParameter(res, 'component', "Route parameter 'lineageKey' must be 1 to 80 letters, digits, dots, dashes or underscores.");
    return;
  }
  next();
}

const router = Router();

type DriverResult = { status: number; data: unknown };

/** Sends the upstream status and body. */
function relay(res: Response, result: DriverResult): void {
  if (result.status === 204 || result.data === undefined || result.data === '') {
    res.status(result.status).send();
    return;
  }
  res.status(result.status).json(result.data);
}

/** The acting user from the token; anything the browser sent as an actor is ignored. */
function actorOf(req: AuthenticatedRequest): { upn: string; objectId: string | null } | null {
  const user = decidingUser(req);
  return user ? { upn: user.decidedByUpn, objectId: user.decidedByObjectId ?? null } : null;
}

function text(value: unknown): string | undefined {
  return typeof value === 'string' ? value : undefined;
}

/** Only the fields onboarding and preview take, so nothing else from the browser goes upstream. */
function selection(body: unknown): { vendor: unknown; catalogModelKeys: unknown; releases: unknown } {
  const source = body && typeof body === 'object' ? (body as Record<string, unknown>) : {};
  return { vendor: source['vendor'], catalogModelKeys: source['catalogModelKeys'], releases: source['releases'] };
}

// 1.
router.get('/vendors', requireTechnician, async (_req: Request, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('GET', 'vendors'));
  } catch (err) { next(err); }
});

// 2. Lenovo and other unsupported vendors are refused by Core.
router.put('/vendors/:vendor', requireOperator, requireVendorParam, async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    const syncEnabled = (req.body as Record<string, unknown> | undefined)?.['syncEnabled'];
    relay(res, await operatorApiClient.drivers('PUT', `vendors/${req.params['vendor'] as string}`, { body: { syncEnabled, actor: actorOf(req) } }));
  } catch (err) { next(err); }
});

// 24.
router.get('/sync/settings', requireTechnician, async (_req: Request, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('GET', 'sync/settings'));
  } catch (err) { next(err); }
});

// 25. Applies at once; Core validates every field.
router.put('/sync/settings', requireOperator, async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    const source = req.body && typeof req.body === 'object' ? (req.body as Record<string, unknown>) : {};
    const body = {
      schedule: source['schedule'],
      weekdays: source['weekdays'],
      repeatWeeks: source['repeatWeeks'],
      localTime: source['localTime'],
      timeZone: source['timeZone'],
      waitForApprovalOfNewComponents: source['waitForApprovalOfNewComponents'],
      actor: actorOf(req),
    };
    relay(res, await operatorApiClient.drivers('PUT', 'sync/settings', { body }));
  } catch (err) { next(err); }
});

// 28.
router.get('/sync/runs/:runId/items', requireTechnician, requireGuidParams('runId'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('GET', `sync/runs/${req.params['runId'] as string}/items`));
  } catch (err) { next(err); }
});

// How far a running sync got, for the Sync running card.
router.get('/sync/runs/:runId/progress', requireTechnician, requireGuidParams('runId'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('GET', `sync/runs/${req.params['runId'] as string}/progress`));
  } catch (err) { next(err); }
});

const ISO_TIMESTAMP = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$/;

// The Driver Synchronization report: report readers, not only those who can open the Drivers page.
router.get('/reports/sync', requireRole('CloudImaging.Reader', 'CloudImaging.Operator', 'CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  const from = text(req.query['from']);
  const to = text(req.query['to']);
  if (!from || !to || !ISO_TIMESTAMP.test(from) || !ISO_TIMESTAMP.test(to) || Number.isNaN(Date.parse(from)) || Number.isNaN(Date.parse(to))) {
    badParameter(res, 'date range', "Query parameters 'from' and 'to' must be ISO 8601 timestamps.");
    return;
  }
  try {
    relay(res, await operatorApiClient.drivers('GET', 'reports/sync', { query: { from, to } }));
  } catch (err) { next(err); }
});

// 3. The vendor's model list is part of onboarding, so it is Operator only.
router.get('/catalog/models', requireOperator, async (req: Request, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('GET', 'catalog/models', { query: { vendor: text(req.query['vendor']), search: text(req.query['search']) } }));
  } catch (err) { next(err); }
});

// 4.
router.post('/models/preview', requireOperator, async (req: Request, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('POST', 'models/preview', { body: selection(req.body) }));
  } catch (err) { next(err); }
});

// 5. Drafts only; Complete onboarding (43) starts the downloads.
router.post('/models', requireOperator, async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('POST', 'models', { body: { ...selection(req.body), actor: actorOf(req) } }));
  } catch (err) { next(err); }
});

// 6.
router.get('/models', requireTechnician, async (_req: Request, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('GET', 'models'));
  } catch (err) { next(err); }
});

// 7.
router.get('/models/:modelId', requireTechnician, requireGuidParams('modelId'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('GET', `models/${req.params['modelId'] as string}`, { query: { release: text(req.query['release']) } }));
  } catch (err) { next(err); }
});

// 8.
router.post('/models/:modelId/releases', requireOperator, requireGuidParams('modelId'), async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    const release = (req.body as Record<string, unknown> | undefined)?.['release'];
    relay(res, await operatorApiClient.drivers('POST', `models/${req.params['modelId'] as string}/releases`, { body: { release, actor: actorOf(req) } }));
  } catch (err) { next(err); }
});

// 45. Check for updates on the Models tab; shares the 60-minute limit with Sync now.
router.post('/models/check', requireOperator, async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('POST', 'models/check', { body: { actor: actorOf(req) } }));
  } catch (err) { next(err); }
});

// 44. Check for updates on one model.
router.post('/models/:modelId/check', requireOperator, requireGuidParams('modelId'), async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('POST', `models/${req.params['modelId'] as string}/check`, { body: { actor: actorOf(req) } }));
  } catch (err) { next(err); }
});

// 43.
router.post('/models/:modelId/complete', requireOperator, requireGuidParams('modelId'), async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('POST', `models/${req.params['modelId'] as string}/complete`, { body: { actor: actorOf(req) } }));
  } catch (err) { next(err); }
});

// 26. Sync now; 409 while running and 429 within the hour carry availableAt.
router.post('/sync/runs', requireOperator, async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('POST', 'sync/runs', { body: { actor: actorOf(req) } }));
  } catch (err) { next(err); }
});

// 27.
router.get('/sync/runs', requireTechnician, async (_req: Request, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('GET', 'sync/runs'));
  } catch (err) { next(err); }
});

/** `?changedBy=` for a DELETE: the acting user's UPN from the token. */
function changedBy(req: AuthenticatedRequest): { changedBy: string | undefined } {
  return { changedBy: actorOf(req)?.upn };
}

/** `models/<id>/releases/<release>` from validated path parameters. */
function pairPath(req: Request): string {
  return `models/${req.params['modelId'] as string}/releases/${req.params['release'] as string}`;
}

/** Only the fields a rule has, so nothing else from the browser goes upstream. */
function ruleFields(body: unknown): Record<string, unknown> {
  const source = body && typeof body === 'object' ? (body as Record<string, unknown>) : {};
  return { name: source['name'], scope: source['scope'], match: source['match'], value: source['value'], action: source['action'] };
}

const pairGuards = [requireGuidParams('modelId'), requireReleaseParam];

// 12. Override on one model; Core refuses an action the component does not allow.
router.put('/models/:modelId/releases/:release/overrides/:lineageKey', requireOperator, ...pairGuards, requireLineageKeyParam, async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    const action = (req.body as Record<string, unknown> | undefined)?.['action'];
    relay(res, await operatorApiClient.drivers('PUT', `${pairPath(req)}/overrides/${req.params['lineageKey'] as string}`, { body: { action, actor: actorOf(req) } }));
  } catch (err) { next(err); }
});

// 13. Undo override.
router.delete('/models/:modelId/releases/:release/overrides/:lineageKey', requireOperator, ...pairGuards, requireLineageKeyParam, async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('DELETE', `${pairPath(req)}/overrides/${req.params['lineageKey'] as string}`, { query: changedBy(req) }));
  } catch (err) { next(err); }
});

// 14. Approve components waiting for approval with the action they have now.
router.post('/models/:modelId/releases/:release/approvals', requireOperator, ...pairGuards, async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    const lineageKeys = (req.body as Record<string, unknown> | undefined)?.['lineageKeys'];
    relay(res, await operatorApiClient.drivers('POST', `${pairPath(req)}/approvals`, { body: { lineageKeys, actor: actorOf(req) } }));
  } catch (err) { next(err); }
});

// 33. Build now: skips the 2-minute wait after the last change.
router.post('/models/:modelId/releases/:release/build', requireOperator, ...pairGuards, async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('POST', `${pairPath(req)}/build`, { body: { actor: actorOf(req) } }));
  } catch (err) { next(err); }
});

// 16. Roll back driver set: current and previous swap; 409 without a previous set.
router.post('/models/:modelId/releases/:release/rollback', requireOperator, ...pairGuards, async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('POST', `${pairPath(req)}/rollback`, { body: { actor: actorOf(req) } }));
  } catch (err) { next(err); }
});

// 34. Version history; only Operators and Administrators make Core ask the vendor (check=true).
router.get('/models/:modelId/releases/:release/components/:lineageKey/versions', requireTechnician, ...pairGuards, requireLineageKeyParam, async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    const check = req.query['check'] === 'true' && getUserRoles(req).has('CloudImaging.Operator') ? 'true' : undefined;
    relay(res, await operatorApiClient.drivers('GET', `${pairPath(req)}/components/${req.params['lineageKey'] as string}/versions`, { query: { check } }));
  } catch (err) { next(err); }
});

// 35. Roll back component; 409 with the reason when the version cannot be fetched again.
router.put('/models/:modelId/releases/:release/components/:lineageKey/rollback', requireOperator, ...pairGuards, requireLineageKeyParam, async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    const source = req.body && typeof req.body === 'object' ? (req.body as Record<string, unknown>) : {};
    relay(res, await operatorApiClient.drivers('PUT', `${pairPath(req)}/components/${req.params['lineageKey'] as string}/rollback`, { body: { version: source['version'], until: source['until'], actor: actorOf(req) } }));
  } catch (err) { next(err); }
});

// 36. Undo roll back.
router.delete('/models/:modelId/releases/:release/components/:lineageKey/rollback', requireOperator, ...pairGuards, requireLineageKeyParam, async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('DELETE', `${pairPath(req)}/components/${req.params['lineageKey'] as string}/rollback`, { query: changedBy(req) }));
  } catch (err) { next(err); }
});

// 15. Withdrawn components only; Core answers 409 for any other.
router.delete('/models/:modelId/releases/:release/components/:lineageKey', requireOperator, ...pairGuards, requireLineageKeyParam, async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('DELETE', `${pairPath(req)}/components/${req.params['lineageKey'] as string}`, { query: changedBy(req) }));
  } catch (err) { next(err); }
});

// 9. Core refuses the last release with 409.
router.delete('/models/:modelId/releases/:release', requireOperator, ...pairGuards, async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('DELETE', pairPath(req), { query: changedBy(req) }));
  } catch (err) { next(err); }
});

// 10. Shown in the removal overlays before anything is removed.
router.get('/models/:modelId/removal-impact', requireOperator, requireGuidParams('modelId'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const release = text(req.query['release']);
    if (release !== undefined && !(WINDOWS_RELEASES as readonly string[]).includes(release)) {
      badParameter(res, 'release', `Query parameter 'release' must be one of ${WINDOWS_RELEASES.join(', ')}.`);
      return;
    }
    relay(res, await operatorApiClient.drivers('GET', `models/${req.params['modelId'] as string}/removal-impact`, { query: { release } }));
  } catch (err) { next(err); }
});

// 11.
router.delete('/models/:modelId', requireOperator, requireGuidParams('modelId'), async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('DELETE', `models/${req.params['modelId'] as string}`, { query: changedBy(req) }));
  } catch (err) { next(err); }
});

// 29.
router.get('/storage', requireTechnician, async (_req: Request, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('GET', 'storage'));
  } catch (err) { next(err); }
});

// 46. Previous versions to keep; Core accepts 0 to 3.
router.put('/storage/settings', requireOperator, async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    const previousVersionsToKeep = (req.body as Record<string, unknown> | undefined)?.['previousVersionsToKeep'];
    relay(res, await operatorApiClient.drivers('PUT', 'storage/settings', { body: { previousVersionsToKeep, actor: actorOf(req) } }));
  } catch (err) { next(err); }
});

// 31.
router.get('/storage/models/:modelId', requireTechnician, requireGuidParams('modelId'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('GET', `storage/models/${req.params['modelId'] as string}`));
  } catch (err) { next(err); }
});

// 32.
router.get('/downloads', requireTechnician, async (_req: Request, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('GET', 'downloads'));
  } catch (err) { next(err); }
});

const MAX_PREVIEW_SESSIONS = 200;
const PREVIEW_GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

// 30. The Driver set column on the Devices page; a read for anyone who can start imaging.
router.post('/assignment-preview', requireTechnician, async (req: Request, res: Response, next: NextFunction) => {
  try {
    const source = req.body && typeof req.body === 'object' ? (req.body as Record<string, unknown>) : {};
    const osImageId = source['osImageId'];
    const sessionIds = source['sessionIds'];
    if (typeof osImageId !== 'string' || !PREVIEW_GUID.test(osImageId)
      || !Array.isArray(sessionIds) || sessionIds.length > MAX_PREVIEW_SESSIONS
      || !sessionIds.every(id => typeof id === 'string' && PREVIEW_GUID.test(id))) {
      badParameter(res, 'request', `Send osImageId and at most ${String(MAX_PREVIEW_SESSIONS)} sessionIds, each a GUID.`);
      return;
    }
    relay(res, await operatorApiClient.drivers('POST', 'assignment-preview', { body: { osImageId, sessionIds } }));
  } catch (err) { next(err); }
});

const BLOCK_ID_PATTERN = /^[A-Za-z0-9+/=]{1,100}$/;
const MAX_BLOCKS = 100_000;

/** Only the fields a custom component has, so nothing else from the browser goes upstream. */
function customFields(body: unknown): Record<string, unknown> {
  const source = body && typeof body === 'object' ? (body as Record<string, unknown>) : {};
  const targets = Array.isArray(source['targets'])
    ? (source['targets'] as unknown[]).map(t => {
      const target = t && typeof t === 'object' ? (t as Record<string, unknown>) : {};
      return { modelId: target['modelId'], replacesLineageKey: target['replacesLineageKey'] };
    })
    : source['targets'];
  return {
    uploadId: source['uploadId'],
    name: source['name'],
    version: source['version'],
    action: source['action'],
    category: source['category'],
    installCommand: source['installCommand'],
    targets,
  };
}

// 37. Staged upload of a custom package; Core refuses other extensions and anything over 5 GB.
router.post('/custom-components/uploads/start', requireOperator, async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    const source = req.body && typeof req.body === 'object' ? (req.body as Record<string, unknown>) : {};
    relay(res, await operatorApiClient.drivers('POST', 'custom-components/uploads/start', { body: { fileName: source['fileName'], sizeBytes: source['sizeBytes'], actor: actorOf(req) } }));
  } catch (err) { next(err); }
});

// 37a. Commits the staged blocks; only the block ids go upstream.
router.post('/custom-components/uploads/:uploadId/complete', requireOperator, requireGuidParams('uploadId'), async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    const blockIds = (req.body as Record<string, unknown> | undefined)?.['blockIds'];
    if (!Array.isArray(blockIds) || blockIds.length === 0 || blockIds.length > MAX_BLOCKS || !blockIds.every(id => typeof id === 'string' && BLOCK_ID_PATTERN.test(id))) {
      badParameter(res, 'request', 'Send the staged blockIds.');
      return;
    }
    relay(res, await operatorApiClient.drivers('POST', `custom-components/uploads/${req.params['uploadId'] as string}/complete`, { body: { blockIds, actor: actorOf(req) } }));
  } catch (err) { next(err); }
});

// 37b.
router.post('/custom-components/uploads/:uploadId/abandon', requireOperator, requireGuidParams('uploadId'), async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('POST', `custom-components/uploads/${req.params['uploadId'] as string}/abandon`, { body: { actor: actorOf(req) } }));
  } catch (err) { next(err); }
});

// 38. Field errors come back as 400 with errors per field; 409 when a lineage is already replaced.
router.post('/custom-components', requireOperator, async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('POST', 'custom-components', { body: { ...customFields(req.body), actor: actorOf(req) } }));
  } catch (err) { next(err); }
});

// 39.
router.get('/custom-components', requireTechnician, async (_req: Request, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('GET', 'custom-components'));
  } catch (err) { next(err); }
});

// 40.
router.put('/custom-components/:customId', requireOperator, requireGuidParams('customId'), async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('PUT', `custom-components/${req.params['customId'] as string}`, { body: { ...customFields(req.body), actor: actorOf(req) } }));
  } catch (err) { next(err); }
});

// 41. Running deployments keep their pinned files.
router.delete('/custom-components/:customId', requireOperator, requireGuidParams('customId'), async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('DELETE', `custom-components/${req.params['customId'] as string}`, { query: changedBy(req) }));
  } catch (err) { next(err); }
});

// 42.
router.post('/custom-components/:customId/notices/:modelId/dismiss', requireOperator, requireGuidParams('customId', 'modelId'), async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('POST', `custom-components/${req.params['customId'] as string}/notices/${req.params['modelId'] as string}/dismiss`, { body: { actor: actorOf(req) } }));
  } catch (err) { next(err); }
});

// 17.
router.get('/rules', requireTechnician, async (_req: Request, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('GET', 'rules'));
  } catch (err) { next(err); }
});

// 18. Suggestions are only offered; nothing is added until an Operator adds one.
router.get('/rules/suggestions', requireOperator, async (_req: Request, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('GET', 'rules/suggestions'));
  } catch (err) { next(err); }
});

// 19.
router.post('/rules', requireOperator, async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('POST', 'rules', { body: { ...ruleFields(req.body), actor: actorOf(req) } }));
  } catch (err) { next(err); }
});

// 20.
router.put('/rules/:ruleId', requireOperator, requireGuidParams('ruleId'), async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('PUT', `rules/${req.params['ruleId'] as string}`, { body: { ...ruleFields(req.body), actor: actorOf(req) } }));
  } catch (err) { next(err); }
});

// 21.
router.delete('/rules/:ruleId', requireOperator, requireGuidParams('ruleId'), async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('DELETE', `rules/${req.params['ruleId'] as string}`, { query: changedBy(req) }));
  } catch (err) { next(err); }
});

// 22.
router.get('/categories', requireTechnician, async (req: Request, res: Response, next: NextFunction) => {
  try {
    relay(res, await operatorApiClient.drivers('GET', 'categories', { query: { vendor: text(req.query['vendor']) } }));
  } catch (err) { next(err); }
});

// 23. Read when a session is assigned, so nothing is rebuilt.
router.put('/critical-categories/:vendor', requireOperator, requireVendorParam, async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    const categories = (req.body as Record<string, unknown> | undefined)?.['categories'];
    relay(res, await operatorApiClient.drivers('PUT', `critical-categories/${req.params['vendor'] as string}`, { body: { categories, actor: actorOf(req) } }));
  } catch (err) { next(err); }
});

export { router as driversRouter };
