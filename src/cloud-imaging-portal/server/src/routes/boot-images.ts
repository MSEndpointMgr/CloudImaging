import { Router, Request, Response, NextFunction } from 'express';
import { requireRole } from '../middleware/roleGuard.js';
import { operatorApiClient } from '../services/operatorApiClient.js';

/** Boot images router (T117, US7). */
const router = Router();

router.get('/', requireRole('CloudImaging.PortalAccess'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const token = req.headers.authorization?.replace('Bearer ', '') ?? '';
    operatorApiClient.setToken(token);
    res.json(await operatorApiClient.getSessions()); // placeholder until getBootImages added to operatorApiClient
  } catch (err) { next(err); }
});

router.delete('/:id', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const token = req.headers.authorization?.replace('Bearer ', '') ?? '';
    operatorApiClient.setToken(token);
    await operatorApiClient.deleteImage(req.params['id']!);
    res.status(204).send();
  } catch (err) { next(err); }
});

export { router as bootImagesRouter };
