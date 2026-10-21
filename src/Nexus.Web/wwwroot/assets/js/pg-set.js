/* Settings, and the audit trail underneath them.
 *
 * The demo had fields for the Tap keys, a VAT rate and a currency list. None of
 * them belongs here: v1 is Kuwait only at a zero VAT rate, and a gateway secret
 * that can be rewritten from a browser form has moved the store's most valuable
 * credential onto its least trusted surface. Those live in server configuration.
 *
 * A row the server marks secret arrives with no value at all — not starred, not
 * shortened, absent — so the panel cannot leak one even by rendering carelessly. */
let ST = { rows: [], audit: [], errors: [] };

const ST_LABEL = () => ({
  'delivery.linkExpiryDays': t('a_s_days'),
  'delivery.maxDownloads': t('a_s_max'),
  'delivery.watermarkEnabled': t('a_s_wm'),
  'delivery.distinctIpThreshold': t('a_s_ipthr'),
  'delivery.reissueLimitPerItem': t('a_s_reissue'),
  'delivery.maxUploadBytes': t('a_s_maxupload'),
  'email.fromAddress': t('a_s_from'),
  'email.fromName': t('a_s_fromname'),
  'email.confirmation.subject.en': t('a_s_subj_en'),
  'email.confirmation.subject.ar': t('a_s_subj_ar'),
  'email.confirmation.body.en': t('a_s_body_en'),
  'email.confirmation.body.ar': t('a_s_body_ar'),
  'email.resend.limitPerDay': t('a_s_resend_limit')
});

const ST_GROUP = () => ({ delivery: t('a_s_del'), email: t('a_s_mail') });

async function load() {
  const [rows, audit] = await Promise.all([Admin.get('settings'), Admin.get('audit?take=25')]);
  if (!rows.ok) { AdminState.failed = true; return; }
  ST.rows = rows.data;
  ST.audit = audit.ok ? audit.data : [];
}

function stField(row) {
  const label = ST_LABEL()[row.key] || row.key;
  const id = 'st-' + row.key.replace(/\./g, '-');

  if (row.isSecret) {
    return `<div class="f"><label for="${id}">${esc(label)}</label>
      <input id="${id}" type="password" data-key="${esc(row.key)}" placeholder="${t('a_s_secret')}" value="">
      </div>`;
  }

  if (!row.editable) {
    return `<div class="f"><label>${esc(label)}</label>
      <input value="${esc(row.value == null ? '' : row.value)}" disabled>
      <span class="note">${t('a_s_readonly')}</span></div>`;
  }

  if (row.kind === 'Boolean') {
    return `<div class="check"><input type="checkbox" id="${id}" data-key="${esc(row.key)}"
        ${String(row.value).toLowerCase() === 'true' ? 'checked' : ''}>
      <label for="${id}" style="margin:0">${esc(label)}</label></div>`;
  }

  if (row.kind === 'MultilineText') {
    return `<div class="f"><label for="${id}">${esc(label)}</label>
      <textarea id="${id}" data-key="${esc(row.key)}">${esc(row.value == null ? '' : row.value)}</textarea></div>`;
  }

  return `<div class="f"><label for="${id}">${esc(label)}</label>
    <input id="${id}" data-key="${esc(row.key)}" value="${esc(row.value == null ? '' : row.value)}">
    ${row.isSet ? '' : `<span class="note">${t('a_s_unset')}</span>`}</div>`;
}

function render() {
  shell('a_set');
  if (!AdminState.ready) { body(adWaiting()); return; }

  const groups = ST_GROUP();
  const boxes = Object.keys(groups).map(g => {
    const rows = ST.rows.filter(r => r.group === g);
    if (!rows.length) return '';
    return `<div class="box"><h2>${groups[g]}</h2>${rows.map(stField).join('')}</div>`;
  }).join('');

  const other = ST.rows.filter(r => !Object.keys(groups).includes(r.group));

  const audit = ST.audit.length ? `
    <table><thead><tr><th>${t('a_s_when')}</th><th>${t('a_s_actor')}</th>
      <th>${t('a_s_action')}</th><th>${t('a_s_what')}</th></tr></thead>
    <tbody>${ST.audit.map(a => `<tr>
      <td class="muted" style="white-space:nowrap">${adWhen(a.at)}</td>
      <td class="muted">${esc(a.actorEmail || '—')}</td>
      <td>${esc(a.action)}</td>
      <td class="muted trunc">${esc(a.entity)} ${esc(a.entityId)}</td></tr>`).join('')}
    </tbody></table>` : adEmpty();

  body(`
  <div class="set">${boxes}
    ${other.length ? `<div class="box"><h2>${t('a_any')}</h2>${other.map(stField).join('')}</div>` : ''}
  </div>
  ${ST.errors.length ? `<ul class="errs">${ST.errors.map(e => `<li>${esc(adFieldError(e))}</li>`).join('')}</ul>` : ''}
  <div style="margin-top:22px"><button class="btn" id="st-save">${t('a_save')}</button></div>
  <div class="box" style="margin-top:28px"><h2>${t('a_s_audit')}</h2>${audit}</div>`);
}

document.addEventListener('click', async e => {
  if (e.target.id !== 'st-save') return;

  ST.errors = [];
  let saved = 0;

  for (const row of ST.rows.filter(r => r.editable)) {
    const el = document.querySelector(`[data-key="${CSS.escape(row.key)}"]`);
    if (!el) continue;

    const value = row.kind === 'Boolean' ? String(el.checked) : el.value;

    // A blank secret box means "leave it alone", not "set it to nothing".
    if (row.isSecret && value === '') continue;
    if (!row.isSecret && value === (row.value == null ? '' : row.value)) continue;

    const r = await Admin.put('settings/' + encodeURIComponent(row.key), { value });
    if (r.ok) saved++;
    else if (r.data && r.data.fields) ST.errors.push(...r.data.fields);
    else ST.errors.push({ field: row.key, code: 'other' });
  }

  toast(ST.errors.length ? t('a_err') : (saved ? t('a_s_saved') : t('a_saved_ok')));
  await refresh();
});
