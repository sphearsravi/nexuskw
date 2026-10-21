/* Subjects. The slug is the public address and is fixed at creation: the
   storefront links to shop.html?cat=investing, and renaming the slug would break
   every link anyone has shared. The display names are editable in both languages. */
let CT = { rows: [], open: null, errors: [] };

async function load() {
  const r = await Admin.get('categories');
  if (!r.ok) { AdminState.failed = true; return; }
  CT.rows = r.data;
}

function ctForm() {
  const c = CT.open === 'new'
    ? { slug: '', nameEn: '', nameAr: '', hue: '#0B5D4A', isActive: true }
    : CT.open;
  const isNew = CT.open === 'new';

  return `<div class="modal"><div class="box set" style="display:block">
    <h2>${isNew ? t('a_cat_new') : esc(c.nameEn)}</h2>
    ${CT.errors.length ? `<ul class="errs">${CT.errors.map(e => `<li>${esc(adFieldError(e))}</li>`).join('')}</ul>` : ''}
    <div class="f"><label for="ct-slug">${t('a_slug')}</label>
      <input id="ct-slug" value="${esc(c.slug)}" ${isNew ? '' : 'disabled'}></div>
    <div class="f"><label for="ct-en">${t('a_cat_name_en')}</label><input id="ct-en" value="${esc(c.nameEn)}"></div>
    <div class="f"><label for="ct-ar">${t('a_cat_name_ar')}</label><input id="ct-ar" dir="rtl" value="${esc(c.nameAr)}"></div>
    <div class="f"><label for="ct-hue">${t('a_hue')}</label><input id="ct-hue" value="${esc(c.hue)}"></div>
    <div class="check"><input type="checkbox" id="ct-on" ${c.isActive ? 'checked' : ''}>
      <label for="ct-on" style="margin:0">${t('a_active')}</label></div>
    <div class="modal-foot">
      <button class="btn btn-ghost btn-sm" id="ct-cancel">${t('a_cancel')}</button>
      <button class="btn btn-sm" id="ct-save">${isNew ? t('a_cat_new') : t('a_save')}</button></div>
  </div></div>`;
}

function render() {
  shell('a_cats');
  if (!AdminState.ready) { body(adWaiting()); return; }

  const table = CT.rows.length ? `
    <table><thead><tr><th>${t('a_order')}</th><th>${t('a_cat_name_en')}</th><th>${t('a_cat_name_ar')}</th>
      <th>${t('a_slug')}</th><th>${t('a_cat_books')}</th><th>${t('a_status')}</th><th></th></tr></thead>
    <tbody>${CT.rows.map((c, i) => `<tr>
      <td style="white-space:nowrap">
        <button class="ico" data-up="${esc(c.slug)}" ${i === 0 ? 'disabled' : ''}>${t('a_up')}</button>
        <button class="ico" data-down="${esc(c.slug)}" ${i === CT.rows.length - 1 ? 'disabled' : ''}>${t('a_down')}</button></td>
      <td><span class="mini"><span style="width:12px;height:22px;background:${esc(c.hue)};border-radius:2px;display:inline-block"></span>${esc(c.nameEn)}</span></td>
      <td dir="rtl">${esc(c.nameAr)}</td>
      <td class="muted">${esc(c.slug)}</td>
      <td>${c.publishedCount} / ${c.bookCount}</td>
      <td><span class="st ${c.isActive ? 'st-live' : 'st-draft'}">${c.isActive ? t('a_active') : t('a_hidden')}</span></td>
      <td style="text-align:end;white-space:nowrap">
        <button class="ico" data-cedit="${esc(c.slug)}">${t('a_edit')}</button>
        <button class="ico" data-cdel="${esc(c.slug)}">${t('a_del')}</button></td></tr>`).join('')}
    </tbody></table>` : adEmpty();

  body(`<div class="tbar"><button class="btn btn-sm" id="ct-new">${t('a_cat_new')}</button></div>
    ${table}
    ${CT.open ? ctForm() : ''}`);

  document.getElementById('ct-new').onclick = () => { CT.open = 'new'; CT.errors = []; render(); };
}

async function ctMove(slug, by) {
  const order = CT.rows.map(c => c.slug);
  const at = order.indexOf(slug);
  const to = at + by;
  if (at < 0 || to < 0 || to >= order.length) return;

  order.splice(to, 0, order.splice(at, 1)[0]);
  const r = await Admin.post('categories/reorder', { slugs: order });
  if (!r.ok) toast(t('a_err'));
  await refresh();
}

document.addEventListener('click', async e => {
  const up = e.target.closest('[data-up]');
  if (up) { await ctMove(up.dataset.up, -1); return; }

  const down = e.target.closest('[data-down]');
  if (down) { await ctMove(down.dataset.down, 1); return; }

  const ed = e.target.closest('[data-cedit]');
  if (ed) {
    CT.open = CT.rows.find(c => c.slug === ed.dataset.cedit) || null;
    CT.errors = [];
    render();
    return;
  }

  const del = e.target.closest('[data-cdel]');
  if (del) {
    if (!confirm(t('a_cat_del_confirm'))) return;
    const r = await Admin.del('categories/' + encodeURIComponent(del.dataset.cdel));
    if (r.ok) toast(t('a_saved_ok'));
    else if (r.data && r.data.error === 'category_has_books') toast(tf('a_cat_del_blocked', { n: r.data.books }));
    else toast(t('a_err'));
    await refresh();
    return;
  }

  if (e.target.id === 'ct-cancel') { CT.open = null; CT.errors = []; render(); return; }

  if (e.target.id === 'ct-save') {
    const slug = document.getElementById('ct-slug').value.trim();
    const write = {
      nameEn: document.getElementById('ct-en').value.trim(),
      nameAr: document.getElementById('ct-ar').value.trim(),
      hue: document.getElementById('ct-hue').value.trim(),
      isActive: document.getElementById('ct-on').checked
    };

    const r = CT.open === 'new'
      ? await Admin.post('categories/' + encodeURIComponent(slug), write)
      : await Admin.put('categories/' + encodeURIComponent(CT.open.slug), write);

    if (!r.ok) {
      CT.errors = (r.data && r.data.fields) || [];
      if (!CT.errors.length) toast(t('a_err'));
      render();
      return;
    }

    CT.open = null;
    CT.errors = [];
    toast(t('a_saved_ok'));
    await refresh();
  }
});
