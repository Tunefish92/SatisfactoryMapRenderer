/*
 * The map being rendered again at start (after an update of the server): until it is done the page shows how
 * far it has come, with the lines the server writes to its console, and nothing behind it can be used. Once it
 * is done, OK loads the new map.
 */
(() => {
  'use strict';

  if (location.protocol === 'file:') return; // opened from disk: no server that renders

  let modal = null, from = 0, highest = 0, timer = 0;
  const started = Date.now();

  function build() {
    modal = document.createElement('div');
    modal.id = 'startup-modal';
    modal.setAttribute('role', 'dialog');
    modal.setAttribute('aria-modal', 'true');
    modal.setAttribute('aria-labelledby', 'startup-title');
    modal.innerHTML = `
      <div class="startup-card">
        <header>
          <img class="load-logo" src="logo.svg" width="52" height="52" alt="">
          <div>
            <p class="news-kicker">Updated</p>
            <h2 id="startup-title">Bringing the map up to date</h2>
          </div>
        </header>
        <div class="startup-body">
          <p class="startup-intro">The server was updated and renders the map again, so that it shows everything the new version can. The map can be used again once this is done.</p>
          <p id="startup-stage" class="load-stage"></p>
          <div class="progress-track" role="progressbar" aria-valuemin="0" aria-valuemax="100" aria-labelledby="startup-stage">
            <div id="startup-bar" class="progress-bar"></div>
          </div>
          <div class="load-meta"><span id="startup-percent"></span><span id="startup-elapsed"></span></div>
          <pre id="startup-log" class="startup-log" aria-live="polite"></pre>
        </div>
        <footer>
          <p id="startup-done" class="startup-done" role="status"></p>
          <button type="button" id="startup-ok" class="primary" disabled>OK</button>
        </footer>
      </div>`;
    // everything behind it stays out of reach of mouse, keyboard and screen readers
    for (const element of document.body.children) element.inert = true;
    document.body.appendChild(modal);
    document.addEventListener('keydown', e => { if (e.key === 'Escape' && modal) e.preventDefault(); }, true);
    modal.querySelector('#startup-ok').addEventListener('click', () => location.reload());
  }

  const elapsed = () => {
    const s = Math.round((Date.now() - started) / 1000);
    return s < 60 ? `${s} s` : `${Math.floor(s / 60)} min ${s % 60} s`;
  };

  function show(state) {
    if (!modal) build();
    const log = modal.querySelector('#startup-log');
    const atEnd = log.scrollHeight - log.scrollTop - log.clientHeight < 24;
    if (state.lines.length) log.textContent += (log.textContent ? '\n' : '') + state.lines.join('\n');
    if (atEnd) log.scrollTop = log.scrollHeight;
    from = state.from + state.lines.length;

    highest = Math.max(highest, state.percent || 0); // never runs backwards
    const done = !state.running && state.finished;
    modal.querySelector('#startup-stage').textContent = done
      ? (state.failed ? 'The map could not be rendered again.' : 'The map is ready.')
      : `${state.stage || 'Starting'}…`;
    modal.querySelector('#startup-bar').style.width = `${done && !state.failed ? 100 : highest}%`;
    modal.querySelector('#startup-percent').textContent = `${done && !state.failed ? 100 : highest} %`;
    modal.querySelector('#startup-elapsed').textContent = elapsed();
    if (done) {
      modal.classList.toggle('failed', state.failed);
      modal.querySelector('#startup-done').textContent = state.failed
        ? 'The map stays as the older version made it. An admin can load or refresh the save game in the side panel.'
        : 'Done. OK opens the new map.';
      const ok = modal.querySelector('#startup-ok');
      ok.disabled = false;
      ok.focus();
    }
    return done;
  }

  async function poll() {
    try {
      const response = await fetch(`/api/startup?from=${from}`, { cache: 'no-store' });
      if (!response.ok) return; // a server of an older version, or none
      const state = await response.json();
      // A page opened after the render was over has nothing to wait for.
      if (!modal && !state.running) return;
      if (show(state)) return;
    } catch {
      if (!modal) return;
      // the server is busy or restarting: keep asking
    }
    timer = setTimeout(poll, 700);
  }

  poll();
  window.addEventListener('pagehide', () => clearTimeout(timer));
})();
