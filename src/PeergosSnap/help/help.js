/* Peergos Snap Help: contents, search, colour scheme and version from the app. */
(function () {
  'use strict';
  const content = document.getElementById('content');
  const toc = document.getElementById('toc');
  const q = document.getElementById('q');
  const noresult = document.getElementById('noresult');
  const sections = Array.from(content.querySelectorAll('section'));

  // ---------- contents ----------
  const links = [];
  for (const sec of sections) {
    const h2 = sec.querySelector('h2');
    const a = document.createElement('a');
    a.href = '#' + sec.id;
    a.textContent = h2.textContent;
    a.dataset.section = sec.id;
    toc.appendChild(a);
    links.push(a);
    for (const h3 of sec.querySelectorAll('h3[id]')) {
      const s = document.createElement('a');
      s.href = '#' + h3.id;
      s.textContent = h3.textContent;
      s.className = 'sub';
      s.dataset.section = sec.id;
      toc.appendChild(s);
      links.push(s);
    }
  }
  const byId = new Map(links.map(l => [l.getAttribute('href').slice(1), l]));

  function setActive(id) {
    for (const l of links) l.classList.toggle('active', l.getAttribute('href') === '#' + id);
  }
  const observer = new IntersectionObserver(entries => {
    const visible = entries.filter(e => e.isIntersecting).sort((a, b) => a.boundingClientRect.top - b.boundingClientRect.top);
    if (visible.length) setActive(visible[0].target.id);
  }, { rootMargin: '-70px 0px -65% 0px' });
  /** (Re)observes the headings; needed again after the search restores a section's markup. */
  function observeAll() {
    observer.disconnect();
    for (const s of sections) observer.observe(s);
    content.querySelectorAll('h3[id]').forEach(h => observer.observe(h));
  }
  observeAll();

  // ---------- going to a section ----------
  function go(id) {
    if (!id) return;
    clearSearch();
    const el = document.getElementById(id);
    if (!el) return;
    el.scrollIntoView({ block: 'start' });
    const flash = el.tagName === 'SECTION' ? el.querySelector('h2') : el;
    flash.classList.remove('flash');
    void flash.offsetWidth;
    flash.classList.add('flash');
    setActive(el.tagName === 'SECTION' ? el.id : id);
  }
  window.__goto = go;
  if (location.hash.length > 1) setTimeout(() => go(location.hash.slice(1)), 0);

  // ---------- search ----------
  const originals = new Map(sections.map(s => [s, s.innerHTML]));
  function clearMarks() {
    let restored = false;
    for (const [s, html] of originals) if (s.querySelector('mark')) { s.innerHTML = html; restored = true; }
    if (restored) observeAll();
  }
  function clearSearch() {
    if (!q.value) return;
    q.value = '';
    runSearch();
  }
  function mark(node, re) {
    if (node.nodeType === 3) {
      const text = node.nodeValue;
      re.lastIndex = 0;
      if (!re.test(text)) return;
      re.lastIndex = 0;
      const frag = document.createDocumentFragment();
      let last = 0, m;
      while ((m = re.exec(text))) {
        frag.append(text.slice(last, m.index));
        const mk = document.createElement('mark');
        mk.textContent = m[0];
        frag.append(mk);
        last = m.index + m[0].length;
        if (m[0].length === 0) break;
      }
      frag.append(text.slice(last));
      node.replaceWith(frag);
    } else if (node.nodeType === 1 && !['SCRIPT', 'STYLE', 'MARK'].includes(node.tagName)) {
      Array.from(node.childNodes).forEach(c => mark(c, re));
    }
  }
  function runSearch() {
    clearMarks();
    const words = q.value.trim().toLowerCase().split(/\s+/).filter(Boolean);
    let shown = 0;
    for (const s of sections) {
      const hay = (s.textContent + ' ' + (s.dataset.keywords || '')).toLowerCase();
      const hit = words.every(w => hay.includes(w));
      s.classList.toggle('hidden', !hit);
      if (hit) shown++;
    }
    for (const l of links) l.classList.toggle('hidden', words.length > 0 && document.getElementById(l.dataset.section).classList.contains('hidden'));
    noresult.hidden = shown > 0;
    if (words.length) {
      const re = new RegExp(words.map(w => w.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|'), 'gi');
      for (const s of sections) if (!s.classList.contains('hidden')) mark(s, re);
      window.scrollTo({ top: 0 });
    }
  }
  q.addEventListener('input', runSearch);
  q.addEventListener('keydown', e => { if (e.key === 'Escape' && q.value) { e.stopPropagation(); clearSearch(); } });
  document.addEventListener('keydown', e => {
    if ((e.ctrlKey && e.key.toLowerCase() === 'f') || (e.key === '/' && document.activeElement !== q)) { e.preventDefault(); q.focus(); q.select(); }
  });
  toc.addEventListener('click', e => {
    const a = e.target.closest('a');
    if (!a) return;
    e.preventDefault();
    go(a.getAttribute('href').slice(1));
  });
  content.addEventListener('click', e => {
    const a = e.target.closest('a[href^="#"]');
    if (!a) return;
    e.preventDefault();
    go(a.getAttribute('href').slice(1));
  });

  // ---------- messages from the app ----------
  const wv = window.chrome && window.chrome.webview;
  if (wv) wv.addEventListener('message', e => {
    const m = e.data || {};
    if (m.type === 'theme') {
      const root = document.documentElement.style;
      for (const [k, v] of Object.entries(m.vars || {})) {
        if (k === 'color-scheme') root.colorScheme = v; else root.setProperty(k, v);
      }
    } else if (m.type === 'info' && m.version) {
      document.getElementById('version').textContent = 'Version ' + m.version;
    }
  });
})();
