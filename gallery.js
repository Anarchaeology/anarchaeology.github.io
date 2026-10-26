// Gallery page: loads data/photos.json, shows thumbnails grouped by month, and opens a full-screen viewer.
// Full-size pictures are only downloaded when a picture is opened or arrowed to; the grid uses thumbnails.
(() => {
  const $ = id => document.getElementById(id);
  const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
  const LAUNCH = '2001-06-27';
  const HOW = {
    embedded: 'Date stored inside the image file.',
    path: "Date in the picture's original web address.",
    server: "The original web server's last-modified date, usually the day it was uploaded.",
    forum: 'The day it was first linked on a forum.',
    capture: 'The day the Wayback Machine first saved it.',
    listed: 'The day the Wayback Machine saved a page that already showed it, such as a gallery page.',
    level: "Estimated from the character level shown in the picture, using that character's level history on auno.org.",
    order: "Estimated from the player's own file numbering, between pictures whose dates are known.",
    series: 'Taken from other pictures in the same series by the same player.',
  };
  const DATE_NAMES = { ed: 'Inside the file', pd: 'In the address', sd: 'Server', fd: 'Forum', cd: 'First archived' };

  let all = [], shown = [], current = -1, opener = null, barInView = true;
  const filter = { site: '', q: '' };
  const isFiltered = () => Boolean(filter.site || filter.q);
  // Names the filters that are on, e.g. "cloudeh.50megs.com · “sgm”".
  const describe = () => [filter.site, filter.q && `“${$('q').value.trim()}”`].filter(Boolean).join(' · ');
  const nf = n => n.toLocaleString('en');

  const url = path => path.split('/').map(encodeURIComponent).join('/');
  const longDate = d => `${+d.slice(8, 10)} ${MONTHS[+d.slice(5, 7) - 1]} ${d.slice(0, 4)}`;
  const shortDate = d => `${+d.slice(8, 10)} ${MONTHS[+d.slice(5, 7) - 1].slice(0, 3)} ${d.slice(0, 4)}`;
  const esc = s => String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const howDated = ds => ds.startsWith('copy:')
    ? 'Earliest date of another copy of this picture. ' + (HOW[ds.slice(5)] || '')
    : (HOW[ds] || '');

  fetch('data/photos.json')
    .then(r => { if (!r.ok) throw new Error(r.status); return r.json(); })
    .then(init)
    .catch(() => { $('grid').innerHTML = '<p class="empty">The picture list could not be loaded.</p>'; });

  function init(data) {
    all = data.photos;
    // What the search box matches: the date, the original address (site and folders) and the collection's file name,
    // rather than p.f, which can be a renamed copy (see site_names.tsv).
    for (const p of all) p.search = `${p.d} ${p.u} ${p.n || ''}`.toLowerCase();
    const sites = new Map();
    for (const p of all) sites.set(p.s, (sites.get(p.s) || 0) + 1);

    if (data.built) $('built').textContent = `List updated ${longDate(data.built)}.`;

    const siteSel = $('site');
    for (const s of [...sites.keys()].sort()) siteSel.append(new Option(`${s} (${sites.get(s)})`, s));
    siteSel.onchange = () => { filter.site = siteSel.value; render(); };
    let timer;
    $('q').oninput = e => { clearTimeout(timer); timer = setTimeout(() => { filter.q = e.target.value.trim().toLowerCase(); render(); }, 150); };
    $('clear').onclick = $('note-clear').onclick = clearFilters;
    // Opens a random picture from the ones the filters show.
    $('random').onclick = () => { if (shown.length) { opener = $('random'); open(Math.floor(Math.random() * shown.length), true); } };
    $('note-change').onclick = () => document.querySelector('.controls').scrollIntoView({ block: 'start' });
    // On phones the filter bar scrolls away with the page, so a small note keeps active filters in sight.
    new IntersectionObserver(([e]) => { barInView = e.isIntersecting; showNote(); }).observe(document.querySelector('.controls'));

    $('sources').innerHTML = [...sites.entries()].sort((a, b) => b[1] - a[1]).map(([s, n]) =>
      `<li><a href="https://web.archive.org/web/*/${esc(s)}/*" target="_blank" rel="noopener">${esc(s)}</a> <small>${n}</small></li>`).join('');

    $('grid').addEventListener('click', e => {
      if (e.target.closest('[data-clear]')) { clearFilters(); return; }
      const tile = e.target.closest('.tile');
      if (!tile || e.ctrlKey || e.metaKey || e.shiftKey || e.button !== 0) return;
      e.preventDefault();
      opener = tile;
      open(+tile.dataset.i, true);
    });
    $('v-close').onclick = close;
    $('v-prev').onclick = () => step(-1);
    $('v-next').onclick = () => step(1);
    document.addEventListener('keydown', onKey);
    swipe($('v-stage'));
    window.addEventListener('popstate', fromHash);
    window.addEventListener('resize', () => { if (!$('viewer').hidden) fit(shown[current]); });

    render();
    fromHash();
  }

  function matches(p) {
    if (filter.site && p.s !== filter.site) return false;
    if (filter.q && !p.search.includes(filter.q)) return false;
    return true;
  }

  function clearFilters() {
    filter.site = ''; filter.q = '';
    $('site').value = ''; $('q').value = '';
    render();
  }

  function showNote() {
    const on = isFiltered() && !barInView;
    $('filter-note').hidden = !on;
    if (on) $('note-text').innerHTML = `<b>Filtered</b> ${nf(shown.length)} of ${nf(all.length)} · ${esc(describe())}`;
  }

  function render() {
    shown = all.filter(matches);
    const filtered = isFiltered();
    $('count').textContent = filtered ? `${nf(shown.length)} of ${nf(all.length)} images` : `${nf(all.length)} images`;
    $('count').classList.toggle('on', filtered);
    $('clear').classList.toggle('off', !filtered);
    $('site').classList.toggle('on', Boolean(filter.site));
    $('q').classList.toggle('on', Boolean(filter.q));
    showNote();
    if (!shown.length) {
      $('grid').innerHTML = '<p class="empty">No pictures match these filters. <button type="button" class="clear" data-clear>Clear filters</button></p>';
      return;
    }
    const parts = [];
    let month = '';
    shown.forEach((p, i) => {
      const m = p.d.slice(0, 7);
      if (m !== month) {
        if (month) parts.push('</div></section>');
        month = m;
        const n = shown.filter(x => x.d.startsWith(m)).length;
        const tag = m < LAUNCH.slice(0, 7) ? '<span class="tag">Before launch</span>' : m === LAUNCH.slice(0, 7) ? '<span class="tag">Launch month</span>' : '';
        parts.push(`<section class="month"><h2>${MONTHS[+m.slice(5) - 1]} ${m.slice(0, 4)} <small>${n}</small>${tag}</h2><div class="tiles">`);
      }
      parts.push(`<a class="tile" href="#${p.id}" data-i="${i}"><img loading="lazy" src="${url(p.t)}" alt="Picture from ${esc(p.s)}, ${shortDate(p.d)}"><span class="td">${shortDate(p.d)}</span></a>`);
    });
    parts.push('</div></section>');
    $('grid').innerHTML = parts.join('');
  }

  // ---- Viewer ----

  function open(i, push) {
    current = i;
    const p = shown[i];
    $('viewer').hidden = false;
    document.body.style.overflow = 'hidden';
    show(p);
    const state = { viewer: true };
    if (push) history.pushState(state, '', '#' + p.id); else history.replaceState(history.state || null, '', '#' + p.id);
    $('v-close').focus({ preventScroll: true });
  }

  // Sizes the picture to fit the stage (small originals up to twice their size), so the thumbnail shown
  // while loading already takes the final size and the full-size picture just sharpens in place.
  function fit(p) {
    const img = $('v-img'), box = $('v-stage').getBoundingClientRect();
    const scale = Math.min((box.width - 24) / p.w, (box.height - 24) / p.h, p.w < 400 ? 2 : 1);
    img.style.width = Math.max(1, Math.round(p.w * scale)) + 'px';
    img.style.height = Math.max(1, Math.round(p.h * scale)) + 'px';
  }

  function show(p) {
    const img = $('v-img'), loading = $('v-loading');
    fit(p);
    // Show the thumbnail at once, then swap in the full-size picture when it has arrived.
    img.src = url(p.t);
    img.alt = `Picture from ${p.s}, ${shortDate(p.d)}`;
    loading.hidden = false;
    const full = new Image();
    full.onload = () => { if (shown[current] === p) { img.src = full.src; loading.hidden = true; } };
    full.onerror = () => { if (shown[current] === p) loading.textContent = 'The full-size picture could not be loaded.'; };
    loading.textContent = 'Loading full size…';
    full.src = url(p.f);

    $('v-pos').textContent = `${current + 1} / ${shown.length}`;
    $('v-filter').hidden = !isFiltered();
    $('v-filter').textContent = isFiltered() ? `Filtered: ${describe()}` : '';
    $('v-prev').disabled = current === 0;
    $('v-next').disabled = current === shown.length - 1;

    const badge = p.d < LAUNCH ? '<span class="badge">Before launch</span>' : '';
    const kb = p.b >= 1048576 ? (p.b / 1048576).toFixed(1) + ' MB' : Math.round(p.b / 1024) + ' KB';
    const dates = Object.entries(DATE_NAMES).filter(([k]) => p[k]).map(([k, name]) => `<li>${name}: ${p[k]}</li>`).join('');
    const links = [
      p.wb ? `<li><a href="${esc(p.wb)}" target="_blank" rel="noopener">Saved copy on the Wayback Machine</a></li>`
           : `<li><a href="${esc(p.u)}" target="_blank" rel="noopener">Original address (still online when collected)</a></li>`,
      `<li><a href="${url(p.f)}" target="_blank" rel="noopener">Open the full-size file</a></li>`,
      p.th ? `<li><a href="https://archives.jeuxonline.info/fils/${esc(p.th)}.html" target="_blank" rel="noopener">Forum thread where it was posted (French)</a></li>` : '',
      p.ft ? `<li><a href="forums/thread/${esc(p.ft)}" target="_blank" rel="noopener">Forum thread where it was posted</a></li>` : '',
    ].join('');
    $('v-info').innerHTML =
      `<p class="v-date">${longDate(p.d)}${badge}</p>` +
      `<p class="v-how">${esc(howDated(p.ds))}</p>` +
      `<dl><dt>Source</dt><dd>${esc(p.s)}</dd>` +
      `<dt>Size</dt><dd>${p.w} × ${p.h} · ${kb}</dd>` +
      `<dt>Original</dt><dd class="mono">${esc(p.u)}</dd></dl>` +
      `<ul class="v-links">${links}</ul>` +
      (dates ? `<details><summary>All dates found</summary><ul>${dates}</ul></details>` : '');
  }

  function step(d) {
    const i = current + d;
    if (i < 0 || i >= shown.length) return;
    current = i;
    show(shown[i]);
    history.replaceState(history.state || null, '', '#' + shown[i].id);
  }

  function close() {
    if (history.state && history.state.viewer) { history.back(); return; }   // popstate hides the viewer
    hide();
    history.replaceState(null, '', location.pathname + location.search);
  }

  function hide() {
    if ($('viewer').hidden) return;
    $('viewer').hidden = true;
    $('v-img').removeAttribute('src');
    document.body.style.overflow = '';
    const tile = document.querySelector(`.tile[data-i="${current}"]`) || opener;
    if (tile) { tile.scrollIntoView({ block: 'center' }); tile.focus({ preventScroll: true }); }
  }

  // Opens the picture named in the address (#id), so links to single pictures work.
  function fromHash() {
    const id = location.hash.slice(1);
    if (!id) { hide(); return; }
    let i = shown.findIndex(p => p.id === id);
    if (i < 0 && all.some(p => p.id === id)) {
      clearFilters();
      i = shown.findIndex(p => p.id === id);
    }
    if (i < 0) return;
    if ($('viewer').hidden) open(i, false); else { current = i; show(shown[i]); }
  }

  function onKey(e) {
    if ($('viewer').hidden || e.ctrlKey || e.metaKey || e.altKey) return;
    if (e.key === 'ArrowRight') step(1);
    else if (e.key === 'ArrowLeft') step(-1);
    else if (e.key === 'Home') step(-current);
    else if (e.key === 'End') step(shown.length - 1 - current);
    // R jumps to a random other picture from the ones shown.
    else if (e.key === 'r' || e.key === 'R') { if (shown.length > 1) { const r = Math.floor(Math.random() * (shown.length - 1)); step((r >= current ? r + 1 : r) - current); } }
    else if (e.key === 'Escape') close();
    else return;
    e.preventDefault();
  }

  function swipe(el) {
    let x0 = null, y0 = null;
    el.addEventListener('pointerdown', e => { x0 = e.clientX; y0 = e.clientY; });
    el.addEventListener('pointerup', e => {
      if (x0 === null) return;
      const dx = e.clientX - x0, dy = e.clientY - y0;
      x0 = null;
      if (Math.abs(dx) > 50 && Math.abs(dx) > Math.abs(dy)) step(dx < 0 ? 1 : -1);
    });
  }
})();
