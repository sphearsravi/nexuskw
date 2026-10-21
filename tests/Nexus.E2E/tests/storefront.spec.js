const { test, expect } = require('@playwright/test');
const { catalogueReady, switchToArabic, ARABIC_INDIC } = require('../helpers');

test.describe('storefront', () => {
  test('the home page lists books from the database', async ({ page }) => {
    await page.goto('/');
    await catalogueReady(page);

    // The counts in the subject menu come from the server, so a non-zero total
    // proves the page is not rendering a hardcoded shelf.
    const total = await page.evaluate(() => Catalogue.total);
    expect(total).toBeGreaterThan(0);
    await expect(page.locator('.card').first()).toBeVisible();
  });

  test('the shop shows every published title', async ({ page }) => {
    await page.goto('/shop.html');
    await catalogueReady(page);
    await expect(page.locator('#grid .card')).toHaveCount(20);
  });

  test('search finds a book by its English title', async ({ page }) => {
    await page.goto('/shop.html');
    await catalogueReady(page);

    await page.locator('#q').fill('Patient');
    await expect(page.locator('#grid .card')).toHaveCount(1);
    await expect(page.locator('#grid')).toContainText('The Patient Portfolio');
  });

  /* CAT-09 and CAT-10 through the browser. The demo searched only the active
     interface language, so an Arabic reader could not find a book by its English
     title; and readers type taa marbuta as haa without thinking. */
  test('search finds the same book by its Arabic title, misspelled, in English mode', async ({ page }) => {
    await page.goto('/shop.html');
    await catalogueReady(page);

    await page.locator('#q').fill('المحفظه الصبوره');
    await expect(page.locator('#grid .card')).toHaveCount(1);
    await expect(page.locator('#grid')).toContainText('The Patient Portfolio');
  });

  test('filters survive a reload and can be shared', async ({ page }) => {
    await page.goto('/shop.html');
    await catalogueReady(page);

    await page.locator('#fcat').selectOption('islamic');
    await page.locator('#fsort').selectOption('price-asc');
    await expect(page).toHaveURL(/cat=islamic/);
    await expect(page).toHaveURL(/sort=price-asc/);

    const before = await page.locator('#grid .card').count();
    await page.reload();
    await catalogueReady(page);

    // CAT-13: the demo read the address on load but never wrote it back, and sort
    // always reset.
    await expect(page.locator('#fcat')).toHaveValue('islamic');
    await expect(page.locator('#fsort')).toHaveValue('price-asc');
    await expect(page.locator('#grid .card')).toHaveCount(before);
  });

  test('an unknown book says so instead of showing a different one', async ({ page }) => {
    await page.goto('/book.html?id=9999');
    await catalogueReady(page);

    // The demo fell back to BOOKS[0], so this URL quietly displayed a real book.
    await expect(page.locator('#main')).not.toContainText('The Patient Portfolio');
    await expect(page.locator('.empty')).toBeVisible();
  });

  test('prices are KWD at three decimals, never two', async ({ page }) => {
    await page.goto('/shop.html');
    await catalogueReady(page);

    const prices = await page.locator('.card .price').allTextContents();
    expect(prices.length).toBeGreaterThan(0);
    for (const p of prices) expect(p).toMatch(/KD \d+\.\d{3}$/);
  });

  test('no second currency survives anywhere on the page', async ({ page }) => {
    await page.goto('/shop.html');
    await catalogueReady(page);

    await expect(page.locator('body')).not.toContainText('AED');
    await expect(page.locator('[data-cur]')).toHaveCount(0);
  });
});

test.describe('Arabic', () => {
  test('the interface flips to RTL and the content is Arabic', async ({ page }) => {
    await page.goto('/shop.html');
    await catalogueReady(page);
    await switchToArabic(page);

    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
    await expect(page.locator('html')).toHaveAttribute('lang', 'ar');
    await expect(page.locator('#grid')).toContainText('المحفظة الصبورة');
  });

  /* The product forbids Arabic-Indic numerals outright: it matches how Gulf banking
     interfaces present money, and it keeps what the payment gateway receives identical
     whichever language the buyer used. This has shipped broken twice. */
  test('digits stay Western in Arabic', async ({ page }) => {
    await page.goto('/shop.html');
    await catalogueReady(page);
    await switchToArabic(page);

    const prices = await page.locator('.card .price').allTextContents();
    expect(prices.length).toBeGreaterThan(0);
    for (const p of prices) {
      expect(p, `price "${p}" contains Arabic-Indic digits`).not.toMatch(ARABIC_INDIC);
      expect(p).toMatch(/\d+\.\d{3}/);
    }

    const body = await page.locator('body').innerText();
    expect(body, 'page body contains Arabic-Indic digits').not.toMatch(ARABIC_INDIC);
  });

  test('the language choice survives navigation', async ({ page }) => {
    await page.goto('/');
    await catalogueReady(page);
    await switchToArabic(page);

    await page.goto('/shop.html');
    await catalogueReady(page);
    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  });
});
