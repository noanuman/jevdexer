/* The mascot as a jelly.
   The illustration is drawn to a texture and stretched over a triangle mesh that fills the body.
   The mesh is a soft body solved with XPBD in small substeps: every edge and diagonal is a soft
   spring, every triangle keeps its area (jelly does not compress), and a weak shape match pulls
   the whole body back to its drawn shape. Grab a spot and that spot follows the pointer while the
   rest hangs from it under gravity and sloshes behind fast moves. Let go and it falls, splats on
   the floor, sits for a moment, then hops back home. A click without a drag pokes it. */
(function () {
  const home = document.querySelector('[data-mascot-home]');
  if (!home) return;
  const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  const BODY = 'M57.90 8.08 C54.94 8.19 51.98 9.04 49.03 10.46 C46.07 11.89 43.11 14.27 40.16 16.63 C37.20 18.99 34.24 22.47 31.29 24.63 C28.33 26.80 25.28 27.48 22.42 29.61 C19.55 31.74 16.16 34.62 14.09 37.40 C12.01 40.17 10.90 43.31 9.98 46.27 C9.06 49.22 9.31 52.18 8.57 55.14 C7.83 58.09 6.34 61.05 5.54 64.01 C4.75 66.96 3.11 70.08 3.81 72.88 C4.51 75.67 7.29 78.95 9.76 80.77 C12.23 82.59 15.67 82.36 18.63 83.80 C21.59 85.25 24.54 88.11 27.50 89.43 C30.46 90.74 33.41 91.36 36.37 91.70 C39.33 92.04 42.28 91.66 45.24 91.48 C48.20 91.30 51.15 90.76 54.11 90.62 C57.07 90.47 60.02 90.78 62.98 90.62 C65.94 90.46 68.89 90.35 71.85 89.64 C74.81 88.94 77.78 88.11 80.72 86.40 C83.66 84.69 87.17 82.02 89.48 79.37 C91.79 76.72 93.43 73.45 94.57 70.50 C95.70 67.54 96.21 64.58 96.30 61.63 C96.39 58.67 95.97 55.71 95.11 52.76 C94.24 49.80 92.91 46.85 91.10 43.89 C89.30 40.93 86.40 37.98 84.29 35.02 C82.18 32.06 80.07 29.11 78.45 26.15 C76.83 23.19 76.50 20.00 74.55 17.28 C72.61 14.56 69.54 11.35 66.77 9.82 C63.99 8.28 60.85 7.98 57.90 8.08 Z';
  const body = new Path2D(BODY);

  // ---- The texture: the illustration, redrawn when the eyes or mouth change ----
  const TEX = 512, U0 = -10, US = 120;              // texture covers mascot units [-10, 110]
  const K = TEX / US;
  const tex = document.createElement('canvas'); tex.width = tex.height = TEX;
  const tx = tex.getContext('2d');
  const face = { px: 0, py: 0, blink: 0, mouth: 0 }; // pupils (units), eyelids 0..1, mouth 0 smile .. 1 big
  function draw() {
    tx.setTransform(1, 0, 0, 1, 0, 0);
    tx.clearRect(0, 0, TEX, TEX);
    tx.setTransform(K, 0, 0, K, -U0 * K, -U0 * K);
    // Body with a soft glow, then a thin rim of light so it reads on black.
    const g = tx.createRadialGradient(38.3, 33, 0, 38.3, 33, 75);
    g.addColorStop(0, '#2f2f34'); g.addColorStop(0.55, '#151518'); g.addColorStop(1, '#070708');
    tx.save();
    tx.shadowColor = 'rgba(255,255,255,0.10)'; tx.shadowBlur = 10 * K;
    tx.fillStyle = g; tx.fill(body);
    tx.restore();
    tx.strokeStyle = 'rgba(255,255,255,0.28)'; tx.lineWidth = 0.7; tx.stroke(body);
    eye(39.65, 56.85, 6.72, 7.03, 42.32, 56.62, 4.93, 44.05, 54.65, 1.38);
    eye(69.78, 52.96, 6.45, 6.84, 67.49, 53.4, 5.03, 69.25, 51.39, 1.41);
    // Mouth: a smile that opens wider (face.mouth).
    const m = face.mouth;
    tx.beginPath();
    tx.moveTo(51.41 - 0.8 * m, 60.87 - 0.47 * m);
    tx.quadraticCurveTo(55.52, 63.53 + 3.07 * m, 59.63 + 0.77 * m, 60.65 - 0.45 * m);
    tx.strokeStyle = '#f4f2ee'; tx.lineWidth = 1.57; tx.lineCap = 'round'; tx.stroke();
  }
  function eye(cx, cy, rx, ry, px, py, pr, sx, sy, sr) {
    const open = 1 - face.blink * 0.92;
    tx.save();
    tx.translate(cx, cy); tx.scale(1, open); tx.translate(-cx, -cy);
    tx.beginPath(); tx.ellipse(cx, cy, rx, ry, 0, 0, Math.PI * 2);
    tx.fillStyle = '#f4f2ee'; tx.fill();
    tx.clip();
    tx.beginPath(); tx.arc(px + face.px, py + face.py, pr, 0, Math.PI * 2); tx.fillStyle = '#0b0b0c'; tx.fill();
    tx.beginPath(); tx.arc(sx + face.px, sy + face.py, sr, 0, Math.PI * 2); tx.fillStyle = '#f4f2ee'; tx.fill();
    tx.restore();
  }
  draw();
  // Alpha of the body, for hit testing (the drawing is only redrawn inside the eyes and mouth).
  const alpha = (function () {
    const d = tx.getImageData(0, 0, TEX, TEX).data, a = new Uint8Array(TEX * TEX);
    for (let i = 0; i < a.length; i++) a[i] = d[i * 4 + 3];
    return a;
  })();

  // ---- The mesh: grid cells that touch the body (plus a little for the glow) ----
  const outline = [];
  (function () {
    const p = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    p.setAttribute('d', BODY);
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.style.cssText = 'position:absolute;width:0;height:0;visibility:hidden';
    svg.appendChild(p); document.body.appendChild(svg);
    const len = p.getTotalLength();
    for (let i = 0; i < 240; i++) { const q = p.getPointAtLength(len * i / 240); outline.push(q.x, q.y); }
    svg.remove();
  })();
  const probe = document.createElement('canvas').getContext('2d');
  const inside = (u, v) => probe.isPointInPath(body, u, v);
  function outside(u, v) {         // distance outside the body, in units (0 inside)
    if (inside(u, v)) return 0;
    let best = 1e9;
    for (let i = 0; i < outline.length; i += 2) best = Math.min(best, Math.hypot(outline[i] - u, outline[i + 1] - v));
    return best;
  }
  const STEP = 6, GX0 = -2, GY0 = 4, NX = 18, NY = 16;
  const vid = new Int32Array((NX + 1) * (NY + 1)).fill(-1);
  const ru = [], rv = [], out = [];                 // rest position (units) and outside distance per vertex
  const tris = [], edges = [];
  function vert(i, j) {
    const k = j * (NX + 1) + i;
    if (vid[k] < 0) {
      const u = GX0 + i * STEP, v = GY0 + j * STEP;
      vid[k] = ru.length; ru.push(u); rv.push(v); out.push(outside(u, v));
    }
    return vid[k];
  }
  const edgeSet = new Set();
  function edge(a, b, shear) {
    const key = a < b ? a + ',' + b : b + ',' + a;
    if (!edgeSet.has(key)) { edgeSet.add(key); edges.push([a, b, shear]); }
  }
  for (let j = 0; j < NY; j++) for (let i = 0; i < NX; i++) {
    let keep = false;
    for (let a = 0; a <= 2 && !keep; a++) for (let b = 0; b <= 2 && !keep; b++) {
      const u = GX0 + (i + a / 2) * STEP, v = GY0 + (j + b / 2) * STEP;
      if (inside(u, v) || outside(u, v) < 3.5) keep = true;
    }
    if (!keep) continue;
    const a = vert(i, j), b = vert(i + 1, j), c = vert(i + 1, j + 1), d = vert(i, j + 1);
    // Alternate the diagonal so the mesh has no built-in lean.
    if ((i + j) % 2) { tris.push([a, b, c], [a, c, d]); } else { tris.push([a, b, d], [b, c, d]); }
    edge(a, b, 0); edge(b, c, 0); edge(c, d, 0); edge(d, a, 0); edge(a, c, 1); edge(b, d, 1);
  }
  const N = ru.length, T = tris.length, E = edges.length;

  // ---- WebGL ----
  const canvas = document.createElement('canvas');
  canvas.className = 'jelly';
  canvas.setAttribute('aria-hidden', 'true');
  document.body.appendChild(canvas);
  const gl = canvas.getContext('webgl', { alpha: true, premultipliedAlpha: true, antialias: true });
  if (!gl) { canvas.remove(); home.style.visibility = 'visible'; home.innerHTML = '<img src="assets/mascot-render-256.png" alt="" width="100%">'; return; }
  function shader(type, src) { const s = gl.createShader(type); gl.shaderSource(s, src); gl.compileShader(s); return s; }
  const prog = gl.createProgram();
  gl.attachShader(prog, shader(gl.VERTEX_SHADER,
    'attribute vec2 p;attribute vec2 t;uniform vec2 view;varying vec2 uv;' +
    'void main(){uv=t;gl_Position=vec4(p.x/view.x*2.0-1.0,1.0-p.y/view.y*2.0,0.0,1.0);}'));
  gl.attachShader(prog, shader(gl.FRAGMENT_SHADER,
    'precision mediump float;uniform sampler2D img;varying vec2 uv;void main(){gl_FragColor=texture2D(img,uv);}'));
  gl.linkProgram(prog); gl.useProgram(prog);
  const uvs = new Float32Array(N * 2);
  for (let i = 0; i < N; i++) { uvs[i * 2] = (ru[i] - U0) / US; uvs[i * 2 + 1] = (rv[i] - U0) / US; }
  const idx = new Uint16Array(T * 3);
  tris.forEach((t, k) => { idx[k * 3] = t[0]; idx[k * 3 + 1] = t[1]; idx[k * 3 + 2] = t[2]; });
  const posBuf = gl.createBuffer(), uvBuf = gl.createBuffer(), idxBuf = gl.createBuffer();
  gl.bindBuffer(gl.ARRAY_BUFFER, uvBuf); gl.bufferData(gl.ARRAY_BUFFER, uvs, gl.STATIC_DRAW);
  const locT = gl.getAttribLocation(prog, 't'); gl.enableVertexAttribArray(locT); gl.vertexAttribPointer(locT, 2, gl.FLOAT, false, 0, 0);
  gl.bindBuffer(gl.ARRAY_BUFFER, posBuf);
  const locP = gl.getAttribLocation(prog, 'p'); gl.enableVertexAttribArray(locP); gl.vertexAttribPointer(locP, 2, gl.FLOAT, false, 0, 0);
  gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, idxBuf); gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, idx, gl.STATIC_DRAW);
  const uView = gl.getUniformLocation(prog, 'view');
  const texture = gl.createTexture();
  gl.bindTexture(gl.TEXTURE_2D, texture);
  gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, true);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
  let texDirty = true;
  function upload() {
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, tex);
    gl.generateMipmap(gl.TEXTURE_2D);
    texDirty = false;
  }
  gl.enable(gl.BLEND); gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
  let W = 0, H = 0, dpr = 1;
  function resize() {
    dpr = Math.min(2, window.devicePixelRatio || 1);
    W = window.innerWidth; H = window.innerHeight;
    canvas.width = Math.round(W * dpr); canvas.height = Math.round(H * dpr);
    gl.viewport(0, 0, canvas.width, canvas.height);
  }
  resize();
  window.addEventListener('resize', () => { resize(); setScale(); });

  // ---- Simulation state ----
  const x = new Float64Array(N), y = new Float64Array(N), px = new Float64Array(N), py = new Float64Array(N);
  const vx = new Float64Array(N), vy = new Float64Array(N);
  const qx = new Float64Array(N), qy = new Float64Array(N);   // rest shape about its centre, px
  const L = new Float64Array(E), A0 = new Float64Array(T);
  const gw = new Float64Array(N);                               // grab weights
  let scale = 1.3, rcx = 50, rcy = 50;
  function homeRect() { return home.getBoundingClientRect(); }
  function setScale() {
    scale = (homeRect().width || 130) / 100;
    rcx = 0; rcy = 0;
    for (let i = 0; i < N; i++) { rcx += ru[i]; rcy += rv[i]; }
    rcx /= N; rcy /= N;
    for (let i = 0; i < N; i++) { qx[i] = (ru[i] - rcx) * scale; qy[i] = (rv[i] - rcy) * scale; }
    edges.forEach((e, k) => { L[k] = Math.hypot(qx[e[1]] - qx[e[0]], qy[e[1]] - qy[e[0]]); });
    tris.forEach((t, k) => { A0[k] = area(qx, qy, t[0], t[1], t[2]); });
  }
  function area(X, Y, a, b, c) { return 0.5 * ((X[b] - X[a]) * (Y[c] - Y[a]) - (Y[b] - Y[a]) * (X[c] - X[a])); }
  // Where the rest centre sits relative to the home box (the drawing's 50,50 is the box centre).
  function anchor() {
    const r = homeRect();
    return { x: r.left + r.width / 2 + (rcx - 50) * scale, y: r.top + r.height / 2 + (rcy - 50) * scale };
  }
  setScale();
  (function place() { const a = anchor(); for (let i = 0; i < N; i++) { x[i] = a.x + qx[i]; y[i] = a.y + qy[i]; } })();

  // Tuning. Compliance is inverse stiffness (XPBD); lengths in CSS px, time in seconds.
  const SUB = 12;
  const GRAVITY = 2300;
  const C_EDGE = 2.2e-5, C_SHEAR = 5e-5, C_AREA = 1e-6, C_SHAPE = 9e-4, C_GRAB = 6e-7;
  const RIGID_DAMP = 0.03, AIR = 0.4, FRICTION = 0.85, ROLL_DAMP = 0.06;
  const HOVER_K = 55, HOVER_Z = 0.55;

  let mode = 'home';                 // home: hovering at home | held | fall | hop
  let cx = 0, cy = 0, vcx = 0, vcy = 0, theta = 0;
  let restTime = 0, restSpeed = 1000, hopTime = 0, hopUntil = 0, time = 0, lastSplat = 0;
  const ptr = { x: -1e4, y: -1e4 }, aim = { x: 0, y: 0 }, lastPtr = { x: 0, y: 0 };
  let grabOffX = 0, grabOffY = 0;

  function centre() {
    cx = cy = vcx = vcy = 0;
    for (let i = 0; i < N; i++) { cx += x[i]; cy += y[i]; vcx += vx[i]; vcy += vy[i]; }
    cx /= N; cy /= N; vcx /= N; vcy /= N;
  }

  function substep(h) {
    centre();
    // External forces.
    let ax = 0, ay = GRAVITY;
    if (mode === 'home') {
      // Hovering: gravity is cancelled and the centre rides a soft spring to its spot, bobbing.
      const a = anchor(), bob = reduced ? 0 : Math.sin(time * 1.6) * 4;
      const c = 2 * HOVER_Z * Math.sqrt(HOVER_K);
      ax = HOVER_K * (a.x - cx) - c * vcx;
      ay = HOVER_K * (a.y + bob - cy) - c * vcy;
    }
    const drag = Math.exp(-AIR * h);
    for (let i = 0; i < N; i++) {
      vx[i] = (vx[i] + ax * h) * drag; vy[i] = (vy[i] + ay * h) * drag;
      px[i] = x[i]; py[i] = y[i];
      x[i] += vx[i] * h; y[i] += vy[i] * h;
    }
    const h2 = h * h;

    // Edges and diagonals: soft springs.
    for (let k = 0; k < E; k++) {
      const a = edges[k][0], b = edges[k][1];
      const dx = x[b] - x[a], dy = y[b] - y[a], len = Math.hypot(dx, dy);
      if (len < 1e-9) continue;
      const alpha = (edges[k][2] ? C_SHEAR : C_EDGE) / h2;
      const dl = -(len - L[k]) / (2 + alpha);
      const nx = dx / len * dl, ny = dy / len * dl;
      x[a] -= nx; y[a] -= ny; x[b] += nx; y[b] += ny;
    }

    // Triangles keep their area: jelly does not compress, and cannot turn inside out.
    const aa = C_AREA / h2;
    for (let k = 0; k < T; k++) {
      const t = tris[k], a = t[0], b = t[1], c = t[2];
      const C = area(x, y, a, b, c) - A0[k];
      const gax = 0.5 * (y[b] - y[c]), gay = 0.5 * (x[c] - x[b]);
      const gbx = 0.5 * (y[c] - y[a]), gby = 0.5 * (x[a] - x[c]);
      const gcx = 0.5 * (y[a] - y[b]), gcy = 0.5 * (x[b] - x[a]);
      const s = gax * gax + gay * gay + gbx * gbx + gby * gby + gcx * gcx + gcy * gcy;
      if (s < 1e-12) continue;
      const dl = -C / (s + aa);
      x[a] += gax * dl; y[a] += gay * dl; x[b] += gbx * dl; y[b] += gby * dl; x[c] += gcx * dl; y[c] += gcy * dl;
    }

    // A weak pull back to the drawn shape, turned to the body's current rotation (upright again
    // slowly while it hovers). Breathing swells the rest shape a little.
    centre();
    let sd = 0, sc = 0;
    for (let i = 0; i < N; i++) { const rx = x[i] - cx, ry = y[i] - cy; sd += qx[i] * rx + qy[i] * ry; sc += qx[i] * ry - qy[i] * rx; }
    theta = Math.atan2(sc, sd);
    const th = mode === 'home' ? theta * 0.985 : theta;
    const co = Math.cos(th), si = Math.sin(th);
    const br = reduced || mode !== 'home' ? 0 : Math.sin(time * 3.2);
    const bx = 1 - 0.012 * br, by = 1 + 0.018 * br;
    const as = C_SHAPE / h2, f = 1 / (1 + as);
    for (let i = 0; i < N; i++) {
      const lx = qx[i] * bx, ly = qy[i] * by;
      const gx = cx + co * lx - si * ly, gy = cy + si * lx + co * ly;
      x[i] += (gx - x[i]) * f; y[i] += (gy - y[i]) * f;
    }

    // The grabbed spot follows the pointer.
    if (mode === 'held') {
      let Gx = 0, Gy = 0, S = 0;
      for (let i = 0; i < N; i++) if (gw[i]) { Gx += gw[i] * x[i]; Gy += gw[i] * y[i]; S += gw[i] * gw[i]; }
      const ox = co * grabOffX - si * grabOffY, oy = si * grabOffX + co * grabOffY;
      const Cx = Gx - (aim.x - ox), Cy = Gy - (aim.y - oy);
      const d = 1 / (S + C_GRAB / h2);
      for (let i = 0; i < N; i++) if (gw[i]) { x[i] -= gw[i] * Cx * d; y[i] -= gw[i] * Cy * d; }
    }

    // Window edges, with friction (the skin grips). Vertices outside the drawn body may pass the
    // edge by that much, so the visible body is what touches.
    let hit = 0, contact = false;
    for (let i = 0; i < N; i++) {
      const o = out[i] * scale;
      if (y[i] > H + o) { contact = true; hit = Math.max(hit, y[i] - py[i]); y[i] = H + o; x[i] = px[i] + (x[i] - px[i]) * (1 - FRICTION); }
      else if (y[i] < -o) { y[i] = -o; x[i] = px[i] + (x[i] - px[i]) * (1 - FRICTION); }
      if (x[i] > W + o) { hit = Math.max(hit, x[i] - px[i]); x[i] = W + o; y[i] = py[i] + (y[i] - py[i]) * (1 - FRICTION); }
      else if (x[i] < -o) { hit = Math.max(hit, px[i] - x[i]); x[i] = -o; y[i] = py[i] + (y[i] - py[i]) * (1 - FRICTION); }
    }

    // Velocities from the corrected positions, then damp only the wobble (not the motion).
    let L0 = 0, I0 = 0;
    centre();
    for (let i = 0; i < N; i++) { vx[i] = (x[i] - px[i]) / h; vy[i] = (y[i] - py[i]) / h; }
    let mvx = 0, mvy = 0;
    for (let i = 0; i < N; i++) { mvx += vx[i]; mvy += vy[i]; }
    mvx /= N; mvy /= N;
    for (let i = 0; i < N; i++) { const rx = x[i] - cx, ry = y[i] - cy; L0 += rx * vy[i] - ry * vx[i]; I0 += rx * rx + ry * ry; }
    const w = L0 / (I0 || 1);
    const rd = contact ? RIGID_DAMP * 3 : RIGID_DAMP; // resting on the floor, the wobble dies sooner
    for (let i = 0; i < N; i++) {
      const rx = x[i] - cx, ry = y[i] - cy;
      vx[i] += rd * (mvx - w * ry - vx[i]);
      vy[i] += rd * (mvy + w * rx - vy[i]);
      // Jelly on the floor sticks and slumps, it does not roll like a ball.
      if (contact) { vx[i] += ROLL_DAMP * w * ry; vy[i] -= ROLL_DAMP * w * rx; }
    }
    vcx = mvx; vcy = mvy;
    if (hit / h > 1100 && time - lastSplat > 0.4) { lastSplat = time; blink(); }
  }

  // ---- Loop ----
  const pos = new Float32Array(N * 2);
  let last = performance.now();
  function frame(now) {
    const dt = Math.min(1 / 30, (now - last) / 1000); last = now;
    const h = dt / SUB;
    // The pointer moved once this frame; the grabbed spot follows it smoothly across the substeps
    // (a whole-frame jump in one substep tangles the mesh when you swoosh fast).
    for (let k = 0; k < SUB; k++) {
      const f = (k + 1) / SUB;
      aim.x = lastPtr.x + (ptr.x - lastPtr.x) * f; aim.y = lastPtr.y + (ptr.y - lastPtr.y) * f;
      substep(h); time += h;
    }
    lastPtr.x = ptr.x; lastPtr.y = ptr.y;
    behave(dt);
    animateFace(dt);
    if (texDirty) upload();
    for (let i = 0; i < N; i++) { pos[i * 2] = x[i]; pos[i * 2 + 1] = y[i]; }
    gl.bindBuffer(gl.ARRAY_BUFFER, posBuf); gl.bufferData(gl.ARRAY_BUFFER, pos, gl.DYNAMIC_DRAW);
    gl.clearColor(0, 0, 0, 0); gl.clear(gl.COLOR_BUFFER_BIT);
    gl.uniform2f(uView, W, H);
    gl.drawElements(gl.TRIANGLES, idx.length, gl.UNSIGNED_SHORT, 0);
    requestAnimationFrame(frame);
  }

  // Falling, resting, hopping home.
  function behave(dt) {
    if (mode === 'fall') {
      // Resting: the centre has hardly moved lately (it still jiggles, so not its speed).
      restSpeed += (Math.hypot(vcx, vcy) - restSpeed) * Math.min(1, dt * 3);
      restTime = restSpeed < 70 ? restTime + dt : 0;
      if (restTime > 0.9) hop();
    } else if (mode === 'hop') {
      hopTime += dt;
      if (hopTime >= hopUntil) { mode = 'home'; }
    }
  }
  function hop() {
    // A ballistic jump that lands on the home spot, peaking a little above it.
    centre();
    const a = anchor();
    const apex = Math.min(a.y, cy) - 70;
    const up = Math.sqrt(2 * GRAVITY * Math.max(1, cy - apex));
    const t1 = up / GRAVITY, t2 = Math.sqrt(2 * Math.max(1, a.y - apex) / GRAVITY);
    const vxh = (a.x - cx) / (t1 + t2);
    for (let i = 0; i < N; i++) { vx[i] = vxh; vy[i] = -up; }
    mode = 'hop'; hopTime = 0; hopUntil = t1 + t2 * 0.85;
    setMouth(1, 0.9);
  }

  // ---- Face ----
  let blinkT = -1, nextBlink = 2, mouthTarget = 0, mouthHold = 0, look = { x: -1, y: -1 };
  function blink() { if (blinkT < 0) blinkT = 0; }
  function setMouth(v, hold) { mouthTarget = v; mouthHold = hold || 0; }
  function animateFace(dt) {
    let changed = false;
    // Blink: close fast, open a little slower.
    nextBlink -= dt;
    if (nextBlink <= 0 && !reduced) { blink(); nextBlink = 1.8 + Math.random() * 3.8; }
    if (blinkT >= 0) {
      blinkT += dt;
      const b = blinkT < 0.07 ? blinkT / 0.07 : Math.max(0, 1 - (blinkT - 0.07) / 0.12);
      face.blink = b; changed = true;
      if (blinkT > 0.2) { blinkT = -1; face.blink = 0; }
    }
    // Pupils toward the pointer, in the body's own frame.
    if (look.x > -1) {
      const dx = look.x - cx, dy = look.y - cy, d = Math.hypot(dx, dy) || 1;
      const k = Math.min(1, d / 260) * 2.2, c = Math.cos(-theta), s = Math.sin(-theta);
      const tx0 = (dx * c - dy * s) / d * k, ty0 = (dx * s + dy * c) / d * k;
      const nx = face.px + (tx0 - face.px) * Math.min(1, dt * 12), ny = face.py + (ty0 - face.py) * Math.min(1, dt * 12);
      if (Math.abs(nx - face.px) + Math.abs(ny - face.py) > 0.02) { face.px = nx; face.py = ny; changed = true; }
    }
    if (mouthHold > 0) { mouthHold -= dt; if (mouthHold <= 0 && mode !== 'held') mouthTarget = 0; }
    const m = face.mouth + (mouthTarget - face.mouth) * Math.min(1, dt * 10);
    if (Math.abs(m - face.mouth) > 0.005) { face.mouth = m; changed = true; }
    if (changed) { draw(); texDirty = true; }
  }

  // ---- Pointer ----
  // Which triangle is under a point, and is the drawing opaque there?
  function hitTest(qx0, qy0) {
    for (let k = 0; k < T; k++) {
      const t = tris[k], a = t[0], b = t[1], c = t[2];
      const d = (y[b] - y[c]) * (x[a] - x[c]) + (x[c] - x[b]) * (y[a] - y[c]);
      if (Math.abs(d) < 1e-9) continue;
      const l1 = ((y[b] - y[c]) * (qx0 - x[c]) + (x[c] - x[b]) * (qy0 - y[c])) / d;
      const l2 = ((y[c] - y[a]) * (qx0 - x[c]) + (x[a] - x[c]) * (qy0 - y[c])) / d;
      const l3 = 1 - l1 - l2;
      if (l1 < 0 || l2 < 0 || l3 < 0) continue;
      const u = l1 * ru[a] + l2 * ru[b] + l3 * ru[c], v = l1 * rv[a] + l2 * rv[b] + l3 * rv[c];
      const ix = Math.round((u - U0) * K), iy = Math.round((v - U0) * K);
      if (ix < 0 || iy < 0 || ix >= TEX || iy >= TEX || alpha[iy * TEX + ix] < 90) return null;
      return { u, v };
    }
    return null;
  }

  let down = null;
  window.addEventListener('pointerdown', (e) => {
    if (e.button !== 0) return;
    const hit = hitTest(e.clientX, e.clientY);
    if (!hit) return;
    e.preventDefault();
    down = { x: e.clientX, y: e.clientY, id: e.pointerId, u: hit.u, v: hit.v };
    ptr.x = e.clientX; ptr.y = e.clientY;
  });
  window.addEventListener('pointermove', (e) => {
    ptr.x = e.clientX; ptr.y = e.clientY;
    look.x = e.clientX; look.y = e.clientY;
    if (down && mode !== 'held' && Math.hypot(e.clientX - down.x, e.clientY - down.y) > 4) grab(down);
    if (!down) document.documentElement.classList.toggle('over-jelly', !!hitTest(e.clientX, e.clientY));
  }, { passive: true });

  function grab(d) {
    // Weight the mesh around the grabbed spot (in the drawing's own space), and remember how far
    // that weighted centre sits from the spot, so the exact point stays under the pointer.
    const sig = 12;
    let sum = 0;
    for (let i = 0; i < N; i++) {
      const r2 = (ru[i] - d.u) ** 2 + (rv[i] - d.v) ** 2;
      gw[i] = r2 < 9 * sig * sig ? Math.exp(-r2 / (2 * sig * sig)) : 0; sum += gw[i];
    }
    let gu = 0, gv = 0;
    for (let i = 0; i < N; i++) { gw[i] /= sum; gu += gw[i] * ru[i]; gv += gw[i] * rv[i]; }
    grabOffX = (gu - d.u) * scale; grabOffY = (gv - d.v) * scale;
    mode = 'held';
    lastPtr.x = ptr.x; lastPtr.y = ptr.y;
    document.documentElement.classList.add('is-grabbing');
    setMouth(1);
  }
  function release(e) {
    if (!down || (e && e.pointerId !== down.id)) return;
    const was = mode === 'held';
    const at = down;
    down = null;
    document.documentElement.classList.remove('is-grabbing');
    if (!was) { poke(at.x, at.y); return; }
    mode = 'fall'; restTime = 0; restSpeed = 1000;
    setMouth(0);
  }
  window.addEventListener('pointerup', release);
  window.addEventListener('pointercancel', release);

  // A poke: the skin under the finger is pushed in and the jelly answers.
  function poke(px0, py0) {
    for (let i = 0; i < N; i++) {
      const dx = x[i] - px0, dy = y[i] - py0, d = Math.hypot(dx, dy);
      const f = Math.exp(-(d * d) / (2 * 30 * 30)) * 420;
      vx[i] += (dx / (d || 1)) * f * 0.35; vy[i] += f * 0.9;
    }
    blink(); setMouth(1, 0.8);
  }

  window.JJelly = { state: () => ({ mode, cx, cy, vcx, vcy, theta, N, T, E }), poke, hop };
  requestAnimationFrame(frame);
})();
