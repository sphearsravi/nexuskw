let q='', editing=null;
function bookForm(id){
  const b=id?book(id):{price:'',cat:'investing',en:{t:'',a:'',d:''},ar:{t:'',a:'',d:''}};
  const o=L(b);
  return `<div class="modal"><div class="box set" style="display:block">
    <h2>${id?t('a_edit'):t('a_book_new')}</h2>
    <div class="f"><label>${t('a_title_c')}</label><input value="${esc(o.t)}"></div>
    <div class="f"><label>${t('a_author')}</label><input value="${esc(o.a)}"></div>
    <div class="f"><label>${t('a_desc')}</label><textarea>${esc(o.d)}</textarea></div>
    <div class="f" style="display:grid;grid-template-columns:1fr 1fr;gap:14px">
      <div><label>${t('a_cat')}</label><select>${CATS.map(c=>`<option ${c.id===b.cat?'selected':''}>${L(c)}</option>`).join('')}</select></div>
      <div><label>${t('a_price')}</label><input value="${b.price}"></div>
    </div>
    <div class="f" style="display:grid;grid-template-columns:1fr 1fr;gap:14px">
      <div><label>${t('a_upload_cover')}</label><div class="drop">${t('a_choose')} — JPG / PNG</div></div>
      <div><label>${t('a_upload_pdf')}</label><div class="drop">${t('a_choose')} — PDF</div></div>
    </div>
    <div class="modal-foot">
      <button class="btn btn-ghost btn-sm" id="cancel">${t('a_cancel')}</button>
      <button class="btn btn-sm" id="save">${t('a_save')}</button></div>
  </div></div>`;
}
function render(){
  shell('a_books');
  const list=BOOKS.filter(b=>(L(b).t+L(b).a).toLowerCase().includes(q.toLowerCase()));
  document.getElementById('ad-body').innerHTML=`
  <div class="tbar">
    <input class="field" id="q" value="${esc(q)}" placeholder="${t('a_search')}">
    <button class="btn btn-sm" id="new">${t('a_book_new')}</button>
  </div>
  <table><thead><tr><th>${t('a_title_c')}</th><th>${t('a_author')}</th><th>${t('a_cat')}</th>
    <th>${t('a_price')}</th><th>${t('a_status')}</th><th></th></tr></thead>
  <tbody>${list.map(b=>`<tr>
    <td><span class="mini">${cover(b)}<span>${esc(L(b).t)}</span></span></td>
    <td>${esc(L(b).a)}</td><td>${L(cat(b.cat))}</td><td>${b.price.toFixed(3)}</td>
    <td><span class="st ${b.id%9===0?'st-draft':'st-live'}">${b.id%9===0?t('a_draft'):t('a_live')}</span></td>
    <td style="text-align:end;white-space:nowrap">
      <button class="ico" data-edit="${b.id}">${t('a_edit')}</button>
      <button class="ico" data-del="${b.id}">${t('a_del')}</button></td></tr>`).join('')}
  </tbody></table>
  ${editing!==null?bookForm(editing):''}`;
  document.getElementById('q').oninput=e=>{q=e.target.value;render();};
  document.getElementById('new').onclick=()=>{editing=0;render();};
}
document.addEventListener('click',e=>{
  const ed=e.target.closest('[data-edit]'); if(ed){editing=+ed.dataset.edit;render();return;}
  const dl=e.target.closest('[data-del]'); if(dl){ if(confirm(t('a_del_confirm'))) toast(t('a_del')); return;}
  if(e.target.id==='cancel'){editing=null;render();return;}
  if(e.target.id==='save'){editing=null;render();toast(t('a_s_saved'));}
});
