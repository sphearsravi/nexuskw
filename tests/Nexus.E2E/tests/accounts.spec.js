const { test, expect } = require('@playwright/test');
const { catalogueReady, uniqueEmail } = require('../helpers');

test.describe('accounts', () => {
  test('the account page offers sign in, register and forgot', async ({ page }) => {
    await page.goto('/account.html');
    await catalogueReady(page);

    await expect(page.locator('#main')).toContainText('Sign in');
    await expect(page.locator('#main')).toContainText('Create an account');
  });

  /* Enumeration. Registering an address that already exists must be
     indistinguishable from registering a free one, or the form becomes a way to
     ask the store who shops there. */
  test('a taken address looks exactly like a free one', async ({ page }) => {
    const email = uniqueEmail('enum');

    const register = () => page.evaluate(async (e) => {
      const r = await fetch('/api/account/register', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ name: 'Enum Test', email: e, password: 'a-long-enough-password', language: 'en' })
      });
      return { status: r.status, body: await r.text() };
    }, email);

    await page.goto('/account.html');
    const first = await register();
    const second = await register();

    expect(first.status).toBe(202);
    expect(second.status).toBe(first.status);
    expect(second.body).toBe(first.body);
  });

  test('a password reset for an unknown address is acknowledged, not refused', async ({ page }) => {
    await page.goto('/account.html');

    const result = await page.evaluate(async () => {
      const r = await fetch('/api/account/forgot', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ email: 'nobody-at-all@example.invalid' })
      });
      return r.status;
    });

    // Telling an attacker the address is unknown is the whole vulnerability.
    expect(result).toBe(202);
  });

  test('an unconfirmed account cannot sign in', async ({ page }) => {
    const email = uniqueEmail('unconfirmed');
    await page.goto('/account.html');

    const outcome = await page.evaluate(async (e) => {
      await fetch('/api/account/register', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ name: 'Not Confirmed', email: e, password: 'a-long-enough-password', language: 'en' })
      });
      const r = await fetch('/api/account/signin', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ email: e, password: 'a-long-enough-password' })
      });
      return { status: r.status, body: await r.json().catch(() => null) };
    }, email);

    expect(outcome.status).toBe(401);
    expect(outcome.body.code).toBe('email_not_confirmed');
  });

  test('a signed-out visitor is refused their own pages, and told to sign in', async ({ page }) => {
    // Navigate first: on about:blank a relative URL has nothing to resolve against.
    await page.goto('/');

    for (const path of ['/api/me/profile', '/api/me/library', '/api/me/orders']) {
      const status = await page.evaluate(async (p) => (await fetch(p)).status, path);
      expect(status, `${path} should refuse an anonymous caller`).toBe(401);
    }

    await page.goto('/library.html');
    await catalogueReady(page);
    await expect(page.locator('#main')).toContainText(/[Ss]ign in/);
  });
});
