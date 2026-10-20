const AD_NAV=[['index.html','a_dash'],['books.html','a_books'],['categories.html','a_cats'],
  ['orders.html','a_orders'],['customers.html','a_custs'],['downloads.html','a_dls'],['settings.html','a_set']];
function shell(title){
  const here=location.pathname.split('/').pop()||'index.html';
  document.getElementById('ad-side').innerHTML=`
    <a class="brand" href="index.html">
      <span class="brand-mark"></span>
      <span><span class="brand-name">${t('brand')}</span><span class="brand-sub">${t('a_title')}</span></span></a>
    <nav>${AD_NAV.map(([f,k])=>`<a href="${f}" class="${here===f?'on':''}">${t(k)}</a>`).join('')}</nav>
    <div class="out"><a href="../index.html">${t('a_exit')}</a></div>`;
  document.getElementById('ad-top').innerHTML=`
    <h1>${t(title)}</h1>
    <div class="tools">
      <div class="seg"><button data-cur="KWD" class="${state.cur==='KWD'?'on':''}">KWD</button>
        <button data-cur="AED" class="${state.cur==='AED'?'on':''}">AED</button></div>
      <button class="lang" data-lang>${isAR()?'English':'العربية'}</button>
    </div>`;
}
function toast(msg){
  document.querySelectorAll('.toast').forEach(e=>e.remove());
  const d=document.createElement('div'); d.className='toast'; d.textContent=msg;
  document.body.appendChild(d); setTimeout(()=>d.remove(),2200);
}
const revenue30=()=>ORDERS.filter(o=>o.status==='paid').reduce((s,o)=>s+o.items.reduce((x,i)=>x+book(i).price,0),0)*6.4;
