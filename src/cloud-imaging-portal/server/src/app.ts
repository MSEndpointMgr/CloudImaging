import 'applicationinsights';
import appInsights from 'applicationinsights';
import express from 'express';
import helmet from 'helmet';
import cors from 'cors';
import { rateLimit } from 'express-rate-limit';
import axios from 'axios';

// Application Insights. Must be set up before importing any other modules (FR-065, FR-067)
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

// CORS. Restrict to configured origins (populated from CORS_ALLOWED_ORIGINS env var)
const allowedOrigins = (process.env['CORS_ALLOWED_ORIGINS'] ?? '')
  .split(',')
  .map(o => o.trim())
  .filter(Boolean);

app.use(cors({
  origin: (origin, callback) => {
    if (!origin || allowedOrigins.includes(origin)) { callback(null, true); return; }
    callback(new Error(`Origin '${origin}' not allowed.`));
  },
  credentials: true,
}));

app.use(express.json({ limit: '1mb' }));

// Basic rate limiting on all portal API routes (operator-side; per-session limits handled at DeviceGateway)
app.use('/api', rateLimit({ windowMs: 60_000, max: 300, standardHeaders: true, legacyHeaders: false }));

// ── Health probe ────────────────────────────────────────────────────────────
app.get('/api/health', (_req, res) => res.json({ status: 'ok' }));

// ── Public runtime configuration ─────────────────────────────────────
// Serves the Entra ID settings the browser SPA needs to initialise MSAL at
// runtime, sourced from the App Service app settings that Bicep populates from the
// deployment's portalClientId/tenantId parameters. This lets a single prebuilt SPA
// bundle work for any tenant without a build-time rebuild (FR-041/FR-042). These
// values are NOT secrets. The client ID, tenant ID, and authority are all public
// and already embedded in every issued token and sign-in redirect.
app.get('/api/config', (_req, res) => {
  const clientId = process.env['ENTRA_CLIENT_ID'] ?? '';
  const tenantId = process.env['ENTRA_TENANT_ID'] ?? '';
  const authority = process.env['ENTRA_AUTHORITY'] ?? '';
  res.json({
    clientId,
    tenantId,
    authority,
    apiScope: clientId ? `api://${clientId}/user_impersonation` : '',
  });
});

// ── Routes (registered after auth middleware) ────────────────────────────────
import { auth } from './middleware/auth.js';
import { sessionsRouter } from './routes/sessions.js';
import { imagesRouter } from './routes/images.js';
import { brandingRouter } from './routes/branding.js';
import { bootImagesRouter } from './routes/boot-images.js';
import { certRouter } from './routes/cert.js';
import { portalConfigRouter } from './routes/portal-config.js';
import { partitioningSchemeRouter } from './routes/partitioning-scheme.js';
import { recoveryImagesRouter } from './routes/recovery-images.js';
import { sessionHistoryRouter } from './routes/session-history.js';

// Apply Entra auth to all /api routes except the public /api/health and /api/config
app.use('/api', (req, res, next) => {
  if (req.path === '/health' || req.path === '/config') { next(); return; }
  return auth(req, res, next);
});

app.use('/api/sessions',      sessionsRouter);
app.use('/api/images',        imagesRouter);
app.use('/api/branding',      brandingRouter);
app.use('/api/boot-images',   bootImagesRouter);
app.use('/api/cert',          certRouter);
app.use('/api/portal-config',  portalConfigRouter);
app.use('/api/partitioning-scheme', partitioningSchemeRouter);
app.use('/api/recovery-images',     recoveryImagesRouter);
app.use('/api/session-history',     sessionHistoryRouter);
// ── Global error handler ─────────────────────────────────────────────────────
app.use((err: unknown, _req: express.Request, res: express.Response, _next: express.NextFunction) => {
  console.error('Unhandled error', err);

  // Translate downstream Operator API failures into meaningful responses so the
  // portal can distinguish "backend unreachable" from an authorization/validation
  // error instead of always surfacing a generic 500.
  if (axios.isAxiosError(err)) {
    // No response received. The Operator API is unreachable (down, wrong URL,
    // connection refused, DNS failure, or timed out).
    if (!err.response) {
      const unreachable = err.code === 'ECONNABORTED';
      res.status(504).json({
        type: 'https://cloudimaging.io/errors/backend-unavailable',
        title: 'Backend API unavailable.',
        status: 504,
        detail: unreachable
          ? 'The Operator API did not respond in time. Confirm it is running and reachable.'
          : `The Operator API could not be reached (${err.code ?? 'connection error'}). Confirm it is running and reachable.`,
      });
      return;
    }

    // Upstream responded with an error status. Forward it (and its problem
    // details when present) so the client sees the real cause (e.g. 403, 400).
    const status = err.response.status;
    const upstream = err.response.data as Record<string, unknown> | undefined;
    res.status(status).json({
      type: (upstream?.['type'] as string | undefined) ?? 'https://cloudimaging.io/errors/upstream-error',
      title: (upstream?.['title'] as string | undefined) ?? 'The backend API returned an error.',
      status,
      detail: (upstream?.['detail'] as string | undefined) ??
        `The Operator API responded with status ${String(status)}.`,
    });
    return;
  }

  res.status(500).json({
    type: 'https://cloudimaging.io/errors/internal-error',
    title: 'An unexpected error occurred.',
    status: 500,
    detail: 'An internal error occurred. Check Application Insights for correlation details.',
  });
});

const port = parseInt(process.env['PORT'] ?? '3000', 10);
app.listen(port, () => { console.log(`Cloud Imaging Portal backend listening on port ${String(port)}`); });

export default app;
