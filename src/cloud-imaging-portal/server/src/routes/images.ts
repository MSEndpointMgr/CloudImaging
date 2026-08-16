import { Router, Request, Response, NextFunction } from 'express';
import { requireRole } from '../middleware/roleGuard.js';
import { operatorApiClient } from '../services/operatorApiClient.js';

/**
 * Images router — OS image catalog CRUD proxy to the Operator API (T086, FR-036, FR-037).
 * Read endpoints require PortalAccess; write endpoints require Administrator.
 */
const router = Router();

// ── GET /api/images ───────────────────────────────────────────────────────────

router.get('/', requireRole('CloudImaging.PortalAccess'), async (_req: Request, res: Response, next: NextFunction) => {
  try {
    res.json(await operatorApiClient.getImages());
  } catch (err) { next(err); }
});

// ── POST /api/images — register new image (Administrator) ─────────────────────

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
    res.json(await operatorApiClient.startOsImageUpload(req.body as unknown));
  } catch (err) { next(err); }
});

router.post('/upload/:uploadId/publish', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const published = await operatorApiClient.publishOsImageUpload(req.params['uploadId'] as string, req.body as unknown);
    res.status(201).json(published);
  } catch (err) { next(err); }
});

// ── PATCH /api/images/:id — update metadata (Administrator) ───────────────────

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
