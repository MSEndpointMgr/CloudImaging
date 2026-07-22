import { Router, Request, Response, NextFunction } from 'express';
import { requireRole } from '../middleware/roleGuard.js';
import { operatorApiClient } from '../services/operatorApiClient.js';

/**
 * Images router — OS image catalog CRUD proxy to the Operator API (T086, FR-036, FR-037).
 * Read endpoints require PortalAccess; write endpoints require Administrator.
 */
const router = Router();

// ── GET /api/images ───────────────────────────────────────────────────────────

router.get('/', requireRole('CloudImaging.PortalAccess'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const token = req.headers.authorization?.replace('Bearer ', '') ?? '';
    operatorApiClient.setToken(token);
    res.json(await operatorApiClient.getImages());
  } catch (err) { next(err); }
});

// ── POST /api/images — register new image (Administrator) ─────────────────────

router.post('/', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const token = req.headers.authorization?.replace('Bearer ', '') ?? '';
    operatorApiClient.setToken(token);
    const result = await operatorApiClient.createImage(req.body as unknown);
    res.status(201).json(result);
  } catch (err) { next(err); }
});

// ── PATCH /api/images/:id — update metadata (Administrator) ───────────────────

router.patch('/:imageId', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const token = req.headers.authorization?.replace('Bearer ', '') ?? '';
    operatorApiClient.setToken(token);
    res.json(await operatorApiClient.updateImage(req.params['imageId'], req.body as unknown));
  } catch (err) { next(err); }
});

// ── DELETE /api/images/:id (Administrator) ────────────────────────────────────

router.delete('/:imageId', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const token = req.headers.authorization?.replace('Bearer ', '') ?? '';
    operatorApiClient.setToken(token);
    await operatorApiClient.deleteImage(req.params['imageId']);
    res.status(204).send();
  } catch (err) { next(err); }
});

export { router as imagesRouter };
