function render(){
  shell('a_cats');
  document.getElementById('ad-body').innerHTML=`
  <div class="tbar"><button class="btn btn-sm" id="new">${t('a_cat_new')}</button></div>
  <table><thead><tr><th>${t('a_cat_name_en')}</th><th>${t('a_cat_name_ar')}</th>
    <th>${t('a_cat_books')}</th><th>${t('a_status')}</th><th></th></tr></thead>
  <tbody>${CATS.map(c=>{
    const n=BOOKS.filter(b=>b.cat===c.id).length;
    return `<tr>
      <td><span class="mini"><span style="width:12px;height:22px;background:${c.hue};border-radius:2px;display:inline-block"></span>${c.en}</span></td>
      <td>${c.ar}</td><td>${n}</td>
      <td><span class="st st-live">${t('a_live')}</span></td>
      <td style="text-align:end"><button class="ico">${t('a_edit')}</button><button class="ico">${t('a_del')}</button></td></tr>`;
  }).join('')}</tbody></table>`;
  document.getElementById('new').onclick=()=>toast(t('a_cat_new'));
}
