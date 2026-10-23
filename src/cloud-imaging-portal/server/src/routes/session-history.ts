import { Router, Request, Response, NextFunction } from 'express';
import { requireRole } from '../middleware/roleGuard.js';
import { operatorApiClient } from '../services/operatorApiClient.js';

/**
 * Session history routes. `GET /` returns full terminal-outcome records and is Reports data, so
 * it accepts Reader, Operator and Administrator only. The Dashboard's Completed Sessions stat is
 * shown to every Portal role, Technician included, so it reads `GET /completed-timestamps`
 * instead: completion times only, with no device, location, pre-flight, image or error detail.
 */
const router = Router();

interface SessionHistoryEntry { finalState?: unknown; terminalAt?: unknown }

// Registered before any parameterised path so a future `/:id` route cannot capture it.
router.get('/completed-timestamps', requireRole('CloudImaging.PortalAccess', 'CloudImaging.Reader'), async (req: Request, res: Response, next: NextFunction) => {
  try {
    const data = await operatorApiClient.getSessionHistory(
      req.query['from'] as string | undefined,
      req.query['to'] as string | undefined,
    );
    const records = Array.isArray(data) ? data as SessionHistoryEntry[] : [];
    const terminalAts = records
      .filter(r => r.finalState === 'SessionCompleted' && typeof r.terminalAt === 'string')
      .map(r => r.terminalAt as string);
    res.json(terminalAts);
  } catch (err) {
    next(err);
  }
});

router.get('/', requireRole('CloudImaging.Reader', 'CloudImaging.Operator', 'CloudImaging.Administrator'), async (req: Request, res: Response, next: NextFunction) => {
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
