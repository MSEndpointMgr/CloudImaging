import { Router, Response, NextFunction } from 'express';
import axios from 'axios';
import { requireRole } from '../middleware/roleGuard.js';
import { operatorApiClient } from '../services/operatorApiClient.js';
import type { AuthenticatedRequest } from '../middleware/auth.js';

/**
 * User location preference router (Location Labels feature). Keyed by the signed-in user's
 * Entra object id (oid claim) — the Operator/Imaging Core APIs have no other concept of "the
 * signed-in portal user", so the oid is forwarded explicitly as a route parameter.
 */
const router = Router();

router.get('/', requireRole('CloudImaging.PortalAccess'), async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    const userId = req.user?.['oid'] as string | undefined;
    if (!userId) { res.status(401).json({ error: 'Missing user identity claim.' }); return; }
    const data = await operatorApiClient.getUserLocationPreference(userId);
    res.json(data);
  } catch (err) {
    // No preference saved yet — surface as null rather than an error so the client can
    // treat "no location set" as a normal, expected state.
    if (axios.isAxiosError(err) && err.response?.status === 404) {
      res.json(null);
      return;
    }
    next(err);
  }
});

router.put('/', requireRole('CloudImaging.PortalAccess'), async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    const userId = req.user?.['oid'] as string | undefined;
    if (!userId) { res.status(401).json({ error: 'Missing user identity claim.' }); return; }
    const data = await operatorApiClient.putUserLocationPreference(userId, req.body);
    res.json(data);
  } catch (err) {
    next(err);
  }
});

export { router as userPreferencesRouter };
