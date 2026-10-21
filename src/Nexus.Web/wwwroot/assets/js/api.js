/* The catalogue, from the server.
 *
 * This file replaces data.js. The shapes are identical to the arrays that used
 * to live there — CATS entries are {id,en,ar,hue}, BOOKS entries are
 * {id,cat,price,pages,year,sales,en:{t,a,d},ar:{t,a,d}} — so site.js's book(),
 * cat() and the cover drawing keep working untouched. That is CAT-01: the data
 * moves, the markup does not.
 *
 * No paging: the catalogue grows by about fifty titles a year, so the whole
 * published set is one small response and filtering stays instant, with no
 * round-trip per keystroke. See CAT-15 and CAT-37 for when to revisit.
 */
let CATS = [];
let BOOKS = [];

const Catalogue = {
  loaded: false,
  failed: false,

  async load() {
    try {
      const [cats, books] = await Promise.all([
        fetch('/api/categories', { headers: { accept: 'application/json' } }),
        fetch('/api/books', { headers: { accept: 'application/json' } })
      ]);
      if (!cats.ok || !books.ok) throw new Error('catalogue unavailable');

      CATS = await cats.json();
      BOOKS = await books.json();
      this.loaded = true;
      this.failed = false;
    } catch (e) {
      // CAT-17: the page must say so in the visitor's language rather than
      // going blank or throwing into the console.
      CATS = [];
      BOOKS = [];
      this.loaded = false;
      this.failed = true;
    }
    return this;
  },

  /* The shop asks the server to filter and sort, so Arabic folding and
     both-language search (CAT-09, CAT-10) live in one tested place. */
  async search({ cat, q, sort, minPrice, maxPrice } = {}) {
    const p = new URLSearchParams();
    if (cat) p.set('cat', cat);
    if (q) p.set('q', q);
    if (sort) p.set('sort', sort);
    if (minPrice != null && minPrice !== '') p.set('minPrice', minPrice);
    if (maxPrice != null && maxPrice !== '') p.set('maxPrice', maxPrice);
    const r = await fetch('/api/books?' + p, { headers: { accept: 'application/json' } });
    if (!r.ok) throw new Error('search failed');
    return r.json();
  },

  async book(id) {
    const r = await fetch('/api/books/' + encodeURIComponent(id), { headers: { accept: 'application/json' } });
    if (r.status === 404) return null;   // CAT-20: unknown means unknown
    if (!r.ok) throw new Error('book unavailable');
    return r.json();
  },

  async related(id) {
    const r = await fetch(`/api/books/${encodeURIComponent(id)}/related`, { headers: { accept: 'application/json' } });
    return r.ok ? r.json() : [];
  },

  /* Published count per category, for the subject menu (CAT-07). */
  count(slug) {
    const c = CATS.find(x => x.id === slug);
    return c ? c.count : 0;
  },

  get total() {
    return CATS.reduce((n, c) => n + c.count, 0);
  }
};

/* Delivery limits the storefront quotes back to the buyer.
 *
 * These are operator settings, not constants: "valid for 7 days, 3 downloads" was
 * hardcoded into the interface copy in both languages, so changing the setting would
 * have left the page confidently stating the old numbers.
 *
 * Phase 4 serves them from /api/settings/public, which is a hardcoded allow-list of
 * exactly these two keys — never a projection over the settings table, because that
 * table will also hold the payment keys. Until that endpoint exists the documented
 * defaults are used, and a failed fetch falls back to them rather than showing nothing.
 */
const Delivery = {
  _limits: { d: 7, n: 3 },

  limits() { return this._limits; },

  async load() {
    try {
      const r = await fetch('/api/settings/public', { headers: { accept: 'application/json' } });
      if (!r.ok) return this._limits;
      const s = await r.json();
      const d = Number(s['delivery.linkExpiryDays']);
      const n = Number(s['delivery.maxDownloads']);
      if (Number.isFinite(d) && d > 0) this._limits.d = d;
      if (Number.isFinite(n) && n > 0) this._limits.n = n;
    } catch (e) { /* defaults stand */ }
    return this._limits;
  }
};
