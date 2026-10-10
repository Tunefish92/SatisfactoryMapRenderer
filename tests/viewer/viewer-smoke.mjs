// Browser smoke test for a generated map: node tests/viewer/viewer-smoke.mjs <output>/index.html [screenshot.png]
// Drives headless Edge/Chrome through the DevTools protocol with real mouse and keyboard input.
import { spawn } from 'node:child_process';
import { mkdtempSync, rmSync, writeFileSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const [page, screenshot] = process.argv.slice(2);
if (!page || !existsSync(page)) {
  console.error('usage: node viewer-smoke.mjs <path to index.html> [screenshot.png]');
  process.exit(2);
}

const browsers = [
  process.env.BROWSER,
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  'C:/Program Files/Google/Chrome/Application/chrome.exe',
  '/usr/bin/google-chrome',
  '/usr/bin/chromium',
].filter(Boolean);
const browserPath = browsers.find(p => existsSync(p));
if (!browserPath) { console.error('No Chromium-based browser found; set BROWSER.'); process.exit(2); }

const port = 9300 + Math.floor(Math.random() * 500);
const profile = mkdtempSync(join(tmpdir(), 'smr-viewer-'));
const browser = spawn(browserPath, [
  '--headless=new', `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`,
  '--window-size=1400,900', '--no-first-run', '--disable-gpu', 'about:blank',
], { stdio: 'ignore' });

const sleep = ms => new Promise(r => setTimeout(r, ms));
let ws, nextId = 1;
const pending = new Map();
const errors = [];
const results = [];

async function connect() {
  for (let i = 0; i < 50; i++) {
    try {
      const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
      const target = targets.find(t => t.type === 'page');
      if (target) return target.webSocketDebuggerUrl;
    } catch { /* browser still starting */ }
    await sleep(200);
  }
  throw new Error('browser did not start');
}

function send(method, params = {}) {
  const id = nextId++;
  ws.send(JSON.stringify({ id, method, params }));
  return new Promise((resolve, reject) => pending.set(id, { resolve, reject }));
}

async function evaluate(expression) {
  const r = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
  if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description ?? r.exceptionDetails.text);
  return r.result.value;
}

async function click(x, y) {
  for (const type of ['mousePressed', 'mouseReleased']) {
    await send('Input.dispatchMouseEvent', { type, x, y, button: 'left', clickCount: 1 });
  }
  await sleep(300);
}

async function check(name, fn) {
  try {
    const detail = await fn();
    results.push({ name, ok: true, detail });
  } catch (e) {
    results.push({ name, ok: false, detail: e.message });
  }
}

function assert(condition, message) { if (!condition) throw new Error(message); }

/** Container pixel of a game position, centring the map there first. */
const pointOf = (x, y, zoom) => evaluate(`(() => {
  const m = window.satisfactoryMap;
  m.setView([${y}, ${x}], ${zoom}, { animate: false });
  const p = m.latLngToContainerPoint([${y}, ${x}]);
  const r = document.getElementById('map').getBoundingClientRect();
  return { x: p.x + r.left, y: p.y + r.top };
})()`);

try {
  ws = new WebSocket(await connect());
  await new Promise(r => ws.addEventListener('open', r, { once: true }));
  ws.addEventListener('message', ev => {
    const msg = JSON.parse(ev.data);
    if (msg.id && pending.has(msg.id)) {
      const p = pending.get(msg.id);
      pending.delete(msg.id);
      msg.error ? p.reject(new Error(msg.error.message)) : p.resolve(msg.result);
    } else if (msg.method === 'Runtime.exceptionThrown') {
      errors.push(msg.params.exceptionDetails.exception?.description ?? msg.params.exceptionDetails.text);
    } else if (msg.method === 'Runtime.consoleAPICalled' && msg.params.type === 'error') {
      errors.push(msg.params.args.map(a => a.value ?? a.description).join(' '));
    } else if (msg.method === 'Log.entryAdded' && msg.params.entry.level === 'error') {
      errors.push(`${msg.params.entry.text} ${msg.params.entry.url ?? ''}`);
    }
  });
  await send('Runtime.enable');
  await send('Log.enable');
  await send('Page.enable');
  // The test must not depend on the network: switch the viewer's update check (a request to GitHub) off.
  await send('Page.addScriptToEvaluateOnNewDocument', { source: "try { localStorage.setItem('smr:updates', 'false'); } catch {}" });
  await send('Page.navigate', { url: pathToFileURL(resolve(page)).href });
  await sleep(2500);

  const data = await evaluate(`({
    title: MAP_DATA.title,
    players: MAP_DATA.markers.players,
    stations: MAP_DATA.markers.stations,
    inspect: MAP_DATA.inspect.slice(0, 50),
    rasterLayers: MAP_DATA.layers.filter(l => l.tiles).map(l => l.id),
  })`);

  await check('page loads without script errors', () => {
    assert(errors.length === 0, errors.join(' | '));
    return 'no errors';
  });

  await check('title and sidebar are populated', async () => {
    const s = await evaluate(`({
      title: document.getElementById('title').textContent,
      layers: document.querySelectorAll('input[data-layer]').length,
      summary: document.querySelectorAll('#summary tr').length,
      counts: document.querySelectorAll('#counts tr').length })`);
    assert(s.title === data.title, `title ${s.title}`);
    assert(s.layers >= 20 && s.summary >= 8 && s.counts > 10, JSON.stringify(s));
    return JSON.stringify(s);
  });

  await check('tiles load for every visible raster layer', async () => {
    await evaluate(`void window.satisfactoryMap.setView(window.satisfactoryMap.getCenter(), 4)`);
    await sleep(1500);
    const t = await evaluate(`(() => {
      const imgs = [...document.querySelectorAll('.leaflet-tile-container img')];
      return { total: imgs.length, broken: imgs.filter(i => i.complete && i.naturalWidth === 0).map(i => i.src).slice(0, 3) };
    })()`);
    assert(t.total > 0, 'no tile images on screen');
    assert(t.broken.length === 0, 'broken tiles: ' + t.broken.join(', '));
    return `${t.total} tiles, none broken`;
  });

  await check('toggling a layer adds and removes its tiles', async () => {
    const count = () => evaluate(`document.querySelectorAll('.tiles-power img').length`);
    const box = `document.querySelector('input[data-layer="power"]')`;
    const before = await count();
    await evaluate(`${box}.click()`);
    await sleep(1200);
    const on = await count();
    await evaluate(`${box}.click()`);
    await sleep(300);
    const off = await count();
    assert(before === 0 && on > 0 && off === 0, `power tiles ${before} → ${on} → ${off}`);
    return `power tiles 0 → ${on} → 0`;
  });

  await check('clicking a player marker opens its popup', async () => {
    assert(data.players.length > 0, 'save has no players');
    const [x, y, , name] = data.players[0];
    const p = await pointOf(x, y, 7);
    await click(p.x, p.y);
    const text = await evaluate(`document.querySelector('.leaflet-popup-content')?.innerText ?? ''`);
    assert(text.includes(name), `popup: "${text}"`);
    return text.split('\n').slice(0, 2).join(' / ');
  });

  await check('clicking a building on the tiles shows its details', async () => {
    const building = data.inspect.find(b => b[2] >= 300) ?? data.inspect[0];
    assert(building, 'no clickable buildings');
    await evaluate(`void window.satisfactoryMap.closePopup()`);
    const p = await pointOf(building[0], building[1], 8);
    await click(p.x, p.y);
    const text = await evaluate(`document.querySelector('.leaflet-popup-content')?.innerText ?? ''`);
    assert(text.includes(building[3]), `expected "${building[3]}", popup: "${text}"`);
    return text.split('\n').slice(0, 3).join(' / ');
  });

  await check('the URL hash follows the view and restores it', async () => {
    const hash = await evaluate(`location.hash`);
    assert(/^#8\/-?\d+\/-?\d+$/.test(hash), `hash ${hash}`);
    await evaluate(`location.hash = '#5/1000/-500'`);
    await sleep(500);
    const c = await evaluate(`(() => { const m = window.satisfactoryMap; return [m.getZoom(), Math.round(m.getCenter().lng / 100), Math.round(m.getCenter().lat / 100)]; })()`);
    assert(c.join() === '5,1000,-500', `view ${c}`);
    return `${hash} → #5/1000/-500 restored`;
  });

  await check('search finds a station and flies to it', async () => {
    const target = data.stations[0] ?? data.players[0];
    const query = target[3].slice(0, 6);
    await evaluate(`(() => { const s = document.getElementById('search'); s.focus(); s.value = ''; })()`);
    await send('Input.insertText', { text: query });
    await sleep(300);
    const hits = await evaluate(`[...document.querySelectorAll('#search-results li')].map(li => li.innerText.replace(/\\n/g, ' '))`);
    assert(hits.some(h => h.includes(target[3])), `results for "${query}": ${hits.join(' | ')}`);
    await evaluate(`[...document.querySelectorAll('#search-results li')].find(li => li.innerText.includes(${JSON.stringify(target[3])})).click()`);
    await sleep(500);
    // The popup auto-pans the map, so check the target is on screen rather than exactly centred.
    const onScreen = await evaluate(`window.satisfactoryMap.getBounds().contains([${target[1]}, ${target[0]}])`);
    const popup = await evaluate(`document.querySelector('.leaflet-popup-content')?.innerText ?? ''`);
    assert(onScreen && popup.includes(target[3]), `on screen ${onScreen}, popup "${popup}"`);
    return `"${query}" → ${hits.length} result(s), showing ${target[3]}`;
  });

  await check('legend filter hides one resource type', async () => {
    const visible = `(() => { let n = 0; window.satisfactoryMap.eachLayer(l => { if (l instanceof L.CircleMarker && l._map) n++; }); return n; })()`;
    const before = await evaluate(visible);
    const row = `[...document.querySelectorAll('input[data-layer="resources"]')][0].closest('.layer-row')`;
    await evaluate(`${row}.querySelector('.expand').click()`);
    const sub = `${row}.nextElementSibling.querySelector('input[data-sub="0"]')`;
    await evaluate(`${sub}.click()`);
    const after = await evaluate(visible);
    await evaluate(`${sub}.click()`);
    const restored = await evaluate(visible);
    assert(after < before && restored === before, `markers ${before} → ${after} → ${restored}`);
    return `markers ${before} → ${after} → ${restored}`;
  });

  await check('labels option hides permanent labels', async () => {
    const shown = () => evaluate(`[...document.querySelectorAll('.map-label.permanent')].filter(e => getComputedStyle(e).display !== 'none').length`);
    const before = await shown();
    await evaluate(`document.getElementById('opt-labels').click()`);
    const after = await shown();
    await evaluate(`document.getElementById('opt-labels').click()`);
    assert(before > 0 && after === 0, `labels ${before} → ${after}`);
    return `labels ${before} → ${after}`;
  });

  await check('sidebar collapses and reopens', async () => {
    await evaluate(`document.getElementById('toggle-sidebar').click()`);
    await sleep(300);
    const collapsed = await evaluate(`document.body.classList.contains('collapsed') && getComputedStyle(document.getElementById('show-sidebar')).display !== 'none'`);
    await evaluate(`document.getElementById('show-sidebar').click()`);
    await sleep(300);
    const open = await evaluate(`!document.body.classList.contains('collapsed')`);
    assert(collapsed && open, `collapsed ${collapsed}, reopened ${open}`);
    return 'ok';
  });

  await check('game icons load and icon markers are drawn', async () => {
    const icons = await evaluate(`(MAP_DATA.icons || []).length`);
    if (icons === 0) return 'skipped (map rendered without game assets)';
    const broken = await evaluate(`Promise.all(MAP_DATA.icons.map(src => new Promise(ok => {
      const img = new Image(); img.onload = () => ok(null); img.onerror = () => ok(src); img.src = src; }))).then(r => r.filter(Boolean))`);
    assert(broken.length === 0, 'broken icons: ' + broken.slice(0, 3).join(', '));
    for (const id of ['slugs', 'artifacts', 'droppods']) {
      await evaluate(`(() => { const b = document.querySelector('input[data-layer="${id}"]'); if (b && !b.checked) b.click(); })()`);
    }
    await evaluate(`void window.satisfactoryMap.setView([${data.players[0]?.[1] ?? 0}, ${data.players[0]?.[0] ?? 0}], 4, { animate: false })`);
    await sleep(800);
    const drawn = await evaluate(`(() => { let n = 0; window.satisfactoryMap.eachLayer(l => { if (l._map && l.options && l.options.iconIndex >= 0) n++; }); return n; })()`);
    assert(drawn > 0, 'no icon markers on the map');
    return `${icons} icons loaded, ${drawn} icon markers on the map`;
  });

  await check('no errors during interaction', () => {
    assert(errors.length === 0, errors.join(' | '));
    return 'no errors';
  });

  if (screenshot) {
    const { data: png } = await send('Page.captureScreenshot', { format: 'png' });
    writeFileSync(screenshot, Buffer.from(png, 'base64'));
  }
} catch (e) {
  results.push({ name: 'test harness', ok: false, detail: e.stack });
} finally {
  ws?.close();
  browser.kill();
  await sleep(500);
  try { rmSync(profile, { recursive: true, force: true }); } catch { /* profile still locked */ }
}

for (const r of results) console.log(`${r.ok ? 'PASS' : 'FAIL'}  ${r.name}${r.detail ? `  (${r.detail})` : ''}`);
const failed = results.filter(r => !r.ok).length;
console.log(`\n${results.length - failed}/${results.length} passed`);
process.exit(failed ? 1 : 0);
