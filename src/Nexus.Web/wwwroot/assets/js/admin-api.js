/* The control panel's client for /admin/api.
 *
 * This file replaces admin-demo.js. Nothing in the panel reads a hardcoded array
 * any more: every row on every screen is a request, and an empty screen means an
 * empty database rather than sample data somebody forgot to remove.
 *
 * Two things are worth knowing before changing anything here.
 *
 * 1. The admin cookie is path-scoped to /admin, so the API has to live under
 *    /admin/api — a call to /api/admin/... would be sent WITHOUT the cookie and
 *    answered with 401 for ever, which looks exactly like a broken password.
 *
 * 2. Every mutating call carries the antiforgery token in the
 *    RequestVerificationToken header. The token comes from GET /admin/api/session,
 *    which is the one anonymous endpoint, so the login form can obtain one before
 *    it has a session.
 */
const Admin = {
  csrf: null,
  me: null,

  async session() {
    const r = await fetch('/admin/api/session', { headers: { accept: 'application/json' } });
    if (!r.ok) return { authenticated: false };
    const s = await r.json();
    this.csrf = s.csrf || this.csrf;
    this.me = s.authenticated ? s : null;
    return s;
  },

  /* Resolves to { ok, status, data } and never throws on an HTTP error: every
     screen has something useful to say about a refusal, and an exception would
     take the whole page down instead. */
  async req(method, path, body) {
    const headers = { accept: 'application/json' };
    if (method !== 'GET') headers['RequestVerificationToken'] = this.csrf || '';
    if (body !== undefined) headers['content-type'] = 'application/json';

    let r;
    try {
      r = await fetch('/admin/api/' + path, {
        method,
        headers,
        body: body === undefined ? undefined : JSON.stringify(body)
      });
    } catch (e) {
      return { ok: false, status: 0, data: { error: 'network' } };
    }

    if (r.status === 401) { Admin.toLogin(); return { ok: false, status: 401, data: {} }; }

    let data = null;
    if (r.status !== 204) { try { data = await r.json(); } catch (e) { data = null; } }
    return { ok: r.ok, status: r.status, data };
  },

  get(path) { return this.req('GET', path); },
  post(path, body) { return this.req('POST', path, body === undefined ? {} : body); },
  put(path, body) { return this.req('PUT', path, body); },
  del(path) { return this.req('DELETE', path); },

  /* Multipart: the browser sets the boundary, so content-type must not be set here. */
  async upload(path, field, file, extra) {
    const form = new FormData();
    form.append(field, file, file.name);
    if (extra) for (const [k, v] of Object.entries(extra)) form.append(k, v);

    let r;
    try {
      r = await fetch('/admin/api/' + path, {
        method: 'POST',
        headers: { accept: 'application/json', 'RequestVerificationToken': this.csrf || '' },
        body: form
      });
    } catch (e) {
      return { ok: false, status: 0, data: { error: 'network' } };
    }

    if (r.status === 401) { Admin.toLogin(); return { ok: false, status: 401, data: {} }; }

    let data = null;
    try { data = await r.json(); } catch (e) { data = null; }
    return { ok: r.ok, status: r.status, data };
  },

  toLogin() {
    if (!location.pathname.endsWith('/login.html')) location.replace('login.html');
  }
};

/* ---- formatting ----
 * Western digits in both languages (PRD section 5), so these build the string by
 * hand rather than calling toLocaleString, which gives Arabic-Indic numerals
 * under ar and would also put a date separator the gateway logs do not use.
 */
const pad2 = n => String(n).padStart(2, '0');

function adDate(iso) {
  if (!iso) return '—';
  const d = new Date(iso);
  if (isNaN(d)) return '—';
  return `${d.getFullYear()}-${pad2(d.getMonth() + 1)}-${pad2(d.getDate())}`;
}

function adWhen(iso) {
  if (!iso) return '—';
  const d = new Date(iso);
  if (isNaN(d)) return '—';
  return `${adDate(iso)} ${pad2(d.getHours())}:${pad2(d.getMinutes())}`;
}

const adNum = n => String(Number(n) || 0);

/* ---- wire codes to sentences, in the reader's language ----
 * The API returns stable codes and no prose: a wire protocol that carries a
 * sentence has two places to change that sentence, and only one of them is ever
 * translated.
 */
function adFieldError(e) {
  const map = {
    required: t('a_e_required'), too_long: t('a_e_too_long'),
    must_be_positive: t('a_e_must_be_positive'), too_large: t('a_e_too_large'),
    too_many_decimals: t('a_e_too_many_decimals'), out_of_range: t('a_e_out_of_range'),
    unknown_category: t('a_e_unknown_category'), unknown_value: t('a_e_unknown_value'),
    unknown_motif: t('a_e_unknown_motif'), not_a_number: t('a_e_not_a_number'),
    not_a_boolean: t('a_e_not_a_boolean'), duplicate_in_file: t('a_e_duplicate_in_file'),
    already_in_catalogue: t('a_e_already_in_catalogue'), missing_column: t('a_e_missing_column'),
    duplicate_column: t('a_e_duplicate_column'), too_few_columns: t('a_e_too_few_columns'),
    empty: t('a_e_empty'), bad_shape: t('a_e_bad_shape'),
    already_exists: t('a_e_already_exists'), not_editable: t('a_e_not_editable')
  };
  return `${e.field} ${map[e.code] || t('a_e_other')}${e.detail ? ' (' + e.detail + ')' : ''}`;
}

/* The publish gate's own message, translated.
 *
 * Book.PublishBlocker() returns one English sentence, and it is the single
 * statement of what makes a title sellable — restating the rules here would make
 * two. So the sentence is mapped to a key, and an unrecognised one falls through
 * in English rather than being swallowed. */
function adBlocker(message) {
  const map = {
    'Arabic title is missing.': t('a_pb_titlear'),
    'Arabic author is missing.': t('a_pb_authorar'),
    'Arabic description is missing.': t('a_pb_descar'),
    'English title is missing.': t('a_pb_titleen'),
    'English author is missing.': t('a_pb_authoren'),
    'English description is missing.': t('a_pb_descen'),
    'Price must be greater than zero.': t('a_pb_price'),
    'Category is not active.': t('a_pb_cat'),
    'PDF file is missing.': t('a_pb_pdf'),
    'PDF is a seed placeholder, not the real file.': t('a_pb_stub')
  };
  return map[message] || message;
}

const AD_ORDER_STATUS = () => ({
  Pending: t('a_st_pending'), Paid: t('a_st_paid'), Failed: t('a_st_failed'),
  Refunded: t('a_st_refunded'), Cancelled: t('a_st_cancelled'), PartiallyRefunded: t('a_st_partial')
});

const AD_ORDER_PILL = {
  Pending: 'st-draft', Paid: 'st-paid', Failed: 'st-failed',
  Refunded: 'st-refunded', Cancelled: 'st-refunded', PartiallyRefunded: 'st-refunded'
};

const AD_GRANT_STATUS = () => ({
  Active: t('a_d_active'), Expired: t('a_d_exp'), Exhausted: t('a_d_used'), Revoked: t('a_d_revoked')
});

const AD_GRANT_PILL = {
  Active: 'st-active', Expired: 'st-expired', Exhausted: 'st-exhausted', Revoked: 'st-failed'
};

const AD_MAIL_STATUS = () => ({
  Queued: t('a_m_queued'), Sending: t('a_m_sending'), Sent: t('a_m_sent'),
  Failed: t('a_m_failed'), Abandoned: t('a_m_abandoned'), Suppressed: t('a_m_suppressed')
});
