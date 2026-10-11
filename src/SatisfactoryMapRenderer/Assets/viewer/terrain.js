/* 3D view of the world: the game's height data as a mesh, the in-game map draped over it. Plain WebGL 2. */
(async () => {
  'use strict';

  const D = window.MAP_DATA || {};
  const W = D.world || { minX: -324698.832031, minY: -375000, size: 750000 };
  const $ = id => document.getElementById(id);
  const status = text => { $('terrain-status').hidden = !text; $('terrain-status').textContent = text || ''; };
  const fail = text => { status(text); $('terrain-status').dataset.kind = 'error'; };

  if (D.title) { $('terrain-title').textContent = D.title; document.title = `${D.title} – 3D`; }
  if (D.logo) { document.querySelector('#terrain-bar .logo').src = D.logo; document.querySelector('link[rel=icon]').href = D.logo; }

  const canvas = $('terrain');
  const gl = canvas.getContext('webgl2', { antialias: true });
  if (!gl) return fail('This browser has no WebGL 2, which the 3D view needs.');

  const loadImage = src => new Promise((resolve, reject) => {
    const img = new Image();
    img.onload = () => resolve(img);
    img.onerror = () => reject(new Error(`could not load ${src}`));
    img.src = src;
  });

  // ---------------------------------------------------------------- heights → mesh

  let info, heightImage;
  try {
    [info, heightImage] = await Promise.all([fetch('terrain/terrain.json').then(r => r.json()), loadImage('terrain/heights.png')]);
  } catch {
    // Opened from disk, fetch is not allowed: the numbers are also in the image's size and a usual range.
    try { heightImage = await loadImage('terrain/heights.png'); info = { zAt0: -52143, zAt1: 46443 }; }
    catch { return fail('This map has no terrain data. Render it again with game data that includes the heights.'); }
  }

  const S = heightImage.width;
  const reader = document.createElement('canvas');
  reader.width = reader.height = S;
  const rc = reader.getContext('2d', { willReadFrequently: true });
  rc.drawImage(heightImage, 0, 0);
  const packed = rc.getImageData(0, 0, S, S).data;
  // 16 bits per sample: high byte in red, low byte in green.
  const height = new Float32Array(S * S);
  for (let i = 0; i < S * S; i++) height[i] = info.zAt0 + (packed[i * 4] * 256 + packed[i * 4 + 1]) / 65535 * (info.zAt1 - info.zAt0);

  /** World height (cm) at a position of the height grid, between samples. */
  function heightAt(px, py) {
    px = Math.min(Math.max(px, 0), S - 1); py = Math.min(Math.max(py, 0), S - 1);
    const x0 = Math.floor(px), y0 = Math.floor(py), x1 = Math.min(x0 + 1, S - 1), y1 = Math.min(y0 + 1, S - 1), fx = px - x0, fy = py - y0;
    return height[y0 * S + x0] * (1 - fx) * (1 - fy) + height[y0 * S + x1] * fx * (1 - fy) + height[y1 * S + x0] * (1 - fx) * fy + height[y1 * S + x1] * fx * fy;
  }
  /** World height (cm) at game coordinates. */
  const groundAt = (x, y) => heightAt((x - W.minX) / W.size * (S - 1), (y - W.minY) / W.size * (S - 1));

  // One vertex every second height sample: about a million vertices, fine for any graphics card of the last decade.
  const N = Math.min(1024, S - 1), V = N + 1;
  const cell = W.size / N;
  const vertices = new Float32Array(V * V * 5); // u, v, height, slope east, slope south
  for (let j = 0; j < V; j++) {
    for (let i = 0; i < V; i++) {
      const px = i / N * (S - 1), py = j / N * (S - 1), o = (j * V + i) * 5, step = (S - 1) / N;
      vertices[o] = i / N;
      vertices[o + 1] = j / N;
      vertices[o + 2] = heightAt(px, py);
      vertices[o + 3] = (heightAt(px + step, py) - heightAt(px - step, py)) / (2 * cell);
      vertices[o + 4] = (heightAt(px, py + step) - heightAt(px, py - step)) / (2 * cell);
    }
  }
  const indices = new Uint32Array(N * N * 6);
  for (let j = 0, k = 0; j < N; j++) {
    for (let i = 0; i < N; i++) {
      const a = j * V + i, b = a + 1, c = a + V, d = c + 1;
      indices[k++] = a; indices[k++] = c; indices[k++] = b;
      indices[k++] = b; indices[k++] = c; indices[k++] = d;
    }
  }

  // ---------------------------------------------------------------- shaders

  function program(vertexSource, fragmentSource) {
    const p = gl.createProgram();
    for (const [type, source] of [[gl.VERTEX_SHADER, vertexSource], [gl.FRAGMENT_SHADER, fragmentSource]]) {
      const s = gl.createShader(type);
      gl.shaderSource(s, source);
      gl.compileShader(s);
      if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s));
      gl.attachShader(p, s);
    }
    gl.linkProgram(p);
    if (!gl.getProgramParameter(p, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(p));
    return p;
  }

  const terrain = program(`#version 300 es
    layout(location = 0) in vec2 uv;
    layout(location = 1) in float height;
    layout(location = 2) in vec2 slope;
    uniform mat4 viewProjection;
    uniform float worldSize, exaggeration;
    out vec2 vUv;
    out vec3 vNormal, vWorld;
    void main() {
      // x east, y up, z south; the world is centred on the origin
      vWorld = vec3((uv.x - 0.5) * worldSize, height * exaggeration, (uv.y - 0.5) * worldSize);
      vNormal = normalize(vec3(-slope.x * exaggeration, 1.0, -slope.y * exaggeration));
      vUv = uv;
      gl_Position = viewProjection * vec4(vWorld, 1.0);
    }`, `#version 300 es
    precision highp float;
    in vec2 vUv;
    in vec3 vNormal, vWorld;
    uniform sampler2D map, overlay;
    uniform float overlayOpacity;
    uniform vec3 eye, sun, haze;
    out vec4 color;
    void main() {
      vec3 base = texture(map, vUv).rgb;
      vec4 factory = texture(overlay, vUv);
      base = mix(base, factory.rgb, factory.a * overlayOpacity);
      vec3 n = normalize(vNormal);
      float light = 0.42 + 0.78 * max(dot(n, sun), 0.0) + 0.12 * n.y;
      vec3 lit = base * light;
      // a little haze towards the horizon gives depth
      float fog = smoothstep(0.55, 1.6, distance(vWorld, eye) / 900000.0);
      color = vec4(mix(lit, haze, fog * 0.55), 1.0);
    }`);

  const vao = gl.createVertexArray();
  gl.bindVertexArray(vao);
  gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
  gl.bufferData(gl.ARRAY_BUFFER, vertices, gl.STATIC_DRAW);
  gl.enableVertexAttribArray(0); gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 20, 0);
  gl.enableVertexAttribArray(1); gl.vertexAttribPointer(1, 1, gl.FLOAT, false, 20, 8);
  gl.enableVertexAttribArray(2); gl.vertexAttribPointer(2, 2, gl.FLOAT, false, 20, 12);
  gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, gl.createBuffer());
  gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, indices, gl.STATIC_DRAW);

  // ---------------------------------------------------------------- textures

  function texture(unit, source) {
    const t = gl.createTexture();
    gl.activeTexture(gl.TEXTURE0 + unit);
    gl.bindTexture(gl.TEXTURE_2D, t);
    gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
    if (source) gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, source);
    else gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 1, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array([0, 0, 0, 0]));
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, source ? gl.LINEAR_MIPMAP_LINEAR : gl.LINEAR);
    if (source) {
      gl.generateMipmap(gl.TEXTURE_2D);
      const anisotropy = gl.getExtension('EXT_texture_filter_anisotropic');
      if (anisotropy) gl.texParameterf(gl.TEXTURE_2D, anisotropy.TEXTURE_MAX_ANISOTROPY_EXT, Math.min(8, gl.getParameter(anisotropy.MAX_TEXTURE_MAX_ANISOTROPY_EXT)));
    }
    return t;
  }

  // ---------------------------------------------------------------- the factory as boxes

  // Every building is a box; every piece of belt, pipe, rail or wire is a long thin box between two points.
  // One instance: centre (3), half size (3), yaw, pitch, colour (3).
  const F = window.MAP_3D;
  const factory = [];
  const centreX = W.minX + W.size / 2, centreY = W.minY + W.size / 2;
  if (F && F.layers) {
    const rgb = F.colors.map(c => { const n = parseInt(c.slice(1), 16); return [(n >> 16 & 255) / 255, (n >> 8 & 255) / 255, (n & 255) / 255]; });
    const remembered = (() => { try { return JSON.parse(localStorage.getItem('smr:layers3d')) || {}; } catch { return {}; } })();
    for (const layer of F.layers) {
      const out = [];
      const add = (cx, cy, cz, hx, hy, hz, yaw, pitch, color) => out.push(cx, cy, cz, hx, hy, hz, yaw, pitch, color[0], color[1], color[2]);
      const b = layer.boxes;
      for (let i = 0; i + 7 < b.length; i += 8) {
        add(b[i] - centreX, b[i + 2] + b[i + 6] / 2, b[i + 1] - centreY, b[i + 4] / 2, b[i + 6] / 2, b[i + 5] / 2, b[i + 3] / 1000, 0, rgb[b[i + 7]]);
      }
      const l = layer.lines;
      for (let i = 0; i + 2 < l.length;) {
        const color = rgb[l[i]], width = l[i + 1], count = l[i + 2];
        // belts and rails are flat, pipes, hypertubes and wires round
        const thickness = layer.id === 'belts' ? 30 : layer.id === 'railways' ? 40 : layer.id === 'power' ? 25 : width;
        const wide = layer.id === 'power' ? 25 : width;
        i += 3;
        for (let p = 0; p + 1 < count; p++) {
          const o = i + p * 3;
          const dx = l[o + 3] - l[o], dz = l[o + 4] - l[o + 1], dy = l[o + 5] - l[o + 2], length = Math.hypot(dx, dy, dz);
          if (length < 1) continue;
          add((l[o] + l[o + 3]) / 2 - centreX, (l[o + 2] + l[o + 5]) / 2 + thickness / 2, (l[o + 1] + l[o + 4]) / 2 - centreY,
            length / 2 + wide * 0.25, thickness / 2, wide / 2, Math.atan2(dz, dx), Math.asin(dy / length), color);
        }
        i += count * 3;
      }
      if (out.length) factory.push({ id: layer.id, title: layer.title, visible: remembered[layer.id] ?? layer.visible, data: new Float32Array(out), count: out.length / 11 });
    }
  }

  const boxes = program(`#version 300 es
    layout(location = 0) in vec3 corner;   // unit cube, -1..1
    layout(location = 1) in vec3 normal;
    layout(location = 2) in vec3 centre;
    layout(location = 3) in vec3 halfSize;
    layout(location = 4) in vec2 turn;     // yaw, pitch
    layout(location = 5) in vec3 tint;
    uniform mat4 viewProjection;
    uniform float exaggeration;
    out vec3 vNormal, vWorld, vTint;
    vec3 turned(vec3 v) {
      float cp = cos(turn.y), sp = sin(turn.y), cy = cos(turn.x), sy = sin(turn.x);
      v = vec3(v.x * cp - v.y * sp, v.x * sp + v.y * cp, v.z);   // tilt along its length
      return vec3(v.x * cy - v.z * sy, v.y, v.x * sy + v.z * cy); // then turn on the ground
    }
    void main() {
      vec3 p = centre + turned(corner * halfSize);
      vWorld = vec3(p.x, p.y * exaggeration, p.z);
      vNormal = turned(normal);
      vTint = tint;
      gl_Position = viewProjection * vec4(vWorld, 1.0);
    }`, `#version 300 es
    precision highp float;
    in vec3 vNormal, vWorld, vTint;
    uniform vec3 eye, sun, haze;
    out vec4 color;
    void main() {
      vec3 n = normalize(vNormal);
      float light = 0.5 + 0.55 * max(dot(n, sun), 0.0) + 0.18 * n.y;
      float fog = smoothstep(0.55, 1.6, distance(vWorld, eye) / 900000.0);
      color = vec4(mix(vTint * light, haze, fog * 0.55), 1.0);
    }`);

  // A cube with a normal per face.
  const cube = [];
  for (const [n, u, v] of [[[1, 0, 0], [0, 1, 0], [0, 0, 1]], [[-1, 0, 0], [0, 0, 1], [0, 1, 0]], [[0, 1, 0], [0, 0, 1], [1, 0, 0]],
    [[0, -1, 0], [1, 0, 0], [0, 0, 1]], [[0, 0, 1], [1, 0, 0], [0, 1, 0]], [[0, 0, -1], [0, 1, 0], [1, 0, 0]]]) {
    for (const [a, b] of [[-1, -1], [1, -1], [1, 1], [-1, -1], [1, 1], [-1, 1]]) {
      cube.push(n[0] + u[0] * a + v[0] * b, n[1] + u[1] * a + v[1] * b, n[2] + u[2] * a + v[2] * b, n[0], n[1], n[2]);
    }
  }
  const cubeBuffer = gl.createBuffer();
  gl.bindBuffer(gl.ARRAY_BUFFER, cubeBuffer);
  gl.bufferData(gl.ARRAY_BUFFER, new Float32Array(cube), gl.STATIC_DRAW);
  for (const layer of factory) {
    layer.vao = gl.createVertexArray();
    gl.bindVertexArray(layer.vao);
    gl.bindBuffer(gl.ARRAY_BUFFER, cubeBuffer);
    gl.enableVertexAttribArray(0); gl.vertexAttribPointer(0, 3, gl.FLOAT, false, 24, 0);
    gl.enableVertexAttribArray(1); gl.vertexAttribPointer(1, 3, gl.FLOAT, false, 24, 12);
    gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
    gl.bufferData(gl.ARRAY_BUFFER, layer.data, gl.STATIC_DRAW);
    for (const [location, size, offset] of [[2, 3, 0], [3, 3, 12], [4, 2, 24], [5, 3, 32]]) {
      gl.enableVertexAttribArray(location);
      gl.vertexAttribPointer(location, size, gl.FLOAT, false, 44, offset);
      gl.vertexAttribDivisor(location, 1);
    }
    layer.data = null;
  }
  gl.bindVertexArray(vao);

  // One tick box per factory layer, as on the 2D map.
  for (const layer of factory) {
    const label = document.createElement('label');
    label.className = 'option';
    label.innerHTML = `<input type="checkbox"> <span></span> <span class="count"></span>`;
    label.querySelector('span').textContent = layer.title;
    label.querySelector('.count').textContent = layer.count.toLocaleString('en-US');
    const box = label.querySelector('input');
    box.checked = layer.visible;
    box.addEventListener('change', () => {
      layer.visible = box.checked;
      try { localStorage.setItem('smr:layers3d', JSON.stringify(Object.fromEntries(factory.map(f => [f.id, f.visible])))); } catch { /* storage unavailable */ }
      needsDraw = true;
    });
    $('factory-layers').appendChild(label);
  }
  // With real boxes the footprints painted on the ground are not needed.
  if (factory.length) { $('opt-factory').checked = false; $('opt-factory-row').hidden = true; }

  let mapTexture;
  try { mapTexture = texture(0, await loadImage('terrain/map-4k.jpg')); }
  catch { return fail('The map texture of the 3D view is missing. Render the map again.'); }
  texture(1, null);
  let needsDraw = true;

  // The sharper texture afterwards, where the graphics hardware takes it.
  if (gl.getParameter(gl.MAX_TEXTURE_SIZE) >= 8192 && (navigator.deviceMemory || 8) >= 4) {
    loadImage('terrain/map-8k.jpg').then(img => { gl.deleteTexture(mapTexture); mapTexture = texture(0, img); needsDraw = true; }).catch(() => { /* the smaller one stays */ });
  }

  // Without box data (an older render): the 2D map's own tiles of the building layers, put together into one picture.
  if (!factory.length) (async () => {
    const zoom = 4, size = 256 << zoom, sheet = document.createElement('canvas');
    sheet.width = sheet.height = size;
    const sc = sheet.getContext('2d');
    const jobs = [];
    for (const id of ['foundations', 'buildings', 'pipes', 'belts', 'railways']) {
      const layer = (D.layers || []).find(l => l.id === id);
      for (const key of layer?.tiles?.list || []) {
        const [z, x, y] = key.split('/').map(Number);
        if (z === zoom) jobs.push({ id, x, y, ext: layer.tiles.ext });
      }
    }
    // Layer by layer, so that belts end up above buildings as on the 2D map.
    const images = await Promise.all(jobs.map(j => loadImage(`tiles/${j.id}/${zoom}/${j.x}/${j.y}.${j.ext}`).catch(() => null)));
    jobs.forEach((j, i) => { if (images[i]) sc.drawImage(images[i], j.x * 256, j.y * 256); });
    if (jobs.length) { texture(1, sheet); needsDraw = true; }
  })();

  // ---------------------------------------------------------------- camera

  const view = { yaw: 0, pitch: 0, distance: 0, target: [0, 0, 0] };
  function reset() {
    // Looking north from the south, at the factory if there is one.
    const f = D.focus;
    const cx = f ? (f[0] + f[2]) / 2 : W.minX + W.size / 2, cy = f ? (f[1] + f[3]) / 2 : W.minY + W.size / 2;
    view.target = [cx - (W.minX + W.size / 2), 0, cy - (W.minY + W.size / 2)];
    view.distance = f ? Math.max(Math.max(f[2] - f[0], f[3] - f[1]) * 1.5, 120000) : W.size * 0.85;
    view.yaw = 0.35;
    view.pitch = 0.75;
    needsDraw = true;
  }
  reset();

  const mul = (a, b) => { const o = new Float32Array(16); for (let c = 0; c < 4; c++) for (let r = 0; r < 4; r++) o[c * 4 + r] = a[r] * b[c * 4] + a[4 + r] * b[c * 4 + 1] + a[8 + r] * b[c * 4 + 2] + a[12 + r] * b[c * 4 + 3]; return o; };
  const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
  const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
  const norm = a => { const l = Math.hypot(a[0], a[1], a[2]) || 1; return [a[0] / l, a[1] / l, a[2] / l]; };
  const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

  let matrix = new Float32Array(16), eye = [0, 0, 0];
  function updateCamera() {
    const e = +$('exaggeration').value;
    const tx = view.target[0], tz = view.target[2];
    const ty = groundAt(tx + W.minX + W.size / 2, tz + W.minY + W.size / 2) * e;
    const cp = Math.cos(view.pitch), sp = Math.sin(view.pitch);
    eye = [tx + view.distance * cp * Math.sin(view.yaw), ty + view.distance * sp, tz + view.distance * cp * Math.cos(view.yaw)];
    const f = norm(sub([tx, ty, tz], eye)), s = norm(cross(f, [0, 1, 0])), u = cross(s, f);
    const look = new Float32Array([s[0], u[0], -f[0], 0, s[1], u[1], -f[1], 0, s[2], u[2], -f[2], 0, -dot(s, eye), -dot(u, eye), dot(f, eye), 1]);
    const aspect = canvas.width / canvas.height, fov = 1 / Math.tan(0.4), near = Math.max(view.distance / 200, 100), far = view.distance + W.size * 2.5;
    const projection = new Float32Array([fov / aspect, 0, 0, 0, 0, fov, 0, 0, 0, 0, (far + near) / (near - far), -1, 0, 0, 2 * far * near / (near - far), 0]);
    matrix = mul(projection, look);
  }

  /** Canvas position of a world point, or null if it is behind the camera. */
  function project(x, y, z) {
    const m = matrix, w = m[3] * x + m[7] * y + m[11] * z + m[15];
    if (w <= 0) return null;
    return [((m[0] * x + m[4] * y + m[8] * z + m[12]) / w * 0.5 + 0.5) * canvas.clientWidth, (1 - ((m[1] * x + m[5] * y + m[9] * z + m[13]) / w * 0.5 + 0.5)) * canvas.clientHeight];
  }

  // ---------------------------------------------------------------- labels (players, landmarks, stations)

  const labelLayer = $('terrain-labels');
  const labels = [];
  for (const [id, cls] of [['landmarks', 'landmark'], ['stations', 'station'], ['players', 'player']]) {
    for (const m of (D.markers || {})[id] || []) {
      const el = document.createElement('div');
      el.className = `terrain-label ${cls}`;
      el.textContent = m[3];
      labelLayer.appendChild(el);
      labels.push({ el, x: m[0], y: m[1], z: m[2] });
    }
  }
  function placeLabels() {
    const e = +$('exaggeration').value, show = $('opt-markers').checked;
    for (const l of labels) {
      // The marker's own height, but never below the ground the view draws there.
      const ground = groundAt(l.x, l.y);
      const p = show && project(l.x - (W.minX + W.size / 2), Math.max(l.z, ground) * e, l.y - (W.minY + W.size / 2));
      l.el.hidden = !p || p[0] < -50 || p[1] < -20 || p[0] > canvas.clientWidth + 50 || p[1] > canvas.clientHeight + 20;
      if (!l.el.hidden) l.el.style.transform = `translate(${Math.round(p[0])}px, ${Math.round(p[1])}px) translate(-50%, -130%)`;
    }
  }

  // ---------------------------------------------------------------- drawing

  function draw() {
    const ratio = Math.min(window.devicePixelRatio || 1, 2), w = Math.round(canvas.clientWidth * ratio), h = Math.round(canvas.clientHeight * ratio);
    if (canvas.width !== w || canvas.height !== h) { canvas.width = w; canvas.height = h; }
    updateCamera();
    const dark = getComputedStyle(document.documentElement).colorScheme.includes('dark') || document.documentElement.dataset.theme === 'dark'
      || (!document.documentElement.dataset.theme && matchMedia('(prefers-color-scheme: dark)').matches);
    const sky = dark ? [0.04, 0.05, 0.07] : [0.80, 0.86, 0.93];
    gl.viewport(0, 0, w, h);
    gl.clearColor(sky[0], sky[1], sky[2], 1);
    gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
    gl.enable(gl.DEPTH_TEST);
    gl.useProgram(terrain);
    const u = name => gl.getUniformLocation(terrain, name);
    gl.uniformMatrix4fv(u('viewProjection'), false, matrix);
    gl.uniform1f(u('worldSize'), W.size);
    gl.uniform1f(u('exaggeration'), +$('exaggeration').value);
    gl.uniform1i(u('map'), 0);
    gl.uniform1i(u('overlay'), 1);
    gl.uniform1f(u('overlayOpacity'), $('opt-factory').checked ? 1 : 0);
    gl.uniform3fv(u('eye'), eye);
    gl.uniform3fv(u('sun'), norm([-0.55, 0.62, -0.55])); // from the north-west, as on the relief map
    gl.uniform3fv(u('haze'), sky);
    gl.bindVertexArray(vao);
    gl.drawElements(gl.TRIANGLES, indices.length, gl.UNSIGNED_INT, 0);

    if (factory.some(f => f.visible)) {
      gl.useProgram(boxes);
      const b = name => gl.getUniformLocation(boxes, name);
      gl.uniformMatrix4fv(b('viewProjection'), false, matrix);
      gl.uniform1f(b('exaggeration'), +$('exaggeration').value);
      gl.uniform3fv(b('eye'), eye);
      gl.uniform3fv(b('sun'), norm([-0.55, 0.62, -0.55]));
      gl.uniform3fv(b('haze'), sky);
      for (const layer of factory) {
        if (!layer.visible) continue;
        gl.bindVertexArray(layer.vao);
        gl.drawArraysInstanced(gl.TRIANGLES, 0, 36, layer.count);
      }
    }
    placeLabels();
  }

  let last = performance.now();
  function frame(now) {
    if ($('opt-spin').checked) { view.yaw += (now - last) * 0.00006; needsDraw = true; }
    last = now;
    if (needsDraw) { needsDraw = false; draw(); }
    requestAnimationFrame(frame);
  }

  // ---------------------------------------------------------------- input

  const clampView = () => {
    view.pitch = Math.min(Math.max(view.pitch, 0.08), 1.54);
    view.distance = Math.min(Math.max(view.distance, 3000), W.size * 1.6);
    const half = W.size * 0.6;
    view.target[0] = Math.min(Math.max(view.target[0], -half), half);
    view.target[2] = Math.min(Math.max(view.target[2], -half), half);
    needsDraw = true;
  };
  function pan(dx, dy) {
    const k = view.distance / canvas.clientHeight * 1.1, c = Math.cos(view.yaw), s = Math.sin(view.yaw);
    view.target[0] -= (dx * c + dy * s) * k;
    view.target[2] -= (-dx * s + dy * c) * k;
  }

  const pointers = new Map();
  let pinch = 0;
  canvas.addEventListener('contextmenu', e => e.preventDefault());
  canvas.addEventListener('pointerdown', e => { canvas.setPointerCapture(e.pointerId); pointers.set(e.pointerId, { x: e.clientX, y: e.clientY, pan: e.button === 2 || e.shiftKey }); });
  canvas.addEventListener('pointerup', e => { pointers.delete(e.pointerId); pinch = 0; });
  canvas.addEventListener('pointercancel', e => { pointers.delete(e.pointerId); pinch = 0; });
  canvas.addEventListener('pointermove', e => {
    const p = pointers.get(e.pointerId);
    if (!p) return;
    const dx = e.clientX - p.x, dy = e.clientY - p.y;
    p.x = e.clientX; p.y = e.clientY;
    if (pointers.size === 2) {
      // two fingers: move with them, zoom by their distance
      const [a, b] = [...pointers.values()], gap = Math.hypot(a.x - b.x, a.y - b.y);
      if (pinch) view.distance *= pinch / gap;
      pinch = gap;
      pan(dx / 2, dy / 2);
    } else if (p.pan) {
      pan(dx, dy);
    } else {
      view.yaw -= dx * 0.005;
      view.pitch += dy * 0.005;
    }
    clampView();
  });
  canvas.addEventListener('wheel', e => { e.preventDefault(); view.distance *= Math.exp(e.deltaY * 0.0012); clampView(); }, { passive: false });
  window.addEventListener('resize', () => { needsDraw = true; });
  window.addEventListener('keydown', e => {
    const step = 40;
    if (e.key === 'ArrowLeft') pan(step, 0); else if (e.key === 'ArrowRight') pan(-step, 0);
    else if (e.key === 'ArrowUp') pan(0, step); else if (e.key === 'ArrowDown') pan(0, -step);
    else if (e.key === '+' || e.key === '=') view.distance *= 0.85; else if (e.key === '-') view.distance *= 1.18;
    else return;
    clampView();
  });

  $('exaggeration').addEventListener('input', () => { $('exaggeration-value').textContent = `${(+$('exaggeration').value).toFixed(1)}×`; needsDraw = true; });
  for (const id of ['opt-factory', 'opt-markers']) $(id).addEventListener('change', () => { needsDraw = true; });
  window.addEventListener('keydown', e => { if (e.key === 'f' && !e.ctrlKey && !e.metaKey) reset(); });
  $('terrain-reset').addEventListener('click', reset);

  window.satisfactoryTerrain = { view, redraw: () => { needsDraw = true; } }; // for automated tests and the console
  status('');
  requestAnimationFrame(frame);
})().catch(e => {
  const s = document.getElementById('terrain-status');
  s.hidden = false; s.dataset.kind = 'error'; s.textContent = `The 3D view could not start: ${e.message}`;
});
