import { Router, Request, Response, NextFunction } from 'express';
import { requireRole } from '../middleware/roleGuard.js';
import { operatorApiClient } from '../services/operatorApiClient.js';

/**
 * Partitioning scheme route.
 * GET is readable by any signed-in user (PortalAccess); PUT is Administrator-only.
 */
const router = Router();

router.get('/', requireRole('CloudImaging.PortalAccess'), async (_req: Request, res: Response, next: NextFunction) => {
  try {
    const scheme = await operatorApiClient.getPartitioningScheme();
    res.json(scheme);
  } catch (err) { next(err); }
});

router.put('/', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    await operatorApiClient.putPartitioningScheme(req.body as unknown);
    res.status(204).send();
  } catch (err) { next(err); }
});

export { router as partitioningSchemeRouter };
