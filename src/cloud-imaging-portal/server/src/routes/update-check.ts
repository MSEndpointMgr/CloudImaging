import { Router, Response, NextFunction } from 'express';
import { requireRole } from '../middleware/roleGuard.js';
import { AuthenticatedRequest } from '../middleware/auth.js';
import { operatorApiClient } from '../services/operatorApiClient.js';
import { getUpdateStatus } from '../services/updateCheck.js';

/**
 * Update check route. Administrator-only: they are the only role that can act on an
 * available upgrade, and the only role the Configuration page is reachable by.
 *
 * The check is opt-in and off by default, read from the portal configuration on each request
 * so toggling it takes effect immediately rather than at the next restart.
 */
const router = Router();

async function isUpdateCheckEnabled(): Promise<boolean> {
  try {
    const config = await operatorApiClient.getConfiguration() as { updateCheckEnabled?: boolean };
    return config.updateCheckEnabled === true;
  } catch {
    // Configuration unreadable: fail closed. A transient Operator API problem can never cause
    // an outbound call the administrator did not opt in to.
    return false;
  }
}

router.get('/', requireRole('CloudImaging.Administrator'), async (_req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    res.json(await getUpdateStatus(await isUpdateCheckEnabled()));
  } catch (err) { next(err); }
});

// Manual "Check now": bypasses the 6h cache so an administrator gets an immediate answer instead
// of waiting for it to expire. Still gated by the same opt-in setting as the GET above.
router.post('/refresh', requireRole('CloudImaging.Administrator'), async (_req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    res.json(await getUpdateStatus(await isUpdateCheckEnabled(), true));
  } catch (err) { next(err); }
});

export { router as updateCheckRouter };
