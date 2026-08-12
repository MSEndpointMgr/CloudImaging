import { Router, Request, Response, NextFunction } from 'express';
import { requireRole } from '../middleware/roleGuard.js';
import { operatorApiClient } from '../services/operatorApiClient.js';

/**
 * Branding router — proxies to Operator API branding endpoints (T094, FR-038).
 */
const router = Router();

router.get('/', requireRole('CloudImaging.PortalAccess'), async (_req: Request, res: Response, next: NextFunction) => {
  try {
    res.json(await operatorApiClient.getBranding());
  } catch (err) { next(err); }
});

router.put('/', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    await operatorApiClient.putBranding(req.body as unknown);
    res.status(204).send();
  } catch (err) { next(err); }
});

router.get('/logo/sas', requireRole('CloudImaging.PortalAccess'), async (_req: Request, res: Response, next: NextFunction) => {
  try {
    res.json(await operatorApiClient.getBrandingLogoSas());
  } catch (err) { next(err); }
});

router.put('/logo', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    res.json(await operatorApiClient.uploadBrandingLogo(req.body as unknown));
  } catch (err) { next(err); }
});

router.put('/portal-logo', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    res.json(await operatorApiClient.uploadBrandingPortalLogo(req.body as unknown));
  } catch (err) { next(err); }
});

router.get('/logo/content', requireRole('CloudImaging.PortalAccess'), async (_req: Request, res: Response, next: NextFunction) => {
  try {
    const { data, contentType } = await operatorApiClient.getBrandingLogoContent();
    res.setHeader('Content-Type', contentType);
    res.setHeader('Cache-Control', 'no-cache');
    res.send(data);
  } catch (err) {
    if (isNotFound(err)) { res.status(404).end(); return; }
    next(err);
  }
});

router.get('/portal-logo/content', requireRole('CloudImaging.PortalAccess'), async (_req: Request, res: Response, next: NextFunction) => {
  try {
    const { data, contentType } = await operatorApiClient.getBrandingPortalLogoContent();
    res.setHeader('Content-Type', contentType);
    res.setHeader('Cache-Control', 'no-cache');
    res.send(data);
  } catch (err) {
    if (isNotFound(err)) { res.status(404).end(); return; }
    next(err);
  }
});

/** True when an Operator API call failed with HTTP 404 (e.g. no logo configured yet). */
function isNotFound(err: unknown): boolean {
  return (err as { response?: { status?: number } }).response?.status === 404;
}

export { router as brandingRouter };
