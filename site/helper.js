(() => {
  const root = new URL('./', document.currentScript.src);
  const live = 'https://yerocsin42.github.io/capri-desktop/';
  const local = ['localhost', '127.0.0.1', '[::1]'].includes(location.hostname);
  const api = (local && new URLSearchParams(location.search).get('helpapi')) || 'https://capri-reactions.yerocsin42.workers.dev/help';
  const still = matchMedia('(prefers-reduced-motion: reduce)');
  const phone = matchMedia('(max-width: 600px)');
  const greeting = 'Ask me for help if you get stuck, choom!';
  const starters = ['Why does Windows warn me?', 'Which brain should I pick?', 'What does it cost?'];
  const fallback = "My help line's glitching right now. The Help page has everything.";
  const saved = {
    get: () => { try { return sessionStorage.getItem('capri-helper-chat'); } catch (error) { return null; } },
    set: value => { try { sessionStorage.setItem('capri-helper-chat', value); } catch (error) { } }
  };
  const make = (tag, className, text) => {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text != null) node.textContent = text;
    return node;
  };
  const icon = path => '<svg viewBox="0 0 24 24" aria-hidden="true">' + path + '</svg>';

  const helper = make('div', 'helper');
  const tab = make('button', 'helper-tab');
  tab.type = 'button';
  tab.innerHTML = icon('<path d="M4 5h16v11H10l-6 4z"/><path d="M8.5 10.5h7"/>');
  const call = make('span', 'helper-tab-words');
  call.append(make('strong', null, 'Stuck? Ask me!'), make('small', null, 'Chat with Capri'));
  tab.append(call);
  tab.hidden = true;
  tab.setAttribute('aria-controls', 'helper-panel');
  tab.setAttribute('aria-expanded', 'false');

  const panel = make('section', 'helper-panel');
  panel.id = 'helper-panel';
  panel.hidden = true;
  panel.setAttribute('role', 'dialog');
  panel.setAttribute('aria-label', 'Chat with Capri');
  const head = make('div', 'helper-head');
  const tuck = make('button', 'helper-tuck');
  tuck.type = 'button';
  tuck.setAttribute('aria-label', 'Minimise chat');
  tuck.innerHTML = icon('<path d="M6 12h12"/>');
  head.append(make('p', 'helper-name', 'Capri'), tuck);
  const log = make('ol', 'helper-log');
  log.setAttribute('aria-live', 'polite');
  const chips = make('div', 'helper-chips');
  const form = make('form', 'helper-form');
  const input = make('input');
  input.type = 'text';
  input.maxLength = 300;
  input.autocomplete = 'off';
  input.setAttribute('aria-label', 'Your question');
  const send = make('button', 'helper-send');
  send.type = 'submit';
  send.setAttribute('aria-label', 'Send');
  send.innerHTML = icon('<path d="M4 12 20 4l-4 16-4-6.5z"/><path d="m12 13.5 8-9.5"/>');
  const attach = make('button', 'helper-attach');
  attach.type = 'button';
  attach.title = 'Add a screenshot';
  attach.setAttribute('aria-label', 'Add a screenshot');
  attach.innerHTML = icon('<rect x="3" y="4.5" width="18" height="15" rx="2.5"/><circle cx="8.5" cy="9.5" r="1.8"/><path d="m3.5 17.5 5.5-5.5 4 4 2.5-2.5 5 5"/>');
  const picker = make('input');
  picker.type = 'file';
  picker.accept = 'image/*';
  picker.hidden = true;
  form.append(attach, input, send, picker);
  const pending = make('div', 'helper-pending');
  pending.hidden = true;
  const preview = make('img');
  preview.alt = 'Your screenshot';
  const unpick = make('button', 'helper-unpick');
  unpick.type = 'button';
  unpick.setAttribute('aria-label', 'Remove screenshot');
  unpick.innerHTML = icon('<path d="m7 7 10 10M17 7 7 17"/>');
  pending.append(preview, unpick);
  const drop = make('div', 'helper-drop', 'Drop a screenshot');
  drop.hidden = true;
  panel.append(head, log, chips, pending, form, drop);

  const capri = make('span', 'helper-capri');
  capri.setAttribute('aria-hidden', 'true');
  const sprite = make('span', 'helper-sprite');
  const tendrils = make('canvas', 'helper-tendrils');
  capri.append(tendrils, sprite, make('span', 'helper-hit'));
  helper.append(panel, tab);
  document.body.append(helper);

  const clips = { perch: [16, 8], perch_blink: [4, 8], perch_blink_glitch: [4, 8], perch_glitch: [10, 15] };
  const loaded = new Set();
  for (const name in clips) {
    const image = new Image();
    image.onload = () => loaded.add(name);
    image.src = new URL('assets/helper/' + name + '.webp', root).href;
  }
  let clip = 'perch';
  let clipStart = performance.now();
  let cycle = 0;
  let blinkWith = '';
  let nextGlitch = clipStart + 12000 + Math.random() * 12000;
  let shown = '';
  const play = name => { clip = name; clipStart = performance.now(); cycle = 0; blinkWith = ''; };
  setInterval(() => {
    if (document.hidden || !capri.isConnected) return;
    const now = performance.now();
    const motion = !still.matches;
    if (clip === 'perch_glitch' && (!motion || !loaded.has(clip))) play('perch');
    let index = Math.floor((now - clipStart) / 1000 * clips[clip][1]);
    if (clip === 'perch_glitch' && index >= clips.perch_glitch[0]) { play('perch'); index = 0; }
    if (clip === 'perch' && motion) {
      const current = Math.floor(index / clips.perch[0]);
      if (current !== cycle) {
        cycle = current;
        const roll = Math.random();
        blinkWith = roll < 0.1 ? 'perch_blink_glitch' : roll < 0.55 ? 'perch_blink' : '';
        if (now >= nextGlitch) { nextGlitch = now + 12000 + Math.random() * 12000; play('perch_glitch'); index = 0; }
      }
    }
    let name = clip;
    let cell = motion ? index % clips[clip][0] : 0;
    if (name === 'perch' && blinkWith && loaded.has(blinkWith) && cell >= 9 && cell < 13) { name = blinkWith; cell -= 9; }
    if (!loaded.has(name)) return;
    if (shown === name + ':' + cell) return;
    if (!shown.startsWith(name + ':')) {
      sprite.style.backgroundImage = 'url("' + new URL('assets/helper/' + name + '.webp', root).href + '")';
      sprite.style.backgroundSize = clips[name][0] * 100 + '% 100%';
    }
    shown = name + ':' + cell;
    sprite.style.backgroundPosition = cell / (clips[name][0] - 1) * 100 + '% 0';
  }, 1000 / 30);

  // Her glitch tendrils, after Tear.Tendrils in the app: angular crimson ribbons with a violet edge, glow, speckles and glitch slices.
  const burst = (() => {
    const brush = tendrils.getContext('2d');
    const ink = ['#e82440', '#ff7890', '#ffffff', '#08060a', '#7a3cff', '#1ef08a'];
    let started = 0;
    let seed = 1;
    let timer = 0;
    const randoms = start => { let s = start >>> 0 || 1; return () => { s = (s * 1664525 + 1013904223) >>> 0; return s / 4294967296; }; };
    const ribbon = (shape, room, x, y, aim, length, thick) => {
      const count = 4 + Math.floor(shape() * 4);
      const hook = shape() < 0.35;
      const parts = [];
      let total = 0;
      for (let i = 0; i < count; i++) { parts.push((0.35 + 1.3 * shape()) * (hook && i === count - 1 ? 0.55 : 1)); total += parts[i]; }
      const spine = [[x, y]];
      const width = [thick * 0.9];
      let side = shape() < 0.5 ? -1 : 1;
      let walkX = x;
      let walkY = y;
      for (let i = 1; i <= count; i++) {
        aim += (shape() - 0.5) * 0.32;
        if (shape() < 0.7) side = -side;
        const heading = aim + side * (hook && i === count ? 1 + shape() * 0.6 : 0.22 + shape() * 0.48);
        walkX += Math.cos(heading) * length * parts[i - 1] / total;
        walkY += Math.sin(heading) * length * parts[i - 1] / total;
        spine.push([walkX, walkY]);
        width.push(i === count ? 0 : thick * Math.pow(1 - i / count, 0.45) * (0.7 + 0.6 * shape()));
      }
      let fit = 1;
      for (const [px, py] of spine) {
        if (px < room.left && px < x) fit = Math.min(fit, (x - room.left) / (x - px));
        if (px > room.right && px > x) fit = Math.min(fit, (room.right - x) / (px - x));
        if (py < room.top && py < y) fit = Math.min(fit, (y - room.top) / (y - py));
        if (py > room.bottom && py > y) fit = Math.min(fit, (room.bottom - y) / (py - y));
      }
      fit = Math.max(0.2, fit);
      return { spine: spine.map(([px, py]) => [x + (px - x) * fit, y + (py - y) * fit]), width };
    };
    const shownPart = (line, amount) => {
      if (amount >= 1) return line;
      const lengths = [0];
      for (let i = 1; i < line.spine.length; i++) lengths.push(lengths[i - 1] + Math.hypot(line.spine[i][0] - line.spine[i - 1][0], line.spine[i][1] - line.spine[i - 1][1]));
      const want = lengths[lengths.length - 1] * amount;
      const spine = [line.spine[0]];
      const width = [line.width[0]];
      for (let i = 1; i < line.spine.length; i++) {
        if (lengths[i] <= want) { spine.push(line.spine[i]); width.push(line.width[i]); continue; }
        const t = (want - lengths[i - 1]) / Math.max(1e-6, lengths[i] - lengths[i - 1]);
        spine.push([line.spine[i - 1][0] + (line.spine[i][0] - line.spine[i - 1][0]) * t, line.spine[i - 1][1] + (line.spine[i][1] - line.spine[i - 1][1]) * t]);
        width.push(0);
        break;
      }
      return { spine, width };
    };
    const outline = (line, grow, extra, dx, dy) => {
      const left = [];
      const right = [];
      const points = line.spine;
      for (let i = 0; i < points.length; i++) {
        const a = points[Math.max(0, i - 1)];
        const b = points[Math.min(points.length - 1, i + 1)];
        const along = Math.hypot(b[0] - a[0], b[1] - a[1]) || 1;
        const nx = -(b[1] - a[1]) / along;
        const ny = (b[0] - a[0]) / along;
        const half = (line.width[i] * grow + (line.width[i] > 0 ? extra : 0)) / 2;
        left.push([points[i][0] + nx * half + dx, points[i][1] + ny * half + dy]);
        right.push([points[i][0] - nx * half + dx, points[i][1] - ny * half + dy]);
      }
      brush.beginPath();
      [...left, ...right.reverse()].forEach(([px, py], i) => i ? brush.lineTo(px, py) : brush.moveTo(px, py));
      brush.closePath();
      brush.fill();
    };
    const frame = () => {
      const progress = (performance.now() - started) / 1700;
      const ratio = Math.min(2, devicePixelRatio || 1);
      const wide = tendrils.clientWidth;
      const tall = tendrils.clientHeight;
      if (progress >= 1 || !wide || !capri.isConnected) { brush.clearRect(0, 0, tendrils.width, tendrils.height); timer = 0; return; }
      if (tendrils.width !== Math.round(wide * ratio) || tendrils.height !== Math.round(tall * ratio)) { tendrils.width = Math.round(wide * ratio); tendrils.height = Math.round(tall * ratio); }
      brush.setTransform(ratio, 0, 0, ratio, 0, 0);
      brush.clearRect(0, 0, wide, tall);
      const flicker = Math.floor(progress * 51);
      const shape = randoms(seed);
      const jitter = randoms(seed ^ (flicker * 7919 + 1));
      const unit = wide / 360;
      let alpha = Math.max(0, Math.min(1, (0.97 - progress) / 0.17));
      alpha = alpha * alpha * (3 - 2 * alpha);
      let open = Math.min(1, progress / 0.26);
      open = 1 - Math.pow(1 - open, 3);
      const seen = tendrils.getBoundingClientRect();
      const room = { left: Math.max(8, 8 - seen.left), top: Math.max(8, 8 - seen.top), right: Math.min(wide - 8, document.documentElement.clientWidth - seen.left - 8), bottom: Math.min(tall - 8, innerHeight - seen.top - 8) };
      const middle = [wide * 0.5, tall * 0.56];
      const lines = [];
      const count = 5 + Math.floor(shape() * 3);
      for (let i = 0; i < count; i++) {
        // She sits in the bottom-right corner, so the fan opens left and up, where the page has room.
        const angle = Math.PI * (0.92 + 0.86 * (i + 0.15 + shape() * 0.7) / count);
        const x = middle[0] + Math.cos(angle) * 18 * unit;
        const y = middle[1] + Math.sin(angle) * 22 * unit;
        const reach = Math.min(Math.cos(angle) > 0.01 ? (room.right - x) / Math.cos(angle) : Math.cos(angle) < -0.01 ? (room.left - x) / Math.cos(angle) : 1e9, Math.sin(angle) > 0.01 ? (room.bottom - y) / Math.sin(angle) : Math.sin(angle) < -0.01 ? (room.top - y) / Math.sin(angle) : 1e9);
        const line = ribbon(shape, room, x, y, angle, Math.max(0, reach) * (0.6 + 0.32 * shape()), (10 + shape() * 6) * unit);
        let close = Math.max(0, Math.min(1, (progress - 0.56 - 0.08 * shape()) / 0.3));
        close = Math.round(close * close * (3 - 2 * close) * 6) / 6;
        lines.push({ ...shownPart(line, open * (1 - close)), dx: jitter() < 0.08 ? (jitter() < 0.5 ? -1 : 1) * (3 + jitter() * 7) * unit : 0 });
      }
      brush.save();
      brush.shadowColor = 'rgba(230, 10, 80, ' + 0.75 * alpha + ')';
      brush.shadowBlur = 18 * unit * ratio;
      brush.fillStyle = 'rgba(210, 12, 96, ' + 0.35 * alpha + ')';
      for (const line of lines) outline(line, 1.8, 6 * unit, line.dx, 0);
      brush.restore();
      const cut = (2 + Math.floor(jitter() * 3)) * unit;
      const split = progress < 0.1 || jitter() < 0.25;
      for (const line of lines) {
        if (split) { brush.fillStyle = 'rgba(30, 240, 138, ' + 0.67 * alpha + ')'; outline(line, 1, 0, line.dx + cut, -cut * 0.4); }
        brush.fillStyle = 'rgba(122, 60, 255, ' + 0.92 * alpha + ')';
        outline(line, 1, 0, line.dx - 3 * unit, 3 * unit);
        brush.fillStyle = 'rgba(236, 6, 68, ' + alpha + ')';
        outline(line, 1, 0, line.dx, 0);
      }
      brush.fillStyle = 'rgba(255, 222, 232, ' + 0.82 * alpha + ')';
      for (const line of lines) {
        for (let k = 1; k < line.spine.length; k++) {
          const [ax, ay] = line.spine[k - 1];
          const [bx, by] = line.spine[k];
          const girth = line.width[k - 1];
          const reach = Math.hypot(bx - ax, by - ay);
          if (girth < 5 * unit || reach < 1) continue;
          const stars = randoms(seed ^ (k * 131 + Math.round(ax * 7 + ay * 13) * 977));
          for (let dot = 0, dots = Math.floor(reach * girth / (180 * unit * unit)) + 1; dot < dots; dot++) {
            const along = stars();
            const across = (stars() * 2 - 1) * 0.3 * girth;
            const size = (1 + Math.floor(stars() * 2)) * Math.max(1, unit * 0.8);
            if (jitter() < 0.25) continue;
            brush.fillRect(ax + (bx - ax) * along - (by - ay) / reach * across + line.dx, ay + (by - ay) * along + (bx - ax) / reach * across, size, size);
          }
        }
      }
      for (const line of lines) {
        const tip = line.spine[line.spine.length - 1];
        const scatter = (6 + 30 * Math.max(0, progress - 0.5)) * unit;
        for (let k = 0, blocks = progress > 0.55 ? 2 + Math.floor(jitter() * 4) : Math.floor(jitter() * 3); k < blocks; k++) {
          const side = (2 + Math.floor(jitter() * 3)) * unit;
          brush.globalAlpha = alpha;
          brush.fillStyle = ink[Math.floor(jitter() * ink.length)];
          brush.fillRect(tip[0] + line.dx + (jitter() * 2 - 1) * scatter, tip[1] + (jitter() * 2 - 1) * scatter, side * (1 + Math.floor(jitter() * 4)), side);
          brush.globalAlpha = 1;
        }
      }
      for (let k = 0, holes = Math.floor(jitter() * 5); k < holes; k++) {
        const line = lines[Math.floor(jitter() * lines.length)];
        const at = line.spine[Math.floor(jitter() * line.spine.length)];
        brush.clearRect(at[0] - (4 + jitter() * 10) * unit, at[1] - unit, (8 + jitter() * 20) * unit, (1 + Math.floor(jitter() * 3)) * unit);
      }
      const bands = progress < 0.1 ? 3 + Math.floor(jitter() * 3) : progress > 0.55 ? 2 + Math.floor(jitter() * 5) : Math.floor(jitter() * 4);
      for (let b = 0; b < bands; b++) {
        const top = Math.floor(jitter() * tendrils.height);
        const height = Math.max(1, Math.floor((2 + jitter() * 14) * unit * ratio));
        const shift = Math.round((jitter() < 0.5 ? -1 : 1) * (5 + jitter() * 18) * unit * ratio);
        const strip = brush.getImageData(0, top, tendrils.width, Math.min(height, tendrils.height - top));
        brush.clearRect(0, top / ratio, wide, height / ratio);
        brush.putImageData(strip, shift, top);
      }
      timer = setTimeout(frame, 1000 / 30);
    };
    return () => {
      if (still.matches || document.hidden) return;
      seed = Math.floor(Math.random() * 2147483647);
      started = performance.now();
      if (!timer) frame();
    };
  })();

  const sitOn = box => {
    if (capri.parentNode === box) return;
    box.append(capri);
    play('perch_glitch');
    burst();
  };
  let calls = 0;
  const callOut = () => {
    if (calls >= 5) return;
    setTimeout(() => {
      if (!tab.hidden && !document.hidden) { calls++; play('perch_glitch'); burst(); }
      callOut();
    }, 35000 + Math.random() * 20000);
  };

  const chat = { log: [], history: [], open: false };
  const remember = () => saved.set(JSON.stringify({ log: chat.log.slice(-30), history: chat.history.slice(-6), open: chat.open }));
  const scrolled = () => { log.scrollTop = log.scrollHeight; };
  const linked = href => {
    if (typeof href !== 'string') return null;
    if (href.startsWith(live)) return new URL(href.slice(live.length), root).href;
    return /^https:\/\/discord\.gg\/[\w-]+$/.test(href) ? href : null;
  };
  const addLinks = (bubble, links) => {
    for (const link of (links || []).slice(0, 2)) {
      const href = link && linked(link.href);
      if (!href || typeof link.title !== 'string') continue;
      const anchor = make('a', 'helper-link', link.title);
      anchor.href = href;
      if (!href.startsWith(root.href)) { anchor.target = '_blank'; anchor.rel = 'noopener noreferrer'; }
      bubble.append(anchor);
    }
  };
  const say = (from, text, links, thumb) => {
    const item = make('li', 'helper-msg ' + from);
    if (thumb) {
      const shot = make('img', 'helper-shot');
      shot.src = thumb;
      shot.alt = 'Your screenshot';
      item.append(shot);
    }
    const words = make('span', 'helper-words', text);
    if (text || from === 'capri') item.append(words);
    if (links) addLinks(item, links);
    log.append(item);
    scrolled();
    return { item, words };
  };
  const typeOut = (words, text, done) => {
    if (still.matches) { words.textContent = text; done(); return; }
    const start = performance.now();
    const step = () => {
      const count = Math.min(text.length, Math.floor((performance.now() - start) / 1000 * 70));
      words.textContent = text.slice(0, count);
      scrolled();
      if (count < text.length) setTimeout(step, 30);
      else done();
    };
    step();
  };
  const showChips = () => {
    chips.replaceChildren();
    if (chat.log.some(line => line.from === 'you')) return;
    for (const question of starters) {
      const chip = make('button', 'helper-chip', question);
      chip.type = 'button';
      chip.addEventListener('click', () => ask(question));
      chips.append(chip);
    }
  };

  const place = (opened, byHand, quietly) => {
    chat.open = opened;
    panel.hidden = !opened;
    tab.hidden = opened;
    tab.setAttribute('aria-expanded', String(opened));
    const box = opened ? panel : tab;
    box.classList.remove('helper-arrive');
    if (!quietly) {
      void box.offsetWidth;
      box.classList.add('helper-arrive');
    }
    sitOn(box);
    if (quietly) play('perch');
    scrolled();
    remember();
    if (!byHand) return;
    if (!opened) tab.focus();
    else if (!phone.matches) input.focus();
  };
  tab.addEventListener('click', () => place(true, true));
  capri.addEventListener('click', () => { if (capri.parentNode === panel) { play('perch_glitch'); burst(); } });
  tuck.addEventListener('click', () => place(false, true));
  panel.addEventListener('keydown', event => { if (event.key === 'Escape') place(false, true); });

  let picked = null;
  let busy = false;
  const shrink = file => new Promise((resolve, reject) => {
    const url = URL.createObjectURL(file);
    const image = new Image();
    const paint = longest => {
      const scale = Math.min(1, longest / Math.max(image.naturalWidth, image.naturalHeight));
      const canvas = document.createElement('canvas');
      canvas.width = Math.max(1, Math.round(image.naturalWidth * scale));
      canvas.height = Math.max(1, Math.round(image.naturalHeight * scale));
      const paper = canvas.getContext('2d');
      paper.fillStyle = '#fff';
      paper.fillRect(0, 0, canvas.width, canvas.height);
      paper.drawImage(image, 0, 0, canvas.width, canvas.height);
      return canvas;
    };
    image.onload = () => {
      URL.revokeObjectURL(url);
      resolve({ full: paint(1280).toDataURL('image/jpeg', 0.85), thumb: paint(240).toDataURL('image/jpeg', 0.7) });
    };
    image.onerror = () => { URL.revokeObjectURL(url); reject(new Error('Unreadable picture')); };
    image.src = url;
  });
  const unchoose = () => { picked = null; pending.hidden = true; preview.removeAttribute('src'); };
  const choose = async file => {
    if (busy) return;
    if (!file || !/^image\//.test(file.type)) { say('capri', "That's not a picture. Try a screenshot."); return; }
    if (file.size > 20000000) { say('capri', "That picture's too big for me. Try a screenshot."); return; }
    try { picked = await shrink(file); } catch (error) { say('capri', "I couldn't open that picture. Try a PNG or JPG."); return; }
    preview.src = picked.thumb;
    pending.hidden = false;
    scrolled();
    if (!phone.matches) input.focus();
  };
  const imageIn = list => [...(list || [])].find(file => /^image\//.test(file.type)) || (list && list[0]);
  const carrying = event => !!event.dataTransfer && [...event.dataTransfer.types].includes('Files');
  let dragging = 0;
  attach.addEventListener('click', () => picker.click());
  picker.addEventListener('change', () => { choose(picker.files[0]); picker.value = ''; });
  unpick.addEventListener('click', () => { unchoose(); input.focus(); });
  panel.addEventListener('paste', event => {
    const file = imageIn(event.clipboardData && event.clipboardData.files);
    if (!file) return;
    event.preventDefault();
    choose(file);
  });
  panel.addEventListener('dragover', event => {
    if (!carrying(event)) return;
    event.preventDefault();
    event.dataTransfer.dropEffect = 'copy';
    drop.hidden = false;
    clearTimeout(dragging);
    dragging = setTimeout(() => { drop.hidden = true; }, 250);
  });
  panel.addEventListener('drop', event => {
    if (!carrying(event)) return;
    event.preventDefault();
    drop.hidden = true;
    choose(imageIn(event.dataTransfer.files));
  });
  tab.addEventListener('dragenter', event => { if (carrying(event)) place(true, true); });
  const placeholder = () => { input.placeholder = phone.matches ? 'Type your question' : 'Type, or drop a screenshot'; };
  phone.addEventListener('change', placeholder);
  placeholder();

  const ask = async text => {
    const question = text.replace(/\s+/g, ' ').trim();
    const shot = picked;
    if ((!question && !shot) || busy) return;
    busy = true;
    send.disabled = true;
    attach.disabled = true;
    input.value = '';
    unchoose();
    say('you', question, null, shot && shot.thumb);
    chat.log.push(shot ? { from: 'you', text: question, thumb: shot.thumb } : { from: 'you', text: question });
    showChips();
    remember();
    const typing = make('li', 'helper-typing');
    typing.setAttribute('aria-label', 'Capri is typing');
    typing.append(make('span'), make('span'), make('span'));
    log.append(typing);
    scrolled();
    let data = null;
    try {
      const response = await fetch(api, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ question, history: chat.history.slice(-6), image: shot ? shot.full : undefined }) });
      data = await response.json();
    } catch (error) { }
    const reply = data && typeof data.reply === 'string' && data.reply.trim() ? data.reply.trim() : fallback;
    const links = data && Array.isArray(data.links) ? data.links : [{ title: 'Help page', href: live + 'help/' }];
    if (data && data.answered) chat.history.push({ role: 'user', text: question || '(sent a screenshot)' }, { role: 'assistant', text: reply });
    typing.remove();
    const answer = say('capri', '');
    typeOut(answer.words, reply, () => {
      addLinks(answer.item, links);
      scrolled();
      chat.log.push({ from: 'capri', text: reply, links });
      remember();
      busy = false;
      send.disabled = false;
      attach.disabled = false;
      if (!panel.hidden && !phone.matches) input.focus();
    });
  };
  form.addEventListener('submit', event => { event.preventDefault(); ask(input.value); });

  let before = null;
  try { before = JSON.parse(saved.get() || 'null'); } catch (error) { }
  if (before && Array.isArray(before.log)) {
    chat.log = before.log.filter(line => line && (line.from === 'you' || line.from === 'capri') && typeof line.text === 'string');
    chat.history = Array.isArray(before.history) ? before.history : [];
  }
  if (!chat.log.length) chat.log.push({ from: 'capri', text: greeting });
  for (const line of chat.log) say(line.from, line.text, line.links, typeof line.thumb === 'string' && line.thumb.startsWith('data:image/jpeg;base64,') ? line.thumb : null);
  showChips();
  if (before) place(before.open === true, false, true);
  else setTimeout(() => { if (panel.hidden && tab.hidden) place(false, false); }, 900);
  callOut();
})();
