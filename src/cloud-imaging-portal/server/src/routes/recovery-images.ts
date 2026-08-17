import { Router, Request, Response, NextFunction } from 'express';
import { requireRole } from '../middleware/roleGuard.js';
import { operatorApiClient } from '../services/operatorApiClient.js';

/** Recovery (WinRE) images router, mirroring boot-images.ts. */
const router = Router();

router.get('/', requireRole('CloudImaging.PortalAccess'), async (_req: Request, res: Response, next: NextFunction) => {
  try {
    res.json(await operatorApiClient.getRecoveryImages());
  } catch (err) { next(err); }
});

router.post('/upload/start', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    res.json(await operatorApiClient.startRecoveryImageUpload(req.body as unknown));
  } catch (err) { next(err); }
});

router.post('/upload/:uploadId/publish', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const published = await operatorApiClient.publishRecoveryImageUpload(req.params['uploadId'] as string, req.body as unknown);
    res.status(201).json(published);
  } catch (err) { next(err); }
});

router.delete('/:id', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    await operatorApiClient.deleteRecoveryImage(req.params['id'] as string);
    res.status(204).send();
  } catch (err) { next(err); }
});

export { router as recoveryImagesRouter };
