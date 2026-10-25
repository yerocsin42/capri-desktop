(() => {
  const repo = 'yerocsin42/capri-desktop';
  const install = document.querySelector('#install');
  const enableDownload = (release) => {
    const asset = release && !release.draft && !release.prerelease && Array.isArray(release.assets) && release.assets.find(item => item.name === 'Capri-Setup.exe' && item.size > 1024 && typeof item.browser_download_url === 'string' && item.browser_download_url.startsWith('https://github.com/' + repo + '/releases/download/'));
    if (!asset) throw new Error('Installer unavailable');
    install.href = asset.browser_download_url;
  };
  const readRelease = async (url) => {
    const response = await fetch(url, { signal: AbortSignal.timeout(6000) });
    if (!response.ok) throw new Error('Release unavailable');
    enableDownload(await response.json());
  };
  readRelease('https://api.github.com/repos/' + repo + '/releases/latest').catch(() => readRelease('release.json')).catch(() => {});
  const headsUp = document.querySelector('#heads-up');
  install.addEventListener('click', () => { if (headsUp.showModal && !headsUp.open) headsUp.showModal(); });
  headsUp.addEventListener('click', event => { if (event.target === headsUp) headsUp.close(); });

  const stage = document.querySelector('#demo');
  const capri = document.querySelector('#demo-capri');
  const sprite = capri.querySelector('.sprite');
  const cursor = document.querySelector('#player-cursor');
  const status = document.querySelector('#demo-status');
  const motion = !matchMedia('(prefers-reduced-motion: reduce)').matches;
  const clips = { idle: [16, 8], blink: [4, 8], blink_glitch: [4, 8], grin: [16, 8], glitch: [10, 15], dangle: [12, 12], sleep: [16, 6], wake: [13, 15], perch: [16, 8], perch_blink: [4, 8], perch_blink_glitch: [4, 8], perch_glitch: [10, 15] };
  const loaded = new Set(['idle']);
  let clip = 'idle';
  let clipStart = performance.now();
  let cycle = 0;
  let blinkWith = '';
  let nextGlitch = clipStart + 12000 + Math.random() * 12000;
  let lastInput = clipStart;
  let shown = '';
  const play = name => { clip = loaded.has(name) ? name : riding && loaded.has('perch') ? 'perch' : 'idle'; clipStart = performance.now(); cycle = 0; blinkWith = ''; };
  const show = (name, cell) => {
    const count = clips[name][0];
    if (!shown.startsWith(name + ':')) {
      sprite.style.backgroundImage = 'url("assets/sprites/' + name + '.webp")';
      sprite.style.backgroundSize = count * 100 + '% 100%';
    }
    if (shown === name + ':' + cell) return;
    shown = name + ':' + cell;
    sprite.style.backgroundPosition = cell / (count - 1) * 100 + '% 0';
  };
  const animate = now => {
    const seated = clip.startsWith('perch');
    if (riding && !seated && loaded.has('perch')) play('perch');
    if ((!riding && seated) || (clip === 'dangle' && !(drag && drag.moved))) play('idle');
    let index = Math.floor((now - clipStart) / 1000 * clips[clip][1]);
    if ((clip === 'glitch' && index >= clips.glitch[0]) || (clip === 'wake' && index >= clips.wake[0]) || (clip === 'grin' && index >= clips.grin[0] * 2)) { play('idle'); index = 0; }
    if (clip === 'perch_glitch' && index >= clips.perch_glitch[0]) { play('perch'); index = 0; }
    if ((clip === 'idle' || clip === 'perch') && motion) {
      const current = Math.floor(index / clips[clip][0]);
      if (current !== cycle) {
        cycle = current;
        const roll = Math.random();
        const prefix = clip === 'perch' ? 'perch_' : '';
        blinkWith = roll < 0.1 ? prefix + 'blink_glitch' : roll < 0.55 ? prefix + 'blink' : '';
        if (now >= nextGlitch) { nextGlitch = now + 12000 + Math.random() * 12000; play(clip === 'perch' ? 'perch_glitch' : 'glitch'); index = 0; }
      }
      if (clip === 'idle' && now - lastInput > 30000 && !riding && !drag) play('sleep');
    }
    let name = clip;
    let cell = motion ? index % clips[clip][0] : 0;
    if ((name === 'idle' || name === 'perch') && blinkWith && loaded.has(blinkWith) && cell >= 9 && cell < 13) { name = blinkWith; cell -= 9; }
    capri.classList.toggle('still', clip === 'sleep' || clip === 'dangle');
    show(name, cell);
    requestAnimationFrame(animate);
  };
  const wake = () => { lastInput = performance.now(); if (clip === 'sleep') play('wake'); };
  ['pointermove', 'pointerdown', 'keydown', 'wheel'].forEach(type => document.addEventListener(type, wake, { passive: true }));
  addEventListener('load', () => Object.keys(clips).forEach(name => {
    const strip = new Image();
    strip.src = 'assets/sprites/' + name + '.webp';
    strip.decode().then(() => loaded.add(name)).catch(() => {});
  }));
  requestAnimationFrame(animate);
  let x = 0;
  let y = 0;
  let size = 220;
  let drag = null;
  let initialized = false;
  let clickTimer;
  let riding = false;
  let dragged = false;
  const taskbar = document.querySelector('#taskbar');
  const floor = () => stage.clientHeight - taskbar.offsetHeight;
  const cancelClick = () => clearTimeout(clickTimer);
  const setRide = on => { cancelClick(); const was = riding; riding = on; if (on && !was) play('perch_glitch'); if (was && !on) play('grin'); capri.classList.toggle('riding', on); if (on) stage.focus({ preventScroll: true }); };
  const placeCursor = (left, top) => {
    left = Math.max(0, Math.min(left, stage.clientWidth - 26));
    top = Math.max(0, Math.min(top, stage.clientHeight - 32));
    cursor.style.transform = 'translate(' + left + 'px, ' + top + 'px)';
  };
  const followPointer = (event) => {
    const bounds = stage.getBoundingClientRect();
    placeCursor(event.clientX - bounds.left, event.clientY - bounds.top);
    if (riding) { x = event.clientX - bounds.left - size * 0.267 + 12; y = event.clientY - bounds.top - size * 0.73 - 12; place(); }
  };
  const place = () => {
    x = Math.max(0, Math.min(x, stage.clientWidth - size));
    y = Math.max(0, Math.min(y, floor() - size));
    capri.style.left = x + 'px';
    capri.style.top = y + 'px';
    capri.style.width = size + 'px';
    capri.style.height = size + 'px';
  };
  const updateMotion = () => {
    capri.classList.toggle('no-motion', !motion);
  };
  const reset = () => {
    size = stage.clientWidth < 550 ? 160 : 220;
    x = stage.clientWidth * (stage.clientWidth < 550 ? 0.46 : 0.62);
    y = floor() * 0.3;
    setRide(false);
    capri.hidden = false;
    play('grin');
    place();
    placeCursor(x + size * 0.6, y + size * 0.5);
    status.textContent = 'Capri is back.';
  };
  const react = () => { if (motion) play('glitch'); };
  const endDrag = () => { drag = null; capri.classList.remove('dragging'); cursor.classList.remove('pressed'); };
  stage.addEventListener('pointermove', followPointer);
  stage.addEventListener('pointerdown', event => { followPointer(event); cursor.classList.add('pressed'); });
  document.addEventListener('pointerup', () => cursor.classList.remove('pressed'));
  stage.addEventListener('pointercancel', endDrag);
  capri.addEventListener('pointerdown', event => {
    if (event.button !== 0) return;
    drag = { id: event.pointerId, startX: event.clientX, startY: event.clientY, originX: x, originY: y, moved: false };
    capri.setPointerCapture(event.pointerId);
  });
  capri.addEventListener('pointermove', event => {
    if (!drag || drag.id !== event.pointerId) return;
    const dx = event.clientX - drag.startX;
    const dy = event.clientY - drag.startY;
    if (dx * dx + dy * dy > 25 && !drag.moved) { drag.moved = true; cancelClick(); play('dangle'); }
    if (!drag.moved) return;
    capri.classList.add('dragging');
    x = drag.originX + dx;
    y = drag.originY + dy;
    place();
  });
  capri.addEventListener('pointerup', event => {
    if (!drag || drag.id !== event.pointerId) return;
    const moved = drag.moved;
    endDrag();
    if (capri.hasPointerCapture(event.pointerId)) capri.releasePointerCapture(event.pointerId);
    dragged = moved;
    clearTimeout(clickTimer);
    if (!moved) clickTimer = setTimeout(() => { if (!riding && !drag && !capri.hidden) react(); }, 500);
  });
  capri.addEventListener('dblclick', event => { cancelClick(); if (dragged || riding) return; setRide(true); followPointer(event); });
  capri.addEventListener('lostpointercapture', endDrag);
  stage.addEventListener('pointercancel', cancelClick);
  stage.addEventListener('pointerdown', event => { if (event.target === stage) stage.focus({ preventScroll: true }); });
  capri.addEventListener('keydown', event => {
    const movement = { ArrowLeft: [-12, 0], ArrowRight: [12, 0], ArrowUp: [0, -12], ArrowDown: [0, 12] }[event.key];
    if (movement) { event.preventDefault(); x += movement[0]; y += movement[1]; place(); placeCursor(x + size * 0.6, y + size * 0.5); }
    if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); react(); }
  });
  stage.addEventListener('keydown', event => { if (event.ctrlKey && event.altKey && (event.code === 'KeyC' || event.key.toLowerCase() === 'c') && riding) { event.preventDefault(); setRide(false); } });
  document.querySelector('#demo-restore').addEventListener('click', reset);
  new ResizeObserver(() => { if (!initialized) { initialized = true; reset(); } else { place(); placeCursor(x + size * 0.6, y + size * 0.5); } }).observe(stage);
  updateMotion();

  const star = document.querySelector('#star');
  const starCount = star.querySelector('span');
  const compact = new Intl.NumberFormat('en', { notation: 'compact', maximumFractionDigits: 1 });
  let stars = null;
  let starred = false;
  try { starred = localStorage.getItem('capri-star') === '1'; } catch {}
  const showStar = () => {
    starCount.textContent = stars === null ? '' : compact.format(stars);
    star.setAttribute('aria-pressed', starred);
  };
  const readStars = async (options) => {
    const response = await fetch(star.dataset.api, { ...options, signal: AbortSignal.timeout(6000) });
    if (!response.ok) throw new Error('Stars unavailable');
    stars = Math.max((await response.json()).count, stars || 0);
    showStar();
  };
  showStar();
  readStars().catch(() => {});
  star.addEventListener('click', () => {
    if (starred) return;
    starred = true;
    try { localStorage.setItem('capri-star', '1'); } catch {}
    if (stars !== null) stars += 1;
    showStar();
    if (motion) {
      const plus = document.createElement('span');
      plus.className = 'star-plus';
      plus.textContent = '+1';
      plus.setAttribute('aria-hidden', 'true');
      star.append(plus);
      plus.animate([{ transform: 'translateY(0)', opacity: 1 }, { transform: 'translateY(-10px)', opacity: 1, offset: 0.5 }, { transform: 'translateY(-18px)', opacity: 0 }], { duration: 900, easing: 'ease-out' }).finished.then(() => plus.remove());
    }
    readStars({ method: 'POST' }).catch(() => {});
  });
})();
