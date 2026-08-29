import { Router, Request, Response, NextFunction } from 'express';
import { requireRole } from '../middleware/roleGuard.js';
import { operatorApiClient } from '../services/operatorApiClient.js';
import { isAllowedImageFile, OS_IMAGE_EXTENSIONS } from '../utils/imageFileValidation.js';

/**
 * Images router. OS image catalog CRUD proxy to the Operator API (T086, FR-036, FR-037).
 * Read endpoints require PortalAccess; write endpoints require Administrator.
 */
const router = Router();

// ── GET /api/images ───────────────────────────────────────────────────────────

router.get('/', requireRole('CloudImaging.PortalAccess'), async (_req: Request, res: Response, next: NextFunction) => {
  try {
    res.json(await operatorApiClient.getImages());
  } catch (err) { next(err); }
});

// ── POST /api/images: register new image (Administrator) ─────────────────────

router.post('/', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const result = await operatorApiClient.createImage(req.body as unknown);
    res.status(201).json(result);
  } catch (err) { next(err); }
});

// ── Staged, direct-to-blob upload (large WIM/ESD files) ───────────────────────
// Browser stages blocks directly to Blob Storage via the SAS URL returned from `start`;
// only small JSON metadata passes through this server (mirrors boot-images upload/*).

router.post('/upload/start', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    // The client sends the original uploaded file's name as `name` (T086); it doubles as the
    // catalog display name, so its extension is what's validated against the allow-list here.
    const { name } = req.body as { name?: string };
    if (!name || !isAllowedImageFile(name, OS_IMAGE_EXTENSIONS)) {
      res.status(400).json({ error: `Only ${OS_IMAGE_EXTENSIONS.join(', ')} files are allowed.` });
      return;
    }
    res.json(await operatorApiClient.startOsImageUpload(req.body as unknown));
  } catch (err) { next(err); }
});

// Publish only performs a cheap file-signature check and enqueues a background job, so it
// answers 202 Accepted with the job. The client polls GET /api/upload-jobs/:uploadId until the
// image is actually in the catalog.
router.post('/upload/:uploadId/publish', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const job = await operatorApiClient.publishOsImageUpload(req.params['uploadId'] as string, req.body as unknown);
    res.status(202).json(job);
  } catch (err) { next(err); }
});

// Best-effort cleanup for a cancelled/discarded upload — deletes the uncommitted staged blob.
router.post('/upload/:uploadId/abandon', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    await operatorApiClient.abandonOsImageUpload(req.params['uploadId'] as string, req.body as unknown);
    res.status(204).send();
  } catch (err) { next(err); }
});

// ── PATCH /api/images/:id: update metadata (Administrator) ───────────────────

router.patch('/:imageId', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    res.json(await operatorApiClient.updateImage(req.params['imageId'] as string, req.body as unknown));
  } catch (err) { next(err); }
});

// ── DELETE /api/images/:id (Administrator) ────────────────────────────────────

router.delete('/:imageId', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    await operatorApiClient.deleteImage(req.params['imageId'] as string);
    res.status(204).send();
  } catch (err) { next(err); }
});

export { router as imagesRouter };
