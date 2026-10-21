/* Reports: what sold, what did not, and whether the Arabic bet is paying off.
 *
 * Every figure is counted from the database by the server. Nothing here estimates,
 * extrapolates or multiplies, because a dashboard that invents a number is worse
 * than one that admits it has none.
 *
 * Revenue counts CAPTURED money net of refunds. A Pending order contributes
 * nothing: planning against orders that have not been paid is planning against a
 * number that can still go to zero. While payments are held that means the money
 * figures are legitimately zero, and the page says so rather than looking broken. */
let REP = { days: 30, data: null };

async function load() {
  const r = await Admin.get('reports?days=' + REP.days);
  if (!r.ok) { AdminState.failed = true; return; }
  REP.data = r.data;
}

function kpi(label, value, note) {
  return `<div class="kpi"><dt>${label}</dt><dd>${value}</dd>
    ${note ? `<span class="delta">${note}</span>` : ''}</div>`;
}

/* A bar per day, drawn from the series itself. Deliberately not a charting
   library: one dependency for seven bars is a dependency to keep patched. */
function sparkline(points) {
  const peak = Math.max(...points.map(p => p.revenueKwd), 0);
  if (peak <= 0) {
    return `<div class="empty" style="padding:28px"><p>${t('a_r_nosales')}</p></div>`;
  }
  return `<div class="spark">${points.map(p => {
    const h = Math.max(2, Math.round((p.revenueKwd / peak) * 100));
    const title = `${p.date} · ${price(p.revenueKwd)} · ${p.orders}`;
    return `<span class="spark-bar" style="height:${h}%" title="${esc(title)}"></span>`;
  }).join('')}</div>
  <div class="spark-axis"><span>${esc(points[0].date)}</span><span>${esc(points[points.length - 1].date)}</span></div>`;
}

function titleOf(row) { return isAR() ? row.titleAr : row.titleEn; }

function render() {
  if (!AdminState.ready || !REP.data) { body(adWaiting()); return; }
  const d = REP.data;

  const ranges = [7, 30, 90, 365].map(n =>
    `<button data-days="${n}" class="${REP.days === n ? 'on' : ''}">${tf('a_r_days', { n })}</button>`).join('');

  body(`
    <div class="toolbar">
      <div class="seg" role="group">${ranges}</div>
      <span class="count">${esc(d.range.from)} → ${esc(d.range.to)}</span>
      <a class="btn btn-ghost btn-sm" href="/admin/api/reports/export.csv?days=${d.range.days}">${t('a_r_csv')}</a>
    </div>

    <dl class="kpis">
      ${kpi(t('a_r_revenue'), price(d.revenueKwd), t('a_r_net'))}
      ${kpi(t('a_r_paid'), d.paidOrders, tf('a_r_of_all', { n: d.allOrders }))}
      ${kpi(t('a_r_aov'), price(d.averageOrderKwd), '')}
      ${kpi(t('a_r_downloads'), d.delivery.downloads, tf('a_r_grants', { n: d.delivery.grantsIssued }))}
    </dl>

    <div class="box" style="margin-top:22px">
      <h2>${t('a_r_trend')}</h2>
      ${sparkline(d.daily)}
    </div>

    <div class="cols" style="margin-top:22px">
      <div class="box">
        <h2>${t('a_r_best')}</h2>
        ${d.bestSellers.length ? `<table><thead><tr>
            <th>${t('a_title_c')}</th><th>${t('a_r_copies')}</th><th>${t('a_r_revenue')}</th></tr></thead>
          <tbody>${d.bestSellers.map(b => `<tr>
            <td>${esc(titleOf(b))}</td><td>${b.copies}</td><td>${price(b.revenueKwd)}</td></tr>`).join('')}
          </tbody></table>` : adEmpty(t('a_r_nosales'))}
      </div>

      <div class="box">
        <h2>${t('a_r_never')}</h2>
        <p class="note">${t('a_r_never_d')}</p>
        ${d.neverSold.length ? `<table><thead><tr>
            <th>${t('a_title_c')}</th><th>${t('a_price')}</th><th>${t('a_state')}</th></tr></thead>
          <tbody>${d.neverSold.slice(0, 12).map(b => `<tr>
            <td>${esc(titleOf(b))}</td><td>${price(b.priceKwd)}</td>
            <td><span class="st ${b.isPublished ? 'st-paid' : 'st-failed'}">${
              b.isPublished ? t('a_published') : t('a_unpublished')}</span></td></tr>`).join('')}
          </tbody></table>` : adEmpty(t('a_r_allsold'))}
      </div>
    </div>

    <div class="cols" style="margin-top:22px">
      <div class="box">
        <h2>${t('a_r_lang')}</h2>
        <p class="note">${t('a_r_lang_d')}</p>
        <div class="row-t"><span>${t('a_r_arabic')}</span><span>${d.languages.arabicOrders}</span></div>
        <div class="row-t"><span>${t('a_r_english')}</span><span>${d.languages.englishOrders}</span></div>
        <div class="row-t grand"><span>${t('a_r_arshare')}</span><span>${d.languages.arabicPercent}%</span></div>
      </div>

      <div class="box">
        <h2>${t('a_r_refunds')}</h2>
        <div class="row-t"><span>${t('a_r_rcount')}</span><span>${d.refunds.count}</span></div>
        <div class="row-t"><span>${t('a_r_rtotal')}</span><span>${price(d.refunds.totalKwd)}</span></div>
        <div class="row-t"><span>${t('a_r_ravg')}</span><span>${price(d.refunds.averageKwd)}</span></div>
        <div class="row-t grand"><span>${t('a_r_rrate')}</span><span>${d.refunds.ratePercent}%</span></div>
      </div>
    </div>

    <div class="cols" style="margin-top:22px">
      <div class="box">
        <h2>${t('a_r_delivery')}</h2>
        <div class="row-t"><span>${t('a_r_exhausted')}</span><span>${d.delivery.exhausted}</span></div>
        <div class="row-t"><span>${t('a_r_expired')}</span><span>${d.delivery.expired}</span></div>
        <div class="row-t"><span>${t('a_r_revoked')}</span><span>${d.delivery.revoked}</span></div>
      </div>

      <div class="box">
        <h2>${t('a_r_customers')}</h2>
        <div class="row-t"><span>${t('a_r_withaccount')}</span><span>${d.customers.withAccount}</span></div>
        <div class="row-t"><span>${t('a_r_guests')}</span><span>${d.customers.guests}</span></div>
        <div class="row-t grand"><span>${t('a_r_repeat')}</span><span>${d.customers.repeat}</span></div>
      </div>
    </div>`);

  shell('a_reports');
}

document.addEventListener('click', e => {
  const r = e.target.closest('[data-days]');
  if (!r) return;
  REP.days = +r.dataset.days;
  refresh();
});
