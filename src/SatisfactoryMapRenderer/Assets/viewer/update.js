/* Footer (version, copyright) and the update check against the project's GitHub releases. */
(() => {
  'use strict';

  const $ = id => document.getElementById(id);
  const renderer = window.MAP_RENDERER || {};
  const store = {
    get(key, fallback) { try { const v = localStorage.getItem('smr:' + key); return v === null ? fallback : JSON.parse(v); } catch { return fallback; } },
    set(key, value) { try { localStorage.setItem('smr:' + key, JSON.stringify(value)); } catch { /* storage unavailable */ } },
  };

  // "2026", from the next year on "2026–2027", "2026–2028", ...
  const FIRST_YEAR = 2026;
  const year = new Date().getFullYear();
  $('footer-years').textContent = year > FIRST_YEAR ? `${FIRST_YEAR}–${year}` : String(FIRST_YEAR);
  if (renderer.version) $('footer-version').textContent = `v${renderer.version}`;

  /** "v1.2.0-beta.3" → { numbers: [1, 2, 0], pre: ['beta', '3'] }; null if it is not a version. */
  function parse(version) {
    const m = /^v?(\d+(?:\.\d+)*)(?:-([0-9A-Za-z.-]+))?/.exec(String(version || '').trim());
    return m && { numbers: m[1].split('.').map(Number), pre: m[2] ? m[2].split('.') : [] };
  }

  /** Semantic version order: 1.2.0-beta.1 < 1.2.0-beta.2 < 1.2.0 < 1.2.1. */
  function compare(a, b) {
    for (let i = 0; i < Math.max(a.numbers.length, b.numbers.length); i++) {
      const d = (a.numbers[i] || 0) - (b.numbers[i] || 0);
      if (d) return d;
    }
    if (!a.pre.length || !b.pre.length) return b.pre.length - a.pre.length; // a full release beats its pre-releases
    for (let i = 0; i < Math.max(a.pre.length, b.pre.length); i++) {
      if (a.pre[i] === undefined) return -1;
      if (b.pre[i] === undefined) return 1;
      const na = /^\d+$/.test(a.pre[i]), nb = /^\d+$/.test(b.pre[i]);
      const d = na && nb ? a.pre[i] - b.pre[i] : na ? -1 : nb ? 1 : a.pre[i].localeCompare(b.pre[i]);
      if (d) return d;
    }
    return 0;
  }

  const CACHE_HOURS = 6; // GitHub allows 60 anonymous requests per hour and address
  async function releases(force) {
    const cached = store.get('releases', null);
    if (!force && cached && cached.repository === renderer.repository && Date.now() - cached.at < CACHE_HOURS * 3600e3) return cached.list;
    const response = await fetch(`https://api.github.com/repos/${renderer.repository}/releases?per_page=30`, { headers: { Accept: 'application/vnd.github+json' } });
    if (response.status === 404) return []; // nothing published yet
    if (!response.ok) throw new Error(response.status === 403 ? 'GitHub is limiting requests from this address; try again later.' : `GitHub answered with HTTP ${response.status}.`);
    const list = (await response.json()).filter(r => !r.draft).map(r => ({ tag: r.tag_name, prerelease: r.prerelease, url: r.html_url }));
    store.set('releases', { at: Date.now(), repository: renderer.repository, list });
    return list;
  }

  function show(state, pill, text) {
    $('update-status').dataset.state = state;
    $('update-pill').textContent = pill;
    $('update-text').textContent = text;
  }

  async function check(force) {
    const current = parse(renderer.version);
    $('update-banner').hidden = true;
    document.querySelector('#tabs [data-tab="settings"]').classList.remove('has-update');
    if (!current || !renderer.repository) return show('unknown', 'Unknown', 'This build has no version number.');
    if (!$('opt-updates').checked) return show('unknown', 'Off', `Version ${renderer.version}. The update check is switched off.`);
    show('unknown', 'Checking…', `Version ${renderer.version}`);
    try {
      const newest = (await releases(force))
        .filter(r => $('opt-prereleases').checked || !r.prerelease)
        .map(r => ({ ...r, parsed: parse(r.tag) }))
        .filter(r => r.parsed)
        .sort((a, b) => compare(b.parsed, a.parsed))[0];
      if (!newest || compare(newest.parsed, current) <= 0) return show('ok', 'Up to date', `Version ${renderer.version} is the newest${$('opt-prereleases').checked ? '' : ' release'}.`);
      const name = newest.tag.replace(/^v/, '');
      show('update', 'Update available', `Version ${name}${newest.prerelease ? ' (pre-release)' : ''} is out; you have ${renderer.version}.`);
      $('update-banner').href = newest.url;
      $('update-banner-version').textContent = `${name}${newest.prerelease ? ' (pre-release)' : ''}`;
      $('update-banner').hidden = false;
      document.querySelector('#tabs [data-tab="settings"]').classList.add('has-update');
    } catch (e) {
      show('error', 'Not checked', e instanceof TypeError ? 'GitHub could not be reached.' : e.message);
    }
  }

  $('opt-updates').checked = store.get('updates', true);
  $('opt-prereleases').checked = store.get('prereleases', false);
  $('opt-updates').addEventListener('change', () => { store.set('updates', $('opt-updates').checked); check(false); });
  $('opt-prereleases').addEventListener('change', () => { store.set('prereleases', $('opt-prereleases').checked); check(false); });
  $('update-check').addEventListener('click', () => check(true));

  // Updates concern whoever runs the renderer: source.js calls this for admins and for pages opened from disk.
  window.satisfactoryMapUpdates = () => { $('about').hidden = false; check(false); };
})();
