import { Router, Request, Response, NextFunction } from 'express';
import axios from 'axios';
import { requireRole } from '../middleware/roleGuard.js';
import { operatorApiClient } from '../services/operatorApiClient.js';

/**
 * Boot media certificate management routes (T178, FR-068).
 * Administrator-only: view active certificate metadata, generate and rotate certificates.
 */
const router = Router();

// GET /api/cert/active: active boot media certificate metadata (thumbprint/validity).
router.get('/active', requireRole('CloudImaging.Administrator'), async (_req: Request, res: Response, next: NextFunction) => {
  try {
    res.json(await operatorApiClient.getBootMediaCertMetadata());
  } catch (err) {
    // No active certificate configured yet. Surface as an empty result rather than an error
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

// POST /api/cert/rotate: the replaced certificate stays accepted for previousAcceptedDays (0 ends it now).
router.post('/rotate', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const { confirmed, previousAcceptedDays } = req.body as { confirmed?: boolean; previousAcceptedDays?: unknown };
    if (previousAcceptedDays !== undefined
      && (typeof previousAcceptedDays !== 'number' || !Number.isInteger(previousAcceptedDays) || previousAcceptedDays < 0 || previousAcceptedDays > 30)) {
      res.status(400).json({ title: 'Bad Request', status: 400, detail: 'previousAcceptedDays must be a whole number from 0 to 30.' });
      return;
    }
    const axiosRes = await (operatorApiClient as unknown as { http: import('axios').AxiosInstance }).http
      .post('/api/cert/rotate', { confirmed, previousAcceptedDays });
    res.json(axiosRes.data);
  } catch (err) { next(err); }
});

// POST /api/cert/previous/stop: the certificate the last rotation replaced is no longer accepted.
router.post('/previous/stop', requireRole('CloudImaging.Administrator'), async (_req: Request, res: Response, next: NextFunction) => {
  try {
    const stopped = await operatorApiClient.stopAcceptingPreviousBootMediaCert();
    if (!stopped) {
      res.status(404).json({ title: 'Not Found', status: 404, detail: 'No previous certificate is still accepted.' });
      return;
    }
    res.status(204).end();
  } catch (err) { next(err); }
});

export { router as certRouter };
