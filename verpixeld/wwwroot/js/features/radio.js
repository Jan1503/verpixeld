/* ============================================================================
   INTERNET RADIO
   Saved stations, Radio Browser search, now playing (song titles from the
   stream), recently played, import/export and the sleep timer.
   ============================================================================ */

const radioState = {
  stations: [],
  current: -1,
  now: null,
  results: [],
  offset: 0,
  query: null,
  tagsLoaded: false,
  countriesLoaded: false,
  timer: null
};

const RADIO_PAGE = 40;

function radioEsc(text) {
  return typeof escapeHtml === 'function' ? escapeHtml(text ?? '') : String(text ?? '');
}

function radioLogo(url, size = 40) {
  const safe = url && /^https?:\/\//i.test(url) ? radioEsc(url) : '';
  const fallback = `<span class="radio-logo-fallback" style="width:${size}px;height:${size}px">📻</span>`;
  if (!safe) return fallback;
  return `<img class="radio-logo" src="${safe}" width="${size}" height="${size}" loading="lazy" alt=""
           referrerpolicy="no-referrer" onerror="this.replaceWith(Object.assign(document.createElement('span'),{className:'radio-logo-fallback',textContent:'📻'}))">`;
}

function radioMeta(s) {
  const bits = [];
  if (s.countryCode) bits.push(s.countryCode);
  if (s.codec) bits.push(s.codec + (s.bitrate ? ` ${s.bitrate}k` : ''));
  else if (s.bitrate) bits.push(`${s.bitrate}k`);
  const tags = (s.tags || '').split(',').map(t => t.trim()).filter(Boolean).slice(0, 3);
  if (tags.length) bits.push(tags.join(', '));
  return radioEsc(bits.join(' · '));
}

// ---- now playing -----------------------------------------------------------

async function refreshRadioNow() {
  try {
    const r = await api.get('/api/radio/now');
    radioState.now = r;
    renderRadioNow();
  } catch { /* host busy */ }
}

function renderRadioNow() {
  const box = document.getElementById('radio-now');
  if (!box) return;
  const n = radioState.now;
  const sleepSel = document.getElementById('radio-sleep');
  const sleepLeft = document.getElementById('radio-sleep-left');
  if (sleepLeft) sleepLeft.textContent = n && n.sleepMinutes ? `${n.sleepMinutes} min left` : '';
  if (sleepSel && document.activeElement !== sleepSel && n && !n.sleepMinutes) sleepSel.value = '0';
  const titles = document.getElementById('radio-titles-toggle');
  if (titles && n) titles.checked = !!n.readTitles;

  if (!n || !n.station || !n.playing) {
    box.classList.remove('playing');
    box.innerHTML = `
      <div class="radio-now-empty">
        <span class="radio-now-icon">📻</span>
        <div>
          <div class="radio-now-name">Radio is off</div>
          <div class="radio-now-sub">Pick a station below, or search for one.</div>
        </div>
      </div>`;
    return;
  }
  const s = n.station;
  box.classList.add('playing');
  const history = (n.titles || []).slice(1, 6).map(t =>
    `<li><span class="radio-hist-time">${new Date(t.at).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}</span> ${radioEsc(t.title)}</li>`).join('');
  box.innerHTML = `
    <div class="radio-now-main">
      ${radioLogo(s.favicon, 72)}
      <div class="radio-now-text">
        <div class="radio-now-live"><span class="radio-live-dot"></span> LIVE</div>
        <div class="radio-now-name">${radioEsc(s.name)}</div>
        <div class="radio-now-title">${n.streamTitle ? '♪ ' + radioEsc(n.streamTitle) : '<span class="text-muted">No song title from this station</span>'}</div>
        <div class="radio-now-sub">${radioMeta(s)}${n.icyBitrate && !s.bitrate ? ` · ${n.icyBitrate}k` : ''}</div>
      </div>
      <div class="radio-now-actions">
        <button class="btn btn-small" onclick="radioStep(-1)" title="Previous station">⏮</button>
        <button class="btn btn-small btn-danger" onclick="radioStop()" title="Stop">⏹</button>
        <button class="btn btn-small" onclick="radioStep(1)" title="Next station">⏭</button>
        ${n.saved ? '' : `<button class="btn btn-small btn-primary" onclick="radioSaveCurrent()" title="Save station">⭐ Save</button>`}
        ${n.streamTitle ? `<button class="btn btn-small" onclick="radioRememberCurrent()" title="Remember this song">♥</button>` : ''}
        ${s.homepage ? `<a class="btn btn-small" href="${radioEsc(s.homepage)}" target="_blank" rel="noopener noreferrer" title="Station website">🌐</a>` : ''}
      </div>
    </div>
    ${history ? `<details class="radio-hist"><summary>Earlier songs</summary><ul>${history}</ul></details>` : ''}`;
}

async function radioStop() {
  try { await api.post('/api/radio/stop'); } catch (e) { window.toast.error('Radio', e.message); }
  refreshRadioNow();
  loadRadioStations();
}

async function radioStep(dir) {
  try { await api.post(dir > 0 ? '/api/radio/next' : '/api/radio/previous'); }
  catch (e) { window.toast.error('Radio', e.message); }
  setTimeout(() => { refreshRadioNow(); loadRadioStations(); }, 600);
}

async function radioSaveCurrent() {
  const s = radioState.now && radioState.now.station;
  if (!s) return;
  await radioAdd(s);
  refreshRadioNow();
}

// ---- saved stations --------------------------------------------------------

async function loadRadioStations() {
  try {
    const r = await api.get('/api/radio/stations');
    radioState.stations = r.stations || [];
    radioState.current = r.current ?? -1;
    renderRadioStations();
  } catch (e) {
    console.error('Radio stations:', e);
  }
}

function renderRadioStations() {
  const list = document.getElementById('radio-stations');
  const count = document.getElementById('radio-station-count');
  if (count) count.textContent = `(${radioState.stations.length})`;
  if (!list) return;
  if (radioState.stations.length === 0) {
    list.innerHTML = `<div class="radio-empty">No saved stations yet. Search below and press ⭐, or add one by its stream address.</div>`;
    return;
  }
  list.innerHTML = radioState.stations.map((s, i) => `
    <div class="radio-item ${i === radioState.current ? 'active' : ''}" data-id="${radioEsc(s.id)}">
      <button class="radio-item-play" onclick="radioPlaySaved('${radioEsc(s.id)}')" title="Play">
        ${radioLogo(s.favicon, 40)}
        <span class="radio-item-overlay">${i === radioState.current ? '♪' : '▶'}</span>
      </button>
      <div class="radio-item-text" onclick="radioPlaySaved('${radioEsc(s.id)}')">
        <div class="radio-item-name">${radioEsc(s.name)}</div>
        <div class="radio-item-meta">${radioMeta(s)}${s.playCount ? ` · played ${s.playCount}×` : ''}</div>
      </div>
      <div class="radio-item-actions">
        <button class="btn btn-small" onclick="radioMove('${radioEsc(s.id)}', -1)" title="Up" ${i === 0 ? 'disabled' : ''}>↑</button>
        <button class="btn btn-small" onclick="radioMove('${radioEsc(s.id)}', 1)" title="Down" ${i === radioState.stations.length - 1 ? 'disabled' : ''}>↓</button>
        <button class="btn btn-small" onclick="radioRename('${radioEsc(s.id)}')" title="Rename">✏️</button>
        <button class="btn btn-small btn-danger" onclick="radioRemove('${radioEsc(s.id)}')" title="Remove">🗑</button>
      </div>
    </div>`).join('');
}

async function radioPlaySaved(id) {
  try {
    await api.post(`/api/radio/stations/${encodeURIComponent(id)}/play`);
  } catch (e) {
    window.toast.error('Radio', e.message);
  }
  setTimeout(() => { refreshRadioNow(); loadRadioStations(); loadRadioRecent(); }, 400);
}

async function radioMove(id, delta) {
  try { await api.post(`/api/radio/stations/${encodeURIComponent(id)}/move?delta=${delta}`); }
  catch (e) { window.toast.error('Radio', e.message); }
  loadRadioStations();
}

async function radioRename(id) {
  const s = radioState.stations.find(x => x.id === id);
  if (!s) return;
  const name = prompt('Station name', s.name);
  if (name === null || !name.trim()) return;
  try { await api.put(`/api/radio/stations/${encodeURIComponent(id)}`, { name: name.trim() }); }
  catch (e) { window.toast.error('Radio', e.message); }
  loadRadioStations();
}

async function radioRemove(id) {
  const s = radioState.stations.find(x => x.id === id);
  if (!s || !confirm(`Remove "${s.name}" from your stations?`)) return;
  try { await api.del(`/api/radio/stations/${encodeURIComponent(id)}`); }
  catch (e) { window.toast.error('Radio', e.message); }
  loadRadioStations();
}

async function radioAdd(station) {
  try {
    await api.post('/api/radio/stations', {
      name: station.name, url: station.url, favicon: station.favicon, homepage: station.homepage,
      tags: station.tags, country: station.country, countryCode: station.countryCode,
      language: station.language, codec: station.codec, bitrate: station.bitrate || 0,
      stationUuid: station.stationUuid
    });
    window.toast.success('Radio', `Saved "${station.name}"`);
    loadRadioStations();
  } catch (e) {
    window.toast.error('Radio', e.message);
  }
}

async function radioAddManual(ev) {
  ev.preventDefault();
  const name = document.getElementById('radio-add-name').value.trim();
  const url = document.getElementById('radio-add-url').value.trim();
  const favicon = document.getElementById('radio-add-logo').value.trim();
  if (!name || !url) return;
  await radioAdd({ name, url, favicon });
  ev.target.reset();
}

async function radioPlayAdhoc(station) {
  try {
    await api.post('/api/radio/play', {
      name: station.name, url: station.url, favicon: station.favicon, homepage: station.homepage,
      tags: station.tags, countryCode: station.countryCode, codec: station.codec,
      bitrate: station.bitrate || 0, stationUuid: station.stationUuid
    });
  } catch (e) {
    window.toast.error('Radio', e.message);
  }
  setTimeout(() => { refreshRadioNow(); loadRadioStations(); loadRadioRecent(); }, 400);
}

// ---- recently played -------------------------------------------------------

async function loadRadioRecent() {
  try {
    const r = await api.get('/api/radio/recent');
    const box = document.getElementById('radio-recent');
    if (!box) return;
    const list = r.stations || [];
    box.innerHTML = list.length === 0 ? '<span class="text-muted">Nothing yet.</span>' :
      list.map((s, i) => `<button class="radio-chip" onclick="radioPlayRecent(${i})" title="${radioEsc(s.name)}">${radioLogo(s.favicon, 18)} ${radioEsc(s.name)}</button>`).join('');
    radioState.recent = list;
  } catch { /* ignore */ }
}

function radioPlayRecent(i) {
  const s = radioState.recent && radioState.recent[i];
  if (s) radioPlayAdhoc(s);
}

async function radioClearRecent() {
  try { await api.del('/api/radio/recent'); } catch { /* ignore */ }
  loadRadioRecent();
}

// ---- search ----------------------------------------------------------------

async function loadRadioFilters() {
  if (!radioState.countriesLoaded) {
    try {
      const r = await api.get('/api/radio/countries');
      const sel = document.getElementById('radio-country');
      if (sel) {
        const keep = sel.value;
        sel.innerHTML = '<option value="">All countries</option>' + (r.countries || []).map(c =>
          `<option value="${radioEsc(c.code)}">${radioEsc(c.name)} (${c.count})</option>`).join('');
        sel.value = keep || (navigator.language || '').split('-')[1]?.toUpperCase() || '';
      }
      radioState.countriesLoaded = true;
    } catch { /* offline */ }
  }
  if (!radioState.tagsLoaded) {
    try {
      const r = await api.get('/api/radio/tags');
      const box = document.getElementById('radio-tags');
      if (box) {
        box.innerHTML = (r.tags || []).slice(0, 40).map(t =>
          `<button class="radio-chip" data-tag="${radioEsc(t.name)}" onclick="radioPickTag(this.dataset.tag)">${radioEsc(t.name)}</button>`).join('');
      }
      radioState.tagsLoaded = true;
    } catch { /* offline */ }
  }
}

function radioPickTag(tag) {
  const input = document.getElementById('radio-tag');
  if (input) input.value = tag;
  radioSearch();
}

async function radioSearch(more = false) {
  const q = {
    name: document.getElementById('radio-q')?.value.trim() || '',
    tag: document.getElementById('radio-tag')?.value.trim() || '',
    country: document.getElementById('radio-country')?.value || '',
    order: document.getElementById('radio-order')?.value || 'clickcount'
  };
  if (!more) {
    radioState.offset = 0;
    radioState.results = [];
  }
  radioState.query = q;
  const box = document.getElementById('radio-results');
  if (box && !more) box.innerHTML = '<div class="radio-empty">⏳ Searching…</div>';
  const params = new URLSearchParams({ ...q, limit: RADIO_PAGE, offset: radioState.offset });
  try {
    const r = await api.get('/api/radio/search?' + params.toString());
    const found = r.stations || [];
    radioState.results = radioState.results.concat(found);
    radioState.offset += found.length;
    renderRadioResults(found.length === RADIO_PAGE);
  } catch (e) {
    if (box) box.innerHTML = `<div class="radio-empty">Search failed: ${radioEsc(e.message)}</div>`;
  }
}

function renderRadioResults(hasMore) {
  const box = document.getElementById('radio-results');
  if (!box) return;
  if (radioState.results.length === 0) {
    box.innerHTML = '<div class="radio-empty">No stations found.</div>';
    return;
  }
  const isSaved = s => radioState.stations.some(x => (s.stationUuid && x.stationUuid === s.stationUuid) ||
    x.url.toLowerCase() === (s.url || '').toLowerCase());
  box.innerHTML = radioState.results.map((s, i) => `
    <div class="radio-item">
      <button class="radio-item-play" onclick="radioPlayResult(${i})" title="Listen">
        ${radioLogo(s.favicon, 40)}<span class="radio-item-overlay">▶</span>
      </button>
      <div class="radio-item-text" onclick="radioPlayResult(${i})">
        <div class="radio-item-name">${radioEsc(s.name)}</div>
        <div class="radio-item-meta">${radioMeta(s)}${s.votes ? ` · 👍 ${s.votes}` : ''}</div>
      </div>
      <div class="radio-item-actions">
        ${isSaved(s) ? '<span class="radio-saved">✓ saved</span>'
          : `<button class="btn btn-small btn-primary" onclick="radioSaveResult(${i})" title="Save">⭐</button>`}
      </div>
    </div>`).join('') +
    (hasMore ? `<button class="btn btn-small radio-more" onclick="radioSearch(true)">Load more</button>` : '');
}

function radioPlayResult(i) {
  const s = radioState.results[i];
  if (s) radioPlayAdhoc(s);
}

async function radioSaveResult(i) {
  const s = radioState.results[i];
  if (!s) return;
  await radioAdd(s);
  renderRadioResults(radioState.results.length % RADIO_PAGE === 0 && radioState.results.length > 0);
}

// ---- sleep, titles, import/export -----------------------------------------

async function radioSetSleep(minutes) {
  try {
    await api.post(`/api/sleep?minutes=${encodeURIComponent(minutes)}`);
    window.toast.info('Sleep timer', +minutes ? `Stops in ${minutes} minutes` : 'Off');
  } catch (e) {
    window.toast.error('Sleep timer', e.message);
  }
  refreshRadioNow();
}

async function radioSetTitles(on) {
  try { await api.post(`/api/radio/titles?enabled=${on ? 'true' : 'false'}`); } catch { /* ignore */ }
}

function radioExport() {
  window.location.href = '/api/radio/export';
}

async function radioImport(input) {
  const file = input.files && input.files[0];
  input.value = '';
  if (!file) return;
  try {
    const list = JSON.parse(await file.text());
    if (!Array.isArray(list)) throw new Error('Expected a list of stations');
    const replace = confirm('Replace your saved stations? Cancel adds the new ones instead.');
    const r = await api.post(`/api/radio/import?replace=${replace}`, list);
    window.toast.success('Radio', `${r.added} stations imported`);
  } catch (e) {
    window.toast.error('Radio import', e.message);
  }
  loadRadioStations();
}

// ---- page life -------------------------------------------------------------

function radioPageShown() {
  loadRadioStations().then(loadRadioAlarms);
  loadRadioRecent();
  refreshRadioNow();
  loadRadioFilters();
  loadRadioSongs();
  if (!radioState.timer) {
    radioState.timer = setInterval(() => {
      const page = document.getElementById('media-page-radio');
      if (!page || !page.classList.contains('active') || document.hidden) return;
      refreshRadioNow();
      loadRadioSongs();
      loadRadioAlarms();
    }, 5000);
  }
}

document.addEventListener('DOMContentLoaded', () => {
  const page = document.getElementById('media-page-radio');
  if (page && page.classList.contains('active')) radioPageShown();
});

// ---- alarm clock -------------------------------------------------------------

const RADIO_DAYS = ['Mo', 'Tu', 'We', 'Th', 'Fr', 'Sa', 'Su'];
radioState.alarms = [];

function radioDaysText(mask) {
  if (!mask) return 'once';
  if (mask === 0x7F) return 'every day';
  if (mask === 0x1F) return 'Mo–Fr';
  if (mask === 0x60) return 'Sa–Su';
  return RADIO_DAYS.filter((_, i) => mask & (1 << i)).join(' ');
}

async function loadRadioAlarms() {
  try {
    const r = await api.get('/api/alarms');
    radioState.alarms = r.alarms || [];
    radioState.ringing = r.ringing;
    renderRadioAlarms();
  } catch { /* ignore */ }
}

function renderRadioAlarms() {
  const box = document.getElementById('radio-alarms');
  if (!box) return;
  const stop = document.getElementById('radio-alarm-stop');
  if (stop) stop.style.display = radioState.ringing ? '' : 'none';
  if (radioState.alarms.length === 0) {
    box.innerHTML = '<div class="radio-empty">No alarm yet. The first alarm is also the one on the panel (MORE page).</div>';
    return;
  }
  const stationName = id => (radioState.stations.find(s => s.id === id) || {}).name || 'last station';
  box.innerHTML = radioState.alarms.map(a => {
    const next = a.next ? new Date(a.next) : null;
    const nextText = next ? next.toLocaleString([], { weekday: 'short', hour: '2-digit', minute: '2-digit' }) : 'off';
    const time = `${String(a.hour).padStart(2, '0')}:${String(a.minute).padStart(2, '0')}`;
    return `
    <div class="radio-item ${a.enabled ? 'active' : ''}">
      <label class="radio-alarm-switch" title="On / off">
        <input type="checkbox" ${a.enabled ? 'checked' : ''} onchange="radioAlarmEnable('${radioEsc(a.id)}', this.checked)">
      </label>
      <div class="radio-item-text" onclick="radioAlarmEdit('${radioEsc(a.id)}')">
        <div class="radio-item-name">${time} <span class="radio-alarm-days">${radioDaysText(a.days)}</span>${a.label ? ' · ' + radioEsc(a.label) : ''}</div>
        <div class="radio-item-meta">📻 ${radioEsc(stationName(a.stationId))} · 🔊 ${a.volume}% · fade ${a.fadeMinutes} min · next: ${radioEsc(nextText)}</div>
      </div>
      <div class="radio-item-actions">
        <button class="btn btn-small" onclick="radioAlarmTest('${radioEsc(a.id)}')" title="Ring now">▶</button>
        <button class="btn btn-small" onclick="radioAlarmEdit('${radioEsc(a.id)}')" title="Edit">✏️</button>
        <button class="btn btn-small btn-danger" onclick="radioAlarmDelete('${radioEsc(a.id)}')" title="Delete">🗑</button>
      </div>
    </div>`;
  }).join('');
}

function radioAlarmEdit(id) {
  const a = radioState.alarms.find(x => x.id === id) ||
    { id: '', enabled: true, hour: 7, minute: 0, days: 0x1F, stationId: '', volume: 40, fadeMinutes: 5, label: '' };
  const form = document.getElementById('radio-alarm-form');
  if (!form) return;
  form.dataset.id = a.id;
  document.getElementById('radio-alarm-time').value =
    `${String(a.hour).padStart(2, '0')}:${String(a.minute).padStart(2, '0')}`;
  document.getElementById('radio-alarm-label').value = a.label || '';
  document.getElementById('radio-alarm-volume').value = a.volume;
  document.getElementById('radio-alarm-volume-val').textContent = a.volume + '%';
  document.getElementById('radio-alarm-fade').value = a.fadeMinutes;
  const sel = document.getElementById('radio-alarm-station');
  sel.innerHTML = '<option value="">Last station played</option>' +
    radioState.stations.map(s => `<option value="${radioEsc(s.id)}">${radioEsc(s.name)}</option>`).join('');
  sel.value = a.stationId || '';
  document.querySelectorAll('#radio-alarm-days input').forEach((cb, i) => { cb.checked = !!(a.days & (1 << i)); });
  form.style.display = '';
}

function radioAlarmCancel() {
  const form = document.getElementById('radio-alarm-form');
  if (form) form.style.display = 'none';
}

async function radioAlarmSubmit(ev) {
  ev.preventDefault();
  const form = ev.target;
  const [h, m] = (document.getElementById('radio-alarm-time').value || '07:00').split(':').map(Number);
  let days = 0;
  document.querySelectorAll('#radio-alarm-days input').forEach((cb, i) => { if (cb.checked) days |= 1 << i; });
  const old = radioState.alarms.find(x => x.id === form.dataset.id);
  const body = {
    id: form.dataset.id || '',
    enabled: old ? old.enabled : true,
    hour: h, minute: m, days,
    stationId: document.getElementById('radio-alarm-station').value,
    volume: +document.getElementById('radio-alarm-volume').value,
    fadeMinutes: +document.getElementById('radio-alarm-fade').value,
    label: document.getElementById('radio-alarm-label').value.trim()
  };
  try {
    await api.post('/api/alarms', body);
    window.toast.success('Alarm', `Set for ${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}`);
    radioAlarmCancel();
  } catch (e) {
    window.toast.error('Alarm', e.message);
  }
  loadRadioAlarms();
}

async function radioAlarmEnable(id, on) {
  const a = radioState.alarms.find(x => x.id === id);
  if (!a) return;
  try { await api.post('/api/alarms', { ...a, enabled: on }); } catch (e) { window.toast.error('Alarm', e.message); }
  loadRadioAlarms();
}

async function radioAlarmDelete(id) {
  if (!confirm('Delete this alarm?')) return;
  try { await api.del(`/api/alarms/${encodeURIComponent(id)}`); } catch (e) { window.toast.error('Alarm', e.message); }
  loadRadioAlarms();
}

async function radioAlarmTest(id) {
  try { await api.post(`/api/alarms/${encodeURIComponent(id)}/test`); } catch (e) { window.toast.error('Alarm', e.message); }
  setTimeout(() => { refreshRadioNow(); loadRadioAlarms(); }, 800);
}

async function radioAlarmStop() {
  try { await api.post('/api/alarms/stop'); } catch { /* ignore */ }
  loadRadioAlarms();
}

// ---- songs: heard and remembered ------------------------------------------------

async function loadRadioSongs() {
  try {
    const r = await api.get('/api/songs');
    radioState.songs = r;
    renderRadioSongs();
  } catch { /* ignore */ }
}

function radioSongRow(s, saved) {
  const at = new Date(s.at);
  const when = at.toLocaleString([], { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });
  const action = saved
    ? `<button class="btn btn-small btn-danger" onclick="radioForgetSong('${radioEsc(s.id)}')" title="Forget">🗑</button>`
    : `<button class="btn btn-small" data-title="${radioEsc(s.title)}" data-source="${radioEsc(s.source || '')}" onclick="radioRememberSong(this.dataset.title, this.dataset.source)" title="Remember">♥</button>`;
  return `
    <div class="radio-song">
      <div class="radio-song-text">
        <div class="radio-song-title">${radioEsc(s.title)}</div>
        <div class="radio-item-meta">${radioEsc(when)}${s.source ? ' · ' + radioEsc(s.source) : ''}</div>
      </div>
      <button class="btn btn-small" data-title="${radioEsc(s.title)}" onclick="radioFindSong(this.dataset.title)" title="Find on YouTube Music">🔍</button>
      ${action}
    </div>`;
}

function renderRadioSongs() {
  const r = radioState.songs || {};
  const saved = document.getElementById('radio-songs-saved');
  const heard = document.getElementById('radio-songs-heard');
  if (saved) {
    saved.innerHTML = (r.saved || []).length
      ? r.saved.map(s => radioSongRow(s, true)).join('')
      : '<div class="radio-empty">Nothing remembered yet. Tap the title on the panel, or press ♥.</div>';
  }
  if (heard) {
    heard.innerHTML = (r.history || []).length
      ? r.history.map(s => radioSongRow(s, false)).join('')
      : '<div class="radio-empty">Songs played on the radio show up here.</div>';
  }
}

async function radioRememberCurrent() {
  try {
    const r = await api.post('/api/songs/save');
    window.toast.success('Song', `Remembered "${r.song.title}"`);
  } catch (e) {
    window.toast.error('Song', e.message);
  }
  loadRadioSongs();
}

async function radioRememberSong(title, source) {
  try { await api.post('/api/songs/saved', { title, source }); } catch (e) { window.toast.error('Song', e.message); }
  loadRadioSongs();
}

async function radioForgetSong(id) {
  try { await api.del(`/api/songs/saved/${encodeURIComponent(id)}`); } catch { /* ignore */ }
  loadRadioSongs();
}

async function radioClearHeard() {
  if (!confirm('Clear the list of songs heard?')) return;
  try { await api.del('/api/songs/history'); } catch { /* ignore */ }
  loadRadioSongs();
}

// Opens Music, fills the search and runs it. The station after "·" is left out.
function radioFindSong(title) {
  if (typeof switchMediaPage === 'function') switchMediaPage('music');
  const input = document.getElementById('music-search-input');
  if (input) input.value = String(title || '').replace(/\s*·.*$/, '');
  if (typeof searchMusic === 'function') searchMusic();
}
