/* Page metadata for search engines and for links shared in a message.
 *
 * The storefront renders client-side, so a crawler that does not run JavaScript sees
 * only the shell. Google does run it; most link-preview bots — WhatsApp, Slack,
 * Telegram, X — do not. The static tags in each page's head are therefore the ones
 * that matter for a shared link, and these only enrich what a JavaScript-capable
 * crawler sees.
 *
 * This is the honest limitation of a no-build storefront. Server-rendering the head
 * is the real fix and is a larger change; this covers the common cases until then.
 */
const Seo = {
  set({ title, description, image, type = 'website', canonical } = {}) {
    if (title) {
      document.title = title;
      Seo._meta('property', 'og:title', title);
      Seo._meta('name', 'twitter:title', title);
    }
    if (description) {
      Seo._meta('name', 'description', description);
      Seo._meta('property', 'og:description', description);
      Seo._meta('name', 'twitter:description', description);
    }
    Seo._meta('property', 'og:type', type);
    Seo._meta('property', 'og:url', canonical || location.href);
    Seo._meta('name', 'twitter:card', image ? 'summary_large_image' : 'summary');
    if (image) {
      Seo._meta('property', 'og:image', image);
      Seo._meta('name', 'twitter:image', image);
    }
    Seo._link('canonical', canonical || location.href.split('#')[0]);
  },

  /* Structured data for a book, so a search result can carry the price and whether it
     is in stock rather than just a title. Prices are KWD at three decimals, as
     everywhere else. */
  book(b, currency) {
    const o = (typeof L === 'function') ? L(b) : b.en;
    Seo._jsonLd({
      '@context': 'https://schema.org',
      '@type': 'Book',
      name: o.t,
      author: { '@type': 'Person', name: o.a },
      description: o.d,
      bookFormat: 'https://schema.org/EBook',
      numberOfPages: b.pages,
      inLanguage: b.languages === 'en' ? ['en'] : b.languages === 'ar' ? ['ar'] : ['ar', 'en'],
      datePublished: String(b.year),
      offers: {
        '@type': 'Offer',
        price: Number(b.price).toFixed(3),
        priceCurrency: currency || 'KWD',
        availability: 'https://schema.org/InStock',
        url: location.href
      }
    });
  },

  _meta(attr, key, value) {
    let el = document.head.querySelector(`meta[${attr}="${key}"]`);
    if (!el) {
      el = document.createElement('meta');
      el.setAttribute(attr, key);
      document.head.appendChild(el);
    }
    el.setAttribute('content', value);
  },

  _link(rel, href) {
    let el = document.head.querySelector(`link[rel="${rel}"]`);
    if (!el) {
      el = document.createElement('link');
      el.setAttribute('rel', rel);
      document.head.appendChild(el);
    }
    el.setAttribute('href', href);
  },

  _jsonLd(data) {
    let el = document.getElementById('ld-json');
    if (!el) {
      el = document.createElement('script');
      el.type = 'application/ld+json';
      el.id = 'ld-json';
      document.head.appendChild(el);
    }
    el.textContent = JSON.stringify(data);
  }
};
