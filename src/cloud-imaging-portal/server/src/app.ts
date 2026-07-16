import 'applicationinsights';
import appInsights from 'applicationinsights';
import express from 'express';
import helmet from 'helmet';
import cors from 'cors';
import { rateLimit } from 'express-rate-limit';

// Application Insights — must be set up before importing any other modules (FR-065, FR-067)
const aiConnectionString = process.env['APPLICATIONINSIGHTS_CONNECTION_STRING'];
if (aiConnectionString) {
  appInsights.setup(aiConnectionString)
    .setAutoDependencyCorrelation(true)
    .setAutoCollectRequests(true)
    .setAutoCollectExceptions(true)
    .setAutoCollectDependencies(true)
    .start();
}

const app = express();

// Security headers
app.use(helmet());

// CORS — restrict to configured origins (populated from CORS_ALLOWED_ORIGINS env var)
const allowedOrigins = (process.env['CORS_ALLOWED_ORIGINS'] ?? '')
  .split(',')
  .map(o => o.trim())
  .filter(Boolean);

app.use(cors({
  origin: (origin, callback) => {
    if (!origin || allowedOrigins.includes(origin)) return callback(null, true);
    callback(new Error(`Origin '${origin}' not allowed.`));
  },
  credentials: true,
}));

app.use(express.json({ limit: '1mb' }));

// Basic rate limiting on all portal API routes (operator-side; per-session limits handled at DeviceGateway)
app.use('/api', rateLimit({ windowMs: 60_000, max: 300, standardHeaders: true, legacyHeaders: false }));

// ── Health probe ────────────────────────────────────────────────────────────
app.get('/api/health', (_req, res) => res.json({ status: 'ok' }));

// ── Routes (registered after auth middleware) ────────────────────────────────
import { auth } from './middleware/auth.js';
import { sessionsRouter } from './routes/sessions.js';
import { imagesRouter } from './routes/images.js';
import { brandingRouter } from './routes/branding.js';
import { configurationRouter } from './routes/configuration.js';
import { bootImagesRouter } from './routes/boot-images.js';

// Apply Entra auth to all /api routes except /api/health
app.use('/api', (req, res, next) => {
  if (req.path === '/health') return next();
  return auth(req, res, next);
});

app.use('/api/sessions',      sessionsRouter);
app.use('/api/images',        imagesRouter);
app.use('/api/branding',      brandingRouter);
app.use('/api/configuration', configurationRouter);
app.use('/api/boot-images',   bootImagesRouter);

// ── Global error handler ─────────────────────────────────────────────────────
app.use((err: unknown, _req: express.Request, res: express.Response, _next: express.NextFunction) => {
  console.error('Unhandled error', err);
  res.status(500).json({
    type: 'https://cloudimaging.io/errors/internal-error',
    title: 'An unexpected error occurred.',
    status: 500,
    detail: 'An internal error occurred. Check Application Insights for correlation details.',
  });
});

const port = parseInt(process.env['PORT'] ?? '3000', 10);
app.listen(port, () => console.log(`Cloud Imaging Portal backend listening on port ${port}`));

export default app;
