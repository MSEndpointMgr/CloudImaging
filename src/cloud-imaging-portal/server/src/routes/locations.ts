import { Router, Request, Response, NextFunction } from 'express';
import { requireOperator, requireRole } from '../middleware/roleGuard.js';
import { requireGuidParams } from '../middleware/validateParams.js';
import { operatorApiClient } from '../services/operatorApiClient.js';

/**
 * Locations router (Location Labels feature). Proxies the admin-managed location catalog to
 * the Operator API. Reading the catalog is available to any signed-in portal user (needed to
 * populate the Sessions page filter and the Header account menu's "my location" picker);
 * creating, editing and deleting entries requires Operator or Administrator.
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

router.post('/', requireOperator, async (req: Request, res: Response, next: NextFunction) => {
  try {
    const data = await operatorApiClient.createLocation(req.body);
    res.status(201).json(data);
  } catch (err) {
    next(err);
  }
});

router.put('/:locationId', requireOperator, requireGuidParams('locationId'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const data = await operatorApiClient.updateLocation(req.params['locationId'] as string, req.body);
    res.json(data);
  } catch (err) {
    next(err);
  }
});

router.delete('/:locationId', requireOperator, requireGuidParams('locationId'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    await operatorApiClient.deleteLocation(req.params['locationId'] as string);
    res.status(204).send();
  } catch (err) {
    next(err);
  }
});

/** Sets which saved Wi-Fi networks devices at the location join; only network location lists change. */
router.put('/:locationId/wifi-networks', requireOperator, requireGuidParams('locationId'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const networkIds = (req.body as { networkIds?: unknown } | undefined)?.networkIds;
    await operatorApiClient.setLocationWifiNetworks(req.params['locationId'] as string, { networkIds: Array.isArray(networkIds) ? networkIds : [] });
    res.status(204).send();
  } catch (err) {
    next(err);
  }
});

export { router as locationsRouter };
