import { Router, Request, Response, NextFunction } from 'express';
import { requireRole } from '../middleware/roleGuard.js';
import { operatorApiClient } from '../services/operatorApiClient.js';
import { isAllowedImageFile, WIM_ONLY_EXTENSIONS } from '../utils/imageFileValidation.js';

/** Boot images router (T117, US7). GET also allows Reader, for the Image Inventory report. */
const router = Router();

router.get('/', requireRole('CloudImaging.PortalAccess', 'CloudImaging.Reader'), async (_req: Request, res: Response, next: NextFunction) => {
  try {
    res.json(await operatorApiClient.getBootImages());
  } catch (err) { next(err); }
});

router.post('/upload/start', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const { fileName } = req.body as { fileName?: string };
    if (!fileName || !isAllowedImageFile(fileName, WIM_ONLY_EXTENSIONS)) {
      res.status(400).json({ error: `Only ${WIM_ONLY_EXTENSIONS.join(', ')} files are allowed.` });
      return;
    }
    res.json(await operatorApiClient.startBootImageUpload(req.body as unknown));
  } catch (err) { next(err); }
});

// Publish enqueues a background job (202 Accepted); the client polls /api/upload-jobs/:id.
router.post('/upload/:token/publish', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const job = await operatorApiClient.publishBootImageUpload(req.params['token'] as string, req.body as unknown);
    res.status(202).json(job);
  } catch (err) { next(err); }
});

router.delete('/:id', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    await operatorApiClient.deleteBootImage(req.params['id'] as string);
    res.status(204).send();
  } catch (err) { next(err); }
});

export { router as bootImagesRouter };
