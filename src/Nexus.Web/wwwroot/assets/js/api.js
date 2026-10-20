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
