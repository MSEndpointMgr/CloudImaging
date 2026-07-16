import { Router, Request, Response, NextFunction } from 'express';
import { requireRole } from '../middleware/roleGuard.js';
import { operatorApiClient } from '../services/operatorApiClient.js';

/** Configuration router (US6). */
const router = Router();

router.get('/', requireRole('CloudImaging.PortalAccess'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const token = req.headers.authorization?.replace('Bearer ', '') ?? '';
    operatorApiClient.setToken(token);
    res.json(await operatorApiClient.getConfiguration());
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

export { router as configurationRouter };
