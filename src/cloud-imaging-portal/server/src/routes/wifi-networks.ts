import { Router, Request, Response, NextFunction } from 'express';
import type { AuthenticatedRequest } from '../middleware/auth.js';
import { requireRole } from '../middleware/roleGuard.js';
import { requireGuidParams } from '../middleware/validateParams.js';
import { operatorApiClient } from '../services/operatorApiClient.js';
import { decidingUser } from './autopilot.js';

/**
 * Wi-Fi networks router. Administrators save networks that devices on a Windows RE boot image join
 * automatically. Passwords are write-only: they pass through to Key Vault and no response carries
 * one. The password's "changed by" is always the signed-in user, never a value from the browser.
 */
const router = Router();

const FIELDS = ['ssid', 'hidden', 'security', 'password', 'allLocations', 'locationIds', 'enabled'] as const;

function toPayload(req: AuthenticatedRequest): Record<string, unknown> {
  const body = (req.body ?? {}) as Record<string, unknown>;
  const payload: Record<string, unknown> = {};
  for (const field of FIELDS) {
    if (field in body) payload[field] = body[field];
  }
  payload['changedBy'] = decidingUser(req)?.decidedByUpn ?? null;
  return payload;
}

router.get('/', requireRole('CloudImaging.Administrator'), async (_req: Request, res: Response, next: NextFunction) => {
  try {
    res.json(await operatorApiClient.getWifiNetworks());
  } catch (err) {
    next(err);
  }
});

router.post('/', requireRole('CloudImaging.Administrator'), async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    res.status(201).json(await operatorApiClient.createWifiNetwork(toPayload(req)));
  } catch (err) {
    next(err);
  }
});

router.put('/order', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const networkIds = (req.body as { networkIds?: unknown } | undefined)?.networkIds;
    await operatorApiClient.setWifiNetworkOrder({ networkIds: Array.isArray(networkIds) ? networkIds : [] });
    res.status(204).send();
  } catch (err) {
    next(err);
  }
});

router.put('/:networkId', requireRole('CloudImaging.Administrator'), requireGuidParams('networkId'), async (req: AuthenticatedRequest, res: Response, next: NextFunction) => {
  try {
    res.json(await operatorApiClient.updateWifiNetwork(req.params['networkId'] as string, toPayload(req)));
  } catch (err) {
    next(err);
  }
});

router.delete('/:networkId', requireRole('CloudImaging.Administrator'), requireGuidParams('networkId'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    await operatorApiClient.deleteWifiNetwork(req.params['networkId'] as string);
    res.status(204).send();
  } catch (err) {
    next(err);
  }
});

export { router as wifiNetworksRouter };
