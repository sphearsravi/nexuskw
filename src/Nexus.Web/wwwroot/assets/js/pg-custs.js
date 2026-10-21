/* Customers: profile, order history, lifetime value, and erasure.
 *
 * Lifetime value is captured minus refunded over paid orders only, so it is what
 * the customer is actually worth rather than what they once put in a basket. */
let CU = { page: null, q: '', open: null, pageNo: 1 };

async function load() {
  const p = new URLSearchParams();
  if (CU.q) p.set('q', CU.q);
  p.set('page', CU.pageNo);

  const r = await Admin.get('customers?' + p);
  if (!r.ok) { AdminState.failed = true; return; }
  CU.page = r.data;

  if (CU.open) {
    const d = await Admin.get('customers/' + CU.open.customer.id);
    CU.open = d.ok ? d.data : null;
  }
}

function cuDetail() {
  const d = CU.open;
  const c = d.customer;
  const status = AD_ORDER_STATUS();

  return `<div class="modal"><div class="box" style="width:min(680px,100%)">
    <h2>${esc(c.name)}</h2>
    <p class="note">${esc(c.email)} · ${esc(c.country)} ·
      ${c.hasLogin ? t('a_c_account') : t('a_c_guest')} · ${t('a_c_joined')} ${adDate(c.createdAt)}</p>

    <dl class="kpis">
      <div class="kpi"><dt>${t('a_c_spent')}</dt><dd>${price(c.lifetimeValueKwd)}</dd></div>
      <div class="kpi"><dt>${t('a_c_orders')}</dt><dd>${c.orderCount}</dd>
        <span class="delta">${c.paidOrderCount} ${t('a_st_paid')}</span></div>
    </dl>

    ${d.orders.length ? `<table><thead><tr><th>${t('a_ord_id')}</th><th>${t('a_total')}</th>
        <th>${t('a_status')}</th><th>${t('a_date')}</th></tr></thead>
      <tbody>${d.orders.map(o => `<tr>
        <td>${esc(o.orderNumber)}</td><td>${price(o.totalKwd)}</td>
        <td><span class="st ${AD_ORDER_PILL[o.status] || ''}">${status[o.status] || o.status}</span></td>
        <td class="muted">${adDate(o.createdAt)}</td></tr>`).join('')}</tbody></table>` : adEmpty()}

    <div class="modal-foot">
      <button class="btn btn-ghost btn-sm" id="cu-close">${t('a_close')}</button>
      ${Admin.me && Admin.me.isAdmin
        ? `<button class="btn btn-sm danger" data-erase="${c.id}">${t('a_c_erase')}</button>` : ''}
    </div>
  </div></div>`;
}

function render() {
  shell('a_custs');
  if (!AdminState.ready || !CU.page) { body(adWaiting()); return; }

  const table = CU.page.rows.length ? `
    <table><thead><tr><th>${t('a_cust')}</th><th>${t('a_email')}</th><th>${t('a_c_country')}</th>
      <th>${t('a_c_orders')}</th><th>${t('a_c_spent')}</th><th>${t('a_c_joined')}</th><th></th></tr></thead>
    <tbody>${CU.page.rows.map(c => `<tr>
      <td>${esc(c.name)}</td>
      <td class="muted">${esc(c.email)}</td>
      <td>${esc(c.country)}</td>
      <td>${c.paidOrderCount} / ${c.orderCount}</td>
      <td>${price(c.lifetimeValueKwd)}</td>
      <td class="muted">${adDate(c.createdAt)}</td>
      <td style="text-align:end"><button class="ico" data-cust="${c.id}">${t('a_order_detail')}</button></td>
    </tr>`).join('')}</tbody></table>` : adEmpty();

  body(`<div class="tbar">
      <input class="field" id="cu-q" value="${esc(CU.q)}" placeholder="${t('a_search')}">
    </div>
    ${table}
    ${CU.open ? cuDetail() : ''}`);

  const q = document.getElementById('cu-q');
  q.oninput = () => { CU.q = q.value; CU.pageNo = 1; clearTimeout(CU.timer); CU.timer = setTimeout(refresh, 250); };
}

document.addEventListener('click', async e => {
  const open = e.target.closest('[data-cust]');
  if (open) {
    const r = await Admin.get('customers/' + open.dataset.cust);
    CU.open = r.ok ? r.data : null;
    render();
    return;
  }

  if (e.target.id === 'cu-close') { CU.open = null; render(); return; }

  const erase = e.target.closest('[data-erase]');
  if (erase) {
    if (!confirm(t('a_c_erase_q'))) return;
    const r = await Admin.del('customers/' + erase.dataset.erase);
    toast(r.ok ? t('a_c_erased') : t('a_err'));
    CU.open = null;
    await refresh();
  }
});
