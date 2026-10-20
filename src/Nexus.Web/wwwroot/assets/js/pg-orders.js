function render(){
  shell('a_orders');
  document.getElementById('ad-body').innerHTML=`
  <div class="tbar"><input class="field" placeholder="${t('a_search')}"></div>
  <table><thead><tr><th>${t('a_ord_id')}</th><th>${t('a_cust')}</th><th>${t('a_items')}</th>
    <th>${t('a_total')}</th><th>${t('a_payref')}</th><th>${t('a_status')}</th><th>${t('a_date')}</th><th></th></tr></thead>
  <tbody>${ORDERS.map(o=>`<tr>
    <td>${o.id}</td>
    <td>${o.cust}<br><span class="muted" style="font-size:12.5px">${o.email}</span></td>
    <td>${o.items.map(i=>esc(L(book(i)).t)).join('<br>')}</td>
    <td>${o.cur==='KWD'?'KD '+o.total.toFixed(3):'AED '+o.total.toFixed(2)}</td>
    <td class="muted" style="font-size:12.5px">${o.pay}</td>
    <td><span class="st st-${o.status}">${t('a_'+o.status)}</span></td>
    <td class="muted" style="white-space:nowrap">${o.date}</td>
    <td style="text-align:end">${o.status==='paid'?`<button class="ico" data-resend="${o.id}">${t('a_resend')}</button>`:''}</td>
  </tr>`).join('')}</tbody></table>`;
}
document.addEventListener('click',e=>{
  const r=e.target.closest('[data-resend]');
  if(r) toast(t('a_resend')+' · '+r.dataset.resend);
});
