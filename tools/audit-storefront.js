/* Storefront consistency check.
 *
 *   node tools/audit-storefront.js
 *
 * The storefront is plain HTML, CSS and vanilla JS with no build step, so
 * nothing catches a reference to an i18n key that no longer exists, or a page
 * that reads the catalogue without loading the client that fetches it. The
 * compiler guards the C#; this guards the other half.
 *
 * Exits non-zero on a real problem, so CI fails rather than shipping a page
 * that renders a raw key name to a customer.
 */
const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..', 'src', 'Nexus.Web', 'wwwroot');
const norm = f => path.relative(ROOT, f).split(path.sep).join('/');

const files = [];
(function walk(d) {
  for (const e of fs.readdirSync(d, { withFileTypes: true })) {
    const p = path.join(d, e.name);
    if (e.isDirectory()) walk(p);
    else if (/\.(html|js)$/.test(e.name)) files.push(p);
  }
})(ROOT);

const read = f => fs.readFileSync(f, 'utf8');
const problems = [];

// --- 1. Every referenced i18n key exists, in BOTH languages ----------------
// Arabic is a first-class edition: a key present in en and missing in ar is a
// bug, not a gap. Keys built at runtime (t('a_' + status)) are skipped - the
// regex cannot resolve them, and guessing produces false alarms.
const I18N = new Function(read(path.join(ROOT, 'assets/js/i18n.js')) + ';return I18N;')();
const used = new Map();

for (const f of files) {
  if (f.endsWith('i18n.js')) continue;
  const s = read(f);
  for (const re of [/\bt\(\s*'([^']+)'\s*\)/g, /\btf\(\s*'([^']+)'/g,
                    /data-i18n="([^"]+)"/g, /data-i18n-ph="([^"]+)"/g]) {
    let m;
    while ((m = re.exec(s))) {
      if (!used.has(m[1])) used.set(m[1], new Set());
      used.get(m[1]).add(norm(f));
    }
  }
}

for (const [key, where] of used) {
  if (!(key in I18N.en)) problems.push(`i18n key '${key}' is referenced but not defined in en  (${[...where].join(', ')})`);
  else if (!(key in I18N.ar)) problems.push(`i18n key '${key}' exists in en but NOT in ar  (${[...where].join(', ')})`);
}

const onlyAr = Object.keys(I18N.ar).filter(k => !(k in I18N.en));
for (const k of onlyAr) problems.push(`i18n key '${k}' exists in ar but not in en`);

// --- 2. Kuwait only: no second currency anywhere ---------------------------
// CAT-25. A stored preference from an earlier visit must not resurrect AED.
for (const f of files) {
  const s = read(f);
  if (/data-cur=|RATE\[|['"]AED['"]/.test(s)) {
    problems.push(`${norm(f)} still refers to a second currency - v1 is KWD only (CAT-25)`);
  }
}

// --- 3. Any page reading the catalogue must load the client that fetches it -
for (const f of files.filter(x => x.endsWith('.html'))) {
  const s = read(f);
  if (!/\b(book\(|cat\(|BOOKS|CATS|Catalogue\.)/.test(s)) continue;
  if (!s.includes('api.js')) problems.push(`${norm(f)} uses the catalogue but does not load api.js`);
  if (!s.includes('site.js')) problems.push(`${norm(f)} uses the catalogue but does not load site.js`);
}

// --- 4. No book data may live in wwwroot ------------------------------------
// CAT-01: the catalogue comes from the server. admin-demo.js is allowed - it
// holds sample orders and customers, which are not book data.
if (fs.existsSync(path.join(ROOT, 'assets/js/data.js'))) {
  problems.push('assets/js/data.js is back - book data must not live in wwwroot (CAT-01)');
}

// --- report -----------------------------------------------------------------
const defined = Object.keys(I18N.en).length;
console.log(`storefront audit: ${files.length} files, ${defined} i18n keys (en), ${used.size} referenced`);

if (problems.length) {
  console.error(`\n${problems.length} problem(s):`);
  for (const p of problems) console.error('  - ' + p);
  process.exit(1);
}
console.log('no problems found');
