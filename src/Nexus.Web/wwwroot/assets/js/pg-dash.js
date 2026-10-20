function render(){
  shell('a_dash');
  const paid=ORDERS.filter(o=>o.status==='paid');
  const rev=paid.reduce((s,o)=>s+o.items.reduce((x,i)=>x+book(i).price,0),0)*6.4;
  const top=[...BOOKS].sort((a,b)=>b.sales-a.sales).slice(0,6);
  const max=Math.max.apply(null,SALES_30D);
  document.getElementById('ad-body').innerHTML=`
  <dl class="kpis">
    <div class="kpi"><dt>${t('a_rev')}</dt><dd>${price(rev)}</dd><span class="delta">+12.4%</span></div>
    <div class="kpi"><dt>${t('a_ord')}</dt><dd>248</dd><span class="delta">+8.1%</span></div>
    <div class="kpi"><dt>${t('a_titles')}</dt><dd>${BOOKS.length}</dd><span class="delta">${CATS.length} ${t('a_cats')}</span></div>
    <div class="kpi"><dt>${t('a_conv')}</dt><dd>63%</dd><span class="delta">+2.0%</span></div>
  </dl>
  <div class="cols">
    <div class="box"><h2>${t('a_chart')}</h2>
      <div class="bars">${SALES_30D.map(v=>`<i style="height:${(v/max)*100}%" title="${v}"></i>`).join('')}</div>
    </div>
    <div class="box"><h2>${t('a_top')}</h2>
      <ol class="rank">${top.map(b=>`<li><span>${esc(L(b).t)}</span><b>${b.sales}</b></li>`).join('')}</ol>
    </div>
  </div>
  <div class="box"><h2>${t('a_recent')}</h2>
    <table><thead><tr><th>${t('a_ord_id')}</th><th>${t('a_cust')}</th><th>${t('a_items')}</th>
      <th>${t('a_total')}</th><th>${t('a_status')}</th><th>${t('a_date')}</th></tr></thead>
    <tbody>${ORDERS.slice(0,5).map(o=>`<tr><td>${o.id}</td><td>${o.cust}</td><td>${o.items.length}</td>
      <td>${o.cur==='KWD'?'KD '+o.total.toFixed(3):'AED '+o.total.toFixed(2)}</td>
      <td><span class="st st-${o.status}">${t('a_'+o.status)}</span></td><td class="muted">${o.date}</td></tr>`).join('')}
    </tbody></table></div>`;
}
