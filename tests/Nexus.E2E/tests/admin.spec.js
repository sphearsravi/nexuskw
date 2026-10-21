const { test, expect } = require('@playwright/test');

/* Dev-only credentials, from appsettings.Development.json. Production seeds no
   default password at all, which is why these are safe to have here. */
const ADMIN_EMAIL = process.env.NEXUS_ADMIN_EMAIL || 'admin@nexus.local';
const ADMIN_PASSWORD = process.env.NEXUS_ADMIN_PASSWORD || 'change-this-locally-please';

async function signIn(page) {
  await page.goto('/admin/login.html');
  await page.locator('#em').fill(ADMIN_EMAIL);
  await page.locator('#pw').fill(ADMIN_PASSWORD);
  await page.locator('#form button[type="submit"]').click();
  // The page navigates with location.replace('index.html') once the session opens,
  // and the shell paints after a second round trip for the operator and categories.
  await page.waitForURL(/\/admin\/index\.html$/, { timeout: 20_000 });
  await page.locator('#ad-side').waitFor({ timeout: 20_000 });
}

test.describe('control panel', () => {
  test('an unauthenticated visitor cannot reach it', async ({ page }) => {
    await page.goto('/admin/index.html');
    // Either redirected to the login page, or shown it. Never the dashboard.
    await expect(page).toHaveURL(/login/, { timeout: 15_000 });
  });

  test('the admin API refuses an anonymous caller with a status code, not a page', async ({ page }) => {
    await page.goto('/');
    for (const path of ['/admin/api/dashboard', '/admin/api/books', '/admin/api/settings']) {
      const result = await page.evaluate(async (p) => {
        const r = await fetch(p, { redirect: 'manual' });
        return { status: r.status, type: r.type };
      }, path);
      // A 302 to an HTML login page would be parsed as JSON by a caller and
      // reported as a confusing error instead of "you are not signed in".
      expect([401, 403], `${path} answered ${result.status}`).toContain(result.status);
    }
  });

  test('an operator can sign in and see real figures', async ({ page }) => {
    await signIn(page);
    await expect(page.locator('#ad-body')).toBeVisible();

    const dashboard = await page.evaluate(async () => {
      const r = await fetch('/admin/api/dashboard', { headers: { accept: 'application/json' } });
      return { status: r.status, body: await r.json() };
    });
    expect(dashboard.status).toBe(200);
    // Real numbers from the database, not an invented multiplier.
    expect(dashboard.body).toHaveProperty('orders30');
    expect(typeof dashboard.body.orders30).toBe('number');
  });

  test('the catalogue screen lists the real books', async ({ page }) => {
    await signIn(page);
    await page.goto('/admin/books.html');
    await expect(page.locator('#ad-body')).toContainText('The Patient Portfolio', { timeout: 20_000 });
  });

  test('every screen loads without an unhandled error', async ({ page }) => {
    const errors = [];
    page.on('pageerror', e => errors.push(e.message));

    await signIn(page);
    for (const screen of ['index', 'books', 'categories', 'orders', 'customers', 'downloads', 'settings']) {
      await page.goto(`/admin/${screen}.html`);
      await expect(page.locator('#ad-body')).toBeVisible({ timeout: 20_000 });
    }

    expect(errors, `console errors: ${errors.join(' | ')}`).toHaveLength(0);
  });

  test('signing out closes the session', async ({ page }) => {
    await signIn(page);
    await page.locator('#ad-signout').click();
    await page.waitForURL(/login/, { timeout: 15_000 });

    const after = await page.evaluate(async () =>
      (await fetch('/admin/api/dashboard', { headers: { accept: 'application/json' } })).status);
    expect(after).toBe(401);
  });

  /* The boundary that matters most: the cheap credential must not be a route to
     the expensive one. */
  test('a customer session opens nothing in the panel', async ({ page }) => {
    await page.goto('/');

    const email = `e2e-boundary-${Math.random().toString(36).slice(2, 10)}@example.invalid`;
    await page.evaluate(async (e) => {
      await fetch('/api/account/register', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ name: 'Boundary Test', email: e, password: 'a-long-enough-password', language: 'en' })
      });
    }, email);

    // Even unconfirmed and unsigned-in, no admin surface may answer.
    for (const path of ['/admin/api/dashboard', '/admin/api/settings']) {
      const status = await page.evaluate(async (p) =>
        (await fetch(p, { headers: { accept: 'application/json' } })).status, path);
      expect(status).toBe(401);
    }
  });
});
