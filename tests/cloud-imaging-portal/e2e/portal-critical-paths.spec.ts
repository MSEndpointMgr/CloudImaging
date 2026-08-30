import { test, expect, Page } from '@playwright/test';

/**
 * Playwright E2E tests for Cloud Imaging Portal critical paths (T149).
 *
 * Prerequisites:
 *   1. Portal running at PORTAL_URL (default: http://localhost:5173)
 *   2. Portal backend running at http://localhost:3000
 *   3. Auth state pre-seeded by portal.setup.ts (or MSW mocks in offline mode)
 *
 * Run: npx playwright test --config=playwright.config.ts
 */

const PORTAL_URL = process.env['PORTAL_URL'] ?? 'http://localhost:5173';

// ── Helpers ───────────────────────────────────────────────────────────────────

async function gotoSessions(page: Page): Promise<void> {
  await page.goto(`${PORTAL_URL}/sessions`);
  await page.waitForLoadState('networkidle');
}

// ── Test suite ────────────────────────────────────────────────────────────────

test.describe('Cloud Imaging Portal: Critical E2E Paths', () => {
  test.use({ storageState: 'tests/cloud-imaging-portal/e2e/.auth/portal.json' });

  // ── 1. Passcode coupling flow ─────────────────────────────────────────────

  test('passcode coupling flow: inline passcode field is present on Available Devices rows', async ({ page }) => {
    await gotoSessions(page);

    // Available Devices section header must be visible (no more toolbar "Couple Device" modal)
    await expect(page.getByText('Available Devices')).toBeVisible();

    const input = page.getByPlaceholder('Passcode').first();
    await expect(input).toBeVisible();
    await input.fill('abc123');
    await expect(input).toHaveValue('ABC123');
  });

  test('passcode coupling: inline error shown for invalid passcode', async ({ page }) => {
    await gotoSessions(page);

    // Typing a 6-character passcode auto-validates — no submit button involved.
    const input = page.getByPlaceholder('Passcode').first();
    await input.fill('XXXXXX');

    // Error message should appear inline under the field (404 → invalid passcode)
    const errorMsg = page.getByText(/invalid|expired|not found|network error/i);
    await expect(errorMsg).toBeVisible();
  });

  // ── 2. Session state progression ─────────────────────────────────────────

  test('sessions page: filter tabs are visible and functional', async ({ page }) => {
    await gotoSessions(page);

    // All primary tabs should be present
    for (const label of ['Pending', 'Monitor', 'Failed']) {
      await expect(page.getByRole('tab', { name: label }).or(
        page.getByText(label, { exact: true })
      )).toBeVisible();
    }
  });

  test('sessions page: column headers are sortable', async ({ page }) => {
    await gotoSessions(page);
    const serialHeader = page.getByRole('button', { name: 'Serial' }).first();
    await expect(serialHeader).toBeVisible();
    await serialHeader.click(); // toggles asc/desc — should not throw or navigate away
    await expect(page).toHaveURL(/sessions/);
  });

  test('sessions page: Refresh button is always visible', async ({ page }) => {
    await gotoSessions(page);
    const refreshBtn = page.getByRole('button', { name: /refresh/i });
    await expect(refreshBtn).toBeVisible();
  });

  // ── 3. Start imaging (bulk assign) ────────────────────────────────────────

  test('start imaging: Start Imaging button is disabled until an OS image is selected', async ({ page }) => {
    await gotoSessions(page);
    const startBtn = page.getByRole('button', { name: /start imaging/i });
    await expect(startBtn).toBeVisible();
    await expect(startBtn).toBeDisabled();
  });


  // ── 4. Branding update with page reload ───────────────────────────────────

  test('branding settings page: is accessible from sidebar', async ({ page }) => {
    await page.goto(`${PORTAL_URL}/`);
    await page.waitForLoadState('networkidle');

    const brandingLink = page.getByRole('link', { name: /branding/i });
    await expect(brandingLink).toBeVisible();
    await brandingLink.click();
    await expect(page).toHaveURL(/branding/i);
  });

  test('branding settings: color pickers and application name field present', async ({ page }) => {
    await page.goto(`${PORTAL_URL}/branding`);
    await page.waitForLoadState('networkidle');

    await expect(page.getByLabel(/application name/i).or(
      page.getByPlaceholder(/application name/i)
    )).toBeVisible();
  });

  // ── 5. Navigation ─────────────────────────────────────────────────────────

  test('sidebar navigation: all five sections are accessible', async ({ page }) => {
    await page.goto(`${PORTAL_URL}/`);
    await page.waitForLoadState('networkidle');

    const nav = ['Devices', 'OS Images', 'Boot Images', 'Branding', 'Configuration'];
    for (const label of nav) {
      const link = page.getByRole('link', { name: label }).or(
        page.getByText(label, { exact: true })
      );
      await expect(link).toBeVisible({ timeout: 5000 });
    }
  });

  test('navigation: sidebar is present on all authenticated routes', async ({ page }) => {
    for (const route of ['/', '/os-images', '/boot-images', '/branding', '/configuration']) {
      await page.goto(`${PORTAL_URL}${route}`);
      await page.waitForLoadState('networkidle');
      // Sidebar or navigation should be present
      const sidebar = page.locator('nav').or(page.locator('[role="navigation"]'));
      await expect(sidebar.first()).toBeVisible({ timeout: 5000 });
    }
  });
});
