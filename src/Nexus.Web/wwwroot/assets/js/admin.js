/* The control panel shell: navigation, the signed-in operator, and the boot that
   every page shares.

   Pages define two functions. `load()` is async and fetches; `render()` is
   synchronous and draws, because site.js calls render() from paint() on the
   first paint and again on every language switch. Keeping render() free of
   fetches is what makes the language toggle instant and reentrant. */
const AD_NAV = [
  ['index.html', 'a_dash'], ['books.html', 'a_books'], ['categories.html', 'a_cats'],
  ['reports.html', 'a_reports'], ['orders.html', 'a_orders'], ['customers.html', 'a_custs'], ['downloads.html', 'a_dls'],
  ['settings.html', 'a_set']
];

/* Settings writes every future order's delivery rules, so Staff do not see it.
   Hiding it is a courtesy; the endpoint is AdminOnly and refuses regardless. */
const AD_ADMIN_ONLY = ['settings.html'];

const AdminState = { ready: false, failed: false };

function shell(title) {
  const here = location.pathname.split('/').pop() || 'index.html';
  const isAdmin = !!(Admin.me && Admin.me.isAdmin);
  const nav = AD_NAV.filter(([f]) => isAdmin || !AD_ADMIN_ONLY.includes(f));

  document.getElementById('ad-side').innerHTML = `
    <a class="brand" href="index.html">
      <span class="brand-mark"></span>
      <span><span class="brand-name">${t('brand')}</span><span class="brand-sub">${t('a_title')}</span></span></a>
    <nav>${nav.map(([f, k]) => `<a href="${f}" class="${here === f ? 'on' : ''}">${t(k)}</a>`).join('')}</nav>
    <div class="out">
      <a href="../index.html">${t('a_exit')}</a>
      <p class="who">${Admin.me ? esc(Admin.me.email || '') : ''}</p>
      <button class="ico" id="ad-signout">${t('a_signout')}</button>
    </div>`;

  document.getElementById('ad-top').innerHTML = `
    <h1>${t(title)}</h1>
    <div class="tools">
      <button class="lang" data-lang>${isAR() ? 'English' : 'العربية'}</button>
    </div>`;
}

function body(html) { document.getElementById('ad-body').innerHTML = html; }

/* Drawn in place of the table until the first load finishes, so a slow query
   reads as "working" rather than as an empty catalogue. */
function adWaiting() {
  if (AdminState.failed) {
    return `<div class="empty"><h3>${t('a_err')}</h3><p>${t('a_err_d')}</p>
      <button class="btn btn-ghost" onclick="location.reload()">${t('err_retry')}</button></div>`;
  }
  return `<div class="empty"><p>${t('a_loading')}</p></div>`;
}

function adEmpty(message) {
  return `<div class="empty"><p>${esc(message || t('a_none'))}</p></div>`;
}

function toast(msg) {
  document.querySelectorAll('.toast').forEach(e => e.remove());
  const d = document.createElement('div');
  d.className = 'toast';
  d.textContent = msg;
  document.body.appendChild(d);
  setTimeout(() => d.remove(), 3200);
}

/* Re-fetches and repaints. Every mutating action ends here rather than patching
   the row it changed: the server may have done more than was asked — publishing
   a subject off can take its titles with it — and a screen that shows only the
   change it expected is a screen that lies. */
async function refresh() {
  try { await load(); } catch (e) { AdminState.failed = true; }
  paint();
}

async function adminBoot() {
  const session = await Admin.session();
  if (!session.authenticated) { Admin.toLogin(); return false; }

  /* The admin list, not /api/categories: an operator has to see the hidden
     subjects, and the storefront endpoint deliberately does not return them. */
  const cats = await Admin.get('categories');
  if (cats.ok && Array.isArray(cats.data)) {
    CATS = cats.data.map(c => ({
      id: c.slug, en: c.nameEn, ar: c.nameAr, hue: c.hue,
      count: c.publishedCount, active: c.isActive, books: c.bookCount, order: c.sortOrder
    }));
  }
  return true;
}

document.addEventListener('click', async e => {
  if (e.target.id === 'ad-signout') {
    await Admin.del('session');
    location.replace('login.html');
  }
});

document.addEventListener('DOMContentLoaded', async () => {
  if (!document.getElementById('ad-side')) return;   // the login page has no shell
  if (!await adminBoot()) return;

  if (typeof load === 'function') {
    try { await load(); } catch (err) { AdminState.failed = true; }
  }

  AdminState.ready = true;
  paint();
});

/* The cover for an admin row.
 *
 * site.js draws the motif from the category slug. A book may override it, and the
 * operator has to be able to see the choice they just made, so the override is
 * substituted for the slug here — which also moves the accent colour, because the
 * motifs and the colours are one set keyed the same way. */
function adCover(b) {
  const motif = b.motif && cat(b.motif) ? b.motif : b.cat;
  return cat(motif) ? cover({ ...b, cat: motif }) : '';
}
