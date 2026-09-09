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

router.get('/', requireRole('CloudImaging.Administrator'), async (_req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    let enabled = false;
    try {
      const config = await operatorApiClient.getConfiguration() as { updateCheckEnabled?: boolean };
      enabled = config.updateCheckEnabled === true;
    } catch {
      // Configuration unreadable: stay disabled. Failing closed here means a transient
      // Operator API problem can never cause an outbound call the administrator did not opt in to.
    }
    res.json(await getUpdateStatus(enabled));
  } catch (err) { next(err); }
});

export { router as updateCheckRouter };
