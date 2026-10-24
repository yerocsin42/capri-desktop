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

  const stage = document.querySelector('#demo');
  const capri = document.querySelector('#demo-capri');
  const sprite = capri.querySelector('img');
  const cursor = document.querySelector('#player-cursor');
  const status = document.querySelector('#demo-status');
  const motion = !matchMedia('(prefers-reduced-motion: reduce)').matches;
  let x = 0;
  let y = 0;
  let size = 220;
  let drag = null;
  let initialized = false;
  let reaction;
  let clickTimer;
  let riding = false;
  let dragged = false;
  const taskbar = document.querySelector('#taskbar');
  const floor = () => stage.clientHeight - taskbar.offsetHeight;
  const cancelClick = () => clearTimeout(clickTimer);
  const setRide = on => { cancelClick(); riding = on; sprite.src = on ? 'assets/perch.png' : 'assets/capri.png'; capri.classList.toggle('riding', on); if (on) stage.focus({ preventScroll: true }); };
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
    place();
    placeCursor(x + size * 0.6, y + size * 0.5);
    status.textContent = 'Capri is back.';
  };
  const react = () => {
    if (!motion) return;
    if (reaction) reaction.cancel();
    reaction = sprite.animate([{ transform: 'translateY(0)' }, { transform: 'translateY(-19px) rotate(-2deg)', offset: 0.38 }, { transform: 'translateY(2px) scaleY(.97) rotate(1deg)', offset: 0.7 }, { transform: 'translateY(0)' }], { duration: 700, easing: 'cubic-bezier(.2,.65,.35,1)' });
  };
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
    if (dx * dx + dy * dy > 25) { drag.moved = true; cancelClick(); }
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
  setInterval(() => { if (motion && !riding && !drag && !capri.hidden && !document.hidden) react(); }, 9000);
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
