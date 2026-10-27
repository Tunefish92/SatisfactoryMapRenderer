/* Satisfactory map viewer. Data comes from map-data.js (window.MAP_DATA). */
(async () => {
  'use strict';

  const D = window.MAP_DATA;

  // Before the first save is loaded there is nothing to show; source.js fills in what to do next.
  if (D.empty) {
    document.body.classList.add('empty');
    document.getElementById('placeholder').hidden = false;
  }

  // Status bar: which save this is and when it was written; source.js adds where it came from.
  if (!D.empty) {
    document.getElementById('status-session').textContent = D.title;
    const saveName = D.saveName || (D.fileName || '').replace(/\.sav$/i, '');
    if (saveName && saveName !== D.title) document.getElementById('status-save').textContent = saveName;
    if (D.savedAt) {
      const saved = new Date(D.savedAt);
      const time = document.getElementById('status-time');
      time.textContent = 'Saved ' + saved.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
      time.title = saved.toISOString().replace('T', ' ').replace(/:\d\d\.000Z$/, ' UTC');
    }
    document.getElementById('statusbar').hidden = false;
  }

  // With game data the game's own icon is the logo; the bundled one is the fallback.
  if (D.logo) {
    document.querySelector('.placeholder-logo').src = D.logo;
    for (const logo of document.querySelectorAll('.load-logo')) logo.src = D.logo; // what is new, loading a save
    document.querySelector('#sidebar header .logo').src = D.logo;
    document.querySelector('link[rel=icon]').href = D.logo;
    document.querySelector('link[rel=icon]').type = 'image/png';
  }
  const W = D.world;
  const scale = 256 / W.size;

  // Game coordinates map directly to Leaflet: lat = game Y (south), lng = game X (east).
  const crs = L.extend({}, L.CRS.Simple, {
    transformation: new L.Transformation(scale, -W.minX * scale, scale, -W.minY * scale),
  });
  const worldBounds = L.latLngBounds([W.minY, W.minX], [W.minY + W.size, W.minX + W.size]);

  const map = L.map('map', {
    crs,
    preferCanvas: true,
    minZoom: 0,
    maxZoom: Math.max(D.maxZoom + 1, 8), // little upscaling keeps the tiles sharp; low-zoom renders stay inspectable
    zoomSnap: 0.5,
    zoomDelta: 0.5,
    wheelPxPerZoomLevel: 90,
    maxBounds: worldBounds.pad(0.2),
  });
  window.satisfactoryMap = map; // for automated viewer tests and console debugging
  map.attributionControl.setPrefix('<a href="https://leafletjs.com">Leaflet</a>');
  map.attributionControl.addAttribution(`Rendered from <b>${esc(D.fileName)}</b>`);

  // The 3D view of the same world, when the game data has the heights for it.
  if (D.terrain) {
    const Button3D = L.Control.extend({
      onAdd() {
        const link = L.DomUtil.create('a', 'view-3d');
        link.href = 'terrain.html';
        link.title = 'Open the 3D view: the world and your factory in the game\'s own models';
        link.innerHTML = '<svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true"><path d="M12 2.5 3.5 7.2v9.6L12 21.5l8.5-4.7V7.2Z" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linejoin="round"/>'
          + '<path d="M3.5 7.2 12 12l8.5-4.8M12 12v9.5" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linejoin="round"/></svg><span>3D view</span>';
        L.DomEvent.disableClickPropagation(link);
        return link;
      },
    });
    new Button3D({ position: 'topleft' }).addTo(map);
  }

  // View state lives in the URL (#zoom/x/y in metres) so views can be bookmarked and shared.
  const fromHash = () => {
    const m = /^#(-?[\d.]+)\/(-?[\d.]+)\/(-?[\d.]+)$/.exec(location.hash);
    if (!m) return false;
    map.setView([+m[3] * 100, +m[2] * 100], +m[1]);
    return true;
  };
  if (!fromHash()) {
    const focus = D.focus ? L.latLngBounds([D.focus[1], D.focus[0]], [D.focus[3], D.focus[2]]) : worldBounds;
    map.fitBounds(focus.pad(0.15));
  }
  map.on('moveend', () => {
    const c = map.getCenter();
    history.replaceState(null, '', `#${map.getZoom()}/${Math.round(c.lng / 100)}/${Math.round(c.lat / 100)}`);
  });
  window.addEventListener('hashchange', fromHash);

  const RADIUS = {
    players: 9, vehicles: 6, landmarks: 8, stations: 7, beacons: 6, resources: 6, deposits: 3.5,
    slugs: 5, artifacts: 6, droppods: 6, plants: 3, pickups: 4, hazards: 4, spawners: 3.5, creatures: 5,
  };
  // Markers with a game icon are drawn larger so the icon stays recognisable.
  const ICON_RADIUS = {
    players: 13, vehicles: 11, landmarks: 13, beacons: 11, resources: 12, deposits: 8, slugs: 10, artifacts: 11,
    droppods: 11, plants: 8, pickups: 9, spawners: 9, creatures: 11,
  };

  // Game icons (from the local installation) are decoded up front so the canvas can draw them immediately.
  const iconImages = (D.icons || []).map(src => {
    const img = new Image();
    img.src = src;
    return img;
  });
  await Promise.all(iconImages.map(img => img.decode().catch(() => { /* missing icon: marker falls back to its disc */ })));

  // Markers grow with the zoom level: small in the overview, easy to recognise when zoomed in.
  const markerScale = zoom => Math.min(2.4, Math.max(1, 1 + (zoom - 5) * 0.35));
  const ScaledMarker = L.CircleMarker.extend({
    _project() {
      this._radius = this.options.radius * markerScale(this._map.getZoom());
      L.CircleMarker.prototype._project.call(this);
    },
  });

  /** Canvas marker: dark disc, optional coloured ring (resource purity), game icon and a check badge. */
  const IconMarker = ScaledMarker.extend({
    _updatePath() {
      const renderer = this._renderer;
      if (!renderer._drawing || this._empty()) return;
      const ctx = renderer._ctx, p = this._point.round(), o = this.options, r = this._radius;
      ctx.save();
      ctx.globalAlpha = o.dimmed ? 0.4 : 1;
      ctx.beginPath();
      ctx.arc(p.x, p.y, r, 0, Math.PI * 2);
      ctx.fillStyle = 'rgba(16, 20, 26, 0.82)';
      ctx.fill();
      ctx.lineWidth = o.ring ? 2.5 : 1.2;
      ctx.strokeStyle = o.ring || 'rgba(255, 255, 255, 0.55)';
      ctx.stroke();
      const img = iconImages[o.iconIndex];
      if (img && img.complete && img.naturalWidth) {
        const s = r * 1.55;
        ctx.imageSmoothingQuality = 'high';
        ctx.drawImage(img, p.x - s / 2, p.y - s / 2, s, s);
      }
      if (o.badge) {
        const bx = p.x + r * 0.72, by = p.y - r * 0.72, br = Math.max(3.5, r * 0.36);
        ctx.beginPath();
        ctx.arc(bx, by, br, 0, Math.PI * 2);
        ctx.fillStyle = '#2ecc71';
        ctx.fill();
        ctx.lineWidth = 1.5;
        ctx.strokeStyle = '#0f1419';
        ctx.stroke();
        ctx.beginPath();
        ctx.moveTo(bx - br * 0.5, by);
        ctx.lineTo(bx - br * 0.1, by + br * 0.45);
        ctx.lineTo(bx + br * 0.55, by - br * 0.4);
        ctx.strokeStyle = '#ffffff';
        ctx.stroke();
      }
      ctx.restore();
    },
  });
  const LABELLED = new Set(['players', 'stations', 'beacons']);

  const store = {
    get(key, fallback) {
      try { const v = localStorage.getItem('smr:' + key); return v === null ? fallback : JSON.parse(v); } catch { return fallback; }
    },
    set(key, value) {
      try { localStorage.setItem('smr:' + key, JSON.stringify(value)); } catch { /* storage unavailable */ }
    },
  };

  function esc(s) {
    return String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  }
  const fmt = n => n.toLocaleString('en-US');
  const metres = v => Math.round(v / 100).toLocaleString('en-US');

  function popupHtml(name, subtitle, x, y, z, details, iconIndex = -1) {
    const rows = (details || []).map(([k, v]) => `<tr><td>${esc(k)}</td><td>${esc(v)}</td></tr>`).join('');
    const pos = `X ${metres(x)} m · Y ${metres(y)} m` + (z !== undefined ? ` · Z ${metres(z)} m` : '');
    const icon = iconIndex >= 0 && D.icons[iconIndex] ? `<img class="popup-icon" src="${D.icons[iconIndex]}" alt="">` : '';
    return `${icon}<h3>${esc(name)}</h3><div class="sub">${esc(subtitle)}<br>${pos}</div>${rows ? `<table>${rows}</table>` : ''}`;
  }

  // ---------------------------------------------------------------- raster layers

  const SparseTiles = L.GridLayer.extend({
    initialize(layer, options) {
      L.GridLayer.prototype.initialize.call(this, options);
      this.layer = layer;
      this.available = new Set(layer.tiles.list);
      // a base map that came cut into tiles has every tile, in a folder of its own
      this.whole = !!layer.tiles.path;
      this.path = layer.tiles.path || `tiles/${layer.id}`;
    },
    createTile(coords, done) {
      const key = `${coords.z}/${coords.x}/${coords.y}`;
      // rounding can put the world's edge a hair into the next tile, which no layer has
      const outside = Math.max(coords.x, coords.y) >= 2 ** coords.z || Math.min(coords.x, coords.y) < 0;
      if (outside || (!this.whole && !this.available.has(key))) {
        const empty = document.createElement('div');
        setTimeout(() => done(null, empty), 0);
        return empty;
      }
      const img = document.createElement('img');
      img.alt = '';
      img.decoding = 'async';
      img.onload = () => done(null, img);
      img.onerror = () => { img.style.visibility = 'hidden'; done(null, img); }; // no broken-image icon
      img.src = `${this.path}/${key}.${this.layer.tiles.ext}`;
      return img;
    },
  });

  // ---------------------------------------------------------------- build layers

  const handles = {};      // layer id → { leaflet, subgroups[] }
  const searchIndex = [];  // { name, layer, lat, lng, open() }

  D.layers.forEach((layer, index) => {
    if (layer.kind === 'raster') {
      if (!layer.tiles || (layer.tiles.list.length === 0 && !layer.tiles.path)) return;
      handles[layer.id] = {
        leaflet: new SparseTiles(layer, {
          tileSize: 256,
          maxNativeZoom: layer.tiles.maxNativeZoom,
          maxZoom: Math.max(D.maxZoom + 1, 8), // little upscaling keeps the tiles sharp; low-zoom renders stay inspectable
          bounds: worldBounds,
          zIndex: index + 1,
          noWrap: true,
          className: `tiles-${layer.id}`,
        }),
      };
      return;
    }

    const items = D.markers[layer.id] || [];
    const group = L.layerGroup();
    const subgroups = layer.legend.map(() => L.layerGroup().addTo(group));
    for (const [x, y, z, name, color, legendIndex, dimmed, details, iconIndex = -1, ring = '', badge = 0] of items) {
      const hasIcon = iconIndex >= 0 && iconImages[iconIndex]?.naturalWidth > 0;
      const radius = hasIcon ? ICON_RADIUS[layer.id] || 10 : RADIUS[layer.id] || 5;
      const marker = hasIcon
        ? new IconMarker([y, x], {
          radius,
          iconIndex,
          ring: ring || (layer.id === 'players' ? '#ff4d4d' : ''),
          badge: badge === 1,
          dimmed: dimmed === 1,
          bubblingMouseEvents: false,
        })
        : new ScaledMarker([y, x], {
          radius,
          color: layer.id === 'players' ? '#ffffff' : 'rgba(0,0,0,.65)',
          weight: layer.id === 'players' ? 2.5 : 1,
          fillColor: color,
          fillOpacity: dimmed ? 0.28 : 0.95,
          opacity: dimmed ? 0.4 : 1,
          bubblingMouseEvents: false,
        });
      const subtitle = layer.legend[legendIndex] ? `${layer.title} · ${layer.legend[legendIndex][0]}` : layer.title;
      marker.bindPopup(() => popupHtml(name, subtitle, x, y, z, details || [], iconIndex));
      if (LABELLED.has(layer.id)) {
        marker.bindTooltip(esc(name), { permanent: true, direction: 'right', offset: [radius + 2, 0], className: 'map-label permanent' });
      } else {
        marker.bindTooltip(esc(name), { direction: 'top', offset: [0, -radius], className: 'map-label' });
      }
      (subgroups[legendIndex] || subgroups[0] || group).addLayer(marker);
      searchIndex.push({ name, layer, lat: y, lng: x, open: () => { ensureVisible(layer.id); marker.openPopup(); } });
    }
    handles[layer.id] = { leaflet: group, subgroups };
  });

  // Clickable buildings (not drawn as markers – they are part of the raster tiles).
  const inspectCell = 5000;
  const inspectGrid = new Map();
  D.inspect.forEach((item, i) => {
    const [x, y, r] = item;
    for (let cx = Math.floor((x - r) / inspectCell); cx <= Math.floor((x + r) / inspectCell); cx++) {
      for (let cy = Math.floor((y - r) / inspectCell); cy <= Math.floor((y + r) / inspectCell); cy++) {
        const key = cx + ',' + cy;
        if (!inspectGrid.has(key)) inspectGrid.set(key, []);
        inspectGrid.get(key).push(i);
      }
    }
    const buildingsLayer = D.layers.find(l => l.id === 'buildings');
    searchIndex.push({
      name: item[3], layer: buildingsLayer, lat: y, lng: x,
      open: () => { ensureVisible('buildings'); showInspect(item); },
    });
  });

  function showInspect([x, y, , name, , group, details, iconIndex = -1]) {
    L.popup({ maxWidth: 320 }).setLatLng([y, x]).setContent(popupHtml(name, `Building · ${group}`, x, y, undefined, details, iconIndex)).openOn(map);
  }

  map.on('click', e => {
    if (!map.hasLayer(handles.buildings?.leaflet)) return;
    const x = e.latlng.lng, y = e.latlng.lat;
    const tolerance = 6 * W.size / (256 * Math.pow(2, map.getZoom()));
    const candidates = inspectGrid.get(Math.floor(x / inspectCell) + ',' + Math.floor(y / inspectCell)) || [];
    let best = null, bestScore = Infinity;
    for (const i of candidates) {
      const item = D.inspect[i];
      const d = Math.hypot(item[0] - x, item[1] - y);
      if (d > item[2] + tolerance) continue;
      const score = d / (item[2] + tolerance);
      if (score < bestScore) { bestScore = score; best = item; }
    }
    if (best) showInspect(best);
  });

  // ---------------------------------------------------------------- grid

  const grid = L.layerGroup();
  const step = 100000; // 1 km
  const start = Math.ceil(W.minX / step) * step, startY = Math.ceil(W.minY / step) * step;
  for (let x = start; x <= W.minX + W.size; x += step) {
    L.polyline([[W.minY, x], [W.minY + W.size, x]], { color: '#8b97a5', opacity: 0.18, weight: 1, interactive: false }).addTo(grid);
  }
  for (let y = startY; y <= W.minY + W.size; y += step) {
    L.polyline([[y, W.minX], [y, W.minX + W.size]], { color: '#8b97a5', opacity: 0.18, weight: 1, interactive: false }).addTo(grid);
  }
  L.rectangle(worldBounds, { color: '#8b97a5', opacity: 0.35, weight: 1, fill: false, interactive: false }).addTo(grid);

  // ---------------------------------------------------------------- sidebar

  document.title = `${D.title} – Satisfactory Map`;
  document.getElementById('title').textContent = D.title;

  const visibility = store.get('layers', {});

  function setLayer(id, on) {
    const h = handles[id];
    if (!h) return;
    if (on) h.leaflet.addTo(map); else map.removeLayer(h.leaflet);
    visibility[id] = on;
    store.set('layers', visibility);
    const box = document.querySelector(`input[data-layer="${id}"]`);
    if (box) box.checked = on;
    refreshGroups();
  }

  /** Group headers: how many of the group's layers are shown, and a tick box for all of them at once. */
  function refreshGroups() {
    for (const group of document.querySelectorAll('#layers .group')) {
      const boxes = [...group.querySelectorAll('input[data-layer]:not(:disabled)')];
      const shown = boxes.filter(b => b.checked).length;
      group.querySelector('.group-count').textContent = group.querySelector('.group-all') ? `${shown}/${boxes.length}` : '';
      const all = group.querySelector('.group-all');
      if (!all) continue;
      all.checked = shown > 0;
      all.indeterminate = shown > 0 && shown < boxes.length;
    }
  }

  function ensureVisible(id) {
    if (!map.hasLayer(handles[id]?.leaflet)) setLayer(id, true);
  }

  // Base maps share one dropdown: the game map (or the explored outline without game data), the same
  // map with shaded relief.
  const BASE_MAPS = ['ground', 'background', 'relief', 'explored'];
  const baseMaps = BASE_MAPS.filter(id => handles[id]).map(id => ({
    id, title: D.layers.find(l => l.id === id).title, leaflet: handles[id].leaflet,
  }));
  baseMaps.push({ id: 'none', title: 'None', leaflet: null });

  function setBaseMap(id) {
    const chosen = baseMaps.find(b => b.id === id) || baseMaps[0];
    for (const b of baseMaps) if (b.leaflet && b !== chosen) map.removeLayer(b.leaflet);
    if (chosen.leaflet) map.addLayer(chosen.leaflet);
    store.set('basemap', chosen.id);
    return chosen.id;
  }

  function addBaseMapSelect(parent) {
    const row = document.createElement('div');
    row.className = 'layer-row select-row';
    row.innerHTML = `<label for="basemap">Map</label><select id="basemap">${baseMaps.map(b => `<option value="${b.id}">${esc(b.title)}</option>`).join('')}</select>`;
    parent.appendChild(row);
    const select = row.querySelector('select');
    select.value = setBaseMap(store.get('basemap', baseMaps[0].id));
    select.addEventListener('change', () => setBaseMap(select.value));
  }

  const layersElement = document.getElementById('layers');
  const closedGroups = new Set(store.get('closedGroups', []));
  let container = layersElement; // body of the group that is being filled
  let currentGroup = null;
  let baseMapSelect = false;
  for (const layer of D.layers) {
    const h = handles[layer.id];
    const isBaseMap = BASE_MAPS.includes(layer.id);
    if (!h && layer.group === 'Base' && (!isBaseMap || baseMapSelect)) continue; // e.g. no fog of war in this save
    if (layer.group !== currentGroup) {
      currentGroup = layer.group;
      const group = document.createElement('details');
      group.className = 'group';
      group.open = !closedGroups.has(currentGroup);
      // The base group holds the map dropdown, so "all on/off" makes no sense there.
      const all = currentGroup === 'Base' ? '' : `<input type="checkbox" class="group-all" title="Show or hide all ${esc(currentGroup.toLowerCase())} layers" aria-label="Show or hide all ${esc(currentGroup)} layers">`;
      group.innerHTML = `<summary>${all}<span class="group-name">${esc(currentGroup)}</span><span class="group-count"></span></summary><div class="group-body"></div>`;
      layersElement.appendChild(group);
      container = group.querySelector('.group-body');
      const name = currentGroup;
      group.addEventListener('toggle', () => {
        if (group.open) closedGroups.delete(name); else closedGroups.add(name);
        store.set('closedGroups', [...closedGroups]);
      });
      group.querySelector('.group-all')?.addEventListener('click', e => e.stopPropagation()); // do not fold the group
      group.querySelector('.group-all')?.addEventListener('change', e => {
        const on = e.target.checked; // read once: every setLayer below re-evaluates this very box
        for (const box of group.querySelectorAll('input[data-layer]:not(:disabled)')) setLayer(box.dataset.layer, on);
      });
    }

    if (isBaseMap) {
      // The base maps share one dropdown instead of a checkbox each.
      if (!baseMapSelect) addBaseMapSelect(container);
      baseMapSelect = true;
      continue;
    }

    const row = document.createElement('div');
    row.className = 'layer-row' + (h ? '' : ' empty');
    const hasLegend = layer.legend.length > 1 || (layer.kind === 'marker' && layer.legend.length > 0);
    row.innerHTML = `
      <label title="${esc(layer.description)}">
        <input type="checkbox" data-layer="${layer.id}" ${h ? '' : 'disabled'}>
        <span class="swatch ${layer.kind === 'marker' ? 'dot' : ''}" style="background:${layer.color}"></span>
        <span class="name">${esc(layer.title)}</span>
      </label>
      <span class="count">${layer.count >= 0 ? fmt(layer.count) : ''}</span>
      <button class="expand" title="Legend" aria-label="Show legend" ${hasLegend ? '' : 'style="visibility:hidden"'}>▸</button>`;
    container.appendChild(row);

    const legend = document.createElement('div');
    legend.className = 'legend';
    legend.hidden = true;
    layer.legend.forEach(([label, color, count, iconIndex = -1], i) => {
      const swatch = iconIndex >= 0 && D.icons[iconIndex]
        ? `<img class="legend-icon" src="${D.icons[iconIndex]}" alt="">`
        : `<span class="swatch ${layer.kind === 'marker' ? 'dot' : ''}" style="background:${color}"></span>`;
      const item = document.createElement('div');
      item.className = 'layer-row';
      const filter = layer.kind === 'marker' && h ? `<input type="checkbox" checked data-sub="${i}">` : '';
      item.innerHTML = `<label>${filter}${swatch}<span class="name">${esc(label)}</span></label><span class="count">${fmt(count)}</span>`;
      legend.appendChild(item);
    });
    container.appendChild(legend);

    row.querySelector('.expand').addEventListener('click', e => {
      legend.hidden = !legend.hidden;
      e.currentTarget.textContent = legend.hidden ? '▸' : '▾';
    });
    const box = row.querySelector('input');
    box.addEventListener('change', () => setLayer(layer.id, box.checked));
    legend.querySelectorAll('input[data-sub]').forEach(sub => sub.addEventListener('change', () => {
      const g = h.subgroups[+sub.dataset.sub];
      if (sub.checked) h.leaflet.addLayer(g); else h.leaflet.removeLayer(g);
    }));

    if (h) setLayer(layer.id, visibility[layer.id] ?? layer.visible);
  }
  refreshGroups();

  // Tabs of the side panel
  const tabs = [...document.querySelectorAll('#tabs [data-tab]')];
  function showTab(name) {
    if (!tabs.some(t => t.dataset.tab === name)) name = 'layers';
    for (const tab of tabs) tab.setAttribute('aria-selected', tab.dataset.tab === name);
    for (const panel of document.querySelectorAll('.tab-panel')) panel.hidden = panel.dataset.panel !== name;
    document.getElementById('sidebar-content').scrollTop = 0;
    store.set('tab', name);
  }
  for (const tab of tabs) tab.addEventListener('click', () => showTab(tab.dataset.tab));
  // Without a map there is only one thing to do: choose a save game.
  showTab(D.empty ? 'save' : store.get('tab', 'layers'));

  // Options
  const labels = document.getElementById('opt-labels');
  labels.checked = store.get('labels', true);
  const applyLabels = () => {
    document.body.classList.toggle('no-labels', !labels.checked);
    store.set('labels', labels.checked);
  };
  labels.addEventListener('change', applyLabels);
  applyLabels();

  const themeSelect = document.getElementById('opt-theme');
  themeSelect.value = store.get('theme', 'system');
  themeSelect.addEventListener('change', () => {
    store.set('theme', themeSelect.value);
    if (themeSelect.value === 'system') delete document.documentElement.dataset.theme;
    else document.documentElement.dataset.theme = themeSelect.value;
  });

  const gridBox = document.getElementById('opt-grid');
  gridBox.checked = store.get('grid', true);
  const applyGrid = () => { if (gridBox.checked) grid.addTo(map); else map.removeLayer(grid); store.set('grid', gridBox.checked); };
  gridBox.addEventListener('change', applyGrid);
  applyGrid();

  // Summary + counts
  document.getElementById('summary').innerHTML =
    D.summary.map(([k, v]) => `<tr><td>${esc(k)}</td><td>${esc(v)}</td></tr>`).join('');
  const countsTable = document.getElementById('counts');
  const renderCounts = filter => {
    const f = filter.toLowerCase();
    countsTable.innerHTML = D.counts
      .filter(([k]) => k.toLowerCase().includes(f))
      .map(([k, v]) => `<tr><td>${esc(k)}</td><td>${fmt(v)}</td></tr>`).join('');
  };
  renderCounts('');
  document.getElementById('count-filter').addEventListener('input', e => renderCounts(e.target.value));

  // Search
  const search = document.getElementById('search');
  const results = document.getElementById('search-results');
  search.addEventListener('input', () => {
    const q = search.value.trim().toLowerCase();
    results.innerHTML = '';
    if (q.length < 2) return;
    const hits = searchIndex
      .filter(s => s.name.toLowerCase().includes(q))
      .sort((a, b) => (b.name.toLowerCase().startsWith(q) - a.name.toLowerCase().startsWith(q)) || a.name.localeCompare(b.name))
      .slice(0, 40);
    for (const hit of hits) {
      const li = document.createElement('li');
      li.innerHTML = `<span class="swatch dot" style="background:${hit.layer.color}"></span><span>${esc(hit.name)}</span><span class="layer">${esc(hit.layer.title)}</span>`;
      li.addEventListener('click', () => {
        map.setView([hit.lat, hit.lng], Math.max(map.getZoom(), D.maxZoom - 1));
        hit.open();
      });
      results.appendChild(li);
    }
    if (hits.length === 0) results.innerHTML = '<li class="layer">No matches</li>';
  });

  // Sidebar toggle
  const toggleSidebar = collapsed => {
    document.body.classList.toggle('collapsed', collapsed);
    setTimeout(() => map.invalidateSize(), 220);
  };
  document.getElementById('toggle-sidebar').addEventListener('click', () => toggleSidebar(true));
  document.getElementById('show-sidebar').addEventListener('click', () => toggleSidebar(false));
  if (window.innerWidth <= 700) toggleSidebar(true);

  // Cursor position
  const cursor = document.getElementById('cursor');
  map.on('mousemove', e => {
    cursor.textContent = `X ${metres(e.latlng.lng)} m   Y ${metres(e.latlng.lat)} m   zoom ${map.getZoom()}`;
  });
})();
