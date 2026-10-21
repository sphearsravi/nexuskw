const { defineConfig, devices } = require('@playwright/test');

/* The app under test. Already running? Point BASE_URL at it and the suite reuses it.
   Otherwise Playwright starts one, which is what CI does. */
const BASE_URL = process.env.NEXUS_E2E_URL || 'http://localhost:5099';

module.exports = defineConfig({
  testDir: './tests',
  // Each spec owns its own data; running them together is the point.
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  workers: process.env.CI ? 2 : undefined,
  reporter: process.env.CI ? [['github'], ['html', { open: 'never' }]] : [['list']],
  timeout: 45_000,
  expect: { timeout: 10_000 },

  use: {
    baseURL: BASE_URL,
    // Evidence only for failures: a passing suite that writes a video per test
    // fills a disk and nobody watches them.
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
    actionTimeout: 10_000,
  },

  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
    // Most KNET buyers are on a phone, so the storefront is checked at phone
    // width too rather than assumed to work.
    {
      name: 'mobile',
      use: { ...devices['Pixel 7'] },
      testIgnore: /admin\.spec\.js/,   // the control panel is a desktop tool
    },
  ],

  /* Started on 5099, not 5091: a developer very likely has the app open on 5091
     already, and a suite that silently tests somebody's hand-edited instance is
     worse than one that cannot start. Skipped entirely when NEXUS_E2E_URL is set. */
  webServer: process.env.NEXUS_E2E_URL ? undefined : {
    command: 'dotnet run --project ../../src/Nexus.Web --no-launch-profile -c Release',
    url: `${BASE_URL}/health`,
    reuseExistingServer: false,
    timeout: 180_000,
    stdout: 'pipe',
    stderr: 'pipe',
    env: {
      ASPNETCORE_URLS: BASE_URL,
      ASPNETCORE_ENVIRONMENT: 'Development',
    },
  },
});
