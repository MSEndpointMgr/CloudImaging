import { Router, Request, Response, NextFunction } from 'express';
import { requireOperator } from '../middleware/roleGuard.js';
import { operatorApiClient } from '../services/operatorApiClient.js';

/**
 * Regions router: the tenant's country-to-region mapping shown on the Locations page's Regions
 * tab. Locations take their region from their country, so both reading and changing the mapping
 * require Operator or Administrator, like editing locations.
 */
const router = Router();

router.get('/', requireOperator, async (_req: Request, res: Response, next: NextFunction) => {
  try {
    res.json(await operatorApiClient.getRegions());
  } catch (err) {
    next(err);
  }
});

router.put('/', requireOperator, async (req: Request, res: Response, next: NextFunction) => {
  try {
    await operatorApiClient.putRegions(req.body);
    res.status(204).send();
  } catch (err) {
    next(err);
  }
});

export { router as regionsRouter };
