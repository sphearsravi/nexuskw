/* Orders: the list, one order in full, and the two actions on it.
 *
 * Re-sending the download email is the fix for the commonest support call and is
 * one click. Refunding is Admin-only and currently refuses, because the gateway
 * is held — the button is here and says so rather than pretending. */
let OR = { page: null, q: '', status: '', open: null, pageNo: 1 };

async function load() {
  const p = new URLSearchParams();
  if (OR.q) p.set('q', OR.q);
  if (OR.status) p.set('status', OR.status);
  p.set('page', OR.pageNo);

  const r = await Admin.get('orders?' + p);
  if (!r.ok) { AdminState.failed = true; return; }
  OR.page = r.data;

  const wanted = OR.open ? OR.open.order.id : new URLSearchParams(location.search).get('id');
  if (wanted) {
    const d = await Admin.get('orders/' + encodeURIComponent(wanted));
    OR.open = d.ok ? d.data : null;
  }
}

function orDetail() {
  const d = OR.open;
  const status = AD_ORDER_STATUS();
  const grantStatus = AD_GRANT_STATUS();
  const mailStatus = AD_MAIL_STATUS();
  const paid = d.order.status === 'Paid' || d.order.status === 'PartiallyRefunded';

  return `<div class="modal"><div class="box" style="width:min(760px,100%)">
    <h2>${t('a_order_detail')} ${esc(d.order.orderNumber)}</h2>
    <p class="note">${esc(d.order.buyerName)} · ${esc(d.order.buyerEmail)} ·
      <span class="st ${AD_ORDER_PILL[d.order.status] || ''}">${status[d.order.status] || d.order.status}</span></p>

    <table><thead><tr><th>${t('a_title_c')}</th><th>${t('a_author')}</th><th>${t('a_price')}</th></tr></thead>
      <tbody>${d.items.map(i => `<tr>
        <td>${esc(isAR() ? i.titleAr : i.titleEn)}</td>
        <td>${esc(isAR() ? i.authorAr : i.authorEn)}</td>
        <td>${price(i.unitPriceKwd)}</td></tr>`).join('')}
        <tr><td colspan="2"><b>${t('a_total')}</b></td><td><b>${price(d.order.totalKwd)}</b></td></tr>
      </tbody></table>

    <p class="note">${t('a_payref')}: ${esc(d.tapChargeId || '—')} · ${esc(d.tapGatewayReference || '—')}</p>

    <h3>${t('a_grants')}</h3>
    ${d.grants.length ? `<table><thead><tr><th>${t('a_d_book')}</th><th>${t('a_d_seq')}</th>
        <th>${t('a_d_count')}</th><th>${t('a_d_state')}</th></tr></thead>
      <tbody>${d.grants.map(g => `<tr>
        <td>${esc(isAR() ? g.titleAr : g.titleEn)}</td><td>${g.sequence}</td>
        <td>${g.used} / ${g.max}</td>
        <td><span class="st ${AD_GRANT_PILL[g.state] || ''}">${grantStatus[g.state] || g.state}</span></td>
      </tr>`).join('')}</tbody></table>` : adEmpty()}

    <h3>${t('a_emails')}</h3>
    ${d.emails.length ? `<table><thead><tr><th>${t('a_status')}</th><th>${t('a_email')}</th><th>${t('a_date')}</th></tr></thead>
      <tbody>${d.emails.map(m => `<tr>
        <td>${mailStatus[m.status] || m.status}</td><td class="muted">${esc(m.to)}</td>
        <td class="muted">${adWhen(m.createdAt)}</td></tr>`).join('')}</tbody></table>` : adEmpty()}

    <h3>${t('a_history')}</h3>
    ${d.events.length ? `<ul class="rank">${d.events.map(ev => `<li>
        <span>${esc(ev.to)}${ev.detail ? ' — ' + esc(ev.detail) : ''}</span>
        <b>${adWhen(ev.at)}</b></li>`).join('')}</ul>` : adEmpty()}

    <div class="modal-foot">
      <button class="btn btn-ghost btn-sm" id="or-close">${t('a_close')}</button>
      ${paid ? `<button class="btn btn-ghost btn-sm" data-resend="${d.order.id}">${t('a_resend')}</button>` : ''}
      ${paid && Admin.me && Admin.me.isAdmin
        ? `<button class="btn btn-sm" data-refund="${d.order.id}">${t('a_refund')}</button>` : ''}
    </div>
  </div></div>`;
}

function render() {
  shell('a_orders');
  if (!AdminState.ready || !OR.page) { body(adWaiting()); return; }

  const status = AD_ORDER_STATUS();
  const filters = { '': t('a_any'), Pending: status.Pending, Paid: status.Paid, Failed: status.Failed,
                    Refunded: status.Refunded, Cancelled: status.Cancelled };

  const table = OR.page.rows.length ? `
    <table><thead><tr><th>${t('a_ord_id')}</th><th>${t('a_cust')}</th><th>${t('a_items')}</th>
      <th>${t('a_total')}</th><th>${t('a_status')}</th><th>${t('a_date')}</th><th></th></tr></thead>
    <tbody>${OR.page.rows.map(o => `<tr>
      <td>${esc(o.orderNumber)}</td>
      <td>${esc(o.buyerName)}<br><span class="muted" style="font-size:12.5px">${esc(o.buyerEmail)}</span></td>
      <td>${o.itemCount}</td>
      <td>${price(o.totalKwd)}</td>
      <td><span class="st ${AD_ORDER_PILL[o.status] || ''}">${status[o.status] || o.status}</span></td>
      <td class="muted" style="white-space:nowrap">${adDate(o.createdAt)}</td>
      <td style="text-align:end"><button class="ico" data-open="${o.id}">${t('a_order_detail')}</button></td>
    </tr>`).join('')}</tbody></table>` : adEmpty();

  body(`
  <div class="tbar">
    <input class="field" id="or-q" value="${esc(OR.q)}" placeholder="${t('a_search')}">
    <select class="field" id="or-sf">${Object.entries(filters).map(([v, label]) =>
      `<option value="${v}" ${OR.status === v ? 'selected' : ''}>${label}</option>`).join('')}</select>
  </div>
  ${table}
  ${OR.open ? orDetail() : ''}`);

  const q = document.getElementById('or-q');
  q.oninput = () => { OR.q = q.value; OR.pageNo = 1; clearTimeout(OR.timer); OR.timer = setTimeout(refresh, 250); };
  document.getElementById('or-sf').onchange = e => { OR.status = e.target.value; OR.pageNo = 1; refresh(); };
}

document.addEventListener('click', async e => {
  const open = e.target.closest('[data-open]');
  if (open) {
    const r = await Admin.get('orders/' + open.dataset.open);
    OR.open = r.ok ? r.data : null;
    render();
    return;
  }

  if (e.target.id === 'or-close') { OR.open = null; render(); return; }

  const resend = e.target.closest('[data-resend]');
  if (resend) {
    const r = await Admin.post(`orders/${resend.dataset.resend}/resend`, {});
    toast(r.ok ? t('a_resent') : t('a_err'));
    await refresh();
    return;
  }

  const refund = e.target.closest('[data-refund]');
  if (refund) {
    const typed = prompt(t('a_refund_amount'));
    if (typed === null) return;
    const r = await Admin.post(`orders/${refund.dataset.refund}/refund`,
      { amountKwd: Number(typed) || 0, reason: null });
    // 503 payments_held is the expected answer today, and it is said plainly.
    toast(r.data && r.data.error === 'payments_held' ? t('a_refund_held') : (r.ok ? t('a_saved_ok') : t('a_err')));
  }
});
