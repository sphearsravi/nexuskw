/* Downloads: the live log, and the two actions that settle a delivery complaint.
 *
 * No token is shown anywhere on this screen, because the server never sends one.
 * A panel that displayed a working download link would turn one borrowed staff
 * session into free copies of the whole shop. Re-issue mails a fresh link to the
 * buyer instead. */
let DL = { page: null, q: '', state: '', flagged: false, pageNo: 1, log: null };

async function load() {
  const p = new URLSearchParams();
  if (DL.q) p.set('q', DL.q);
  if (DL.state) p.set('state', DL.state);
  if (DL.flagged) p.set('flagged', 'true');
  p.set('page', DL.pageNo);

  const r = await Admin.get('downloads?' + p);
  if (!r.ok) { AdminState.failed = true; return; }
  DL.page = r.data;
}

function dlLog() {
  const rows = DL.log.rows;
  return `<div class="modal"><div class="box" style="width:min(680px,100%)">
    <h2>${t('a_d_log')}</h2>
    ${rows.length ? `<table><thead><tr><th>${t('a_d_when')}</th><th>${t('a_d_ip')}</th>
        <th>${t('a_status')}</th></tr></thead>
      <tbody>${rows.map(l => `<tr>
        <td class="muted">${adWhen(l.atUtc)}</td>
        <td class="muted">${esc(l.ipPrefix || '—')}</td>
        <td>${esc(l.outcome)}</td></tr>`).join('')}</tbody></table>` : adEmpty()}
    <div class="modal-foot"><button class="btn btn-ghost btn-sm" id="dl-close">${t('a_close')}</button></div>
  </div></div>`;
}

function render() {
  shell('a_dls');
  if (!AdminState.ready || !DL.page) { body(adWaiting()); return; }

  const state = AD_GRANT_STATUS();
  const filters = { '': t('a_any'), Active: state.Active, Expired: state.Expired,
                    Exhausted: state.Exhausted, Revoked: state.Revoked };

  const table = DL.page.rows.length ? `
    <table><thead><tr><th>${t('a_ord_id')}</th><th>${t('a_d_book')}</th><th>${t('a_email')}</th>
      <th>${t('a_d_when')}</th><th>${t('a_d_ip')}</th><th>${t('a_d_ips')}</th>
      <th>${t('a_d_count')}</th><th>${t('a_d_state')}</th><th></th></tr></thead>
    <tbody>${DL.page.rows.map(g => `<tr>
      <td>${esc(g.orderNumber)}</td>
      <td>${esc(isAR() ? g.titleAr : g.titleEn)}${g.sequence > 1 ? ` <span class="muted">· ${t('a_d_seq')} ${g.sequence}</span>` : ''}</td>
      <td class="muted">${esc(g.buyerEmail)}</td>
      <td class="muted">${adWhen(g.lastUsedAt)}</td>
      <td class="muted">${esc(g.lastIpPrefix || '—')}</td>
      <td>${g.distinctIpCount}${g.flaggedAt ? ` <span class="st st-failed">${t('a_d_flagged')}</span>` : ''}</td>
      <td>${g.used} / ${g.max}</td>
      <td><span class="st ${AD_GRANT_PILL[g.state] || ''}">${state[g.state] || g.state}</span></td>
      <td style="text-align:end;white-space:nowrap">
        <button class="ico" data-log="${g.grantId}">${t('a_d_log')}</button>
        ${g.state !== 'Revoked' ? `<button class="ico" data-revoke="${g.grantId}">${t('a_d_revoke')}</button>` : ''}
        <button class="ico" data-reissue="${g.grantId}">${t('a_d_reissue')}</button></td>
    </tr>`).join('')}</tbody></table>` : adEmpty();

  body(`
  <div class="tbar">
    <input class="field" id="dl-q" value="${esc(DL.q)}" placeholder="${t('a_search')}">
    <select class="field" id="dl-sf">${Object.entries(filters).map(([v, label]) =>
      `<option value="${v}" ${DL.state === v ? 'selected' : ''}>${label}</option>`).join('')}</select>
    <label class="check"><input type="checkbox" id="dl-fl" ${DL.flagged ? 'checked' : ''}>
      <span>${t('a_d_flagged')}</span></label>
  </div>
  ${table}
  ${DL.log ? dlLog() : ''}`);

  const q = document.getElementById('dl-q');
  q.oninput = () => { DL.q = q.value; DL.pageNo = 1; clearTimeout(DL.timer); DL.timer = setTimeout(refresh, 250); };
  document.getElementById('dl-sf').onchange = e => { DL.state = e.target.value; DL.pageNo = 1; refresh(); };
  document.getElementById('dl-fl').onchange = e => { DL.flagged = e.target.checked; DL.pageNo = 1; refresh(); };
}

document.addEventListener('click', async e => {
  const log = e.target.closest('[data-log]');
  if (log) {
    const r = await Admin.get(`downloads/${log.dataset.log}/log`);
    DL.log = { rows: r.ok ? r.data : [] };
    render();
    return;
  }

  if (e.target.id === 'dl-close') { DL.log = null; render(); return; }

  const revoke = e.target.closest('[data-revoke]');
  if (revoke) {
    if (!confirm(t('a_d_revoke_q'))) return;
    const r = await Admin.post(`downloads/${revoke.dataset.revoke}/revoke`, { reason: 'OperatorRequest' });
    toast(r.ok ? t('a_d_revoked_ok') : t('a_err'));
    await refresh();
    return;
  }

  const reissue = e.target.closest('[data-reissue]');
  if (reissue) {
    const r = await Admin.post(`downloads/${reissue.dataset.reissue}/reissue`);
    toast(r.ok ? t('a_d_reissued_ok') : t('a_err'));
    await refresh();
  }
});
