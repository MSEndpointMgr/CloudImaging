import { Router, Request, Response, NextFunction } from 'express';
import { requireRole } from '../middleware/roleGuard.js';
import { operatorApiClient } from '../services/operatorApiClient.js';

/** Configuration router (US6). Administrator-only. Deployment/security settings. */
const router = Router();

router.get('/', requireRole('CloudImaging.Administrator'), async (_req: Request, res: Response, next: NextFunction) => {
  try {
    res.json(await operatorApiClient.getConfiguration());
  } catch (err) { next(err); }
});

router.put('/', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    await operatorApiClient.putConfiguration(req.body as unknown);
    res.status(204).send();
  } catch (err) { next(err); }
});

export { router as configurationRouter };
