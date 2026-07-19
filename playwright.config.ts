import { defineConfig, devices } from '@playwright/test';

/**
 * Playwright configuration for Cloud Imaging Portal E2E tests (T149).
 *
 * Run: npx playwright test
 * Report: npx playwright show-report
 */
export default defineConfig({
  testDir: './tests/cloud-imaging-portal/e2e',
  fullyParallel: false,
  retries: process.env['CI'] ? 2 : 0,
  reporter: process.env['CI']
    ? [['github'], ['html', { open: 'never' }]]
    : [['html', { open: 'on-failure' }]],

  use: {
    baseURL: process.env['PORTAL_URL'] ?? 'http://localhost:5173',
    trace:   'on-first-retry',
    video:   'on-first-retry',
  },

  globalSetup: './tests/cloud-imaging-portal/e2e/portal.setup.ts',

  projects: [
    // Setup project (authentication)
    {
      name: 'setup',
      testMatch: /portal\.setup\.ts/,
    },
    // Chromium — primary browser
    {
      name: 'chromium',
      use: {
        ...devices['Desktop Chrome'],
        storageState: 'tests/cloud-imaging-portal/e2e/.auth/portal.json',
      },
      dependencies: ['setup'],
    },
    // Firefox — secondary (CI only)
    ...(process.env['CI'] ? [{
      name: 'firefox',
      use: {
        ...devices['Desktop Firefox'],
        storageState: 'tests/cloud-imaging-portal/e2e/.auth/portal.json',
      },
      dependencies: ['setup'],
    }] : []),
  ],

  // Start the portal dev server if not already running
  webServer: process.env['PORTAL_URL']
    ? undefined
    : {
        command: 'npm run dev',
        cwd: 'src/cloud-imaging-portal/client',
        url: 'http://localhost:5173',
        reuseExistingServer: true,
        timeout: 30_000,
      },
});
