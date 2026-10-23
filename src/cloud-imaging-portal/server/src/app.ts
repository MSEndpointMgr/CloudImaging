import 'applicationinsights';
import appInsights from 'applicationinsights';
import express from 'express';
import helmet from 'helmet';
import cors from 'cors';
import { rateLimit } from 'express-rate-limit';
import { DEPLOYED_VERSION } from './version.js';
import { errorHandler } from './middleware/errorHandler.js';

// Application Insights. Must be set up before importing any other modules (FR-065, FR-067)
const aiConnectionString = process.env['APPLICATIONINSIGHTS_CONNECTION_STRING'];
if (aiConnectionString) {
  appInsights.setup(aiConnectionString)
    .setAutoDependencyCorrelation(true)
    .setAutoCollectRequests(true)
    .setAutoCollectExceptions(true)
    .setAutoCollectDependencies(true)
    // Second argument is what forwards console.* to App Insights traces. Without it the
    // backend's own diagnostics (token validation failures, operator token role checks) only
    // reach container stdout, which App Service discards unless filesystem logging happens to
    // be switched on.
    .setAutoCollectConsole(true, true)
    .start();
}

const app = express();

// Trusts exactly one hop: App Service's own front-end reverse proxy sets X-Forwarded-For/-Proto,
// so rate-limiting (and any req.ip use) must trust it, or express-rate-limit refuses to key
// requests and express-rate-limit throws ERR_ERL_UNEXPECTED_X_FORWARDED_FOR on every request.
app.set('trust proxy', 1);

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

// Setup profiles carry up to 20 scripts of 64 KB plus a 256 KB answer file, so their router gets a
// larger limit. It must run first: body-parser skips a body that is already parsed.
app.use('/api/setup-profiles', express.json({ limit: '4mb' }));
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
    // Stamped into the build at release time (see version.ts). A product version is not
    // sensitive, and this is already the SPA's bootstrap call.
    version: DEPLOYED_VERSION,
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
import { locationsRouter } from './routes/locations.js';
import { regionsRouter } from './routes/regions.js';
import { wifiNetworksRouter } from './routes/wifi-networks.js';
import { userPreferencesRouter } from './routes/user-preferences.js';
import { uploadJobsRouter } from './routes/upload-jobs.js';
import { updateCheckRouter } from './routes/update-check.js';
import { autopilotRouter } from './routes/autopilot.js';
import { preFlightOverridesRouter } from './routes/preflight-overrides.js';
import { setupProfilesRouter } from './routes/setup-profiles.js';

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
app.use('/api/locations',           locationsRouter);
app.use('/api/regions',             regionsRouter);
app.use('/api/wifi-networks',       wifiNetworksRouter);
app.use('/api/user-preferences',    userPreferencesRouter);
app.use('/api/upload-jobs',         uploadJobsRouter);
app.use('/api/update-check',        updateCheckRouter);
app.use('/api/autopilot',           autopilotRouter);
app.use('/api/preflight-overrides', preFlightOverridesRouter);
app.use('/api/setup-profiles',      setupProfilesRouter);

// ── Global error handler ─────────────────────────────────────────────────────
app.use(errorHandler);

const port = parseInt(process.env['PORT'] ?? '3000', 10);
app.listen(port, () => { console.log(`Cloud Imaging Portal backend listening on port ${String(port)}`); });

export default app;
