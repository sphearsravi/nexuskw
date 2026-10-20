function render(){
  shell('a_dls');
  document.getElementById('ad-body').innerHTML=`
  <div class="tbar"><input class="field" placeholder="${t('a_search')}"></div>
  <table><thead><tr><th>${t('a_ord_id')}</th><th>${t('a_title_c')}</th><th>${t('a_user')}</th>
    <th>${t('a_d_when')}</th><th>${t('a_d_ip')}</th><th>${t('a_d_count')}</th><th>${t('a_d_state')}</th><th></th></tr></thead>
  <tbody>${DOWNLOADS.map(d=>{
    const key={active:'a_d_active',expired:'a_d_exp',exhausted:'a_d_used'}[d.state];
    return `<tr>
      <td>${d.order}</td>
      <td><span class="mini">${cover(book(d.book))}<span>${esc(L(book(d.book)).t)}</span></span></td>
      <td class="muted">${d.email}</td><td class="muted">${d.when}</td><td class="muted">${d.ip}</td>
      <td>${d.used} / ${d.max}</td>
      <td><span class="st st-${d.state}">${t(key)}</span></td>
      <td style="text-align:end">${d.state==='active'?`<button class="ico" data-revoke="${d.order}">${t('a_d_revoke')}</button>`:''}</td></tr>`;
  }).join('')}</tbody></table>`;
}
document.addEventListener('click',e=>{
  const r=e.target.closest('[data-revoke]');
  if(r) toast(t('a_d_revoke')+' · '+r.dataset.revoke);
});
