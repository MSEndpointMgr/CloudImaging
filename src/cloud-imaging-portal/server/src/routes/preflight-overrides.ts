import { Router, Response, NextFunction } from 'express';
import { requireRole } from '../middleware/roleGuard.js';
import { operatorApiClient } from '../services/operatorApiClient.js';
import { decidingUser } from './autopilot.js';
import type { AuthenticatedRequest } from '../middleware/auth.js';

/**
 * Pre-flight overrides: one-time passes an administrator grants a blocked device so its next
 * session skips the checks that failed. Listing is open to every portal user (the Overrides tab
 * on Devices); revoking is Administrator-only. Approve lives on the sessions router because it
 * acts on a blocked session.
 */
const router = Router();

/** Upper bound on the serial query value; real serial numbers are far shorter. */
const MAX_SERIAL_LENGTH = 128;

router.get('/', requireRole('CloudImaging.PortalAccess'), async (_req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    res.json(await operatorApiClient.listPreFlightOverrides());
  } catch (err) { next(err); }
});

router.delete('/', requireRole('CloudImaging.Administrator'), async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    const serialNumber = req.query['serialNumber'];
    if (typeof serialNumber !== 'string' || !serialNumber.trim() || serialNumber.length > MAX_SERIAL_LENGTH) {
      res.status(400).json({ type: 'https://cloudimaging.io/errors/invalid-request', title: 'Invalid request.', status: 400, detail: 'serialNumber is required.' });
      return;
    }
    const user = decidingUser(req);
    if (!user) { res.status(401).json({ title: 'Unauthorized', status: 401, detail: 'Missing user identity claim.' }); return; }
    await operatorApiClient.revokePreFlightOverride(serialNumber, user.decidedByUpn);
    res.status(204).send();
  } catch (err) { next(err); }
});

export { router as preFlightOverridesRouter };
