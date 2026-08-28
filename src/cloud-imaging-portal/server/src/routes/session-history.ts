import { Router, Request, Response, NextFunction } from 'express';
import { requireRole } from '../middleware/roleGuard.js';
import { operatorApiClient } from '../services/operatorApiClient.js';

/**
 * Session history route (Reports feature). Proxies durable terminal-outcome records to the
 * Operator API. Uses the same `CloudImaging.PortalAccess` gate as `/api/sessions` (rather than
 * Administrator-only) because the Dashboard's "Completed Sessions" stat card — visible to every
 * signed-in portal user, not just Administrators — also reads this endpoint (Reports feature,
 * Phase 7). The dedicated Reports *pages* that surface this data in more detail are still
 * Administrator-only, but that's enforced client-side (RequireAdmin + Sidebar adminOnly), not
 * by restricting the underlying data endpoint.
 */
const router = Router();

router.get('/', requireRole('CloudImaging.PortalAccess'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const data = await operatorApiClient.getSessionHistory(
      req.query['from'] as string | undefined,
      req.query['to'] as string | undefined,
    );
    res.json(data);
  } catch (err) {
    next(err);
  }
});

export { router as sessionHistoryRouter };
