/* User notes module — browser UI (plain DOM, no framework). Global: UserNotesUI. Needs usernotes-core.js and usernotes.css.
 * Part of the reusable "User notes" module: see README.md in this folder.
 *
 *   const notesUI = UserNotesUI.create({
 *     api: { get, save, saveText, copy, openFolder, print },  // async functions supplied by the host app (print is optional)
 *     config: () => ({ appName, version, ... }),               // texts for generated prompts (see usernotes-core buildPrompt)
 *     title: 'User notes', toast: (text, kind) => {},           // optional
 *     extraHeader: () => element,                               // optional: extra control in the window header (e.g. a font picker)
 *   });
 *   notesUI.open();  notesUI.isOpen();  notesUI.close();
 *
 * 1.3.0: bulk bar for the ticked notes: set type, area, priority or status for all of them, or delete them.
 * 1.4.0: the field with the focus keeps it when the note is redrawn (Tab after changing the status goes on to Details);
 *        the last 5 deletions can be brought back ("↶ Undo delete" in the footer, Ctrl+Z outside text fields).
 * 1.2.0: title bar order is fixed: … controls · extraHeader · Close (last, also in the prompt/done-list views); clearPrefs().
 * 1.1.0: statuses change only by hand (the prompt view has a "Mark as Sent" button); tick all / invert / none;
 *        area picked from a list with "New area…"; done-list report (copy, save, print, PDF); the window can be moved and resized.
 */
(function (root) {
  'use strict';
  const C = root.UserNotesCore;
  const h = (tag, attrs = {}, ...kids) => {
    const e = document.createElement(tag);
    for (const [k, v] of Object.entries(attrs || {})) {
      if (v === undefined || v === null || v === false) continue;
      if (k === 'class') e.className = v; else if (k === 'style') e.style.cssText = v;
      else if (k.startsWith('on')) e.addEventListener(k.slice(2), v); else e.setAttribute(k, v === true ? '' : v);
    }
    for (const c of kids.flat()) if (c !== null && c !== undefined && c !== false) e.append(c.nodeType ? c : document.createTextNode(String(c)));
    return e;
  };
  const debounce = (fn, ms) => { let t; return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); }; };
  const store = { get(k, d) { try { const v = localStorage.getItem('usernotes.' + k); return v === null ? d : JSON.parse(v); } catch { return d; } }, set(k, v) { try { localStorage.setItem('usernotes.' + k, JSON.stringify(v)); } catch {} } };
  const plural = (n, w) => `${n} ${w}${n === 1 ? '' : 's'}`;

  // Move a box by dragging its header (elements inside the header that are controls keep working).
  function makeMovable(box, handleSel) {
    box.addEventListener('mousedown', e => {
      const hd = e.target.closest(handleSel);
      if (!hd || !box.contains(hd) || e.button !== 0) return;
      if (e.target.closest('button, input, select, textarea, a, label, .un-nodrag')) return;
      e.preventDefault();
      const r = box.getBoundingClientRect(), dx = e.clientX - r.left, dy = e.clientY - r.top;
      box.style.position = 'fixed'; box.style.margin = '0'; box.style.left = r.left + 'px'; box.style.top = r.top + 'px';
      const mv = ev => {
        const x = Math.min(Math.max(ev.clientX - dx, 40 - r.width), window.innerWidth - 40);
        const y = Math.min(Math.max(ev.clientY - dy, 0), window.innerHeight - 30);
        box.style.left = x + 'px'; box.style.top = y + 'px';
      };
      const up = () => { window.removeEventListener('mousemove', mv); window.removeEventListener('mouseup', up); };
      window.addEventListener('mousemove', mv); window.addEventListener('mouseup', up);
    });
  }

  function create(opts) {
    const api = opts.api; const title = opts.title || 'User notes';
    const toast = opts.toast || (() => {});
    let notes = null, overlay = null, box = null, st = null, onWinUp = null;
    const saveSoon = debounce(() => api.save(notes), 250);
    const persist = () => saveSoon();
    // 1.4.0: the last C.UNDO_DELETES deletions (single or bulk) can be brought back; kept in localStorage, so a restart keeps them
    let deleted = C.pushDeletion(store.get('deleted', []), null);
    function remember(rec) { deleted = C.pushDeletion(deleted, rec); store.set('deleted', deleted); }
    function undoDelete() {
      const rec = deleted.pop(); store.set('deleted', deleted);
      if (!rec) { toast('Nothing to bring back', 'error'); return; }
      notes = C.restoreDeletion(notes, rec);
      const first = rec.items[0] && notes.find(n => n.id === rec.items[0].note.id);
      if (first) { st.cur = first.id; if (!C.matchesFilter(first, st.filter)) { st.filter = 'all'; store.set('filter', 'all'); } }
      st.confirmDel = false; st.confirmBulkDel = false;
      persist(); renderList();
      toast(rec.items.length === 1 ? `Brought back: ${rec.items[0].note.title || '(untitled)'}` : `Brought back ${plural(rec.items.length, 'note')}`, 'ok');
    }
    function deleteNotes(ids) {
      const rec = C.deletion(notes, ids); remember(rec);
      notes = C.removeNotes(notes, ids);
      for (const id of ids) st.reportSel.delete(id);
      return rec.items.length;
    }

    function shown() { return notes.filter(n => C.matchesFilter(n, st.filter)); }
    function isDoneView() { return st.filter === 'done'; }
    function close() { if (overlay) { overlay.remove(); overlay = null; box = null; if (onWinUp) window.removeEventListener('mouseup', onWinUp); onWinUp = null; if (opts.onClose) opts.onClose(); } }
    function isOpen() { return !!overlay; }

    function frame(content) {
      if (!overlay) {
        overlay = h('div', { class: 'un-overlay', tabindex: '-1' });
        overlay.addEventListener('mousedown', e => { if (e.target === overlay) close(); });
        overlay.addEventListener('keydown', e => {
          e.stopPropagation();                         // keep the host app's shortcuts out while notes are open
          if (e.key === 'Escape') { e.preventDefault(); close(); }
          // 1.4.0: Ctrl+Z brings back the last deleted note(s); inside a text field it stays the field's own undo
          else if ((e.ctrlKey || e.metaKey) && !e.shiftKey && e.key.toLowerCase() === 'z' && st && st.view === 'list' && !/^(INPUT|TEXTAREA)$/.test(e.target.tagName) && deleted.length) { e.preventDefault(); undoDelete(); }
          else if (e.altKey && (e.key === 'ArrowUp' || e.key === 'ArrowDown') && st && st.view === 'list') { e.preventDefault(); moveCur(e.key === 'ArrowUp' ? -1 : 1); }
        });
        box = h('div', { class: 'un-box' });
        const size = store.get('size', null);                     // remembered window size (resizable box)
        if (size && size.w > 300 && size.h > 250) { box.style.width = Math.min(size.w, window.innerWidth - 20) + 'px'; box.style.height = Math.min(size.h, window.innerHeight - 20) + 'px'; }
        let r0 = null;                                            // remember the size only when the user actually resized it
        box.addEventListener('mousedown', () => { r0 = box.getBoundingClientRect(); });
        onWinUp = () => { if (!box || !r0) return; const r = box.getBoundingClientRect(); if (Math.abs(r.width - r0.width) > 2 || Math.abs(r.height - r0.height) > 2) store.set('size', { w: Math.round(r.width), h: Math.round(r.height) }); r0 = null; };
        window.addEventListener('mouseup', onWinUp);
        makeMovable(box, '.un-head');
        overlay.append(box);
        document.body.append(overlay);
      }
      box.innerHTML = '';
      box.append(...content);
      overlay.focus();
    }
    // Title bar order (1.2.0, same as the host's windows): title … view controls · host control (e.g. Aa) · Close (always last).
    function header(...kids) {
      return h('div', { class: 'un-head', title: 'Drag here to move the window; drag the bottom-right corner to resize it' }, ...kids,
        opts.extraHeader ? opts.extraHeader() : null,
        h('button', { class: 'un-btn un-close', title: 'Close (Esc)', onclick: close }, 'Close'));
    }

    async function open(o = {}) {
      notes = (await api.get()).map(C.normalizeNote);
      st = { view: 'list', filter: store.get('filter', 'active'), cur: null, confirmDel: false, confirmBulkDel: false, newArea: false, reportSel: new Set() };
      st.cur = (shown()[0] || {}).id || null;
      renderList();
      if (o.focusNew) addNote();
    }

    // ---------------------------------------------------------------- list view
    let listEl, editorEl, footEl, bulkEl;
    function renderList() {
      st.view = 'list';
      listEl = h('div', { class: 'un-list' });
      editorEl = h('div', { class: 'un-editor' });
      footEl = h('div', { class: 'un-foot' });
      bulkEl = h('div', { class: 'un-bulk hidden' });
      const filters = [['active', 'Not done'], ['open', 'Open'], ['queued', 'Queued'], ['sent', 'Sent'], ['done', 'Done'], ['all', 'All']];
      const seg = h('div', { class: 'un-seg' }, filters.map(([k, l]) => h('button', {
        class: st.filter === k ? 'on' : '', 'data-filter': k, title: C.STATUS_HINT[k] || (k === 'active' ? 'Everything that is not Done' : ''),
        onclick: e => { st.filter = k; store.set('filter', k); [...seg.children].forEach(b => b.classList.toggle('on', b === e.currentTarget)); if (!shown().some(n => n.id === st.cur)) st.cur = (shown()[0] || {}).id || null; drawHelp(); drawList(); drawEditor(); drawFoot(); },
      }, l)));
      helpEl = h('div', { class: 'un-help' });
      frame([
        header(h('h2', {}, '📝 ' + title), seg,
          h('button', { class: 'un-btn pri', onclick: () => addNote() }, '+ Add note')),
        helpEl,
        h('div', { class: 'un-body' }, listEl, editorEl),
        bulkEl,
        footEl,
      ]);
      drawHelp(); drawList(); drawEditor(); drawFoot();
    }
    let helpEl;
    function drawHelp() {
      helpEl.textContent = isDoneView()
        ? 'Done notes. Tick the ones you want in a list of the work that was done, then "Print / export done list". Ticking here does not change anything in the notes.'
        : 'Tick notes to queue them for the next prompt or feedback. Statuses change only when you change them: after using a prompt, press "Mark as Sent" there, or set the status in the note.';
    }

    function addNote() {
      const n = C.normalizeNote({ title: '', status: 'open' });
      notes.push(n); st.cur = n.id;
      if (st.filter !== 'active' && st.filter !== 'open' && st.filter !== 'all') { st.filter = 'active'; store.set('filter', 'active'); }
      persist(); renderList();
      setTimeout(() => { const i = overlay && overlay.querySelector('.un-editor input'); if (i) i.focus(); }, 30);
    }
    function moveCur(delta) { notes = C.move(notes, st.cur, delta); persist(); drawList(); }
    function redrawAll() { drawList(); drawEditor(); drawFoot(); }

    let dragId = null;
    function drawList() {
      listEl.innerHTML = '';
      const items = shown();
      if (!items.length) listEl.append(h('div', { class: 'un-empty' }, isDoneView() ? 'No done notes yet.' : 'No notes here. "+ Add note" starts one.'));
      for (const n of items) {
        const doneView = isDoneView();
        const cb = h('input', { type: 'checkbox', title: doneView ? 'Include in the done list (does not change the note)' : 'Queue for the next prompt / feedback' });
        cb.checked = doneView ? st.reportSel.has(n.id) : n.status === 'queued';
        cb.addEventListener('click', e => e.stopPropagation());
        cb.addEventListener('change', () => {
          if (doneView) { if (cb.checked) st.reportSel.add(n.id); else st.reportSel.delete(n.id); drawFoot(); return; }
          C.setQueued(n, cb.checked); persist(); drawList(); if (st.cur === n.id) drawEditor(); drawFoot();
        });
        const row = h('div', { class: 'un-row' + (st.cur === n.id ? ' cur' : '') + ' st-' + n.status, draggable: 'true', 'data-id': n.id },
          h('span', { class: 'un-grip', title: 'Drag to reorder' }, '⋮⋮'), cb,
          h('span', { class: 'un-num' }, (notes.indexOf(n) + 1) + '.'),
          h('span', { class: 'un-t' }, n.title || '(untitled)', n.area ? h('small', {}, n.area) : null),
          n.type !== 'improvement' ? h('span', { class: 'un-type', title: C.TYPE_LABEL[n.type] }, { fix: '🛠', adjustment: '↔', question: '?' }[n.type]) : null,
          n.priority !== 'normal' ? h('span', { class: 'un-prio ' + n.priority, title: 'Priority: ' + n.priority }, n.priority === 'high' ? '▲' : '▽') : null,
          h('span', { class: 'un-badge ' + n.status, title: C.STATUS_HINT[n.status] + (n.sentAt ? ' (sent ' + String(n.sentAt).slice(0, 10) + ')' : '') }, C.STATUS_LABEL[n.status]));
        row.addEventListener('click', () => { st.cur = n.id; st.confirmDel = false; st.newArea = false; drawList(); drawEditor(); });
        row.addEventListener('dragstart', e => { dragId = n.id; e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', n.id); });
        row.addEventListener('dragover', e => { e.preventDefault(); row.classList.add('drag-over'); });
        row.addEventListener('dragleave', () => row.classList.remove('drag-over'));
        row.addEventListener('drop', e => { e.preventDefault(); row.classList.remove('drag-over'); if (dragId) { notes = C.moveBefore(notes, dragId, n.id); persist(); drawList(); } });
        listEl.append(row);
      }
    }

    function allAreas() { return [...new Set(notes.map(x => x.area).filter(Boolean).concat(opts.areas || ['UI', 'Performance', 'Settings', 'Help']))].sort((a, b) => a.localeCompare(b)); }
    function field(label, input, extra) { return h('div', { class: 'un-fld' }, h('label', {}, h('span', {}, label), extra || null), input); }
    function drawEditor() {
      // 1.4.0: the field that had the focus gets it back after the redraw (changing the status used to lose it, so Tab
      // no longer went on to Details)
      const act = document.activeElement, focusKey = act && editorEl.contains(act) && act.dataset ? act.dataset.k : null;
      editorEl.innerHTML = '';
      const n = notes.find(x => x.id === st.cur);
      if (!n) { editorEl.append(h('div', { class: 'un-empty' }, 'Select a note on the left, or add one.')); return; }
      const upd = (k, v, redrawEditor) => { n[k] = v; n.updated = new Date().toISOString(); persist(); drawList(); drawFoot(); if (redrawEditor) drawEditor(); };
      const t = h('input', { type: 'text', 'data-k': 'title', value: n.title, placeholder: 'Short title, e.g. "Fonts"' }); t.addEventListener('input', () => upd('title', t.value));
      // Area: a list of all areas in use (+ defaults), "(none)" and "New area…" (1.1.0: the old type-ahead box only offered areas matching the current text).
      let area;
      if (st.newArea) {
        area = h('input', { type: 'text', class: 'un-area-new', placeholder: 'New area, Enter' });
        const commit = () => { const v = area.value.trim(); st.newArea = false; if (v) upd('area', v, true); else drawEditor(); };
        area.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); commit(); } else if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); st.newArea = false; drawEditor(); } });
        area.addEventListener('blur', () => { if (st.newArea) commit(); });
        setTimeout(() => area.focus(), 0);
      } else {
        area = h('select', { class: 'un-area', 'data-k': 'area', title: 'Where in the app' }, h('option', { value: '' }, '(none)'), allAreas().map(a => h('option', { value: a }, a)), h('option', { value: '\u0000new' }, 'New area…'));
        area.value = n.area || '';
        area.addEventListener('change', () => { if (area.value === '\u0000new') { st.newArea = true; drawEditor(); } else upd('area', area.value, false); });
      }
      const type = h('select', { 'data-k': 'type' }, C.TYPES.map(v => h('option', { value: v }, C.TYPE_LABEL[v]))); type.value = n.type; type.addEventListener('change', () => upd('type', type.value));
      const prio = h('select', { 'data-k': 'priority' }, C.PRIORITIES.map(v => h('option', { value: v }, v))); prio.value = n.priority; prio.addEventListener('change', () => upd('priority', prio.value));
      const status = h('select', { class: 'un-status', 'data-k': 'status', title: 'Queued = ticked for the next prompt/feedback' }, C.STATUSES.map(v => h('option', { value: v, title: C.STATUS_HINT[v] }, C.STATUS_LABEL[v]))); status.value = n.status;
      status.addEventListener('change', () => {
        if (status.value === 'queued') C.setQueued(n, true);
        else {
          if (n.status === 'queued') C.setQueued(n, false);
          n.status = status.value;
          if (status.value === 'sent' && !n.sentAt) { n.sentAt = new Date().toISOString(); n.sentAs = n.sentAs || 'manual'; }
          if (status.value === 'done' && !n.doneIn) n.doneIn = (opts.config && opts.config().version) || null;
        }
        upd('status', n.status, true);
      });
      const det = h('textarea', { class: 'un-details', 'data-k': 'details', placeholder: 'What should change, and why. The more concrete, the better.' }); det.value = n.details;
      det.addEventListener('input', () => { n.details = det.value; n.updated = new Date().toISOString(); persist(); });
      const del = st.confirmDel
        ? h('span', { class: 'un-inline' }, 'Delete this note?', h('button', { class: 'un-btn bad', 'data-k': 'del-yes', onclick: () => { deleteNotes([n.id]); st.cur = (shown()[0] || {}).id || null; st.confirmDel = false; persist(); redrawAll(); toast('Note deleted (Ctrl+Z or "Undo delete" brings it back)', 'ok'); } }, 'Yes, delete'), h('button', { class: 'un-btn', onclick: () => { st.confirmDel = false; drawEditor(); } }, 'No'))
        : h('button', { class: 'un-btn', 'data-k': 'del', onclick: () => { st.confirmDel = true; drawEditor(); } }, 'Delete note');
      const meta = [`Created ${String(n.created).slice(0, 10)}`, n.sentAt ? `sent ${String(n.sentAt).slice(0, 16).replace('T', ' ')}${n.sentAs ? ' as ' + n.sentAs : ''}` : null, n.doneIn ? `done in ${n.doneIn}` : null].filter(Boolean).join(' · ');
      editorEl.append(
        field('Title', t),
        h('div', { class: 'un-grid4' }, field('Type', type), field('Area', area), field('Priority', prio), field('Status', status)),
        field('Details', det, h('span', { class: 'un-link', onclick: () => { api.copy(det.value); toast('Details copied', 'ok'); } }, 'copy')),
        h('div', { class: 'un-meta' }, meta, n.sentFile ? h('span', { class: 'un-link', title: n.sentFile, onclick: () => api.openFolder(n.sentFile) }, ' · open file folder') : null),
        h('div', { class: 'un-actions' }, h('button', { class: 'un-btn', title: 'Alt+↑', onclick: () => moveCur(-1) }, '↑ Up'), h('button', { class: 'un-btn', title: 'Alt+↓', onclick: () => moveCur(1) }, '↓ Down'), del));
      if (focusKey) { const f = editorEl.querySelector(`[data-k="${focusKey}"]`) || (focusKey === 'del-yes' ? editorEl.querySelector('[data-k="del"]') : null); if (f) f.focus(); }
    }

    function tick(how) {
      const list = shown();
      if (isDoneView()) { C.selectMany(st.reportSel, list, how); drawList(); drawFoot(); return; }
      C.tickMany(list, how); persist(); redrawAll();
    }
    // ---- bulk bar (1.3.0): what the tick boxes select, changed in one go
    function ticked() { return isDoneView() ? notes.filter(n => st.reportSel.has(n.id)) : C.queued(notes); }
    function applyBulk(patch) {
      const ids = ticked().map(n => n.id);
      const n = C.bulkUpdate(notes, ids, patch, { version: (opts.config && opts.config().version) || null });
      if (isDoneView() && 'status' in patch && patch.status !== 'done') for (const id of ids) st.reportSel.delete(id);
      persist(); redrawAll();
      const what = Object.entries(patch).map(([k, v]) => `${k} → ${k === 'status' ? C.STATUS_LABEL[v] : k === 'type' ? C.TYPE_LABEL[v] : (v || '(none)')}`).join(', ');
      toast(`${plural(n, 'note')}: ${what}`, 'ok');
    }
    function drawBulk() {
      if (!bulkEl) return;
      const list = ticked();
      bulkEl.innerHTML = '';
      bulkEl.classList.toggle('hidden', !list.length);
      if (!list.length) return;
      const pick = (label, options, onPick, dataKey) => {
        const sel = h('select', { class: 'un-mode', 'data-bulk': dataKey, title: label + ' for all ticked notes' }, h('option', { value: '' }, label + '…'), options.map(([v, l]) => h('option', { value: v }, l)));
        sel.addEventListener('change', () => { const v = sel.value; sel.value = ''; if (v !== '') onPick(v); });
        return sel;
      };
      const areaOpts = [['\u0000none', '(none)'], ...allAreas().map(a => [a, a]), ['\u0000new', 'New area…']];
      let areaCtl = pick('Area', areaOpts, v => {
        if (v === '\u0000none') return applyBulk({ area: '' });
        if (v !== '\u0000new') return applyBulk({ area: v });
        const inp = h('input', { type: 'text', class: 'un-area-new', placeholder: 'New area, Enter' });
        inp.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); const t = inp.value.trim(); if (t) applyBulk({ area: t }); else drawBulk(); } else if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); drawBulk(); } });
        areaCtl.replaceWith(inp); inp.focus();
      }, 'area');
      const del = st.confirmBulkDel
        ? h('span', { class: 'un-inline' }, `Delete ${plural(list.length, 'note')}?`,
          h('button', { class: 'un-btn bad', 'data-bulk': 'delete-yes', onclick: () => { const ids = list.map(n => n.id); deleteNotes(ids); if (ids.includes(st.cur)) st.cur = (shown()[0] || {}).id || null; st.confirmBulkDel = false; persist(); redrawAll(); toast(`${plural(ids.length, 'note')} deleted (Ctrl+Z or "Undo delete" brings them back)`, 'ok'); } }, 'Yes, delete'),
          h('button', { class: 'un-btn', onclick: () => { st.confirmBulkDel = false; drawBulk(); } }, 'No'))
        : h('button', { class: 'un-btn', 'data-bulk': 'delete', onclick: () => { st.confirmBulkDel = true; drawBulk(); } }, 'Delete…');
      bulkEl.append(h('span', { class: 'un-lbl' }, `With the ${plural(list.length, 'ticked note')}:`),
        pick('Type', C.TYPES.map(v => [v, C.TYPE_LABEL[v]]), v => applyBulk({ type: v }), 'type'),
        areaCtl,
        pick('Priority', C.PRIORITIES.map(v => [v, v]), v => applyBulk({ priority: v }), 'priority'),
        pick('Status', C.STATUSES.map(v => [v, C.STATUS_LABEL[v]]), v => applyBulk({ status: v }), 'status'),
        del);
    }

    function undoBtn() {
      if (!deleted.length) return null;
      const last = deleted[deleted.length - 1];
      const names = last.items.map(x => x.note.title || '(untitled)').slice(0, 5).join(', ') + (last.items.length > 5 ? ', …' : '');
      return h('button', { class: 'un-btn', 'data-act': 'undo-delete', title: `Bring back the last deleted: ${names}\n(${deleted.length} deletion${deleted.length === 1 ? '' : 's'} can be undone; Ctrl+Z)`, onclick: () => undoDelete() }, `↶ Undo delete (${deleted.length})`);
    }
    function drawFoot() {
      drawBulk();
      footEl.innerHTML = '';
      const ub = undoBtn(); if (ub) footEl.append(ub);
      const tickSeg = h('span', { class: 'un-inline un-ticks' }, h('span', { class: 'un-lbl' }, 'Tick shown:'),
        h('div', { class: 'un-seg' },
          h('button', { 'data-tick': 'all', title: 'Tick every note shown in the list', onclick: () => tick('all') }, 'All'),
          h('button', { 'data-tick': 'invert', title: 'Tick the unticked ones and untick the ticked ones', onclick: () => tick('invert') }, 'Invert'),
          h('button', { 'data-tick': 'none', title: 'Untick every note shown in the list', onclick: () => tick('none') }, 'None')));
      if (isDoneView()) {
        const sel = shown().filter(n => st.reportSel.has(n.id));
        footEl.append(h('span', { class: 'un-foot-info' }, sel.length ? `${plural(sel.length, 'done note')} selected for the list` : 'Tick done notes for a list of the work done'), tickSeg,
          h('button', { class: 'un-btn pri', 'data-act': 'report', disabled: !sel.length, onclick: () => report() }, 'Print / export done list'));
        return;
      }
      const q = C.queued(notes).length;
      const modeSel = h('select', { class: 'un-mode', title: 'Full: everything a fresh session needs. Short: only the changes, for a follow-up in the same session.' },
        h('option', { value: 'full' }, 'Full prompt (new session)'), h('option', { value: 'short' }, 'Short prompt (follow-up)'));
      modeSel.value = store.get('mode', 'full');
      modeSel.addEventListener('change', () => store.set('mode', modeSel.value));
      footEl.append(h('span', { class: 'un-foot-info' }, q ? `${plural(q, 'note')} queued` : 'Tick notes to queue them'), tickSeg,
        h('button', { class: 'un-btn', title: 'Reorder the list: high → normal → low', onclick: () => { notes = C.sortByPriority(notes); persist(); drawList(); } }, 'Sort by priority'),
        modeSel,
        h('button', { class: 'un-btn pri', 'data-act': 'prompt', disabled: !q, onclick: () => generate('prompt', modeSel.value) }, 'Make a prompt'),
        h('button', { class: 'un-btn', 'data-act': 'feedback', disabled: !q, title: 'Plain feedback text (fixes, adjustments, wishes) to paste anywhere', onclick: () => generate('feedback') }, 'User feedback'));
    }

    // ---------------------------------------------------------------- output view (prompt / feedback / done list)
    function outputView({ heading, help, text, kind, modeSeg, extraButtons, onSaved }) {
      st.view = 'output';
      const boxEl = h('textarea', { class: 'un-output', spellcheck: 'false' }); boxEl.value = text;
      const info = h('span', { class: 'un-foot-info' }, '');
      const out = { box: boxEl, info, savedFile: null, edited: false };
      boxEl.addEventListener('input', () => { out.edited = true; });
      out.save = async () => {
        if (opts.dryRun) { info.textContent = '(preview, not saved)'; return; }
        try { const r = await api.saveText(boxEl.value, typeof kind === 'function' ? kind() : kind); out.savedFile = r.file; info.textContent = 'Saved: ' + r.file; info.title = r.file; if (onSaved) onSaved(r.file); }
        catch (e) { info.textContent = 'Could not save: ' + e; }
      };
      frame([
        header(h('h2', {}, heading), modeSeg || null),
        h('div', { class: 'un-help' }, help),
        h('div', { class: 'un-body one' }, boxEl),
        h('div', { class: 'un-foot' }, info,
          h('button', { class: 'un-btn', onclick: () => renderList() }, '← Back to notes'),
          h('button', { class: 'un-btn', onclick: () => { if (out.savedFile) api.openFolder(out.savedFile); else toast('Not saved yet', 'warn'); } }, 'Open folder'),
          h('button', { class: 'un-btn', onclick: () => out.save() }, 'Save edited copy'),
          ...(extraButtons ? extraButtons(out) : []),
          h('button', { class: 'un-btn pri', onclick: () => { api.copy(boxEl.value); toast('Copied to the clipboard', 'ok'); } }, 'Copy to clipboard')),
      ]);
      return out;
    }

    async function generate(kind, mode) {
      const chosen = C.queued(notes);
      if (!chosen.length) return;
      const cfg = (opts.config && opts.config()) || {};
      const make = m => kind === 'feedback' ? C.buildFeedback(chosen, cfg) : C.buildPrompt(chosen, cfg, { mode: m });
      let curMode = mode || 'full', out = null;
      const modeSeg = kind === 'feedback' ? null : h('div', { class: 'un-seg' }, [['full', 'Full'], ['short', 'Short']].map(([k, l]) => h('button', {
        class: curMode === k ? 'on' : '',
        title: k === 'full' ? 'Everything a fresh session needs' : 'Only the changes, for a follow-up in the same session',
        onclick: async e => {
          if (out.edited && !confirm('Replace your edits with the ' + l.toLowerCase() + ' version?')) return;
          curMode = k; store.set('mode', k); out.edited = false; out.box.value = make(k);
          [...modeSeg.children].forEach(b => b.classList.toggle('on', b === e.currentTarget));
          await out.save();
        },
      }, l)));
      const heading = kind === 'feedback' ? `User feedback (${plural(chosen.length, 'note')})` : `Prompt for the next build (${plural(chosen.length, 'note')})`;
      out = outputView({
        heading, modeSeg, text: make(curMode),
        kind: () => (kind === 'feedback' ? 'feedback' : 'prompt-' + curMode),
        help: (kind === 'feedback' ? 'Plain feedback you can paste into a message or ticket. Edit it here if you like.'
          : 'Paste this into a Claude session. Full = a new session; Short = a follow-up in the session that already knows the project.') +
          ' Nothing changes in your notes until you press "Mark as Sent".',
        extraButtons: o => {
          const b = h('button', { class: 'un-btn', 'data-act': 'mark-sent', title: 'Set these notes to Sent (with today\'s date and the saved file). They stay queued until you do.' }, `Mark these ${chosen.length} as Sent`);
          b.addEventListener('click', async () => {
            C.markSent(notes, chosen.map(n => n.id), { as: kind === 'feedback' ? 'feedback' : 'prompt-' + curMode, file: o.savedFile });
            if (!opts.dryRun) await api.save(notes);
            b.disabled = true; b.textContent = `Marked ${chosen.length} as Sent`; toast(`${plural(chosen.length, 'note')} marked as Sent`, 'ok');
          });
          return [b];
        },
      });
      await out.save();                       // keeps a copy of the text as a file; does not change any note
      return out.box.value;
    }

    // Done list: the ticked done notes, as text you can copy/save and as a printed page or PDF.
    async function report() {
      const chosen = notes.filter(n => st.reportSel.has(n.id) && C.matchesFilter(n, 'done'));
      if (!chosen.length) return;
      const cfg = (opts.config && opts.config()) || {};
      const out = outputView({
        heading: `Done list (${plural(chosen.length, 'task')})`, kind: 'done-list', text: C.buildDoneReport(chosen, cfg),
        help: 'The work that was done, one entry per note. Edit it here if you like; Print and PDF use this text.',
        extraButtons: o => !api.print ? [] : [
          h('button', { class: 'un-btn', 'data-act': 'print', onclick: async () => { try { await api.print(C.buildDoneReportHtml(o.box.value, (cfg.appName || 'App') + ' — work done'), {}); } catch (e) { toast('Could not print: ' + e, 'bad'); } } }, 'Print…'),
          h('button', { class: 'un-btn', 'data-act': 'pdf', onclick: async () => { try { const r = await api.print(C.buildDoneReportHtml(o.box.value, (cfg.appName || 'App') + ' — work done'), { pdf: true }); if (r && r.file) { o.info.textContent = 'PDF saved: ' + r.file; o.info.title = r.file; toast('PDF saved', 'ok'); } } catch (e) { toast('Could not make the PDF: ' + e, 'bad'); } } }, 'Save as PDF…'),
        ],
      });
      if (!opts.dryRun) out.info.textContent = 'Not saved yet: "Save edited copy" keeps it as a .md file.';
      else out.info.textContent = '(preview, not saved)';
      return out.box.value;
    }

    return { open, close, isOpen, generate, report, tick, applyBulk, undoDelete, deletedCount: () => deleted.length, _state: () => ({ notes, st }) };
  }

  // Forget the window's remembered choices (filter, prompt mode, window size), e.g. for a host's "factory settings".
  // Forgets filter, prompt mode and window size. The list of deleted notes (1.4.0, for Undo delete) is data, not a preference: kept.
  function clearPrefs() { try { for (const k of Object.keys(localStorage)) if (k.startsWith('usernotes.') && k !== 'usernotes.deleted') localStorage.removeItem(k); } catch {} }

  root.UserNotesUI = { create, clearPrefs };
})(typeof self !== 'undefined' ? self : this);
