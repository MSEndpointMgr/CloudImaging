import { chromium, FullConfig } from '@playwright/test';

/**
 * Playwright global setup. Authenticates to the portal and saves auth state (T149).
 *
 * Run against a locally running portal dev server:
 *   BASE_URL=http://localhost:5173 npx playwright test
 *
 * The setup uses environment variables for credentials so secrets are never
 * committed to source control.
 *
 * Required env vars:
 *   PORTAL_URL         Portal frontend URL (default: http://localhost:5173)
 *   PORTAL_USERNAME    Entra ID UPN of a test account with PortalAccess or Administrator role
 *   PORTAL_PASSWORD    Password for the test account
 *   E2E_AUTH_FILE      Path to store auth state JSON (default: tests/e2e/.auth/portal.json)
 */
async function globalSetup(config: FullConfig): Promise<void> {
  const portalUrl   = process.env['PORTAL_URL']      ?? 'http://localhost:5173';
  const username    = process.env['PORTAL_USERNAME']  ?? '';
  const password    = process.env['PORTAL_PASSWORD']  ?? '';
  const authFile    = process.env['E2E_AUTH_FILE']    ?? 'tests/cloud-imaging-portal/e2e/.auth/portal.json';

  if (!username || !password) {
    console.warn(
      '[E2E Setup] PORTAL_USERNAME / PORTAL_PASSWORD not set, skipping pre-auth.\n' +
      '            Tests that require authentication will use mock/stub flows.'
    );
    return;
  }

  const browser = await chromium.launch();
  const context = await browser.newContext();
  const page    = await context.newPage();

  try {
    console.log(`[E2E Setup] Navigating to ${portalUrl}…`);
    await page.goto(portalUrl, { waitUntil: 'networkidle' });

    // MSAL redirect login: handle Entra ID login page
    if (page.url().includes('login.microsoftonline.com')) {
      console.log('[E2E Setup] Completing Entra ID sign-in…');
      await page.fill('input[type="email"]', username);
      await page.click('input[type="submit"]');
      await page.fill('input[type="password"]', password);
      await page.click('input[type="submit"]');
      // Handle "Stay signed in?" prompt
      await page.click('input[type="submit"]').catch(() => { /* may not appear */ });
      await page.waitForURL(portalUrl + '**', { timeout: 30_000 });
    }

    // Save auth state
    const { mkdirSync } = await import('fs');
    mkdirSync(authFile.replace(/\/[^/]+$/, ''), { recursive: true });
    await context.storageState({ path: authFile });
    console.log(`[E2E Setup] Auth state saved to ${authFile}`);
  } finally {
    await browser.close();
  }
}

export default globalSetup;
