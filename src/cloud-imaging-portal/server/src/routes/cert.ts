import { Router, Request, Response, NextFunction } from 'express';
import { requireRole } from '../middleware/roleGuard.js';
import { operatorApiClient } from '../services/operatorApiClient.js';

/**
 * Boot media certificate management routes (T178, FR-068).
 * Administrator-only: generate and rotate certificates.
 */
const router = Router();

// GET /api/cert/active
router.get('/active', requireRole('CloudImaging.PortalAccess'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const token = req.headers.authorization?.replace('Bearer ', '') ?? '';
    operatorApiClient.setToken(token);
    res.json(await operatorApiClient.getConfiguration()); // cert metadata from operator
  } catch (err) { next(err); }
});

// POST /api/cert/generate
router.post('/generate', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const token = req.headers.authorization?.replace('Bearer ', '') ?? '';
    operatorApiClient.setToken(token);
    const axiosRes = await (operatorApiClient as unknown as { http: import('axios').AxiosInstance }).http
      .post('/api/cert/generate', {});
    res.status(201).json(axiosRes.data);
  } catch (err) { next(err); }
});

// POST /api/cert/rotate
router.post('/rotate', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const token = req.headers.authorization?.replace('Bearer ', '') ?? '';
    operatorApiClient.setToken(token);
    const { confirmed } = req.body as { confirmed?: boolean };
    const axiosRes = await (operatorApiClient as unknown as { http: import('axios').AxiosInstance }).http
      .post('/api/cert/rotate', { confirmed });
    res.json(axiosRes.data);
  } catch (err) { next(err); }
});

export { router as certRouter };
