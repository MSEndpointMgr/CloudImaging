import { Router, Request, Response, NextFunction } from 'express';
import axios from 'axios';
import { requireRole } from '../middleware/roleGuard.js';
import { operatorApiClient } from '../services/operatorApiClient.js';

/**
 * Boot media certificate management routes (T178, FR-068).
 * Administrator-only: view active certificate metadata, generate and rotate certificates.
 */
const router = Router();

// GET /api/cert/active — active boot media certificate metadata (thumbprint/validity).
router.get('/active', requireRole('CloudImaging.Administrator'), async (_req: Request, res: Response, next: NextFunction) => {
  try {
    res.json(await operatorApiClient.getBootMediaCertMetadata());
  } catch (err) {
    // No active certificate configured yet — surface as an empty result rather than an error
    // so the Configuration page renders the "no certificate" state instead of failing to load.
    if (axios.isAxiosError(err) && err.response?.status === 404) {
      res.json(null);
      return;
    }
    next(err);
  }
});

// POST /api/cert/generate
router.post('/generate', requireRole('CloudImaging.Administrator'), async (_req: Request, res: Response, next: NextFunction) => {
  try {
    const axiosRes = await (operatorApiClient as unknown as { http: import('axios').AxiosInstance }).http
      .post('/api/cert/generate', {});
    res.status(201).json(axiosRes.data);
  } catch (err) { next(err); }
});

// POST /api/cert/rotate
router.post('/rotate', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const { confirmed } = req.body as { confirmed?: boolean };
    const axiosRes = await (operatorApiClient as unknown as { http: import('axios').AxiosInstance }).http
      .post('/api/cert/rotate', { confirmed });
    res.json(axiosRes.data);
  } catch (err) { next(err); }
});

export { router as certRouter };
