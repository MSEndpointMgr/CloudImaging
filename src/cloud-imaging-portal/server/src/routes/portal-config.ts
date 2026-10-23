import { Router, Request, Response, NextFunction } from 'express';
import type { AuthenticatedRequest } from '../middleware/auth.js';
import { requireRole } from '../middleware/roleGuard.js';
import { operatorApiClient } from '../services/operatorApiClient.js';
import { decidingUser } from './autopilot.js';

/**
 * Portal configuration route (T146, FR-026).
 * GET/PUT proxied to Operator API /api/configuration. Administrator-only.
 */
const router = Router();

router.get('/', requireRole('CloudImaging.Administrator'), async (_req: Request, res: Response, next: NextFunction) => {
  try {
    const config = await operatorApiClient.getConfiguration();
    res.json(config);
  } catch (err) { next(err); }
});

router.put('/', requireRole('CloudImaging.Administrator'), async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    // Who changed a tracked setting comes from the token, never from the browser.
    const body = req.body && typeof req.body === 'object' && !Array.isArray(req.body) ? (req.body as Record<string, unknown>) : {};
    await operatorApiClient.putConfiguration({ ...body, changedBy: decidingUser(req)?.decidedByUpn ?? null });
    res.status(204).send();
  } catch (err) { next(err); }
});

export { router as portalConfigRouter };
