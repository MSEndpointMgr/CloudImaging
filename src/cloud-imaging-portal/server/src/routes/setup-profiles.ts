import { Router, Request, Response, NextFunction } from 'express';
import type { AuthenticatedRequest } from '../middleware/auth.js';
import { requireOperator, requireTechnician } from '../middleware/roleGuard.js';
import { requireGuidParams } from '../middleware/validateParams.js';
import { operatorApiClient } from '../services/operatorApiClient.js';
import { decidingUser } from './autopilot.js';

/**
 * Setup profiles router. Technicians and up can read profiles; Operators and Administrators can
 * change them. The acting user always comes from the token: only the listed request fields reach
 * the Operator API, so any actor, createdBy, changedBy or lastModifiedBy sent by the browser is
 * dropped. All rules (names, targeting, content and answer file checks) live in Imaging Core;
 * its problem responses reach the browser through the global error handler.
 */
const router = Router();

const CREATE_FIELDS = ['name', 'targeting', 'duplicatedFrom', 'content'] as const;
const SAVE_FIELDS = ['baseVersion', 'name', 'targeting', 'content'] as const;
const RESTORE_FIELDS = ['version', 'baseVersion'] as const;

/** Highest version number a route accepts; matches the six-digit version blob names. */
const MAX_VERSION = 999_999;

const SHA256_PATTERN = /^[0-9a-f]{64}$/;

function toPayload(req: AuthenticatedRequest, fields: readonly string[]): Record<string, unknown> {
  const body = (req.body ?? {}) as Record<string, unknown>;
  const payload: Record<string, unknown> = {};
  for (const field of fields) {
    if (field in body) payload[field] = body[field];
  }
  const user = decidingUser(req);
  payload['actor'] = user ? { upn: user.decidedByUpn, objectId: user.decidedByObjectId ?? null } : null;
  return payload;
}

function requireVersionParam(req: Request, res: Response, next: NextFunction): void {
  const raw = req.params['version'];
  const version = typeof raw === 'string' && /^\d{1,6}$/.test(raw) ? Number(raw) : NaN;
  if (!Number.isInteger(version) || version < 1 || version > MAX_VERSION) {
    res.status(400).json({
      type: 'https://cloudimaging.io/errors/invalid-parameter',
      title: 'Invalid version.',
      status: 400,
      detail: `Route parameter 'version' must be a whole number from 1 to ${String(MAX_VERSION)}.`,
    });
    return;
  }
  next();
}

router.get('/', requireTechnician, async (_req: Request, res: Response, next: NextFunction) => {
  try {
    res.json(await operatorApiClient.getSetupProfiles());
  } catch (err) {
    next(err);
  }
});

// Raw bytes from express.raw in app.ts; a script or answer file text, never parsed here.
router.post('/content', requireOperator, async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  if (!Buffer.isBuffer(req.body)) {
    res.status(400).json({
      type: 'https://cloudimaging.io/errors/invalid-request',
      title: 'Invalid request.',
      status: 400,
      detail: 'Send the text as application/octet-stream.',
    });
    return;
  }
  try {
    const kind = typeof req.query['kind'] === 'string' ? req.query['kind'] : undefined;
    // The uploading user comes from the token only; any uploadedBy in the query is ignored.
    res.json(await operatorApiClient.uploadSetupProfileContent(kind, req.body, decidingUser(req)?.decidedByUpn));
  } catch (err) {
    next(err);
  }
});

router.get('/:profileId', requireTechnician, requireGuidParams('profileId'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    res.json(await operatorApiClient.getSetupProfile(req.params['profileId'] as string));
  } catch (err) {
    next(err);
  }
});

router.post('/', requireOperator, async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    res.status(201).json(await operatorApiClient.createSetupProfile(toPayload(req, CREATE_FIELDS)));
  } catch (err) {
    next(err);
  }
});

router.put('/:profileId', requireOperator, requireGuidParams('profileId'), async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    res.json(await operatorApiClient.updateSetupProfile(req.params['profileId'] as string, toPayload(req, SAVE_FIELDS)));
  } catch (err) {
    next(err);
  }
});

router.delete('/:profileId', requireOperator, requireGuidParams('profileId'), async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    await operatorApiClient.deleteSetupProfile(req.params['profileId'] as string, decidingUser(req)?.decidedByUpn);
    res.status(204).send();
  } catch (err) {
    next(err);
  }
});

router.post('/:profileId/restore', requireOperator, requireGuidParams('profileId'), async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    res.json(await operatorApiClient.restoreSetupProfileVersion(req.params['profileId'] as string, toPayload(req, RESTORE_FIELDS)));
  } catch (err) {
    next(err);
  }
});

router.get('/:profileId/versions', requireTechnician, requireGuidParams('profileId'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    res.json(await operatorApiClient.getSetupProfileVersions(req.params['profileId'] as string));
  } catch (err) {
    next(err);
  }
});

router.get('/:profileId/versions/:version', requireTechnician, requireGuidParams('profileId'), requireVersionParam, async (req: Request, res: Response, next: NextFunction) => {
  try {
    res.json(await operatorApiClient.getSetupProfileVersion(req.params['profileId'] as string, Number(req.params['version'])));
  } catch (err) {
    next(err);
  }
});

router.get('/:profileId/history', requireTechnician, requireGuidParams('profileId'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    res.json(await operatorApiClient.getSetupProfileHistory(req.params['profileId'] as string));
  } catch (err) {
    next(err);
  }
});

router.get('/:profileId/content/:sha256', requireTechnician, requireGuidParams('profileId'), async (req: Request, res: Response, next: NextFunction) => {
  const sha256 = req.params['sha256'];
  if (typeof sha256 !== 'string' || !SHA256_PATTERN.test(sha256)) {
    res.status(400).json({
      type: 'https://cloudimaging.io/errors/invalid-parameter',
      title: 'Invalid content hash.',
      status: 400,
      detail: "Route parameter 'sha256' must be 64 lowercase hexadecimal characters.",
    });
    return;
  }
  try {
    const content = await operatorApiClient.getSetupProfileContent(req.params['profileId'] as string, sha256);
    res.setHeader('Content-Type', content.contentType);
    res.setHeader('Cache-Control', 'private, no-store');
    res.send(content.data);
  } catch (err) {
    next(err);
  }
});

export { router as setupProfilesRouter };
