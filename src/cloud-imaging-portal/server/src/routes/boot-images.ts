import { Router, Request, Response, NextFunction } from 'express';
import { requireRole } from '../middleware/roleGuard.js';
import { operatorApiClient } from '../services/operatorApiClient.js';

/** Boot images router (T117, US7). */
const router = Router();

router.get('/', requireRole('CloudImaging.PortalAccess'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const token = req.headers.authorization?.replace('Bearer ', '') ?? '';
    operatorApiClient.setToken(token);
    res.json(await operatorApiClient.getBootImages());
  } catch (err) { next(err); }
});

router.post('/upload/start', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const token = req.headers.authorization?.replace('Bearer ', '') ?? '';
    operatorApiClient.setToken(token);
    res.json(await operatorApiClient.startBootImageUpload(req.body as unknown));
  } catch (err) { next(err); }
});

router.post('/upload/:token/publish', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const token = req.headers.authorization?.replace('Bearer ', '') ?? '';
    operatorApiClient.setToken(token);
    const published = await operatorApiClient.publishBootImageUpload(req.params['token'] as string, req.body as unknown);
    res.status(201).json(published);
  } catch (err) { next(err); }
});

router.delete('/:id', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const token = req.headers.authorization?.replace('Bearer ', '') ?? '';
    operatorApiClient.setToken(token);
    await operatorApiClient.deleteBootImage(req.params['id'] as string);
    res.status(204).send();
  } catch (err) { next(err); }
});

export { router as bootImagesRouter };
