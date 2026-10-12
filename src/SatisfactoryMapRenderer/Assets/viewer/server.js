/* The Server tab: what a dedicated server says about itself, and for admins a console and shutdown.
   Only there when the save game comes from a dedicated server's API. */
(async () => {
  'use strict';

  if (location.protocol === 'file:') return;
  const $ = id => document.getElementById(id);
  const esc = s => String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const tab = document.querySelector('#tabs [data-tab="server"]'), panel = document.querySelector('.tab-panel[data-panel="server"]');

  async function api(path, options = {}) {
    const response = await fetch(path, { ...options, headers: { 'X-Map-Renderer': '1', ...(options.headers || {}) } });
    const body = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(body.error || `The renderer answered with HTTP ${response.status}.`);
    return body;
  }

  let source;
  try { source = await api('/api/source'); } catch { return; }
  if (source.kind !== 'server') {
    // a tab remembered from when the source was a server
    if (!panel.hidden) document.querySelector('#tabs [data-tab="layers"]').click();
    return;
  }
  tab.hidden = false;
  $('server-admin').hidden = !source.admin;

  // The game writes names as "NumConnectedPlayers" in its documentation and "numConnectedPlayers" on the wire.
  const field = (object, name) => {
    if (!object) return undefined;
    const key = Object.keys(object).find(k => k.toLowerCase() === name.toLowerCase());
    return key === undefined ? undefined : object[key];
  };
  const duration = seconds => {
    const h = Math.floor(seconds / 3600), m = Math.floor(seconds % 3600 / 60);
    return h >= 24 ? `${Math.floor(h / 24)} d ${h % 24} h` : `${h} h ${String(m).padStart(2, '0')} min`;
  };
  // "/Game/…/Schematic_5-1.Schematic_5-1_C" → "Schematic 5-1"; "…GP_Project_Assembly_Phase_3" → "Project Assembly Phase 3"
  const assetName = path => String(path || '').replace(/['"]/g, '').split(/[./]/).filter(Boolean).pop()?.replace(/_C$/, '').replace(/^(GP|EGP)_/, '').replace(/_/g, ' ') || '';
  const rows = pairs => pairs.filter(([, value]) => value !== undefined && value !== null && value !== '')
    .map(([label, value]) => `<tr><td>${esc(label)}</td><td>${esc(value)}</td></tr>`).join('');
  // "FG.DSAutoPause" → "DS Auto Pause"
  const optionName = key => key.replace(/^FG\./, '').replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2').replace(/([a-z0-9])([A-Z])/g, '$1 $2');

  let failures = 0;
  async function refresh() {
    let stats;
    try { stats = await api('/api/serverstats'); } catch (e) { stats = { available: true, error: e.message }; }
    if (!stats.available) return;
    const health = $('server-health'), state = field(stats, 'state');
    $('server-name').hidden = !stats.name;
    $('server-name').textContent = stats.name || '';
    $('server-error').hidden = !stats.error;
    $('server-error').textContent = stats.error ? `The server does not answer: ${stats.error}` : '';
    health.hidden = false;
    health.textContent = stats.error ? 'Not reachable' : stats.health === 'healthy' ? 'Healthy' : stats.health === 'slow' ? 'Slow' : (stats.health || 'Unknown');
    health.dataset.state = stats.error ? 'down' : stats.health === 'healthy' ? 'ok' : 'slow';
    if (stats.error) { failures++; } else { failures = 0; }

    if (state) {
      const players = field(state, 'numConnectedPlayers'), limit = field(state, 'playerLimit'), tick = field(state, 'averageTickRate');
      const running = field(state, 'isGameRunning'), paused = field(state, 'isGamePaused');
      $('server-state').innerHTML = rows([
        ['Status', running === false ? 'No game loaded' : paused ? 'Paused' : 'Running'],
        ['Session', field(state, 'activeSessionName')],
        ['Players', players === undefined ? undefined : `${players} of ${limit ?? '?'}`],
        ['Tech tier', field(state, 'techTier')],
        ['Game phase', assetName(field(state, 'gamePhase'))],
        ['Active milestone', assetName(field(state, 'activeSchematic'))],
        ['Play time', Number.isFinite(field(state, 'totalGameDuration')) ? duration(field(state, 'totalGameDuration')) : undefined],
        ['Tick rate', Number.isFinite(tick) ? `${tick.toFixed(1)} per second` : undefined],
        ['Loads at start', field(state, 'autoLoadSessionName')],
      ]);
    } else if (stats.error) {
      $('server-state').innerHTML = '';
    }

    const options = field(stats, 'options'), pending = field(stats, 'pendingOptions') || {};
    if (options) {
      $('server-options').innerHTML = rows(Object.keys(options).sort().map(key => [optionName(key), options[key]]));
      const waiting = Object.keys(pending).filter(key => pending[key] !== options[key]);
      $('server-pending').hidden = waiting.length === 0;
      $('server-pending').textContent = waiting.length ? `After a restart: ${waiting.map(key => `${optionName(key)} = ${pending[key]}`).join(', ')}` : '';
    }
    $('server-updated').textContent = `Updated ${new Date().toLocaleTimeString()}`;
  }

  // Asked only while someone looks at the tab.
  let timer = 0;
  const watch = () => {
    clearInterval(timer);
    if (panel.hidden || document.hidden) return;
    refresh();
    timer = setInterval(refresh, 10000);
  };
  new MutationObserver(watch).observe(panel, { attributes: true, attributeFilter: ['hidden'] });
  document.addEventListener('visibilitychange', watch);
  watch();

  if (!source.admin) return;

  // ---------------------------------------------------------------- admin: console and shutdown

  const output = $('server-output');
  $('server-command-form').addEventListener('submit', async e => {
    e.preventDefault();
    const command = $('server-command').value.trim();
    if (!command) return;
    output.hidden = false;
    output.textContent = `> ${command}\n…`;
    try {
      const answer = await api('/api/server/command', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ command }) });
      output.textContent = `> ${command}\n${answer.output || '(no output)'}`;
      $('server-command').select();
    } catch (error) {
      output.textContent = `> ${command}\n${error.message}`;
    }
  });

  // The commands and settings that matter for a Satisfactory server, asked from the server when the list is
  // opened for the first time. What each one does is not something the server tells; that is written here.
  const ABOUT = {
    'server.savegame': 'Saves the game now. Add a name for the save file.',
    'server.generateapitoken': 'Makes a new API token for tools like this map.',
    'server.invalidateapitokens': 'Revokes every API token, the one this map uses included.',
    'fg.autosaveinterval': 'Seconds between two autosaves.',
    'fg.dsautopause': 'Pauses the game while nobody is connected.',
    'fg.dsautosaveondisconnect': 'Saves the game when a player leaves.',
    'fg.networkquality': 'Network quality, 0 (low) to 3 (ultra).',
    'fg.serverrestarttimeslot': 'Time of the daily restart, in minutes after midnight (UTC).',
    'fg.sendgameplaydata': 'Sends gameplay data to Coffee Stain Studios.',
    'fg.disableseasonalevents': 'Switches the seasonal events (FICSMAS) off.',
    'fg.enableseasonalevents': 'Switches the seasonal events (FICSMAS) on.',
  };
  const list = $('server-list'), items = $('server-list-items');
  let known = null;
  list.addEventListener('toggle', async () => {
    if (!list.open || known) return;
    try {
      known = await api('/api/server/commands', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' });
      const entries = [...known.commands.map(name => [name, null]), ...known.settings];
      items.innerHTML = entries.map(([name, value]) => {
        const about = ABOUT[name.toLowerCase()];
        return `<li><button type="button" data-name="${esc(name)}"><span><b>${esc(name)}</b>${about ? `<small>${esc(about)}</small>` : ''}</span>`
          + `${value === null ? '<em>command</em>' : `<code>${esc(value)}</code>`}</button></li>`;
      }).join('') || '<li class="none">The server lists none.</li>';
      $('server-list-status').hidden = true;
      $('server-list-body').hidden = false;
    } catch (error) {
      $('server-list-status').textContent = `The list could not be read: ${error.message}`;
    }
  });
  items.addEventListener('click', e => {
    const button = e.target.closest('button[data-name]');
    if (!button) return;
    $('server-command').value = button.dataset.name + ' ';
    $('server-command').focus();
  });

  async function shutDown(question, done) {
    if (!confirm(question)) return;
    output.hidden = false;
    output.textContent = 'Shutting the server down…';
    try {
      await api('/api/server/shutdown', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' });
      output.textContent = done;
    } catch (error) {
      output.textContent = error.message;
    }
    setTimeout(refresh, 4000);
  }
  $('server-restart').addEventListener('click', () => shutDown(
    'Restart the game server?\n\nIt is shut down and everyone playing is disconnected. It only comes back if its host restarts it (Docker restart policy, service).',
    'The server was told to shut down. It comes back in a minute or two if its host restarts it.'));
  $('server-stop').addEventListener('click', () => shutDown(
    'Stop the game server?\n\nEveryone playing is disconnected. If its host restarts servers automatically, it will come back; otherwise it has to be started on the host.',
    'The server was told to shut down.'));
})();
