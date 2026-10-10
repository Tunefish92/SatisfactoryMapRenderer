/* "Save game source" panel. Needs the renderer's server mode (--serve); a page opened from disk only says so. */
(async () => {
  'use strict';

  // The panel is for admins. Visitors of a served map do not see it, only an "Admin login" link at the bottom.
  const $ = id => document.getElementById(id);
  const current = $('source-current'), status = $('source-status'), controls = $('source-controls');
  const data = window.MAP_DATA || {};
  const esc = s => String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

  async function api(path, options = {}) {
    const response = await fetch(path, { ...options, headers: { 'X-Map-Renderer': '1', ...(options.headers || {}) } });
    const body = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(body.error || `The renderer answered with HTTP ${response.status}.`);
    return body;
  }
  const postJson = (path, body) => api(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });

  // What the "No map yet" placeholder tells this visitor to do.
  const placeholder = (text, canLoad) => {
    $('placeholder-text').textContent = text;
    $('placeholder-browse').hidden = $('placeholder-steps').hidden = !canLoad;
  };

  // Status bar: where the save came from.
  const SOURCE_LABEL = { file: 'Local file', server: 'Server API', ssh: 'SSH' };
  const showOrigin = s => {
    const pill = $('status-source');
    pill.textContent = SOURCE_LABEL[s.kind] || 'Local file';
    pill.dataset.kind = s.kind || 'file';
    pill.title = s.loadedAt ? `Loaded ${new Date(s.loadedAt).toLocaleString()}` : '';
    pill.hidden = false;
  };

  let source;
  try {
    if (location.protocol === 'file:') throw new Error('opened from disk');
    source = await api('/api/source');
  } catch {
    showOrigin({});
    window.satisfactoryMapUpdates?.();
    $('source').hidden = false;
    current.textContent = data.fileName ? `File: ${data.fileName}` : 'No save loaded.';
    $('source-offline').hidden = false;
    placeholder('Render a save game with the command line tool, or start it with --serve to choose one here.', false);
    return;
  }

  showOrigin(source);
  if (!source.admin) {
    placeholder('No save game has been loaded so far. An admin can choose one after logging in from the side panel.', false);
    $('admin').hidden = false;
    $('admin-open').addEventListener('click', () => {
      $('admin-form').hidden = !$('admin-form').hidden;
      if (!$('admin-form').hidden) $('admin-password').focus();
    });
    $('admin-form').addEventListener('submit', async e => {
      e.preventDefault();
      try {
        await postJson('/api/login', { password: $('admin-password').value });
        location.reload();
      } catch (err) {
        $('admin-error').hidden = false;
        $('admin-error').textContent = err.message;
        $('admin-password').select();
      }
    });
    return;
  }
  window.satisfactoryMapUpdates?.();
  $('source').hidden = false;
  controls.hidden = false;
  if (source.loginRequired) {
    $('admin-logout').hidden = false;
    $('admin-logout').addEventListener('click', async () => { await api('/api/logout', { method: 'POST' }).catch(() => {}); location.reload(); });
  }

  const KIND = { file: 'File', server: 'Dedicated server', ssh: 'SSH' };
  const COMPATIBILITY = { ok: 'Compatible', untested: 'Newer than tested', unsupported: 'Not supported', unknown: 'Version unknown' };

  function showSource(s) {
    source = s;
    const loaded = s.loadedAt ? ` · loaded ${new Date(s.loadedAt).toLocaleString()}` : '';
    current.innerHTML = s.kind
      ? `<b>${KIND[s.kind] || s.kind}:</b> ${esc(s.name)}<span class="muted">${esc(loaded)}</span>`
      : (data.fileName ? `<b>File:</b> ${esc(data.fileName)}` : 'No save loaded yet. Choose one below.');
    // Only a save fetched from a server can be fetched again.
    $('source-refresh').hidden = $('status-refresh').hidden = !(s.kind === 'server' || s.kind === 'ssh');
    $('server-versions').textContent = s.supportedVersions || '';
    if (s.kind === 'server' && s.serverInfo) showServerInfo(s.compatibility, s.serverInfo, '');
  }

  function showServerInfo(compatibility, info, message) {
    const box = $('server-info');
    box.hidden = false;
    box.dataset.state = compatibility || 'unknown';
    box.innerHTML = `<span class="pill">${COMPATIBILITY[compatibility] || COMPATIBILITY.unknown}</span>` +
      (info ? `<div>${esc(info)}</div>` : '') + (message ? `<div class="muted">${esc(message)}</div>` : '');
  }

  function setStatus(text, kind) {
    status.hidden = !text;
    status.textContent = text || '';
    status.dataset.kind = kind || '';
  }

  // ---- progress modal: what the renderer is doing with the save, in the middle of the page
  // Share of the bar at which each step begins; the renderer's percentages are fixed per step.
  const STEPS = [['fetch', 0], ['read', 25], ['find', 36], ['draw', 43]];
  const modal = $('load-modal');
  let modalStarted = 0, modalClock = 0;

  function openModal(title) {
    $('load-title').textContent = title;
    $('load-error').hidden = $('load-close').hidden = true;
    modal.classList.remove('failed');
    modal.hidden = false;
    modalStarted = Date.now();
    clearInterval(modalClock);
    modalClock = setInterval(() => { $('load-elapsed').textContent = `${Math.round((Date.now() - modalStarted) / 1000)} s`; }, 500);
    $('load-elapsed').textContent = '0 s';
    showProgress('Starting…', null);
  }

  /** @param percent null while the renderer has not answered yet: the bar just runs. */
  function showProgress(stage, percent) {
    const track = modal.querySelector('.progress-track');
    $('progress-stage').textContent = stage;
    track.classList.toggle('running', percent === null);
    $('progress-bar').style.width = percent === null ? '' : `${percent}%`;
    $('progress-percent').textContent = percent === null ? '' : `${percent} %`;
    if (percent === null) track.removeAttribute('aria-valuenow'); else track.setAttribute('aria-valuenow', percent);
    const current = percent === null ? -1 : STEPS.findLastIndex(([, from]) => percent >= from);
    modal.querySelectorAll('.load-steps li').forEach((li, i) => {
      li.dataset.state = percent === 100 || i < current ? 'done' : i === current ? 'active' : 'pending';
    });
  }

  function failModal(message) {
    clearInterval(modalClock);
    modal.classList.add('failed');
    modal.querySelector('.progress-track').classList.remove('running');
    $('load-title').textContent = 'The save game could not be loaded';
    $('progress-stage').textContent = '';
    $('load-error').textContent = message;
    $('load-error').hidden = $('load-close').hidden = false;
    $('load-close').focus();
  }
  $('load-close').addEventListener('click', () => { modal.hidden = true; });

  /** Follows the renderer's progress until it is done; resolves when nothing is being loaded any more. */
  function followProgress(isActive) {
    let highest = 0;
    return setInterval(async () => {
      try {
        const p = await api('/api/progress');
        if (!isActive() || !p.busy || !p.stage) return;
        highest = Math.max(highest, p.percent); // never runs backwards, even if answers arrive out of order
        showProgress(`${p.stage}…`, highest);
      } catch { /* the next poll will do */ }
    }, 400);
  }

  let busy = false;
  /** Runs one load; the page is reloaded afterwards because the whole map has been rendered anew. */
  async function load(what, action) {
    if (busy) return;
    busy = true;
    setStatus('', '');
    openModal(what);
    const poll = followProgress(() => busy);
    try {
      await action();
      showProgress('Done. Opening the map…', 100);
      location.reload();
    } catch (e) {
      failModal(e.message);
    } finally {
      clearInterval(poll);
      busy = false;
    }
  }

  // ---- file: browse and drag & drop
  const upload = file => {
    if (!file) return;
    if (!/\.sav$/i.test(file.name)) { setStatus('Please choose a Satisfactory save file (.sav).', 'error'); return; }
    load(`Loading ${file.name}`, () => api('/api/upload?name=' + encodeURIComponent(file.name), { method: 'POST', body: file }));
  };
  $('source-browse').addEventListener('click', () => $('source-file').click());
  $('placeholder-browse').addEventListener('click', () => $('source-file').click());
  placeholder('Load a save game to see your factory on the map.', true);
  $('source-file').addEventListener('change', e => { upload(e.target.files[0]); e.target.value = ''; });

  // Dropping works anywhere on the page; the drop zone only shows where to aim.
  const hasFiles = e => [...(e.dataTransfer?.types || [])].includes('Files');
  let dragDepth = 0;
  window.addEventListener('dragenter', e => { if (hasFiles(e)) { e.preventDefault(); dragDepth++; document.body.classList.add('dragging-file'); } });
  window.addEventListener('dragover', e => { if (hasFiles(e)) e.preventDefault(); });
  window.addEventListener('dragleave', e => { if (hasFiles(e) && --dragDepth <= 0) { dragDepth = 0; document.body.classList.remove('dragging-file'); } });
  window.addEventListener('drop', e => {
    if (!hasFiles(e)) return;
    e.preventDefault();
    dragDepth = 0;
    document.body.classList.remove('dragging-file');
    upload(e.dataTransfer.files[0]);
  });

  // ---- dedicated server
  const serverSettings = () => ({
    host: $('server-host').value.trim(),
    port: +$('server-port').value || 7777,
    token: $('server-token').value,
    verifyCertificate: $('server-verify').checked,
    saveFirst: $('server-savefirst').checked,
  });
  $('server-host').value = source.host || '';
  $('server-port').value = source.port || 7777;
  $('server-verify').checked = !!source.verifyCertificate;
  $('server-savefirst').checked = source.saveFirst !== false;
  if (source.hasToken) $('server-token').placeholder = 'saved – leave empty to keep it';

  /** Stores the form's settings in the renderer; they are also stored whenever they are checked or used. */
  async function saveSettings(form, path, settings) {
    if (!form.reportValidity()) return;
    try {
      const s = await postJson(path, settings);
      if (s.hasToken) { $('server-token').value = ''; $('server-token').placeholder = 'saved – leave empty to keep it'; }
      setStatus('Settings saved.', 'ok');
    } catch (e) {
      setStatus(e.message, 'error');
    }
  }
  $('server-save').addEventListener('click', () => saveSettings($('server-form'), '/api/server/settings', serverSettings()));

  $('server-check').addEventListener('click', async () => {
    if (!$('server-form').reportValidity()) return;
    showServerInfo('unknown', '', 'Asking the server…');
    try {
      const c = await postJson('/api/server/check', serverSettings());
      const info = [c.serverName && `Server "${c.serverName}"`, c.gameVersion && `game version ${c.gameVersion}`,
        c.saveVersion && `save version ${c.saveVersion}`, c.build && `build ${c.build}`, c.session && `session "${c.session}"`].filter(Boolean).join(' · ');
      showServerInfo(c.compatibility, info, c.message);
    } catch (e) {
      showServerInfo('unsupported', '', e.message);
      $('server-info').querySelector('.pill').textContent = 'Not reachable';
    }
  });
  $('server-form').addEventListener('submit', e => {
    e.preventDefault();
    const s = serverSettings();
    load(`Fetching the save game from ${s.host}`, () => postJson('/api/server', s));
  });

  // ---- ssh
  $('ssh-target').value = source.sshTarget || '';
  $('ssh-port').value = source.sshPort || 22;
  $('ssh-path').value = source.sshPath || '';
  const sshSettings = () => ({ target: $('ssh-target').value.trim(), port: +$('ssh-port').value || 22, path: $('ssh-path').value.trim() });
  $('ssh-save').addEventListener('click', () => saveSettings($('ssh-form'), '/api/ssh/settings', sshSettings()));
  $('ssh-form').addEventListener('submit', e => {
    e.preventDefault();
    const s = sshSettings();
    load(`Copying the save game from ${s.target}`, () => postJson('/api/ssh', s));
  });

  const refresh = () =>
    load(source.kind === 'server' ? `Fetching a new snapshot from ${source.host}` : `Copying the save game from ${source.sshTarget}`, () => api('/api/refresh', { method: 'POST' }));
  $('source-refresh').addEventListener('click', refresh);
  $('status-refresh').addEventListener('click', refresh); // the same, one click away in the status bar

  // Open the forms that are in use or already filled in.
  if (source.kind === 'server' || source.host) $('source-server').open = true;
  if (source.kind === 'ssh' || source.sshTarget) $('source-ssh').open = true;
  showSource(source);
  // Opened while someone else's load is running: show it, and pick up the new map when it is done.
  if (source.busy) {
    openModal('A save game is being loaded');
    const poll = followProgress(() => true);
    const done = setInterval(async () => {
      const p = await api('/api/progress').catch(() => null);
      if (p && !p.busy) { clearInterval(poll); clearInterval(done); location.reload(); }
    }, 1000);
  }
})();
