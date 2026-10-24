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
  const menu = document.querySelector('#demo-menu');
  const cursor = document.querySelector('#player-cursor');
  const status = document.querySelector('#demo-status');
  const motionButton = menu.querySelector('[data-action="motion"]');
  let motion = !matchMedia('(prefers-reduced-motion: reduce)').matches;
  let x = 0;
  let y = 0;
  let size = 220;
  let drag = null;
  let initialized = false;
  let reaction;
  const placeCursor = (left, top) => {
    left = Math.max(0, Math.min(left, stage.clientWidth - 26));
    top = Math.max(0, Math.min(top, stage.clientHeight - 32));
    cursor.style.transform = 'translate(' + left + 'px, ' + top + 'px)';
  };
  const followPointer = (event) => {
    const bounds = stage.getBoundingClientRect();
    placeCursor(event.clientX - bounds.left, event.clientY - bounds.top);
  };
  const place = () => {
    x = Math.max(0, Math.min(x, stage.clientWidth - size));
    y = Math.max(0, Math.min(y, stage.clientHeight - size));
    capri.style.left = x + 'px';
    capri.style.top = y + 'px';
    capri.style.width = size + 'px';
    capri.style.height = size + 'px';
  };
  const closeMenu = () => { menu.hidden = true; };
  const updateMotion = () => {
    capri.classList.toggle('no-motion', !motion);
    motionButton.setAttribute('aria-checked', String(motion));
    motionButton.querySelector('span').textContent = motion ? '\u2713' : '';
  };
  const reset = () => {
    size = stage.clientWidth < 550 ? 160 : 220;
    x = stage.clientWidth * (stage.clientWidth < 550 ? 0.46 : 0.62);
    y = stage.clientHeight * 0.38;
    capri.hidden = false;
    closeMenu();
    place();
    placeCursor(x + size * 0.6, y + size * 0.5);
    status.textContent = 'Capri is back.';
  };
  const react = () => {
    if (!motion) return;
    if (reaction) reaction.cancel();
    reaction = sprite.animate([{ transform: 'translateY(0)' }, { transform: 'translateY(-19px) rotate(-2deg)', offset: 0.42 }, { transform: 'translateY(0)' }], { duration: 600, easing: 'cubic-bezier(.2,.65,.35,1)' });
  };
  const endDrag = () => { drag = null; capri.classList.remove('dragging'); cursor.classList.remove('pressed'); };
  stage.addEventListener('pointermove', followPointer);
  stage.addEventListener('pointerdown', event => { followPointer(event); cursor.classList.add('pressed'); });
  document.addEventListener('pointerup', () => cursor.classList.remove('pressed'));
  stage.addEventListener('pointercancel', endDrag);
  capri.addEventListener('pointerdown', event => {
    if (event.button !== 0) return;
    closeMenu();
    drag = { id: event.pointerId, startX: event.clientX, startY: event.clientY, originX: x, originY: y, moved: false };
    capri.setPointerCapture(event.pointerId);
  });
  capri.addEventListener('pointermove', event => {
    if (!drag || drag.id !== event.pointerId) return;
    const dx = event.clientX - drag.startX;
    const dy = event.clientY - drag.startY;
    if (dx * dx + dy * dy > 25) drag.moved = true;
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
    if (!moved) react();
  });
  capri.addEventListener('lostpointercapture', endDrag);
  capri.addEventListener('contextmenu', event => {
    event.preventDefault();
    endDrag();
    menu.hidden = false;
    const bounds = stage.getBoundingClientRect();
    menu.style.left = Math.max(8, Math.min(event.clientX - bounds.left, stage.clientWidth - menu.offsetWidth - 8)) + 'px';
    menu.style.top = Math.max(8, Math.min(event.clientY - bounds.top, stage.clientHeight - menu.offsetHeight - 8)) + 'px';
    menu.querySelector('button').focus({ preventScroll: true });
  });
  capri.addEventListener('keydown', event => {
    const movement = { ArrowLeft: [-12, 0], ArrowRight: [12, 0], ArrowUp: [0, -12], ArrowDown: [0, 12] }[event.key];
    if (movement) { event.preventDefault(); x += movement[0]; y += movement[1]; place(); placeCursor(x + size * 0.6, y + size * 0.5); }
    if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); react(); }
  });
  menu.addEventListener('click', event => {
    const button = event.target.closest('button[data-action]');
    if (!button) return;
    if (button.dataset.action === 'hide') { capri.hidden = true; status.textContent = 'Capri is hidden.'; }
    if (button.dataset.action === 'size') { size = size >= 220 ? 140 : size + 40; place(); }
    if (button.dataset.action === 'motion') { motion = !motion; updateMotion(); }
    closeMenu();
  });
  document.addEventListener('pointerdown', event => { if (!menu.hidden && !menu.contains(event.target) && !capri.contains(event.target)) closeMenu(); });
  document.addEventListener('keydown', event => { if (event.key === 'Escape' && !menu.hidden) { closeMenu(); capri.focus({ preventScroll: true }); } });
  document.querySelector('#demo-restore').addEventListener('click', reset);
  new ResizeObserver(() => { if (!initialized) { initialized = true; reset(); } else { place(); placeCursor(x + size * 0.6, y + size * 0.5); } }).observe(stage);
  updateMotion();
})();
