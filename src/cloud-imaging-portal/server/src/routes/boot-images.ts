import { Router, Request, Response, NextFunction } from 'express';
import { requireRole } from '../middleware/roleGuard.js';
import { operatorApiClient } from '../services/operatorApiClient.js';
import { isAllowedImageFile, ALLOWED_IMAGE_EXTENSIONS } from '../utils/imageFileValidation.js';

/** Boot images router (T117, US7). */
const router = Router();

router.get('/', requireRole('CloudImaging.PortalAccess'), async (_req: Request, res: Response, next: NextFunction) => {
  try {
    res.json(await operatorApiClient.getBootImages());
  } catch (err) { next(err); }
});

router.post('/upload/start', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const { fileName } = req.body as { fileName?: string };
    if (!fileName || !isAllowedImageFile(fileName)) {
      res.status(400).json({ error: `Only ${ALLOWED_IMAGE_EXTENSIONS.join(', ')} files are allowed.` });
      return;
    }
    res.json(await operatorApiClient.startBootImageUpload(req.body as unknown));
  } catch (err) { next(err); }
});

router.post('/upload/:token/publish', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const published = await operatorApiClient.publishBootImageUpload(req.params['token'] as string, req.body as unknown);
    res.status(201).json(published);
  } catch (err) { next(err); }
});

router.delete('/:id', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    await operatorApiClient.deleteBootImage(req.params['id'] as string);
    res.status(204).send();
  } catch (err) { next(err); }
});

export { router as bootImagesRouter };
