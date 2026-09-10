import { Router, Request, Response, NextFunction } from 'express';
import { requireRole } from '../middleware/roleGuard.js';
import { requireGuidParams } from '../middleware/validateParams.js';
import { operatorApiClient } from '../services/operatorApiClient.js';

/**
 * Upload job status router.
 *
 * The OS, boot and recovery image publish endpoints answer 202 Accepted and hand the expensive
 * work (full-file SHA-256 verification, ISO extraction, blob move, catalog write) to a
 * background worker in the Imaging Core API. That is required because Azure Static Web Apps
 * caps every API request at a fixed 45 seconds, which multi-GB OS images cannot meet. The
 * portal client polls this endpoint until the job reports Completed or Failed.
 *
 * Jobs are deliberately NOT scoped to the operator who started them. Creating one requires
 * `CloudImaging.Administrator` (all three publish endpoints) and so does reading one, so there
 * is no privilege boundary between the poller and the uploader — only administrators can see
 * administrator-created jobs either way. Adding an owner claim would mean persisting the
 * caller's object id on the job in the Imaging Core API and having this backend assert it over
 * the wire, which would make a downstream service trust a caller-supplied identity header; that
 * is a weaker model than the one already in place, for no reduction in exposure. The
 * unguessable, server-generated GUID job id (validated below) prevents blind enumeration.
 */
const router = Router();

router.get('/:uploadId', requireRole('CloudImaging.Administrator'), requireGuidParams('uploadId'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    res.json(await operatorApiClient.getUploadJob(req.params['uploadId'] as string));
  } catch (err) { next(err); }
});

export { router as uploadJobsRouter };
