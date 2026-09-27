/* Peergos Snap host for the User notes module (WebView2). Talks to NotesWindow.cs through postMessage. */
(function () {
  'use strict';
  const wv = window.chrome && window.chrome.webview;
  let seq = 0;
  const pending = new Map();
  function applyTheme(vars) {
    const root = document.documentElement.style;
    for (const [k, v] of Object.entries(vars || {})) {
      if (k === 'color-scheme') root.colorScheme = v; else root.setProperty(k, v);
    }
  }
  if (wv) wv.addEventListener('message', e => {
    const r = e.data || {};
    if (r.type === 'theme') { applyTheme(r.vars); return; }
    const p = pending.get(r.id);
    if (!p) return;
    pending.delete(r.id);
    if (r.ok) p.res(r.v); else p.rej(new Error(r.err || 'failed'));
  });
  const rpc = (m, ...a) => new Promise((res, rej) => {
    if (!wv) { rej(new Error('No host')); return; }
    const id = ++seq;
    pending.set(id, { res, rej });
    wv.postMessage({ id, m, a });
  });

  const toastEl = document.getElementById('toast');
  let toastTimer = 0;
  function toast(text, kind) {
    toastEl.textContent = text;
    toastEl.className = 'show ' + (kind || '');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { toastEl.className = ''; }, 2600);
  }

  let config = { appName: 'Peergos Snap', version: '' };
  const api = {
    get: async () => UserNotesCore.migrate(await rpc('get')).notes,
    save: list => rpc('save', list),
    saveText: (text, kind) => rpc('saveText', text, kind),
    copy: text => rpc('copy', text),
    openFolder: file => rpc('openFolder', file),
    print: (html, o) => rpc('print', html, o || {}),
  };

  const closedEl = document.getElementById('closed');
  const notesUI = UserNotesUI.create({
    api,
    config: () => config,
    title: 'User notes',
    toast,
    fontControl: true,                // text size control in the header (module 1.5.0), remembered by the module
    onClose: () => { closedEl.style.display = 'flex'; rpc('close').catch(() => {}); },
  });
  window.__openNotes = () => { if (!notesUI.isOpen()) { closedEl.style.display = 'none'; notesUI.open(); } };
  window.__notesUI = notesUI;
  document.getElementById('reopen').addEventListener('click', () => window.__openNotes());

  rpc('config').then(c => { config = c; }).catch(() => {}).finally(() => window.__openNotes());
})();
