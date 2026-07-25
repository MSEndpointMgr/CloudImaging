import dotenv from 'dotenv';

/**
 * Loads local development environment variables before any other module reads
 * `process.env`. This module MUST be imported first (see index.ts) because
 * middleware such as auth.ts captures Entra settings at import time.
 *
 * `.env.local` holds developer-specific overrides and is never committed; `.env`
 * provides shared defaults. Neither exists in deployed environments, where the
 * platform (App Service application settings) supplies configuration instead —
 * dotenv silently no-ops when the files are absent and never overrides variables
 * already present in `process.env`.
 */
dotenv.config({ path: '.env.local' });
dotenv.config();
