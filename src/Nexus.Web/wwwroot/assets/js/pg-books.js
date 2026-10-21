/* Books: the catalogue as the operator edits it.
 *
 * The form holds both languages at once rather than switching with the interface,
 * because a title is only complete when both are filled in and a form that hides
 * half of them is a form that lets you forget. */
let BK = { rows: [], q: '', cat: '', status: '', open: null, motif: null, errors: [], importing: false, report: null };

async function load() {
  const p = new URLSearchParams();
  if (BK.q) p.set('q', BK.q);
  if (BK.cat) p.set('cat', BK.cat);
  if (BK.status) p.set('status', BK.status);

  const r = await Admin.get('books?' + p);
  if (!r.ok) { AdminState.failed = true; return; }
  BK.rows = r.data;
}

function bkField(id, label, value, type) {
  return `<div class="f"><label for="${id}">${label}</label>
    <input id="${id}" type="${type || 'text'}" value="${esc(value == null ? '' : value)}"></div>`;
}

function bkForm() {
  const blank = {
    cat: CATS.length ? CATS[0].id : '', price: '', pages: '', year: new Date().getFullYear(),
    isbn: '', languages: 'ArabicAndEnglish', motif: '', en: { t: '', a: '', d: '' }, ar: { t: '', a: '', d: '' }
  };
  const b = BK.open === 'new' ? blank : BK.open;
  const isNew = !b.id;

  const errs = BK.errors.length
    ? `<ul class="errs">${BK.errors.map(e => `<li>${esc(adFieldError(e))}</li>`).join('')}</ul>`
    : '';

  const pdf = isNew
    ? ''
    : `<div class="f"><label>${t('a_upload_pdf')}</label>
        <div class="drop">
          <span>${b.hasPdf ? (b.pdfPlaceholder ? t('a_pdf_stub') : `${esc(b.pdfOriginalFileName || '')} · ${t('a_pdf_ok')}`) : t('a_pdf_none')}</span>
          <input type="file" id="bk-pdf" accept="application/pdf">
        </div></div>`;

  return `<div class="modal"><div class="box set" style="display:block">
    <h2>${isNew ? t('a_book_new') : esc(b.en.t || b.ar.t)}</h2>
    ${errs}
    ${b.publishBlocker ? `<p class="note">${t('a_pb_why')} ${esc(adBlocker(b.publishBlocker))}</p>` : ''}

    <div class="two">
      <div>
        <h3>${t('lang_v_en')}</h3>
        ${bkField('bk-te', t('a_title_c'), b.en.t)}
        ${bkField('bk-ae', t('a_author'), b.en.a)}
        <div class="f"><label for="bk-de">${t('a_desc')}</label><textarea id="bk-de">${esc(b.en.d)}</textarea></div>
      </div>
      <div dir="rtl">
        <h3>${t('lang_v_ar')}</h3>
        ${bkField('bk-ta', t('a_title_c'), b.ar.t)}
        ${bkField('bk-aa', t('a_author'), b.ar.a)}
        <div class="f"><label for="bk-da">${t('a_desc')}</label><textarea id="bk-da">${esc(b.ar.d)}</textarea></div>
      </div>
    </div>

    <div class="two">
      <div class="f"><label for="bk-cat">${t('a_cat')}</label>
        <select id="bk-cat">${CATS.map(c =>
          `<option value="${esc(c.id)}" ${c.id === b.cat ? 'selected' : ''}>${esc(L(c))}${c.active === false ? ' · ' + t('a_hidden') : ''}</option>`).join('')}</select></div>
      ${bkField('bk-price', t('a_price'), b.price)}
    </div>

    <div class="two">
      ${bkField('bk-pages', t('f_pages'), b.pages)}
      ${bkField('bk-year', t('a_year'), b.year)}
    </div>

    <div class="two">
      ${bkField('bk-isbn', t('a_isbn'), b.isbn)}
      <div class="f"><label for="bk-langs">${t('a_langs')}</label>
        <select id="bk-langs">
          <option value="ArabicAndEnglish" ${b.languages === 'ArabicAndEnglish' ? 'selected' : ''}>${t('lang_v_both')}</option>
          <option value="EnglishOnly" ${b.languages === 'EnglishOnly' ? 'selected' : ''}>${t('lang_v_en')}</option>
          <option value="ArabicOnly" ${b.languages === 'ArabicOnly' ? 'selected' : ''}>${t('lang_v_ar')}</option>
        </select></div>
    </div>

    <div class="f"><label>${t('a_motif')}</label>
      <div class="motifs">
        <button class="motif ${BK.motif ? '' : 'on'}" data-motif="">${t('a_motif_auto')}</button>
        ${CATS.map(c => `<button class="motif ${BK.motif === c.id ? 'on' : ''}" data-motif="${esc(c.id)}"
            style="--c:${esc(c.hue)}">${esc(L(c))}</button>`).join('')}
      </div></div>
    ${pdf}

    <div class="modal-foot">
      <button class="btn btn-ghost btn-sm" id="bk-cancel">${t('a_cancel')}</button>
      <button class="btn btn-sm" id="bk-save">${isNew ? t('a_book_new') : t('a_save')}</button>
    </div>
  </div></div>`;
}

function bkImport() {
  const r = BK.report;
  let result = '';

  if (r && !r.accepted) {
    result = `<p class="note err">${tf('a_import_bad', { n: r.errors.length })}</p>
      <ul class="errs">${r.errors.slice(0, 50).map(e =>
        `<li>${t('a_import_line')} ${e.line} — ${esc(adFieldError(e))}</li>`).join('')}</ul>`;
  } else if (r && r.dryRun) {
    result = `<p class="note">${tf('a_import_dry', { n: r.rowsRead })}</p>`;
  } else if (r) {
    result = `<p class="note">${tf('a_import_ok', { n: r.created })}</p>`;
  }

  return `<div class="modal"><div class="box set" style="display:block">
    <h2>${t('a_import')}</h2>
    <p class="note">${t('a_import_d')}</p>
    <p><a class="ico" href="/admin/api/books/import/template">${t('a_import_tmpl')}</a></p>
    <div class="f"><label for="bk-csv">${t('a_import_file')}</label>
      <input type="file" id="bk-csv" accept=".csv,text/csv"></div>
    ${result}
    <div class="modal-foot">
      <button class="btn btn-ghost btn-sm" id="bk-imp-close">${t('a_close')}</button>
      <button class="btn btn-ghost btn-sm" id="bk-imp-check">${t('a_import_check')}</button>
      <button class="btn btn-sm" id="bk-imp-run">${t('a_import_run')}</button>
    </div>
  </div></div>`;
}

function render() {
  shell('a_books');
  if (!AdminState.ready) { body(adWaiting()); return; }

  const status = { '': t('a_any'), published: t('a_live'), draft: t('a_draft'), nofile: t('a_filter_nofile') };

  const table = BK.rows.length ? `
    <table><thead><tr><th>${t('a_title_c')}</th><th>${t('a_author')}</th><th>${t('a_cat')}</th>
      <th>${t('a_price')}</th><th>${t('a_upload_pdf')}</th><th>${t('a_status')}</th><th></th></tr></thead>
    <tbody>${BK.rows.map(b => `<tr>
      <td><span class="mini">${adCover(b)}<span>${esc(L(b).t)}</span></span></td>
      <td>${esc(L(b).a)}</td>
      <td>${cat(b.cat) ? esc(L(cat(b.cat))) : esc(b.cat)}</td>
      <td>${price(b.price)}</td>
      <td>${b.hasPdf ? (b.pdfPlaceholder ? `<span class="st st-draft">${t('a_pdf_stub')}</span>`
                                         : `<span class="st st-active">${t('a_pdf_ok')}</span>`)
                     : `<span class="st st-failed">${t('a_pdf_none')}</span>`}</td>
      <td><span class="st ${b.published ? 'st-live' : 'st-draft'}">${b.published ? t('a_live') : t('a_draft')}</span></td>
      <td style="text-align:end;white-space:nowrap">
        <button class="ico" data-edit="${b.id}">${t('a_edit')}</button>
        ${b.published
          ? `<button class="ico" data-unpub="${b.id}">${t('a_unpublish')}</button>`
          : `<button class="ico" data-pub="${b.id}">${t('a_publish')}</button>`}
        <button class="ico" data-del="${b.id}">${t('a_del')}</button></td></tr>`).join('')}
    </tbody></table>` : adEmpty();

  body(`
  <div class="tbar">
    <input class="field" id="bk-q" value="${esc(BK.q)}" placeholder="${t('a_search')}">
    <select class="field" id="bk-cf">
      <option value="">${t('filter_all')}</option>
      ${CATS.map(c => `<option value="${esc(c.id)}" ${BK.cat === c.id ? 'selected' : ''}>${esc(L(c))}</option>`).join('')}
    </select>
    <select class="field" id="bk-sf">
      ${Object.entries(status).map(([v, label]) =>
        `<option value="${v}" ${BK.status === v ? 'selected' : ''}>${label}</option>`).join('')}
    </select>
    <button class="btn btn-ghost btn-sm" id="bk-import">${t('a_import')}</button>
    <button class="btn btn-sm" id="bk-new">${t('a_book_new')}</button>
  </div>
  ${table}
  ${BK.open ? bkForm() : ''}
  ${BK.importing ? bkImport() : ''}`);

  const q = document.getElementById('bk-q');
  q.oninput = () => { BK.q = q.value; clearTimeout(BK.timer); BK.timer = setTimeout(refresh, 250); };
  document.getElementById('bk-cf').onchange = e => { BK.cat = e.target.value; refresh(); };
  document.getElementById('bk-sf').onchange = e => { BK.status = e.target.value; refresh(); };
  document.getElementById('bk-new').onclick = () => { BK.open = 'new'; BK.motif = null; BK.errors = []; render(); };
  document.getElementById('bk-import').onclick = () => { BK.importing = true; BK.report = null; render(); };
}

function bkRead() {
  const v = id => document.getElementById(id).value.trim();
  return {
    categorySlug: v('bk-cat'),
    priceKwd: Number(v('bk-price')) || 0,
    pages: Number(v('bk-pages')) || 0,
    year: Number(v('bk-year')) || 0,
    isbn: v('bk-isbn') || null,
    titleEn: v('bk-te'), authorEn: v('bk-ae'), descriptionEn: v('bk-de'),
    titleAr: v('bk-ta'), authorAr: v('bk-aa'), descriptionAr: v('bk-da'),
    contentLanguages: v('bk-langs'),
    coverMotif: BK.motif || null
  };
}

async function bkSave() {
  const write = bkRead();
  const editing = BK.open !== 'new' && BK.open ? BK.open.id : null;
  const r = editing ? await Admin.put('books/' + editing, write) : await Admin.post('books', write);

  if (!r.ok) {
    BK.errors = (r.data && r.data.fields) || [];
    if (!BK.errors.length) toast(t('a_err'));
    render();
    return;
  }

  // The file is uploaded after the record exists, because the library stores it
  // under the book's own id.
  const file = document.getElementById('bk-pdf');
  if (file && file.files && file.files[0]) {
    const up = await Admin.upload(`books/${r.data.id}/pdf`, 'file', file.files[0]);
    toast(up.ok ? t('a_pdf_sent') : t('a_err'));
  } else {
    toast(editing ? t('a_saved_ok') : t('a_created_ok'));
  }

  BK.open = null;
  BK.errors = [];
  BK.motif = null;
  await refresh();
}

document.addEventListener('click', async e => {
  const ed = e.target.closest('[data-edit]');
  if (ed) {
    BK.open = BK.rows.find(b => b.id === +ed.dataset.edit) || null;
    // The row reports the motif it draws with, which falls back to the subject;
    // an override equal to the subject is indistinguishable from none, and means
    // the same thing.
    BK.motif = BK.open && BK.open.motif !== BK.open.cat ? BK.open.motif : null;
    BK.errors = [];
    render();
    return;
  }

  const motif = e.target.closest('[data-motif]');
  if (motif) {
    // The classes are toggled in place rather than re-rendering: a redraw would
    // throw away everything the operator has typed into the form behind it.
    BK.motif = motif.dataset.motif || null;
    document.querySelectorAll('[data-motif]').forEach(el =>
      el.classList.toggle('on', (el.dataset.motif || '') === (BK.motif || '')));
    return;
  }

  const pub = e.target.closest('[data-pub]');
  if (pub) {
    const r = await Admin.post(`books/${pub.dataset.pub}/publish`);
    if (r.ok) toast(t('a_published_ok'));
    else if (r.data && r.data.blocker) toast(`${t('a_pb_why')} ${adBlocker(r.data.blocker)}`);
    else toast(t('a_err'));
    await refresh();
    return;
  }

  const unpub = e.target.closest('[data-unpub]');
  if (unpub) {
    const r = await Admin.post(`books/${unpub.dataset.unpub}/unpublish`);
    toast(r.ok ? t('a_unpublished_ok') : t('a_err'));
    await refresh();
    return;
  }

  const del = e.target.closest('[data-del]');
  if (del) {
    if (!confirm(t('a_del_confirm'))) return;
    const r = await Admin.del('books/' + del.dataset.del);
    if (r.ok) toast(t('a_saved_ok'));
    else if (r.data && r.data.error === 'book_has_orders') toast(t('a_del_blocked'));
    else toast(t('a_err'));
    await refresh();
    return;
  }

  if (e.target.id === 'bk-cancel') { BK.open = null; BK.errors = []; BK.motif = null; render(); return; }
  if (e.target.id === 'bk-save') { await bkSave(); return; }

  if (e.target.id === 'bk-imp-close') { BK.importing = false; BK.report = null; await refresh(); return; }

  if (e.target.id === 'bk-imp-check' || e.target.id === 'bk-imp-run') {
    const input = document.getElementById('bk-csv');
    if (!input.files || !input.files[0]) return;
    const dry = e.target.id === 'bk-imp-check';
    const r = await Admin.upload(`books/import?dryRun=${dry}`, 'file', input.files[0]);
    BK.report = r.data;
    if (r.ok && !dry) { BK.importing = false; await refresh(); toast(tf('a_import_ok', { n: r.data.created })); }
    else render();
  }
});
