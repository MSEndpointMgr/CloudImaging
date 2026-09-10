import { Router, Request, Response, NextFunction } from 'express';
import { requireRole } from '../middleware/roleGuard.js';
import { requireGuidParams } from '../middleware/validateParams.js';
import { operatorApiClient } from '../services/operatorApiClient.js';

/**
 * Locations router (Location Labels feature). Proxies the admin-managed location catalog to
 * the Operator API. Reading the catalog is available to any signed-in portal user (needed to
 * populate the Sessions page filter and the Header account menu's "my location" picker);
 * creating/deleting entries is Administrator-only.
 */
const router = Router();

router.get('/', requireRole('CloudImaging.PortalAccess'), async (_req: Request, res: Response, next: NextFunction) => {
  try {
    const data = await operatorApiClient.getLocations();
    res.json(data);
  } catch (err) {
    next(err);
  }
});

router.post('/', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const data = await operatorApiClient.createLocation(req.body);
    res.status(201).json(data);
  } catch (err) {
    next(err);
  }
});

router.delete('/:locationId', requireRole('CloudImaging.Administrator'), requireGuidParams('locationId'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    await operatorApiClient.deleteLocation(req.params['locationId'] as string);
    res.status(204).send();
  } catch (err) {
    next(err);
  }
});

export { router as locationsRouter };
