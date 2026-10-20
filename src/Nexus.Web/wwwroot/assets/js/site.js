/* ---- state (falls back to memory if storage is blocked) ---- */
const _mem = {};
function S(k, v) {
  try { if (v === undefined) return localStorage.getItem(k); localStorage.setItem(k, v); }
  catch (e) { if (v === undefined) return k in _mem ? _mem[k] : null; _mem[k] = v; }
}
const state = {
  get lang() { return S('lang') || 'en'; }, set lang(v) { S('lang', v); },
  /* Kuwait only: KWD is the single currency. A 'cur' left in storage by an
     earlier visit must not resurrect AED anywhere (CAT-25). */
  get cur()  { return 'KWD'; },
  get cart() { try { return JSON.parse(S('cart') || '[]'); } catch (e) { return []; } },
  set cart(v) { S('cart', JSON.stringify(v)); },
  get lib()  { try { return JSON.parse(S('lib')  || '[]'); } catch (e) { return []; } },
  set lib(v) { S('lib', JSON.stringify(v)); }
};
const t = k => (I18N[state.lang] && I18N[state.lang][k]) || I18N.en[k] || k;
const L = o => o[state.lang] || o.en;
const isAR = () => state.lang === 'ar';
const book = id => BOOKS.find(b => b.id === +id);
const cat  = id => CATS.find(c => c.id === id);
const esc  = s => String(s).replace(/[&<>"]/g, c => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;' }[c]));

/* ---- money ----
 * KWD is a three-decimal currency: 6.5 renders as 6.500, never 6.50.
 * Digits stay Western in both languages (CAT-24), so toFixed and not a
 * locale-aware formatter, which would give Arabic-Indic numerals under ar.
 */
function price(kwd) {
  const n = Number(kwd).toFixed(3);
  return isAR() ? `${n} د.ك` : `KD ${n}`;
}
const cartTotal = () => state.cart.reduce((s, id) => s + (book(id) ? book(id).price : 0), 0);

/* ---- book cover, drawn not loaded ---- */
/* each subject gets its own motif, so a shelf of covers reads by category */
const MOTIF = {
  investing: `<g fill="none" stroke="currentColor" stroke-width="1.6">
      <path d="M4 92a58 58 0 0 1 58-58"/><path d="M4 92a44 44 0 0 1 44-44"/>
      <path d="M4 92a30 30 0 0 1 30-30"/><path d="M4 92a16 16 0 0 1 16-16"/></g>
    <circle cx="62" cy="34" r="4.5" fill="var(--acc)"/>`,
  trading: `<g stroke="currentColor" stroke-width="1.5">
      <path d="M9 34v42M23 20v54M37 44v34M51 28v46M65 52v26M79 12v62"/></g>
    <g fill="currentColor"><rect x="5" y="46" width="8" height="20"/><rect x="19" y="30" width="8" height="30"/>
      <rect x="33" y="54" width="8" height="16"/><rect x="47" y="38" width="8" height="26"/>
      <rect x="61" y="60" width="8" height="12"/></g>
    <rect x="75" y="22" width="8" height="34" fill="var(--acc)"/>`,
  personal: `<g fill="currentColor">
      <rect x="4" y="24" width="66" height="9" rx="4.5"/><rect x="4" y="40" width="50" height="9" rx="4.5"/>
      <rect x="4" y="56" width="34" height="9" rx="4.5"/></g>
    <rect x="4" y="72" width="20" height="9" rx="4.5" fill="var(--acc)"/>`,
  islamic: `<g fill="none" stroke="currentColor" stroke-width="1.6" transform="translate(44 50)">
      <rect x="-26" y="-26" width="52" height="52"/><rect x="-26" y="-26" width="52" height="52" transform="rotate(45)"/>
      <rect x="-15" y="-15" width="30" height="30" transform="rotate(22.5)"/></g>
    <circle cx="44" cy="50" r="5" fill="var(--acc)"/>`,
  accounting: `<g stroke="currentColor" stroke-width="1.3">
      <path d="M4 22h84M4 36h84M4 50h84M4 64h84M4 78h84"/></g>
    <path d="M66 14v72" stroke="var(--acc)" stroke-width="1.6"/>
    <g fill="currentColor"><rect x="10" y="26" width="30" height="6"/><rect x="10" y="40" width="20" height="6"/>
      <rect x="10" y="54" width="38" height="6"/><rect x="10" y="68" width="14" height="6"/></g>`,
  business: `<g fill="currentColor">
      <rect x="4" y="66" width="18" height="20"/><rect x="27" y="52" width="18" height="34"/>
      <rect x="50" y="36" width="18" height="50"/></g>
    <rect x="73" y="18" width="18" height="68" fill="var(--acc)"/>`
};
function cover(b, cls) {
  const c = cat(b.cat), o = L(b);
  const a = 138 + (b.id * 29) % 58, m = 74 + (b.id * 13) % 18;
  return `<span class="cover ${cls || ''}" style="--ink:${c.hue};--a:${a}deg;--m:${m}%" aria-hidden="true">
    <span class="cover-spine"></span>
    <span class="cover-body">
      <span class="cover-title">${esc(o.t)}</span>
      <span class="cover-rule"></span>
      <svg class="cover-art" viewBox="0 0 92 92" preserveAspectRatio="xMidYMid meet">${MOTIF[b.cat]}</svg>
      <span class="cover-foot">
        <span class="cover-author">${esc(o.a)}</span>
        <span class="cover-imprint">${t('brand')}</span>
      </span>
    </span>
    <span class="cover-edge"></span></span>`;
}
function bookCard(b) {
  const o = L(b), c = cat(b.cat), inCart = state.cart.includes(b.id);
  return `<article class="card">
    <a class="card-cover" href="book.html?id=${b.id}">${cover(b)}</a>
    <div class="card-meta" style="--c:${c.hue}">
      <p class="card-cat">${L(c)}</p>
      <a class="card-title" href="book.html?id=${b.id}">${esc(o.t)}</a>
      <p class="card-author">${esc(o.a)}</p>
      <div class="card-foot">
        <span class="price">${price(b.price)}</span>
        <button class="btn btn-sm ${inCart ? 'is-in' : ''}" data-add="${b.id}">${inCart ? t('added') : t('add')}</button>
      </div>
    </div></article>`;
}

/* ---- chrome ---- */
function chrome() {
  const n = state.cart.length, here = location.pathname.split('/').pop() || 'index.html';
  const on = f => here === f ? ' class="on"' : '';
  const hdr = document.getElementById('site-header');
  /* Counts come from the server so they stay right when the operator
     publishes or unpublishes a title (CAT-07). */
  const subs = CATS.map(x => `<a class="menu-row" href="shop.html?cat=${x.id}">
      <span class="menu-tick" style="background:${x.hue}"></span>
      <span class="menu-name">${L(x)}</span>
      <span class="menu-n">${x.count}</span></a>`).join('');
  if (hdr) hdr.innerHTML = `
    <div class="topbar"><div class="wrap top-in">
      <span class="top-note">${t('top_note')}</span>
      <div class="tools">
        <button class="lang" data-lang>${isAR() ? 'English' : 'العربية'}</button>
      </div>
    </div></div>
    <div class="wrap bar">
      <a class="brand" href="index.html">
        <span class="brand-mark" aria-hidden="true"></span>
        <span><span class="brand-name">${t('brand')}</span><span class="brand-sub">${t('brandsub')}</span></span>
      </a>
      <nav class="nav">
        <a href="index.html"${on('index.html')}>${t('nav_home')}</a>
        <span class="nav-item has-menu">
          <a href="shop.html"${on('shop.html')}>${t('nav_shop')}<i class="caret"></i></a>
          <span class="menu">
            <span class="menu-head">${t('nav_subjects')}</span>
            <span class="menu-grid">${subs}</span>
            <a class="menu-all" href="shop.html">${t('menu_all')}</a>
          </span>
        </span>
        <a href="library.html"${on('library.html')}>${t('nav_library')}</a>
        <a href="admin/index.html" class="nav-admin">${t('nav_admin')}</a>
      </nav>
      <div class="bar-end">
        <input class="hsearch" id="hq" data-i18n-ph="search_ph" value="">
        <a class="cart-btn" href="cart.html">${t('nav_cart')}<span class="pill${n ? '' : ' zero'}">${n}</span></a>
      </div>
    </div>`;
  const ftr = document.getElementById('site-footer');
  if (ftr) ftr.innerHTML = `
    <div class="wrap foot">
      <div><span class="brand-name">${t('brand')}</span><p class="muted">${t('footer_about')}</p></div>
      <div><h4>${t('footer_shop')}</h4>
        <a href="shop.html">${t('nav_shop')}</a><a href="library.html">${t('nav_library')}</a><a href="cart.html">${t('nav_cart')}</a></div>
      <div><h4>${t('footer_help')}</h4>
        <a href="#">${t('f_faq')}</a><a href="#">${t('f_returns')}</a><a href="#">${t('f_contact')}</a></div>
      <div><h4>${t('footer_legal')}</h4>
        <a href="#">${t('f_terms')}</a><a href="#">${t('f_privacy')}</a></div>
    </div>`;
}
function applyI18n(root) {
  (root || document).querySelectorAll('[data-i18n]').forEach(e => e.textContent = t(e.dataset.i18n));
  (root || document).querySelectorAll('[data-i18n-ph]').forEach(e => e.placeholder = t(e.dataset.i18nPh));
}
/* Fills {n}-style tokens so interface copy stops hardcoding how many books
   exist (CAT-28): "Twenty titles across six subjects" became wrong the moment
   the operator added the twenty-first. */
function tf(key, vars) {
  return String(t(key)).replace(/\{(\w+)\}/g, (m, k) => (k in vars ? vars[k] : m));
}

/* CAT-17: if the catalogue cannot be read, say so in the visitor's language.
   The header, footer and language switch keep working; the page is not blank
   and nothing is thrown into the console. */
function catalogueDown() {
  const m = document.getElementById('main') || document.getElementById('grid');
  if (m) m.innerHTML = `<div class="empty" style="grid-column:1/-1">
      <h3>${t('err_cat_t')}</h3><p>${t('err_cat_d')}</p>
      <button class="btn btn-ghost" onclick="location.reload()">${t('err_retry')}</button></div>`;
}

function paint() {
  document.documentElement.lang = state.lang;
  document.documentElement.dir = isAR() ? 'rtl' : 'ltr';
  chrome(); applyI18n();
  if (Catalogue.failed) { catalogueDown(); return; }
  if (typeof render === 'function') render();
}
document.addEventListener('click', e => {
  const lang = e.target.closest('[data-lang]');
  if (lang) { state.lang = isAR() ? 'en' : 'ar'; paint(); return; }
  const add = e.target.closest('[data-add]');
  if (add) {
    const id = +add.dataset.add, c = state.cart;
    if (!c.includes(id)) { c.push(id); state.cart = c; }
    paint(); return;
  }
  const rm = e.target.closest('[data-remove]');
  if (rm) { state.cart = state.cart.filter(x => x !== +rm.dataset.remove); paint(); }
});
document.addEventListener('keydown', e => {
  if (e.key === 'Enter' && e.target.id === 'hq' && e.target.value.trim())
    location.href = 'shop.html?q=' + encodeURIComponent(e.target.value.trim());
});
/* The catalogue is fetched before the first paint, so every page renders once
   with real data rather than flashing an empty shelf. */
document.addEventListener('DOMContentLoaded', async () => {
  await Catalogue.load();
  paint();
});
