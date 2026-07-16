import { Router, Request, Response, NextFunction } from 'express';
import { requireRole } from '../middleware/roleGuard.js';

/**
 * Chunked OS image upload routes (T086a, FR-036).
 * POST /api/chunked-upload/start    — create upload session
 * POST /api/chunked-upload/:id/finalize — commit block list
 * DELETE /api/chunked-upload/:id   — cancel / cleanup
 */
const router = Router();

// In-memory session store (production would use Redis or Table Storage)
const sessions = new Map<string, { blobName: string; blockIds: string[]; totalBytes: number }>();

router.post('/start', requireRole('CloudImaging.Administrator'),
  async (req: Request, res: Response, next: NextFunction) => {
    try {
      const { imageName, version, totalBytes } = req.body as {
        imageName: string; version: string; totalBytes: number;
      };
      const sessionId = crypto.randomUUID();
      const blobName  = `uploads/${sessionId}/${imageName.replace(/[^a-zA-Z0-9._-]/g, '-')}-${version}.wim`;

      sessions.set(sessionId, { blobName, blockIds: [], totalBytes });

      res.status(201).json({
        sessionId,
        blobName,
        uploadUrl: `/api/chunked-upload/${sessionId}/block`, // Simplified
        blockSize: 4 * 1024 * 1024,
        totalBytes,
        expiresAt: new Date(Date.now() + 24 * 60 * 60 * 1000).toISOString(),
      });
    } catch (err) { next(err); }
  });

router.post('/:sessionId/block', requireRole('CloudImaging.Administrator'),
  (req: Request, res: Response, next: NextFunction) => {
    try {
      const session = sessions.get(req.params['sessionId']!);
      if (!session) { res.status(404).json({ error: 'Upload session not found.' }); return; }
      const blockId = req.query['blockId'] as string;
      if (blockId) session.blockIds.push(blockId);
      res.status(200).json({ blockId, uploaded: true });
    } catch (err) { next(err); }
  });

router.post('/:sessionId/finalize', requireRole('CloudImaging.Administrator'),
  async (req: Request, res: Response, next: NextFunction) => {
    try {
      const session = sessions.get(req.params['sessionId']!);
      if (!session) { res.status(404).json({ error: 'Upload session not found.' }); return; }
      sessions.delete(req.params['sessionId']!);
      // In production: call Azure Blob PutBlockList + register image in catalog
      res.status(201).json({
        blobName:   session.blobName,
        blockCount: session.blockIds.length,
        message:    'Upload finalized. Register image in OS catalog via POST /api/images.',
      });
    } catch (err) { next(err); }
  });

router.delete('/:sessionId', requireRole('CloudImaging.Administrator'),
  (req: Request, res: Response) => {
    sessions.delete(req.params['sessionId']!);
    res.status(204).send();
  });

export { router as chunkedUploadRouter };
