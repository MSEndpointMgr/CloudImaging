import { Router, Request, Response, NextFunction } from 'express';
import { requireRole } from '../middleware/roleGuard.js';
import { operatorApiClient } from '../services/operatorApiClient.js';

/**
 * Branding router — proxies to Operator API branding endpoints (T094, FR-038).
 */
const router = Router();

router.get('/', requireRole('CloudImaging.PortalAccess'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const token = req.headers.authorization?.replace('Bearer ', '') ?? '';
    operatorApiClient.setToken(token);
    res.json(await operatorApiClient.getBranding());
  } catch (err) { next(err); }
});

router.put('/', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const token = req.headers.authorization?.replace('Bearer ', '') ?? '';
    operatorApiClient.setToken(token);
    await operatorApiClient.putBranding(req.body as unknown);
    res.status(204).send();
  } catch (err) { next(err); }
});

router.get('/logo/sas', requireRole('CloudImaging.PortalAccess'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const token = req.headers.authorization?.replace('Bearer ', '') ?? '';
    operatorApiClient.setToken(token);
    res.json(await operatorApiClient.getBrandingLogoSas());
  } catch (err) { next(err); }
});

router.put('/logo', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const token = req.headers.authorization?.replace('Bearer ', '') ?? '';
    operatorApiClient.setToken(token);
    res.json(await operatorApiClient.uploadBrandingLogo(req.body as unknown));
  } catch (err) { next(err); }
});

export { router as brandingRouter };
