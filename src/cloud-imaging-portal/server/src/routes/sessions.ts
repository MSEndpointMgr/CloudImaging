import { Router, Request, Response, NextFunction } from 'express';
import { requireRole } from '../middleware/roleGuard.js';
import { operatorApiClient } from '../services/operatorApiClient.js';

/**
 * Sessions router — proxies all session-related operations to the Operator API (T041, T041a).
 * Authentication is already validated by auth middleware on the parent app.
 * Role enforcement uses requireRole() factory from roleGuard middleware.
 */
const router = Router();

// ── GET /api/sessions — list sessions (PortalAccess required) ─────────────

router.get('/', requireRole('CloudImaging.PortalAccess'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const token = req.headers.authorization?.replace('Bearer ', '') ?? '';
    operatorApiClient.setToken(token);
    const data = await operatorApiClient.getSessions(req.query['filter'] as string | undefined);
    res.json(data);
  } catch (err) {
    next(err);
  }
});

// ── GET /api/sessions/:id — get single session ────────────────────────────

router.get('/:sessionId', requireRole('CloudImaging.PortalAccess'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const token = req.headers.authorization?.replace('Bearer ', '') ?? '';
    operatorApiClient.setToken(token);
    const data = await operatorApiClient.getSession(req.params['sessionId']);
    res.json(data);
  } catch (err) {
    next(err);
  }
});

// ── POST /api/sessions/couple — couple by passcode (PortalAccess) ─────────

router.post('/couple', requireRole('CloudImaging.PortalAccess'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const token = req.headers.authorization?.replace('Bearer ', '') ?? '';
    operatorApiClient.setToken(token);
    const { passcode } = req.body as { passcode: string };
    if (!passcode) {
      res.status(400).json({ error: 'passcode is required' });
      return;
    }
    const data = await operatorApiClient.coupleSession(passcode);
    res.status(201).json(data);
  } catch (err) {
    next(err);
  }
});

// ── POST /api/sessions/:sessionId/assign — assign image (PortalAccess) ────

router.post('/:sessionId/assign', requireRole('CloudImaging.PortalAccess'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const token = req.headers.authorization?.replace('Bearer ', '') ?? '';
    operatorApiClient.setToken(token);
    const { osImageId } = req.body as { osImageId: string };
    if (!osImageId) {
      res.status(400).json({ error: 'osImageId is required' });
      return;
    }
    const data = await operatorApiClient.assignSession(req.params['sessionId'], osImageId);
    res.status(201).json(data);
  } catch (err) {
    next(err);
  }
});

// ── POST /api/sessions/bulk-assign — bulk assign (Administrator only) ─────

router.post('/bulk-assign', requireRole('CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const token = req.headers.authorization?.replace('Bearer ', '') ?? '';
    operatorApiClient.setToken(token);
    const { sessionIds, osImageId } = req.body as { sessionIds: string[]; osImageId: string };
    const data = await operatorApiClient.bulkAssign(sessionIds, osImageId);
    res.status(202).json(data);
  } catch (err) {
    next(err);
  }
});

export { router as sessionsRouter };
