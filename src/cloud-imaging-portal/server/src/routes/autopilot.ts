import { Router, Response, NextFunction } from 'express';
import { requireRole } from '../middleware/roleGuard.js';
import { requireGuidParams } from '../middleware/validateParams.js';
import { operatorApiClient } from '../services/operatorApiClient.js';
import type { AuthenticatedRequest } from '../middleware/auth.js';

/**
 * Autopilot registration router. Devices submit hardware hashes through the Device Gateway;
 * this router lets portal users review and decide on them.
 *
 * Approval queue (open requests only): Approver, Administrator, Technician (read-only).
 * Handled requests (audit report): Approver, Administrator, Reader.
 * Approve, reject, retry: Approver or Administrator.
 * Group tag definitions: readable by Approver and Administrator, managed by Administrator.
 *
 * The deciding user's identity comes from their validated token, never from the request body,
 * because the Operator API only sees the portal backend's own service identity.
 */
const router = Router();

const MAX_REASON_LENGTH = 500;
const GUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const ISO_TIMESTAMP = /^\d{4}-\d{2}-\d{2}(T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})?)?$/;

const canView = requireRole('CloudImaging.AutopilotApprover', 'CloudImaging.Administrator', 'CloudImaging.Technician');
const canAudit = requireRole('CloudImaging.AutopilotApprover', 'CloudImaging.Administrator', 'CloudImaging.Reader');
const canDecide = requireRole('CloudImaging.AutopilotApprover', 'CloudImaging.Administrator');

type Decision = { decidedByUpn: string; decidedByObjectId?: string; groupTagDefinitionId?: string; reason?: string };

/** Builds the decision identity from the caller's token. Returns null when the token names nobody. */
export function decidingUser(req: AuthenticatedRequest): Pick<Decision, 'decidedByUpn' | 'decidedByObjectId'> | null {
  const claims = req.user ?? {};
  const upn = [claims['preferred_username'], claims['upn'], claims['unique_name'], claims['email']]
    .find((value): value is string => typeof value === 'string' && value.length > 0);
  const oid = typeof claims['oid'] === 'string' ? claims['oid'] : undefined;
  const name = upn ?? oid;
  return name ? { decidedByUpn: name, decidedByObjectId: oid } : null;
}

function badRequest(res: Response, detail: string): void {
  res.status(400).json({ type: 'https://cloudimaging.io/errors/invalid-request', title: 'Invalid request.', status: 400, detail });
}

router.get('/registrations', canView, async (_req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    res.json(await operatorApiClient.listAutopilotRegistrations());
  } catch (err) { next(err); }
});

/** Returns the query value when it is an ISO 8601 timestamp, undefined when absent, or null when invalid. */
function dateParam(value: unknown): string | undefined | null {
  if (value === undefined) return undefined;
  return typeof value === 'string' && value.length <= 40 && ISO_TIMESTAMP.test(value) && !Number.isNaN(Date.parse(value)) ? value : null;
}

router.get('/history', canAudit, async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    const from = dateParam(req.query['from']);
    const to = dateParam(req.query['to']);
    if (from === null || to === null) {
      badRequest(res, 'from and to must be ISO 8601 timestamps.');
      return;
    }
    res.json(await operatorApiClient.listHandledAutopilotRegistrations(from, to));
  } catch (err) { next(err); }
});

router.get('/registrations/:requestId', canView, requireGuidParams('requestId'), async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    res.json(await operatorApiClient.getAutopilotRegistration(req.params['requestId'] as string));
  } catch (err) { next(err); }
});

router.post('/registrations/:requestId/approve', canDecide, requireGuidParams('requestId'), async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    const user = decidingUser(req);
    if (!user) { res.status(401).json({ title: 'Unauthorized', status: 401, detail: 'Missing user identity claim.' }); return; }

    const body = (req.body ?? {}) as Record<string, unknown>;
    const tagId = body['groupTagDefinitionId'];
    if (tagId !== undefined && tagId !== null && (typeof tagId !== 'string' || !GUID_PATTERN.test(tagId))) {
      badRequest(res, 'groupTagDefinitionId must be a GUID.');
      return;
    }

    const decision: Decision = { ...user, groupTagDefinitionId: typeof tagId === 'string' ? tagId : undefined };
    res.json(await operatorApiClient.decideAutopilotRegistration(req.params['requestId'] as string, 'approve', decision));
  } catch (err) { next(err); }
});

router.post('/registrations/:requestId/reject', canDecide, requireGuidParams('requestId'), async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    const user = decidingUser(req);
    if (!user) { res.status(401).json({ title: 'Unauthorized', status: 401, detail: 'Missing user identity claim.' }); return; }

    const reason = ((req.body ?? {}) as Record<string, unknown>)['reason'];
    if (reason !== undefined && reason !== null && (typeof reason !== 'string' || reason.length > MAX_REASON_LENGTH)) {
      badRequest(res, `reason must be text of at most ${String(MAX_REASON_LENGTH)} characters.`);
      return;
    }

    const decision: Decision = { ...user, reason: typeof reason === 'string' && reason.trim() ? reason.trim() : undefined };
    res.json(await operatorApiClient.decideAutopilotRegistration(req.params['requestId'] as string, 'reject', decision));
  } catch (err) { next(err); }
});

router.post('/registrations/:requestId/retry', canDecide, requireGuidParams('requestId'), async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    const user = decidingUser(req);
    if (!user) { res.status(401).json({ title: 'Unauthorized', status: 401, detail: 'Missing user identity claim.' }); return; }
    res.json(await operatorApiClient.decideAutopilotRegistration(req.params['requestId'] as string, 'retry', user));
  } catch (err) { next(err); }
});

router.get('/group-tags', requireRole('CloudImaging.AutopilotApprover', 'CloudImaging.Administrator'), async (_req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    res.json(await operatorApiClient.listAutopilotGroupTags());
  } catch (err) { next(err); }
});

/** Copies only the fields a group tag definition has, so nothing else in the body reaches the backend. */
function groupTagPayload(body: unknown): Record<string, unknown> {
  const source = (body ?? {}) as Record<string, unknown>;
  return { name: source['name'], kind: source['kind'], value: source['value'], description: source['description'] };
}

router.post('/group-tags', requireRole('CloudImaging.Administrator'), async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    res.status(201).json(await operatorApiClient.createAutopilotGroupTag(groupTagPayload(req.body)));
  } catch (err) { next(err); }
});

router.put('/group-tags/:tagId', requireRole('CloudImaging.Administrator'), requireGuidParams('tagId'), async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    res.json(await operatorApiClient.updateAutopilotGroupTag(req.params['tagId'] as string, groupTagPayload(req.body)));
  } catch (err) { next(err); }
});

router.delete('/group-tags/:tagId', requireRole('CloudImaging.Administrator'), requireGuidParams('tagId'), async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    await operatorApiClient.deleteAutopilotGroupTag(req.params['tagId'] as string);
    res.status(204).send();
  } catch (err) { next(err); }
});

export { router as autopilotRouter };
