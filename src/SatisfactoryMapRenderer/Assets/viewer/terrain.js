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
    uniform float worldSize;
    out vec2 vUv;
    out vec3 vNormal, vWorld;
    void main() {
      // x east, y up, z south; the world is centred on the origin
      vWorld = vec3((uv.x - 0.5) * worldSize, height, (uv.y - 0.5) * worldSize);
      vNormal = normalize(vec3(-slope.x, 1.0, -slope.y));
      vUv = uv;
      gl_Position = viewProjection * vec4(vWorld, 1.0);
    }`, `#version 300 es
    precision highp float;
    in vec2 vUv;
    in vec3 vNormal, vWorld;
    precision highp sampler2DArray;
    uniform sampler2D map, overlay, ground;
    uniform sampler2DArray groundLayers;
    uniform float overlayOpacity;
    uniform vec3 eye, sun, haze;
    uniform bool useGround;
    uniform vec4 groundPlace; // where the ground map begins (x, z), its sample distance, the edge of one texture repetition
    uniform float depthStep, cliffLayer;
    out vec4 color;

    // The game's ground texture of one layer: fine close by, in larger repetitions further away, so that the
    // pattern does not show as a grid from above.
    vec3 layer(float index, float far) {
      vec3 near = texture(groundLayers, vec3(vWorld.xz / groundPlace.w, index)).rgb;
      vec3 wide = texture(groundLayers, vec3(vWorld.zx / (groundPlace.w * 7.3), index)).rgb;
      return mix(near, wide, far);
    }
    // One sample of the ground map: colour, and in alpha the water depth in cm.
    vec4 sampleGround(ivec2 at, vec3 fallback, float far) {
      ivec2 size = textureSize(ground, 0);
      // Beyond the game's landscape the in-game map shows; where that is sea, it counts as deep water.
      vec4 beyond = vec4(fallback, fallback.b > fallback.r * 1.12 && fallback.g > fallback.r ? 3000.0 : 0.0);
      if (at.x < 0 || at.y < 0 || at.x >= size.x || at.y >= size.y) return beyond;
      vec3 t = texelFetch(ground, at, 0).rgb * 255.0;
      float first = floor(t.r / 16.0 + 0.001), second = t.r - first * 16.0;
      if (first > 14.5) return beyond;
      vec3 c = layer(first, far);
      if (second < 14.5 && t.g < 254.5) c = mix(layer(second, far), c, t.g / 255.0);
      return vec4(c, t.b * depthStep);
    }

    void main() {
      vec3 base = texture(map, vUv).rgb;
      if (useGround) {
        float far = smoothstep(2500.0, 30000.0, distance(vWorld, eye));
        vec2 p = (vWorld.xz - groundPlace.xy) / groundPlace.z;
        ivec2 i = ivec2(floor(p));
        vec2 f = p - floor(p);
        vec4 g = mix(mix(sampleGround(i, base, far), sampleGround(i + ivec2(1, 0), base, far), f.x),
                     mix(sampleGround(i + ivec2(0, 1), base, far), sampleGround(i + ivec2(1, 1), base, far), f.x), f.y);
        // The game's cliffs and rocks are objects standing on the landscape, which the heights include but
        // the ground map does not: steep faces get the game's cliff texture, laid on from the side.
        float up = normalize(vNormal).y, steep = cliffLayer < 0.0 ? 0.0 : smoothstep(0.80, 0.60, up);
        if (steep > 0.0) {
          vec3 rock = texture(groundLayers, vec3(vec2(vWorld.x + vWorld.z, vWorld.y) / (groundPlace.w * 2.0), cliffLayer)).rgb;
          vec3 wide = texture(groundLayers, vec3(vec2(vWorld.x - vWorld.z, vWorld.y) / (groundPlace.w * 14.6), cliffLayer)).rgb;
          g.rgb = mix(g.rgb, mix(rock, wide, far) * 1.3, steep);
        }
        // water: clear at the shore, the game's deep blue-green further out; water lies flat
        float depth = g.a * smoothstep(0.75, 0.93, up);
        vec3 water = mix(vec3(0.16, 0.40, 0.44), vec3(0.04, 0.17, 0.27), smoothstep(0.0, 1500.0, depth));
        base = mix(g.rgb, water, depth > 0.0 ? clamp(0.30 + depth / 500.0, 0.0, 0.93) : 0.0);
      }
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
  // The buildings' real shapes, where the game data has them: meshes in terrain/models.bin, described in F.meshes
  // and F.classes. Without them (or when the file cannot be fetched) every building is a box.
  let modelData = null;
  if (F && F.meshes && F.classes) {
    try {
      const response = await fetch('terrain/models.bin');
      if (response.ok) modelData = await response.arrayBuffer();
    } catch { /* opened from disk: boxes */ }
  }

  /** 3×4 matrix (rows) of a transform: scale, then rotate by the quaternion, then translate. In game coordinates. */
  function transform(tx, ty, tz, qx, qy, qz, qw, sx, sy, sz) {
    const xx = qx * qx, yy = qy * qy, zz = qz * qz, xy = qx * qy, xz = qx * qz, yz = qy * qz, wx = qw * qx, wy = qw * qy, wz = qw * qz;
    return [
      (1 - 2 * (yy + zz)) * sx, 2 * (xy - wz) * sy, 2 * (xz + wy) * sz, tx,
      2 * (xy + wz) * sx, (1 - 2 * (xx + zz)) * sy, 2 * (yz - wx) * sz, ty,
      2 * (xz - wy) * sx, 2 * (yz + wx) * sy, (1 - 2 * (xx + yy)) * sz, tz,
    ];
  }
  /** a · b for 3×4 matrices: b is applied first. */
  function combine(a, b) {
    const o = new Array(12);
    for (let r = 0; r < 3; r++) {
      for (let c = 0; c < 4; c++) o[r * 4 + c] = a[r * 4] * b[c] + a[r * 4 + 1] * b[4 + c] + a[r * 4 + 2] * b[8 + c] + (c === 3 ? a[r * 4 + 3] : 0);
    }
    return o;
  }
  const partMatrices = modelData ? F.classes.map(parts => {
    const list = [];
    for (let i = 0; i + 10 < parts.length; i += 11) list.push({ mesh: parts[i], matrix: transform(...parts.slice(i + 1, i + 11)) });
    return list;
  }) : [];

  // What the 2D map tells about a building on a click, found again here by the building's position.
  const inspectAt = new Map();
  for (const item of D.inspect || []) {
    const key = item[0] + ',' + item[1];
    if (!inspectAt.has(key)) inspectAt.set(key, []);
    inspectAt.get(key).push(item);
  }
  const pickable = []; // buildings that can be clicked: their box in the world and what to show

  const meshInstances = []; // { layer, mesh, data: number[] } – 15 numbers per instance: matrix rows, colour
  let modelCount = 0, triangleCount = 0;
  if (F && F.layers) {
    const rgb = F.colors.map(c => { const n = parseInt(c.slice(1), 16); return [(n >> 16 & 255) / 255, (n >> 8 & 255) / 255, (n & 255) / 255]; });
    const remembered = (() => { try { return JSON.parse(localStorage.getItem('smr:layers3d')) || {}; } catch { return {}; } })();
    for (const layer of F.layers) {
      const out = [];
      const perMesh = new Map();
      const add = (cx, cy, cz, hx, hy, hz, yaw, pitch, color) => out.push(cx, cy, cz, hx, hy, hz, yaw, pitch, color[0], color[1], color[2]);
      const b = layer.boxes;
      for (let i = 0; i + 15 < b.length; i += 16) {
        const color = rgb[b[i + 14]], parts = partMatrices[b[i + 15]];
        const qx = b[i + 3] / 10000, qy = b[i + 4] / 10000, qz = b[i + 5] / 10000, qw = b[i + 6] / 10000;
        const known = inspectAt.get(b[i] + ',' + b[i + 1]);
        if (known && known.length) {
          pickable.push({
            item: known.shift(), layer: null, x: b[i] - centreX, y: b[i + 1] - centreY, z: b[i + 2] + b[i + 13] + b[i + 12] / 2,
            hx: b[i + 10] / 2 * b[i + 7] / 1000, hy: b[i + 11] / 2 * b[i + 8] / 1000, hz: b[i + 12] / 2 * b[i + 9] / 1000,
            yaw: Math.atan2(2 * (qw * qz + qx * qy), 1 - 2 * (qy * qy + qz * qz)), first: pickable.length,
          });
        }
        if (parts && parts.length) {
          // the real model: every part of the building, placed with the building's own position and rotation
          const building = transform(b[i] - centreX, b[i + 1] - centreY, b[i + 2], qx, qy, qz, qw, b[i + 7] / 1000, b[i + 8] / 1000, b[i + 9] / 1000);
          // shapes read better in a light tone with a hint of the map's colour than fully coloured
          const tint = [0.5 + color[0] * 0.38, 0.5 + color[1] * 0.38, 0.5 + color[2] * 0.38];
          for (const part of parts) {
            let list = perMesh.get(part.mesh);
            if (!list) perMesh.set(part.mesh, list = []);
            list.push(...combine(building, part.matrix), tint[0], tint[1], tint[2]);
            triangleCount += F.meshes[part.mesh][1] / 3;
          }
          modelCount++;
        } else {
          const yaw = Math.atan2(2 * (qw * qz + qx * qy), 1 - 2 * (qy * qy + qz * qz));
          add(b[i] - centreX, b[i + 2] + b[i + 13] + b[i + 12] / 2, b[i + 1] - centreY, b[i + 10] / 2, b[i + 12] / 2, b[i + 11] / 2, yaw, 0, color);
        }
      }
      const l = layer.lines;
      let lineCount = 0;
      for (let i = 0; i + 2 < l.length;) {
        const color = rgb[l[i]], width = l[i + 1], cls = l[i + 2], count = l[i + 3];
        // belts and rails are flat, pipes, hypertubes and wires round
        const thickness = layer.id === 'belts' ? 30 : layer.id === 'railways' ? 40 : layer.id === 'power' ? 25 : width;
        const wide = layer.id === 'power' ? 25 : width;
        i += 4;
        lineCount++;
        const parts = cls >= 0 ? partMatrices[cls] : null, pieceLength = parts && parts.length ? F.pieces[cls] : 0;
        if (pieceLength > 0) {
          // The real thing: the game's piece of belt or pipe, repeated along the path and stretched to fit,
          // as the game does it. Each piece runs straight from one point of the path to the next.
          const at = [0];
          for (let p = 0; p + 1 < count; p++) { const o = i + p * 3; at.push(at[p] + Math.hypot(l[o + 3] - l[o], l[o + 4] - l[o + 1], l[o + 5] - l[o + 2])); }
          const total = at[count - 1], pieces = Math.max(1, Math.round(total / pieceLength));
          const point = d => {
            let p = 0;
            while (p + 2 < count && at[p + 1] < d) p++;
            const o = i + p * 3, t = at[p + 1] > at[p] ? (d - at[p]) / (at[p + 1] - at[p]) : 0;
            return [l[o] + (l[o + 3] - l[o]) * t, l[o + 1] + (l[o + 4] - l[o + 1]) * t, l[o + 2] + (l[o + 5] - l[o + 2]) * t];
          };
          const tint = [0.5 + color[0] * 0.38, 0.5 + color[1] * 0.38, 0.5 + color[2] * 0.38];
          let a = point(0);
          for (let k = 1; k <= pieces && total > 1; k++) {
            const b = point(total * k / pieces), x = [b[0] - a[0], b[1] - a[1], b[2] - a[2]], length = Math.hypot(x[0], x[1], x[2]);
            if (length > 0.5) {
              for (const part of parts) {
                const native = F.meshes[part.mesh][5] + F.meshes[part.mesh][8]; // the piece reaches from 0 to here along x
                // sideways is level, up is at a right angle to both
                let y = [-x[1], x[0], 0];
                const flat = Math.hypot(y[0], y[1]);
                y = flat > length * 0.02 ? [y[0] / flat, y[1] / flat, 0] : [0, 1, 0];
                const z = [(x[1] * y[2] - x[2] * y[1]) / length, (x[2] * y[0] - x[0] * y[2]) / length, (x[0] * y[1] - x[1] * y[0]) / length], s = 1 / native;
                let list = perMesh.get(part.mesh);
                if (!list) perMesh.set(part.mesh, list = []);
                list.push(x[0] * s, y[0], z[0], a[0] - centreX, x[1] * s, y[1], z[1], a[1] - centreY, x[2] * s, y[2], z[2], a[2], tint[0], tint[1], tint[2]);
                triangleCount += F.meshes[part.mesh][1] / 3;
              }
            }
            a = b;
          }
          i += count * 3;
          continue;
        }
        for (let p = 0; p + 1 < count; p++) {
          const o = i + p * 3;
          const dx = l[o + 3] - l[o], dz = l[o + 4] - l[o + 1], dy = l[o + 5] - l[o + 2], length = Math.hypot(dx, dy, dz);
          if (length < 1) continue;
          add((l[o] + l[o + 3]) / 2 - centreX, (l[o + 2] + l[o + 5]) / 2 + thickness / 2, (l[o + 1] + l[o + 4]) / 2 - centreY,
            length / 2 + wide * 0.25, thickness / 2, wide / 2, Math.atan2(dz, dx), Math.asin(dy / length), color);
        }
        i += count * 3;
      }
      const entry = { id: layer.id, title: layer.title, visible: remembered[layer.id] ?? layer.visible, data: new Float32Array(out), count: out.length / 11, pieces: layer.boxes.length / 16 + lineCount };
      if (out.length || perMesh.size) factory.push(entry);
      for (const p of pickable) if (!p.layer) p.layer = entry;
      for (const [mesh, data] of perMesh) meshInstances.push({ layer: entry, mesh, data: new Float32Array(data), count: data.length / 15 });
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
    out vec3 vNormal, vWorld, vTint;
    vec3 turned(vec3 v) {
      float cp = cos(turn.y), sp = sin(turn.y), cy = cos(turn.x), sy = sin(turn.x);
      v = vec3(v.x * cp - v.y * sp, v.x * sp + v.y * cp, v.z);   // tilt along its length
      return vec3(v.x * cy - v.z * sy, v.y, v.x * sy + v.z * cy); // then turn on the ground
    }
    void main() {
      vec3 p = centre + turned(corner * halfSize);
      vWorld = p;
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
    if (!layer.count) continue;
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
  // The real models: one vertex and index buffer per mesh, one instance buffer per layer and mesh.
  const models = program(`#version 300 es
    layout(location = 0) in vec3 position;  // 16 bit, to be scaled into the mesh's bounds
    layout(location = 1) in vec3 normal;
    layout(location = 2) in vec4 row0;      // the instance's 3×4 matrix, in game coordinates (x east, y south, z up)
    layout(location = 3) in vec4 row1;
    layout(location = 4) in vec4 row2;
    layout(location = 5) in vec3 tint;
    layout(location = 6) in vec2 uv;
    uniform mat4 viewProjection;
    uniform vec3 boundsMin, boundsSize;
    out vec3 vNormal, vWorld, vTint;
    out vec2 vUv;
    void main() {
      vUv = uv;
      vec4 local = vec4(boundsMin + (position + 32768.0) / 65535.0 * boundsSize, 1.0);
      vec3 p = vec3(dot(row0, local), dot(row1, local), dot(row2, local));
      vWorld = vec3(p.x, p.z, p.y);
      vec3 n = vec3(dot(row0.xyz, normal), dot(row1.xyz, normal), dot(row2.xyz, normal));
      vNormal = vec3(n.x, n.z, n.y);
      vTint = tint;
      gl_Position = viewProjection * vec4(vWorld, 1.0);
    }`, `#version 300 es
    precision highp float;
    precision highp sampler2DArray;
    in vec3 vNormal, vWorld, vTint;
    in vec2 vUv;
    uniform vec3 eye, sun, haze;
    uniform int kind;               // 0 one colour, 1 texture image, 2 the game's shared factory material, 3 painted texture
    uniform vec3 flatColor, primaryPaint, secondaryPaint;
    uniform sampler2D albedo;
    uniform sampler2DArray factoryLayers;
    out vec4 color;
    void main() {
      vec3 base;
      if (kind == 1) {
        base = texture(albedo, vUv).rgb;
      } else if (kind == 3) {
        // a painted surface: paint where the texture is dark, the texture's bare metal elsewhere
        vec3 bare = texture(albedo, vUv).rgb;
        float tone = dot(bare, vec3(0.299, 0.587, 0.114));
        base = mix(flatColor * (0.8 + tone), bare, smoothstep(0.24, 0.36, tone));
      } else if (kind == 2) {
        // The texture coordinate lies in one of 3 × 3 squares; the square says which of the nine surface
        // types the face has. Inside the square the surface repeats.
        vec2 square = clamp(floor(fract(vUv) * 3.0), 0.0, 2.0);
        // the layers are counted from the bottom row of squares upwards
        float layer = square.x + (2.0 - square.y) * 3.0;
        base = texture(factoryLayers, vec3(fract(vUv * 3.0) * 4.0, layer)).rgb;
        // the painted metals are white in the texture and take the factory's colours
        if (abs(layer - 1.0) < 0.5) base *= primaryPaint;
        else if (layer < 0.5) base *= secondaryPaint;
      } else {
        base = flatColor;
      }
      vec3 n = normalize(vNormal);
      float light = 0.40 + 0.50 * max(dot(n, sun), 0.0) + 0.12 * n.y;
      float fog = smoothstep(0.55, 1.6, distance(vWorld, eye) / 900000.0);
      color = vec4(mix(base * light, haze, fog * 0.55), 1.0);
    }`);

  // Materials: texture images load in the background and appear when they are there.
  const hex = c => { const n = parseInt(String(c).slice(1), 16) || 0x9aa0a6; return [(n >> 16 & 255) / 255, (n >> 8 & 255) / 255, (n & 255) / 255]; };
  const srgb = c => c.map(v => Math.pow(v, 1.0)); // colours are used as they are, like the textures
  const materials = (F && F.materials || []).map(([kind, value]) => {
    const textured = kind.startsWith('texture');
    // pipes in the paint they have as standard, FICSIT orange
    const material = { kind: kind === 'texture-painted' ? 3 : textured ? 1 : kind === 'factory' ? 2 : 0, color: kind === 'flat' ? srgb(hex(value)) : kind === 'texture-painted' ? (value.startsWith('TX_Pipe_') ? [0.93, 0.50, 0.20] : [0.98, 0.58, 0.29]) : [0.62, 0.64, 0.66], texture: null };
    if (textured && modelData) {
      loadImage(`terrain/textures/${value}`).then(img => {
        const t = gl.createTexture();
        gl.activeTexture(gl.TEXTURE2);
        gl.bindTexture(gl.TEXTURE_2D, t);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, img);
        const wrap = kind === 'texture-mirrored' ? gl.MIRRORED_REPEAT : gl.REPEAT;
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, wrap);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, wrap);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
        gl.generateMipmap(gl.TEXTURE_2D);
        material.texture = t;
        needsDraw = true;
      }).catch(() => { /* stays grey */ });
    }
    return material;
  });
  let factoryTexture = null;
  if (modelData && F.factoryLayers && F.factoryLayers.length === 9) {
    Promise.all(F.factoryLayers.map(f => loadImage(`terrain/textures/${f}`))).then(images => {
      const size = images[0].width, sheet = document.createElement('canvas');
      sheet.width = size; sheet.height = size * images.length;
      const sc = sheet.getContext('2d');
      images.forEach((img, i) => sc.drawImage(img, 0, i * size, size, size));
      const t = gl.createTexture();
      gl.activeTexture(gl.TEXTURE3);
      gl.bindTexture(gl.TEXTURE_2D_ARRAY, t);
      gl.texImage3D(gl.TEXTURE_2D_ARRAY, 0, gl.RGBA, size, size, images.length, 0, gl.RGBA, gl.UNSIGNED_BYTE, sc.getImageData(0, 0, size, size * images.length).data);
      gl.texParameteri(gl.TEXTURE_2D_ARRAY, gl.TEXTURE_WRAP_S, gl.REPEAT);
      gl.texParameteri(gl.TEXTURE_2D_ARRAY, gl.TEXTURE_WRAP_T, gl.REPEAT);
      gl.texParameteri(gl.TEXTURE_2D_ARRAY, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
      gl.generateMipmap(gl.TEXTURE_2D_ARRAY);
      factoryTexture = t;
      needsDraw = true;
    }).catch(() => { /* the factory surfaces stay grey */ });
  }
  const meshBuffers = new Map();
  for (const item of meshInstances) {
    const [vertexCount, indexCount, wide, vertexOffset, indexOffset, minX, minY, minZ, sizeX, sizeY, sizeZ, ...sectionNumbers] = F.meshes[item.mesh];
    const sections = [];
    for (let i = 0; i + 2 < sectionNumbers.length; i += 3) sections.push({ first: sectionNumbers[i], count: sectionNumbers[i + 1], material: materials[sectionNumbers[i + 2]] });
    let shared = meshBuffers.get(item.mesh);
    if (!shared) {
      shared = { vertices: gl.createBuffer(), indices: gl.createBuffer() };
      gl.bindVertexArray(null);
      gl.bindBuffer(gl.ARRAY_BUFFER, shared.vertices);
      gl.bufferData(gl.ARRAY_BUFFER, new Uint8Array(modelData, vertexOffset, vertexCount * 20), gl.STATIC_DRAW);
      gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, shared.indices);
      gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, new Uint8Array(modelData, indexOffset, indexCount * (wide ? 4 : 2)), gl.STATIC_DRAW);
      meshBuffers.set(item.mesh, shared);
    }
    item.vao = gl.createVertexArray();
    gl.bindVertexArray(item.vao);
    gl.bindBuffer(gl.ARRAY_BUFFER, shared.vertices);
    gl.enableVertexAttribArray(0); gl.vertexAttribPointer(0, 3, gl.SHORT, false, 20, 0);
    gl.enableVertexAttribArray(1); gl.vertexAttribPointer(1, 3, gl.BYTE, true, 20, 6);
    gl.enableVertexAttribArray(6); gl.vertexAttribPointer(6, 2, gl.FLOAT, false, 20, 12);
    gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, shared.indices);
    gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
    gl.bufferData(gl.ARRAY_BUFFER, item.data, gl.STATIC_DRAW);
    for (const [location, size, offset] of [[2, 4, 0], [3, 4, 16], [4, 4, 32], [5, 3, 48]]) {
      gl.enableVertexAttribArray(location);
      gl.vertexAttribPointer(location, size, gl.FLOAT, false, 60, offset);
      gl.vertexAttribDivisor(location, 1);
    }
    Object.assign(item, { sections, indexBytes: wide ? 4 : 2, indexType: wide ? gl.UNSIGNED_INT : gl.UNSIGNED_SHORT, min: [minX, minY, minZ], size: [sizeX, sizeY, sizeZ], data: null });
  }
  modelData = null;
  gl.bindVertexArray(vao);

  // One tick box per factory layer, as on the 2D map.
  for (const layer of factory) {
    const label = document.createElement('label');
    label.className = 'option';
    label.innerHTML = `<input type="checkbox"> <span></span> <span class="count"></span>`;
    label.querySelector('span').textContent = layer.title;
    label.querySelector('.count').textContent = (layer.pieces || layer.count).toLocaleString('en-US');
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

  // The game's own ground: a map of which ground texture lies where (and how deep the water is), and one
  // tiling texture per layer. Until both are loaded, and without them, the in-game map lies on the terrain.
  const G = D.ground;
  let groundReady = false;
  if (G && G.layers && G.layers.length && gl.getParameter(gl.MAX_TEXTURE_SIZE) >= 4096) {
    Promise.all([loadImage('terrain/ground/ground.png'), ...G.layers.map(f => loadImage(`terrain/ground/${f}`))]).then(([mapImage, ...images]) => {
      gl.activeTexture(gl.TEXTURE4);
      gl.bindTexture(gl.TEXTURE_2D, gl.createTexture());
      // the numbers in this image are not colours: take them as they are
      gl.pixelStorei(gl.UNPACK_COLORSPACE_CONVERSION_WEBGL, gl.NONE);
      gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, false);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGB8, gl.RGB, gl.UNSIGNED_BYTE, mapImage);
      gl.pixelStorei(gl.UNPACK_COLORSPACE_CONVERSION_WEBGL, gl.BROWSER_DEFAULT_WEBGL);
      for (const [name, value] of [[gl.TEXTURE_MIN_FILTER, gl.NEAREST], [gl.TEXTURE_MAG_FILTER, gl.NEAREST], [gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE], [gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE]]) gl.texParameteri(gl.TEXTURE_2D, name, value);

      const size = images[0].width, sheet = document.createElement('canvas');
      sheet.width = size; sheet.height = size * images.length;
      const sc = sheet.getContext('2d');
      images.forEach((img, i) => sc.drawImage(img, 0, i * size, size, size));
      gl.activeTexture(gl.TEXTURE5);
      gl.bindTexture(gl.TEXTURE_2D_ARRAY, gl.createTexture());
      gl.texImage3D(gl.TEXTURE_2D_ARRAY, 0, gl.RGBA, size, size, images.length, 0, gl.RGBA, gl.UNSIGNED_BYTE, sc.getImageData(0, 0, size, size * images.length).data);
      gl.texParameteri(gl.TEXTURE_2D_ARRAY, gl.TEXTURE_WRAP_S, gl.REPEAT);
      gl.texParameteri(gl.TEXTURE_2D_ARRAY, gl.TEXTURE_WRAP_T, gl.REPEAT);
      gl.texParameteri(gl.TEXTURE_2D_ARRAY, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
      gl.generateMipmap(gl.TEXTURE_2D_ARRAY);
      const anisotropy = gl.getExtension('EXT_texture_filter_anisotropic');
      if (anisotropy) gl.texParameterf(gl.TEXTURE_2D_ARRAY, anisotropy.TEXTURE_MAX_ANISOTROPY_EXT, Math.min(8, gl.getParameter(anisotropy.MAX_TEXTURE_MAX_ANISOTROPY_EXT)));
      gl.activeTexture(gl.TEXTURE0);
      groundReady = true;
      needsDraw = true;
    }).catch(() => { $('opt-ground-row').hidden = true; });
    try { $('opt-ground').checked = localStorage.getItem('smr:ground3d') !== 'false'; } catch { /* storage unavailable */ }
    $('opt-ground').addEventListener('change', () => {
      try { localStorage.setItem('smr:ground3d', $('opt-ground').checked); } catch { /* storage unavailable */ }
      needsDraw = true;
    });
  } else {
    $('opt-ground-row').hidden = true;
  }

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
    const tx = view.target[0], tz = view.target[2];
    const ty = groundAt(tx + W.minX + W.size / 2, tz + W.minY + W.size / 2);
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
    const show = $('opt-markers').checked;
    for (const l of labels) {
      // The marker's own height, but never below the ground the view draws there.
      const ground = groundAt(l.x, l.y);
      const p = show && project(l.x - (W.minX + W.size / 2), Math.max(l.z, ground), l.y - (W.minY + W.size / 2));
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
    gl.uniform1i(u('map'), 0);
    gl.uniform1i(u('overlay'), 1);
    gl.uniform1f(u('overlayOpacity'), $('opt-factory').checked ? 1 : 0);
    gl.uniform1i(u('ground'), 4);
    gl.uniform1i(u('groundLayers'), 5);
    gl.uniform1i(u('useGround'), groundReady && $('opt-ground').checked ? 1 : 0);
    if (G) {
      gl.uniform4f(u('groundPlace'), G.minX - centreX, G.minY - centreY, G.cell, G.tile);
      gl.uniform1f(u('depthStep'), G.depthStep);
      gl.uniform1f(u('cliffLayer'), G.layers.indexOf('cliff.jpg'));
    }
    gl.uniform3fv(u('eye'), eye);
    gl.uniform3fv(u('sun'), norm([-0.55, 0.62, -0.55])); // from the north-west, as on the relief map
    gl.uniform3fv(u('haze'), sky);
    gl.bindVertexArray(vao);
    gl.drawElements(gl.TRIANGLES, indices.length, gl.UNSIGNED_INT, 0);

    if (factory.some(f => f.visible)) {
      gl.useProgram(boxes);
      const b = name => gl.getUniformLocation(boxes, name);
      gl.uniformMatrix4fv(b('viewProjection'), false, matrix);
      gl.uniform3fv(b('eye'), eye);
      gl.uniform3fv(b('sun'), norm([-0.55, 0.62, -0.55]));
      gl.uniform3fv(b('haze'), sky);
      for (const layer of factory) {
        if (!layer.visible || !layer.count) continue;
        gl.bindVertexArray(layer.vao);
        gl.drawArraysInstanced(gl.TRIANGLES, 0, 36, layer.count);
      }

      if (meshInstances.length) {
        gl.useProgram(models);
        const m = name => gl.getUniformLocation(models, name);
        gl.uniformMatrix4fv(m('viewProjection'), false, matrix);
        gl.uniform3fv(m('eye'), eye);
        gl.uniform3fv(m('sun'), norm([-0.55, 0.62, -0.55]));
        gl.uniform3fv(m('haze'), sky);
        const boundsMin = m('boundsMin'), boundsSize = m('boundsSize'), kind = m('kind'), flatColor = m('flatColor');
        gl.uniform1i(m('albedo'), 2);
        gl.uniform1i(m('factoryLayers'), 3);
        gl.uniform3fv(m('primaryPaint'), [0.98, 0.58, 0.29]);   // the factory's standard colours: FICSIT orange
        gl.uniform3fv(m('secondaryPaint'), [0.37, 0.40, 0.55]); // and blue-grey
        if (factoryTexture) { gl.activeTexture(gl.TEXTURE3); gl.bindTexture(gl.TEXTURE_2D_ARRAY, factoryTexture); }
        gl.activeTexture(gl.TEXTURE2);
        for (const item of meshInstances) {
          if (!item.layer.visible) continue;
          gl.uniform3fv(boundsMin, item.min);
          gl.uniform3fv(boundsSize, item.size);
          gl.bindVertexArray(item.vao);
          for (const section of item.sections) {
            const material = section.material || { kind: 0, color: [0.62, 0.64, 0.66] };
            // until its image is there, a textured surface is drawn in plain grey
            const ready = material.kind === 1 || material.kind === 3 ? material.texture : material.kind === 2 ? factoryTexture : true;
            gl.uniform1i(kind, ready ? material.kind : 0);
            gl.uniform3fv(flatColor, material.color);
            if ((material.kind === 1 || material.kind === 3) && material.texture) gl.bindTexture(gl.TEXTURE_2D, material.texture);
            gl.drawElementsInstanced(gl.TRIANGLES, section.count, item.indexType, section.first * item.indexBytes, item.count);
          }
        }
        gl.activeTexture(gl.TEXTURE0);
      }
    }
    placeLabels();
    placePopup();
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
  // Left or right button turns the view; the wheel button (or Shift with any button) moves it.
  canvas.addEventListener('pointerdown', e => { canvas.setPointerCapture(e.pointerId); pointers.set(e.pointerId, { x: e.clientX, y: e.clientY, pan: e.button === 1 || e.shiftKey }); });
  // no scroll cursor of the browser while the wheel button is held
  canvas.addEventListener('mousedown', e => { if (e.button === 1) e.preventDefault(); });
  canvas.addEventListener('auxclick', e => e.preventDefault());
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
  /** The point of the terrain under a position of the canvas (view coordinates), or null where there is only sky. */
  /** Direction of the line of sight through a position of the canvas (view coordinates), from the eye. */
  function sightLine(clientX, clientY) {
    updateCamera();
    const box = canvas.getBoundingClientRect(), half = Math.tan(0.4);
    const nx = ((clientX - box.left) / box.width * 2 - 1) * half * box.width / box.height, ny = (1 - (clientY - box.top) / box.height * 2) * half;
    const ty = groundAt(view.target[0] + centreX, view.target[2] + centreY);
    const f = norm(sub([view.target[0], ty, view.target[2]], eye)), s = norm(cross(f, [0, 1, 0])), u = cross(s, f);
    return norm([f[0] + s[0] * nx + u[0] * ny, f[1] + s[1] * nx + u[1] * ny, f[2] + s[2] * nx + u[2] * ny]);
  }
  function terrainUnder(clientX, clientY) {
    const d = sightLine(clientX, clientY);
    const above = t => eye[1] + d[1] * t - groundAt(eye[0] + d[0] * t + centreX, eye[2] + d[2] * t + centreY);
    // walk along the line of sight until it is below the ground, then narrow the step down
    const reach = view.distance * 6 + W.size, step = Math.max(view.distance / 150, 200);
    for (let t = step; t < reach; t += step) {
      if (above(t) > 0) continue;
      let lo = t - step, hi = t;
      for (let i = 0; i < 12; i++) { const mid = (lo + hi) / 2; if (above(mid) > 0) lo = mid; else hi = mid; }
      return [eye[0] + d[0] * hi, eye[2] + d[2] * hi, hi];
    }
    return null;
  }

  // ---------------------------------------------------------------- click on a building: what the 2D map shows

  const popup = $('terrain-popup');
  let shown = null;
  const esc = s => String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const metres = v => Math.round(v / 100).toLocaleString('en-US');
  function closePopup() { shown = null; popup.hidden = true; }
  function openPopup(p) {
    const [x, y, , name, , group, details, iconIndex = -1] = p.item;
    const rows = (details || []).map(([k, v]) => `<tr><td>${esc(k)}</td><td>${esc(v)}</td></tr>`).join('');
    const icon = iconIndex >= 0 && D.icons && D.icons[iconIndex] ? `<img class="popup-icon" src="${D.icons[iconIndex]}" alt="">` : '';
    popup.innerHTML = `<button type="button" class="close" aria-label="Close">×</button>${icon}<h3>${esc(name)}</h3>`
      + `<div class="sub">Building · ${esc(group)}<br>X ${metres(x)} m · Y ${metres(y)} m · Z ${metres(p.z - p.hz)} m</div>${rows ? `<table>${rows}</table>` : ''}`;
    popup.querySelector('.close').addEventListener('click', closePopup);
    shown = p;
    popup.hidden = false;
    needsDraw = true;
  }
  function placePopup() {
    if (!shown) return;
    if (!shown.layer.visible) return closePopup();
    const at = project(shown.x, shown.z + shown.hz, shown.y);
    popup.style.visibility = at ? 'visible' : 'hidden';
    if (!at) return;
    // above the building, but never outside the window
    const w = popup.offsetWidth, h = popup.offsetHeight;
    const left = Math.min(Math.max(at[0] - w / 2, 8), Math.max(canvas.clientWidth - w - 8, 8)), top = Math.min(Math.max(at[1] - h - 12, 8), Math.max(canvas.clientHeight - h - 8, 8));
    popup.style.transform = `translate(${Math.round(left)}px, ${Math.round(top)}px)`;
    popup.classList.toggle('moved', Math.abs(left - (at[0] - w / 2)) > 1 || Math.abs(top - (at[1] - h - 12)) > 1);
  }
  /** The nearest clickable building on the line of sight, if it is in front of the terrain. */
  function buildingUnder(clientX, clientY) {
    const d = sightLine(clientX, clientY), ground = terrainUnder(clientX, clientY);
    let best = null, nearest = ground ? ground[2] + 200 : Infinity;
    for (const p of pickable) {
      if (!p.layer || !p.layer.visible) continue;
      // the line of sight in the box's own directions (game axes: x east, y south, z up)
      const c = Math.cos(p.yaw), s = Math.sin(p.yaw);
      const ox = eye[0] - p.x, oy = eye[2] - p.y, oz = eye[1] - p.z;
      const o = [ox * c + oy * s, -ox * s + oy * c, oz], dir = [d[0] * c + d[2] * s, -d[0] * s + d[2] * c, d[1]], half = [p.hx, p.hy, p.hz];
      let enter = 0, leave = nearest;
      for (let a = 0; a < 3 && enter <= leave; a++) {
        if (Math.abs(dir[a]) < 1e-9) { if (Math.abs(o[a]) > half[a]) enter = Infinity; continue; }
        let t0 = (-half[a] - o[a]) / dir[a], t1 = (half[a] - o[a]) / dir[a];
        if (t0 > t1) [t0, t1] = [t1, t0];
        enter = Math.max(enter, t0); leave = Math.min(leave, t1);
      }
      if (enter <= leave && enter < nearest) { nearest = enter; best = p; }
    }
    return best;
  }
  let pressed = null;
  canvas.addEventListener('pointerdown', e => { pressed = e.button === 0 && !e.shiftKey ? { x: e.clientX, y: e.clientY } : null; });
  canvas.addEventListener('pointerup', e => {
    if (!pressed || Math.hypot(e.clientX - pressed.x, e.clientY - pressed.y) > 4) return;
    const hit = buildingUnder(e.clientX, e.clientY);
    if (hit) openPopup(hit); else closePopup();
  });
  canvas.addEventListener('pointermove', e => { if (!pointers.size) canvas.style.cursor = pickable.length && buildingUnder(e.clientX, e.clientY) ? 'pointer' : ''; });
  window.addEventListener('keydown', e => { if (e.key === 'Escape') closePopup(); });
  // The wheel zooms towards what is under the mouse: that point stays where it is on the screen.
  canvas.addEventListener('wheel', e => {
    e.preventDefault();
    const before = view.distance, under = terrainUnder(e.clientX, e.clientY);
    view.distance = Math.min(Math.max(before * Math.exp(e.deltaY * 0.0012), 3000), W.size * 1.6);
    if (under) {
      const k = view.distance / before;
      view.target[0] = under[0] + (view.target[0] - under[0]) * k;
      view.target[2] = under[1] + (view.target[2] - under[1]) * k;
    }
    clampView();
  }, { passive: false });
  window.addEventListener('resize', () => { needsDraw = true; });
  window.addEventListener('keydown', e => {
    const step = 40;
    if (e.key === 'ArrowLeft') pan(step, 0); else if (e.key === 'ArrowRight') pan(-step, 0);
    else if (e.key === 'ArrowUp') pan(0, step); else if (e.key === 'ArrowDown') pan(0, -step);
    else if (e.key === '+' || e.key === '=') view.distance *= 0.85; else if (e.key === '-') view.distance *= 1.18;
    else return;
    clampView();
  });

  for (const id of ['opt-factory', 'opt-markers']) $(id).addEventListener('change', () => { needsDraw = true; });
  window.addEventListener('keydown', e => { if (e.key === 'f' && !e.ctrlKey && !e.metaKey) reset(); });
  $('terrain-reset').addEventListener('click', reset);

  window.satisfactoryTerrain = { view, redraw: () => { needsDraw = true; }, pickable: pickable.length, project, models: modelCount, triangles: Math.round(triangleCount) }; // for automated tests and the console
  status('');
  requestAnimationFrame(frame);
})().catch(e => {
  const s = document.getElementById('terrain-status');
  s.hidden = false; s.dataset.kind = 'error'; s.textContent = `The 3D view could not start: ${e.message}`;
});
