/* Shared helpers. Kept small: a test that needs a page object to be understood is
   usually a test that will not be read. */

/** A fresh address per test, so parallel runs cannot collide on a unique email. */
function uniqueEmail(tag) {
  const noise = Math.random().toString(36).slice(2, 10);
  return `e2e-${tag}-${noise}@example.invalid`;
}

/** Waits for the catalogue to have painted. Every page fetches before its first render. */
async function catalogueReady(page) {
  // Catalogue is a top-level const, which creates a lexical binding rather than a
  // property on window — so window.Catalogue is undefined and only the bare name works.
  await page.waitForFunction(
    () => typeof Catalogue !== 'undefined' && Catalogue.loaded === true,
    null, { timeout: 15_000 });
}

/** Switches the whole interface to Arabic and waits for the direction to actually flip. */
async function switchToArabic(page) {
  await page.getByRole('button', { name: 'العربية' }).first().click();
  await page.waitForFunction(() => document.documentElement.dir === 'rtl');
}

/**
 * Every price on the page, as rendered. Used to assert the three-decimal rule and the
 * absence of Arabic-Indic digits, which is a product requirement and not a nicety.
 */
async function renderedPrices(page) {
  return page.locator('.price, .row-t span:last-child').allTextContents();
}

const ARABIC_INDIC = /[٠-٩۰-۹]/;

/**
 * Adds a book to the cart through the UI, from its detail page, and confirms the
 * button changed state rather than trusting the click.
 */
async function addToCart(page, bookId) {
  await page.goto(`/book.html?id=${bookId}`);
  await catalogueReady(page);
  const add = page.locator(`[data-add="${bookId}"]`);
  await add.click();
  await page.locator(`[data-add="${bookId}"].is-in`).waitFor({ timeout: 10_000 });
}

/** Fills the checkout contact form and submits it. */
async function placeOrder(page, { name, email }) {
  await page.locator('#nm').fill(name);
  await page.locator('#em').fill(email);
  await page.locator('#go').click();
}

module.exports = {
  uniqueEmail,
  catalogueReady,
  switchToArabic,
  renderedPrices,
  ARABIC_INDIC,
  addToCart,
  placeOrder,
};
