function render(){
  shell('a_custs');
  document.getElementById('ad-body').innerHTML=`
  <div class="tbar"><input class="field" placeholder="${t('a_search')}"></div>
  <table><thead><tr><th>${t('a_cust')}</th><th>${t('a_user')}</th><th>${t('a_c_country')}</th>
    <th>${t('a_c_orders')}</th><th>${t('a_c_spent')}</th><th>${t('a_c_joined')}</th></tr></thead>
  <tbody>${CUSTOMERS.map(c=>`<tr>
    <td>${c.name}</td><td class="muted">${c.email}</td><td>${c.country}</td>
    <td>${c.orders}</td><td>${price(c.spent)}</td><td class="muted">${c.joined}</td></tr>`).join('')}
  </tbody></table>`;
}
