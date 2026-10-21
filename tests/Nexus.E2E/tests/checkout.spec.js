const { test, expect } = require('@playwright/test');
const { catalogueReady, addToCart, placeOrder, uniqueEmail, switchToArabic, ARABIC_INDIC } = require('../helpers');

test.describe('cart and checkout', () => {
  test('a book can be added and the cart shows it', async ({ page }) => {
    await addToCart(page, 1);

    await page.goto('/cart.html');
    await catalogueReady(page);
    await expect(page.locator('.line')).toHaveCount(1);
    await expect(page.locator('#main')).toContainText('The Patient Portfolio');
  });

  test('the same book cannot be added twice — it is a file, not a quantity', async ({ page }) => {
    await addToCart(page, 1);
    await page.goto('/book.html?id=1');
    await catalogueReady(page);
    await page.locator('[data-add="1"]').click();

    await page.goto('/cart.html');
    await catalogueReady(page);
    await expect(page.locator('.line')).toHaveCount(1);
  });

  test('checkout explains what the buyer gets and what happens next', async ({ page }) => {
    await addToCart(page, 1);
    await page.goto('/checkout.html');
    await catalogueReady(page);

    await expect(page.locator('#main')).toContainText('What you get');
    await expect(page.locator('#main')).toContainText('What happens next');
    // The download allowance is an operator setting, so it must be rendered, not frozen.
    await expect(page.locator('#main')).toContainText(/\d+ downloads over \d+ days/);
    await expect(page.locator('#main')).toContainText('No VAT in Kuwait');
  });

  test('there is no card form anywhere — card details belong on the gateway', async ({ page }) => {
    await addToCart(page, 1);
    await page.goto('/checkout.html');
    await catalogueReady(page);

    await expect(page.locator('input[autocomplete="cc-number"]')).toHaveCount(0);
    await expect(page.locator('#cn, #cv, #ex, #hn')).toHaveCount(0);
    await expect(page.locator('#main')).not.toContainText('CVV');
  });

  test('an empty cart cannot reach checkout', async ({ page }) => {
    await page.goto('/checkout.html');
    await page.waitForURL(/cart\.html/, { timeout: 15_000 });
  });

  test('placing an order reserves it and says payment is not switched on', async ({ page }) => {
    await addToCart(page, 8);
    await page.goto('/checkout.html');
    await catalogueReady(page);

    await placeOrder(page, { name: 'Fatima Al-Sayegh', email: uniqueEmail('order') });

    await page.waitForURL(/success\.html/, { timeout: 20_000 });

    // No query string: the reference travels in an HttpOnly cookie, so there is
    // nothing in the address bar to leak through Referer or to share by accident.
    expect(new URL(page.url()).search).toBe('');

    await expect(page.locator('#main')).toContainText(/KW-\d{6}-[0-9A-Z]{6}/);
    await expect(page.locator('#main')).toContainText('Payment is not switched on yet');
  });

  test('the confirmation page refuses to render without the cookie', async ({ page, context }) => {
    await addToCart(page, 2);
    await page.goto('/checkout.html');
    await catalogueReady(page);
    await placeOrder(page, { name: 'Cookie Test', email: uniqueEmail('cookie') });
    await page.waitForURL(/success\.html/, { timeout: 20_000 });

    await context.clearCookies();
    await page.goto('/success.html');

    // Sent home rather than shown somebody else's order.
    await page.waitForURL(/index\.html|\/$/, { timeout: 15_000 });
  });

  test('the whole purchase works in Arabic, with Western digits', async ({ page }) => {
    await addToCart(page, 12);
    await page.goto('/checkout.html');
    await catalogueReady(page);
    await switchToArabic(page);

    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
    await placeOrder(page, { name: 'دلال الصباح', email: uniqueEmail('ar') });
    await page.waitForURL(/success\.html/, { timeout: 20_000 });

    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
    const body = await page.locator('#main').innerText();
    expect(body, 'confirmation page contains Arabic-Indic digits').not.toMatch(ARABIC_INDIC);
    expect(body).toMatch(/KW-\d{6}-[0-9A-Z]{6}/);
  });

  /* The order total is recomputed from the catalogue, always. This drives the
     storefront through a tampered figure the way a stale cart would produce one. */
  test('a stale cart is refused with the new price, not silently repriced', async ({ page }) => {
    await addToCart(page, 4);
    await page.goto('/checkout.html');
    await catalogueReady(page);

    await page.locator('#nm').fill('Stale Cart');
    await page.locator('#em').fill(uniqueEmail('stale'));

    // Make the page claim a price it was never shown.
    await page.evaluate(() => {
      const original = window.fetch;
      window.fetch = (url, init) => {
        if (typeof url === 'string' && url.includes('/api/checkout') && init && init.body) {
          const body = JSON.parse(init.body);
          body.clientTotalFils = 1;
          init.body = JSON.stringify(body);
        }
        return original(url, init);
      };
    });

    await page.locator('#go').click();

    await expect(page.locator('#co-problem')).toContainText('The price has changed', { timeout: 15_000 });
    await expect(page.locator('#co-problem')).toContainText(/\d+\.\d{3}/);
    expect(page.url()).toContain('checkout.html');
  });
});
