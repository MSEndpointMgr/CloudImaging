import { Router, Request, Response, NextFunction } from 'express';
import { requireRole } from '../middleware/roleGuard.js';
import { operatorApiClient } from '../services/operatorApiClient.js';

/**
 * Upload job status router.
 *
 * The OS, boot and recovery image publish endpoints answer 202 Accepted and hand the expensive
 * work (full-file SHA-256 verification, ISO extraction, blob move, catalog write) to a
 * background worker in the Imaging Core API. That is required because Azure Static Web Apps
 * caps every API request at a fixed 45 seconds, which multi-GB OS images cannot meet. The
 * portal client polls this endpoint until the job reports Completed or Failed.
 */
const router = Router();

router.get('/:uploadId', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    res.json(await operatorApiClient.getUploadJob(req.params['uploadId'] as string));
  } catch (err) { next(err); }
});

export { router as uploadJobsRouter };
