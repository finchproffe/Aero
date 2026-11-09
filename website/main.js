(() => {
  const reduce = matchMedia("(prefers-reduced-motion: reduce)").matches;
  const root = document.documentElement;
  const clamp = (v, a = 0, b = 1) => Math.min(b, Math.max(a, v));

  /* ---------- sky: one flight from day through storm and night to dawn ---------- */
  const skies = {
    day:   { a: [122, 172, 226], b: [222, 235, 248], ink: "#0f2236", soft: "#4a5b70", stars: 0 },
    storm: { a: [27, 34, 48],    b: [58, 68, 86],    ink: "#eef2f8", soft: "#a9b3c2", stars: 0 },
    night: { a: [4, 7, 15],      b: [15, 23, 43],    ink: "#e9edf6", soft: "#8e99b0", stars: 1 },
    dawn:  { a: [246, 212, 191], b: [207, 224, 241], ink: "#0f2236", soft: "#55627a", stars: 0 },
  };
  const skySections = [...document.querySelectorAll("[data-sky]")];
  const mix = (x, y, t) => x.map((v, i) => Math.round(v + (y[i] - v) * t));
  let lastInk = "";
  function paintSky() {
    const mid = innerHeight * .5;
    let i = 0;
    skySections.forEach((s, k) => { if (s.getBoundingClientRect().top <= mid) i = k; });
    const cur = skies[skySections[i].dataset.sky];
    const prev = skies[skySections[Math.max(0, i - 1)].dataset.sky];
    const t = i === 0 ? 1 : clamp((mid - skySections[i].getBoundingClientRect().top) / (innerHeight * .45));
    const e = t * t * (3 - 2 * t);
    root.style.setProperty("--sky-a", `rgb(${mix(prev.a, cur.a, e)})`);
    root.style.setProperty("--sky-b", `rgb(${mix(prev.b, cur.b, e)})`);
    root.style.setProperty("--stars", (prev.stars + (cur.stars - prev.stars) * e).toFixed(3));
    // Text flips at the midpoint instead of fading through an unreadable grey.
    const pal = e > .5 ? cur : prev;
    if (pal.ink !== lastInk) {
      lastInk = pal.ink;
      root.style.setProperty("--ink", pal.ink);
      root.style.setProperty("--soft", pal.soft);
    }
  }

  /* ---------- scroll progress for pinned / viewed sections ---------- */
  const tracked = [...document.querySelectorAll("[data-scroll]")].map(el => ({
    el, pin: el.dataset.scroll === "pin", p: 0,
    at: [...el.querySelectorAll("[data-at]")],
    track: el.hasAttribute("data-hscroll") ? el.querySelector(".track") : null,
  }));
  const byId = id => tracked.find(t => t.el.id === id || t.el.classList.contains(id));

  function measure() {
    for (const t of tracked) if (t.track) t.el.style.setProperty("--dist", Math.max(0, t.track.scrollWidth - innerWidth) + "px");
  }

  /* ---------- hero headline arrives on the wind (the one load-time moment) ---------- */
  const h1 = document.querySelector(".windy");
  const heroText = document.querySelector(".hero-text");
  function splitLetters(el, wrapWords) {
    const text = el.textContent;
    el.textContent = "";
    const sr = document.createElement("span");
    sr.className = "sr"; sr.textContent = text;
    const vis = document.createElement("span");
    vis.setAttribute("aria-hidden", "true");
    const letters = [];
    text.split(" ").forEach((word, wi, all) => {
      const w = document.createElement("span");
      if (wrapWords) w.className = "w";
      for (const ch of word) {
        const c = document.createElement("span");
        c.className = "c"; c.textContent = ch;
        w.appendChild(c); letters.push(c);
      }
      vis.appendChild(w);
      if (wi < all.length - 1) vis.appendChild(document.createTextNode(" "));
    });
    el.append(sr, vis);
    return letters;
  }
  splitLetters(h1, true).forEach((c, i) => c.style.setProperty("--d", `${150 + i * 22 + Math.random() * 120}ms`));
  requestAnimationFrame(() => requestAnimationFrame(() => { h1.classList.add("go"); heroText.classList.add("go"); }));

  /* ---------- quiet: clutter blown off to the right ---------- */
  const gusts = [];
  document.querySelectorAll(".clutter li").forEach((li, i) => {
    splitLetters(li, true).forEach((c, k) => gusts.push({
      c, start: .06 + i * .085 + k * .004 + Math.random() * .03,
      dx: 500 + Math.random() * 900, dy: (Math.random() - .5) * 420, r: (Math.random() - .3) * 260,
    }));
  });
  function paintQuiet(p) {
    for (const g of gusts) {
      const k = clamp((p - g.start) * 3.2), e = k * k;
      g.c.style.transform = reduce ? "" : `translate(${g.dx * e}px, ${g.dy * e - Math.sin(k * Math.PI) * 40}px) rotate(${g.r * e}deg)`;
      g.c.style.opacity = (1 - k).toFixed(3);
    }
  }

  /* ---------- storm: the browser's password file ---------- */
  const cfBody = document.querySelector(".cf-body");
  const rows = [["почта", "anna.k", "Kotik2019!"], ["банк", "anna_k", "Qwerty-77"], ["госуслуги", "+7 9…", "Anna1985"], ["соцсеть", "anna.k", "Kotik2019!"], ["магазин", "anna@…", "summer24"], ["работа", "a.kuz", "Pass#2024"]];
  const noise = "▚▞▙▟▛▜░▒▓#%&@";
  const rnd = () => noise[Math.random() * noise.length | 0];
  const hash = n => ((n * 2654435761) >>> 0) / 4294967296;
  let theftP = 0;
  function paintLoginData() {
    const p = theftP;
    // In Aero the same file holds nothing: passwords never land in it.
    if (p >= .84) { cfBody.textContent = "\n   0 записей\n"; return; }
    const reveal = clamp((p - .42) / .18);
    cfBody.textContent = rows.map(([site, login, pw], i) =>
      site.padEnd(10) + login.padEnd(8) + [...pw.padEnd(11)].map((ch, k) => hash(i * 31 + k + 1) < reveal ? ch : rnd()).join("")
    ).join("\n");
  }

  /* ---------- gallery skews with scroll speed ---------- */
  const gallery = byId("gallery");
  let lastY = scrollY, vel = 0, velQueued = false;
  function settleVelocity() {
    velQueued = false;
    vel *= .86;
    if (Math.abs(vel) < .02) vel = 0; else { velQueued = true; requestAnimationFrame(settleVelocity); }
    gallery.el.style.setProperty("--vel", vel.toFixed(2));
  }

  /* ---------- main scroll loop ---------- */
  let queued = false;
  function update() {
    queued = false;
    const vh = innerHeight;
    for (const t of tracked) {
      const r = t.el.getBoundingClientRect();
      if (r.bottom < -vh || r.top > vh * 2) continue;
      const raw = t.pin ? -r.top / Math.max(1, r.height - vh) : (vh - r.top) / (vh + r.height * .5);
      t.p = clamp(raw);
      t.el.style.setProperty("--p", t.p.toFixed(4));
      for (const a of t.at) a.classList.toggle("on", t.p >= +a.dataset.at);
      if (t.el.id === "quiet") paintQuiet(t.p);
      if (t.el.id === "theft") { theftP = t.p; paintLoginData(); }
    }
    paintSky();
    hero.scroll = byId("takeoff").p;

    const dy = scrollY - lastY; lastY = scrollY;
    if (!reduce) {
      vel = clamp(vel + dy * .04, -9, 9);
      if (!velQueued) { velQueued = true; requestAnimationFrame(settleVelocity); }
    }
  }
  const onScroll = () => { if (!queued) { queued = true; requestAnimationFrame(update); } };

  /* ---------- takeoff: wind tunnel around the A ---------- */
  const hero = { scroll: 0 };
  const cv = document.getElementById("tunnel");
  const ctx = cv.getContext("2d");
  const aSvg = document.querySelector(".hero-a");
  let W = 0, H = 0, obstacles = [], parts = [], running = false;
  const mouse = { x: -9999, y: -9999, tx: -9999, ty: -9999 };

  function buildObstacles() {
    const cr = cv.getBoundingClientRect(), ar = aSvg.getBoundingClientRect();
    const s = ar.width / 200, ox = ar.left - cr.left, oy = ar.top - cr.top;
    const P = (x, y) => [ox + (x - 40) * s, oy + (y - 40) * s];
    obstacles = [];
    const seg = (x1, y1, x2, y2, r1, r2, n) => {
      for (let i = 0; i <= n; i++) {
        const t = i / n, [x, y] = P(x1 + (x2 - x1) * t, y1 + (y2 - y1) * t);
        obstacles.push({ x, y, r: (r1 + (r2 - r1) * t) * s });
      }
    };
    seg(64, 198, 132, 56, 15, 15, 9);
    seg(132, 56, 196, 198, 15, 15, 9);
    seg(92, 147, 232, 119, 11, 3, 9);
  }

  function spawn(p, anywhere) {
    p.x = anywhere ? Math.random() * W : -20 - Math.random() * 80;
    p.y = Math.random() * H * 1.1 - H * .05;
    p.age = 0; p.life = 160 + Math.random() * 260;
    p.w = .6 + Math.random() * 1.1;
  }

  function layout() {
    const dpr = Math.min(devicePixelRatio || 1, 2);
    W = cv.clientWidth; H = cv.clientHeight;
    cv.width = W * dpr; cv.height = H * dpr;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    buildObstacles();
    const n = Math.round(clamp(W * H / 1100, 300, 1400));
    parts = Array.from({ length: n }, () => { const p = {}; spawn(p, true); return p; });
  }

  function velocity(x, y, U, lift) {
    let vx = U, vy = -U * lift;
    for (const o of obstacles) {
      const dx = x - o.x, dy = y - o.y, d2 = dx * dx + dy * dy, R2 = o.r * o.r;
      if (d2 < R2) return null;
      // Potential flow around a cylinder; the circles overlap, so each pushes at a third.
      const k = .34 * U * R2 / (d2 * d2);
      vx -= k * (dx * dx - dy * dy);
      vy -= k * 2 * dx * dy;
    }
    const mx = x - mouse.x, my = y - mouse.y, m2 = mx * mx + my * my, R = 70;
    if (m2 < R * R * .5) return null;
    if (m2 < 90000) {
      const k = U * R * R / (m2 * m2);
      vx -= k * (mx * mx - my * my);
      vy -= k * 2 * mx * my;
      const swirl = 900 / (m2 + 400);           // the cursor drags a small vortex behind it
      vx += -my * swirl; vy += mx * swirl;
    }
    return [vx, vy];
  }

  function frame() {
    if (!running) return;
    step();
    requestAnimationFrame(frame);
  }

  function step() {
    mouse.x += (mouse.tx - mouse.x) * .18;
    mouse.y += (mouse.ty - mouse.y) * .18;
    const U = 2.1 + hero.scroll * 6, lift = hero.scroll * .45;
    ctx.globalCompositeOperation = "destination-out";
    ctx.fillStyle = "rgba(0,0,0,.035)";
    ctx.fillRect(0, 0, W, H);
    ctx.globalCompositeOperation = "source-over";
    ctx.lineCap = "round";
    for (const p of parts) {
      const v = velocity(p.x, p.y, U, lift);
      p.age++;
      if (!v || p.x > W + 20 || p.y < -40 || p.y > H + 40 || p.age > p.life) { spawn(p, Math.random() < .25); continue; }
      const nx = p.x + v[0], ny = p.y + v[1];
      const fade = Math.min(p.age / 30, 1, (p.life - p.age) / 40);
      ctx.strokeStyle = `rgba(255,255,255,${(.6 * fade).toFixed(3)})`;
      ctx.lineWidth = p.w;
      ctx.beginPath(); ctx.moveTo(p.x, p.y); ctx.lineTo(nx, ny); ctx.stroke();
      p.x = nx; p.y = ny;
    }
  }

  addEventListener("pointermove", e => {
    if (e.pointerType !== "mouse") return;
    const r = cv.getBoundingClientRect();
    mouse.tx = e.clientX - r.left; mouse.ty = e.clientY - r.top;
    if (mouse.x < -999) { mouse.x = mouse.tx; mouse.y = mouse.ty; }
  }, { passive: true });

  layout();
  if (reduce) {
    // One still frame of long streamlines instead of animation.
    for (let i = 0; i < 140; i++) step();
  } else {
    new IntersectionObserver(([e]) => {
      const was = running;
      running = e.isIntersecting;
      if (running && !was) requestAnimationFrame(frame);
    }).observe(cv);
  }

  /* ---------- one-shot reveals: table rows, counters ---------- */
  const countUp = el => {
    const end = +el.dataset.count, dec = end % 1 ? 1 : 0, t0 = performance.now();
    const fmt = v => v.toLocaleString("ru-RU", { minimumFractionDigits: dec, maximumFractionDigits: dec });
    const tick = now => {
      const k = Math.min(1, (now - t0) / 1400);
      el.textContent = fmt(end * (1 - Math.pow(1 - k, 3)));
      if (k < 1) requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
  };
  const io = new IntersectionObserver(entries => {
    for (const e of entries) {
      if (!e.isIntersecting) continue;
      e.target.classList.add("in");
      if (e.target.dataset.count && !reduce) countUp(e.target);
      io.unobserve(e.target);
    }
  }, { rootMargin: "0px 0px -15% 0px" });
  document.querySelectorAll("tbody tr, [data-count]").forEach(el => io.observe(el));

  // Missing screenshot: drop the frame instead of showing a broken image.
  document.querySelectorAll(".shot img").forEach(img => img.addEventListener("error", () => { img.closest(".shot").remove(); measure(); }));

  addEventListener("scroll", onScroll, { passive: true });
  addEventListener("resize", () => { layout(); measure(); onScroll(); });
  addEventListener("load", () => { layout(); measure(); update(); });
  measure(); update(); paintLoginData();
})();
