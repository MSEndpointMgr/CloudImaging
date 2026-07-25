import { Router, Request, Response, NextFunction } from 'express';
import { requireRole } from '../middleware/roleGuard.js';
import { operatorApiClient } from '../services/operatorApiClient.js';

/**
 * Portal configuration route (T146, FR-026).
 * GET/PUT proxied to Operator API /api/configuration. Administrator-only.
 */
const router = Router();

router.get('/', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const token = req.headers.authorization?.replace('Bearer ', '') ?? '';
    operatorApiClient.setToken(token);
    const config = await operatorApiClient.getConfiguration();
    res.json(config);
  } catch (err) { next(err); }
});

router.put('/', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const token = req.headers.authorization?.replace('Bearer ', '') ?? '';
    operatorApiClient.setToken(token);
    await operatorApiClient.putConfiguration(req.body as unknown);
    res.status(204).send();
  } catch (err) { next(err); }
});

export { router as portalConfigRouter };
