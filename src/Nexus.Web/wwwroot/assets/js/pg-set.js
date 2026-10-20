function render(){
  shell('a_set');
  document.getElementById('ad-body').innerHTML=`
  <div class="set">
    <div class="box"><h2>${t('a_s_pay')}</h2>
      <div class="f"><label>${t('a_s_tapp')}</label><input value="pk_test_••••••••••••7Kd2"></div>
      <div class="f"><label>${t('a_s_tapk')}</label><input type="password" value="sk_test_secret_value"></div>
      <div class="check"><input type="checkbox" id="tm" checked><label for="tm" style="margin:0">${t('a_s_test')}</label></div>
    </div>
    <div class="box"><h2>${t('a_s_del')}</h2>
      <div class="f"><label>${t('a_s_days')}</label><input value="7"></div>
      <div class="f"><label>${t('a_s_max')}</label><input value="3"></div>
      <div class="check"><input type="checkbox" id="wm" checked><label for="wm" style="margin:0">${t('a_s_wm')}</label></div>
    </div>
    <div class="box"><h2>${t('a_s_store')}</h2>
      <div class="f"><label>${t('a_s_cur')}</label><select><option>KWD + AED</option><option>KWD</option><option>AED</option></select></div>
      <div class="f"><label>${t('a_s_vat')}</label><input value="5"></div>
      <div class="check"><input type="checkbox" id="gs" checked><label for="gs" style="margin:0">${t('a_s_guest')}</label></div>
    </div>
    <div class="box"><h2>${t('a_s_mail')}</h2>
      <div class="f"><label>${t('a_s_from')}</label><input value="orders@nexus.com"></div>
      <div class="f"><label>${t('a_s_tmpl')}</label><textarea>${t('ok_body')} {{email}} — {{download_link}}</textarea></div>
    </div>
  </div>
  <div style="margin-top:22px"><button class="btn" id="save">${t('a_save')}</button></div>`;
  document.getElementById('save').onclick=()=>toast(t('a_s_saved'));
}
