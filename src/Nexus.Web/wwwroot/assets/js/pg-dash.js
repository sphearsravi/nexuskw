/* Dashboard. Every figure is counted by the server; nothing here scales, pads or
   estimates. The demo multiplied its sample total by 6.4 to look busy, which made
   the one screen the owner checks daily the one screen that could not be trusted. */
let DASH = null;

async function load() {
  const r = await Admin.get('dashboard');
  DASH = r.ok ? r.data : null;
  if (!r.ok) AdminState.failed = true;
}

function render() {
  shell('a_dash');
  if (!AdminState.ready || !DASH) { body(adWaiting()); return; }

  const max = Math.max(1, ...DASH.trend.map(d => Number(d.revenueKwd) || 0));
  const status = AD_ORDER_STATUS();

  const bars = DASH.trend.map(d => {
    const v = Number(d.revenueKwd) || 0;
    // A day with no revenue still gets a visible foot, so thirty days read as
    // thirty days rather than as a chart that failed to load.
    const h = v > 0 ? Math.max(4, (v / max) * 100) : 1;
    return `<i style="height:${h}%" title="${d.day} · ${price(v)}"></i>`;
  }).join('');

  const top = DASH.topTitles.length
    ? `<ol class="rank">${DASH.topTitles.map(x => `<li>
        <span>${esc(isAR() ? x.titleAr : x.titleEn)}</span>
        <b>${x.copies} ${t('a_copies')}</b></li>`).join('')}</ol>`
    : adEmpty();

  const recent = DASH.recentOrders.length
    ? `<table><thead><tr><th>${t('a_ord_id')}</th><th>${t('a_cust')}</th><th>${t('a_items')}</th>
        <th>${t('a_total')}</th><th>${t('a_status')}</th><th>${t('a_date')}</th></tr></thead>
      <tbody>${DASH.recentOrders.map(o => `<tr>
        <td><a href="orders.html?id=${o.id}">${esc(o.orderNumber)}</a></td>
        <td>${esc(o.buyerName)}</td><td>${o.itemCount}</td>
        <td>${price(o.totalKwd)}</td>
        <td><span class="st ${AD_ORDER_PILL[o.status] || ''}">${status[o.status] || o.status}</span></td>
        <td class="muted">${adDate(o.createdAt)}</td></tr>`).join('')}
      </tbody></table>`
    : adEmpty();

  body(`
  <dl class="kpis">
    <div class="kpi"><dt>${t('a_rev')}</dt><dd>${price(DASH.revenueKwd30)}</dd>
      <span class="delta">${DASH.paidOrders30} ${t('a_st_paid')}</span></div>
    <div class="kpi"><dt>${t('a_ord')}</dt><dd>${DASH.orders30}</dd>
      <span class="delta">${DASH.pendingOrders} ${t('a_pending_n')}</span></div>
    <div class="kpi"><dt>${t('a_titles')}</dt><dd>${DASH.titlesLive}</dd>
      <span class="delta">${DASH.titlesTotal - DASH.titlesLive} ${t('a_draft')}</span></div>
    <div class="kpi"><dt>${t('a_dl30')}</dt><dd>${DASH.downloads30}</dd>
      <span class="delta">${DASH.customersTotal} ${t('a_custs_n')}</span></div>
  </dl>
  ${DASH.paidOrders30 === 0 ? `<p class="note">${t('a_no_sales')}</p>` : ''}
  <div class="cols">
    <div class="box"><h2>${t('a_chart')}</h2><div class="bars">${bars}</div></div>
    <div class="box"><h2>${t('a_top')}</h2>${top}</div>
  </div>
  <div class="box"><h2>${t('a_recent')}</h2>${recent}</div>`);
}
