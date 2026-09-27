/* User notes module — core (no DOM, no Node APIs). Works as a browser global (UserNotesCore) and as a CommonJS module.
 * Part of the reusable "User notes" module: see README.md in this folder.
 * Note lifecycle:  open → queued (ticked for the next prompt/feedback) → sent → done.
 * Statuses change only when the user changes them (1.1.0: generating a prompt no longer marks notes as sent by itself).
 */
(function (root, factory) {
  if (typeof module === 'object' && module.exports) module.exports = factory();
  else root.UserNotesCore = factory();
})(typeof self !== 'undefined' ? self : this, function () {
  'use strict';
  const MODULE_VERSION = '1.5.0';
  const STATUSES = ['open', 'queued', 'sent', 'done'];
  const STATUS_LABEL = { open: 'Open', queued: 'Queued', sent: 'Sent', done: 'Done' };
  const STATUS_HINT = {
    open: 'Written down, not planned yet',
    queued: 'Ticked: goes into the next prompt or feedback',
    sent: 'Marked as sent: included in a prompt or feedback you used',
    done: 'Implemented',
  };
  const PRIORITIES = ['high', 'normal', 'low'];
  const TYPES = ['improvement', 'fix', 'adjustment', 'question'];
  const TYPE_LABEL = { improvement: 'New / improvement', fix: 'Fix (something is wrong)', adjustment: 'Adjustment', question: 'Question' };
  const PRIO_RANK = { high: 0, normal: 1, low: 2 };

  function uid() { return Math.random().toString(16).slice(2, 10) + Date.now().toString(16).slice(-4); }
  function nowIso() { return new Date().toISOString(); }
  function today() { const d = new Date(); const p = n => String(n).padStart(2, '0'); return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`; }

  function normalizeNote(n) {
    n = n || {};
    let status = String(n.status || 'open');
    if (status === 'prompted') status = 'sent';            // v0.2.x name
    if (!STATUSES.includes(status)) status = 'open';
    return {
      id: String(n.id || uid()),
      title: String(n.title || '').slice(0, 300),
      details: String(n.details || ''),
      area: String(n.area || ''),
      type: TYPES.includes(n.type) ? n.type : 'improvement',
      priority: PRIORITIES.includes(n.priority) ? n.priority : 'normal',
      status,
      created: n.created || nowIso(),
      updated: n.updated || n.created || nowIso(),
      sentAt: n.sentAt || n.promptedAt || null,
      sentAs: n.sentAs || (n.promptedAt ? 'prompt-full' : null),
      sentFile: n.sentFile || n.promptFile || null,
      doneIn: n.doneIn || null,
      ...(status === 'queued' && n.prevStatus ? { prevStatus: String(n.prevStatus) } : {}),
    };
  }

  function migrate(data) {
    const notes = Array.isArray(data && data.notes) ? data.notes : [];
    return { version: 2, notes: notes.map(normalizeNote) };
  }

  // Tick box <-> status: ticking queues a note, unticking returns it to the status it had before (open if unknown).
  function setQueued(note, on) {
    if (on) { if (note.status !== 'queued') { note.prevStatus = note.status; note.status = 'queued'; } }
    else if (note.status === 'queued') { note.status = note.prevStatus && note.prevStatus !== 'queued' ? note.prevStatus : 'open'; delete note.prevStatus; }
    note.updated = nowIso();
    return note;
  }
  function queued(notes) { return notes.filter(n => n.status === 'queued'); }
  // The status a note has apart from being ticked (a ticked note keeps showing under its own status filter).
  function baseStatus(n) { return n.status === 'queued' ? (n.prevStatus && n.prevStatus !== 'queued' ? n.prevStatus : 'open') : n.status; }
  function matchesFilter(n, f) {
    if (f === 'all') return true;
    if (f === 'queued') return n.status === 'queued';
    if (f === 'active') return baseStatus(n) !== 'done';
    return baseStatus(n) === f;
  }
  // Tick helpers for a list of shown notes: 'all' | 'none' | 'invert'.
  function tickMany(list, how) {
    for (const n of list) setQueued(n, how === 'all' ? true : how === 'none' ? false : n.status !== 'queued');
    return list;
  }
  // Same for a plain selection Set of ids (used for the done-list report).
  function selectMany(sel, list, how) {
    for (const n of list) { const on = how === 'all' ? true : how === 'none' ? false : !sel.has(n.id); if (on) sel.add(n.id); else sel.delete(n.id); }
    return sel;
  }

  // Bulk changes (1.3.0) for the ticked notes: patch may hold type, area, priority, status.
  // A status other than "queued" unticks the notes; "done" records the version, "sent" the date. Returns how many changed.
  function bulkUpdate(notes, ids, patch, info) {
    const set = new Set(ids); let n = 0; const at = nowIso();
    for (const x of notes) {
      if (!set.has(x.id)) continue;
      if ('type' in patch && TYPES.includes(patch.type)) x.type = patch.type;
      if ('priority' in patch && PRIORITIES.includes(patch.priority)) x.priority = patch.priority;
      if ('area' in patch) x.area = String(patch.area || '');
      if ('status' in patch && STATUSES.includes(patch.status)) {
        if (patch.status === 'queued') setQueued(x, true);
        else {
          if (x.status === 'queued') setQueued(x, false);
          x.status = patch.status;
          if (patch.status === 'sent' && !x.sentAt) { x.sentAt = at; x.sentAs = x.sentAs || 'manual'; }
          if (patch.status === 'done' && !x.doneIn) x.doneIn = (info && info.version) || null;
        }
      }
      x.updated = at; n++;
    }
    return n;
  }
  function removeNotes(notes, ids) { const set = new Set(ids); return notes.filter(x => !set.has(x.id)); }
  // 1.4.0: deleted notes can be brought back. A deletion record keeps copies of the notes and where they were;
  // the UI keeps the last UNDO_DELETES records (a single delete or a bulk delete is one record each).
  const UNDO_DELETES = 5;
  function deletion(notes, ids) {
    const set = new Set(ids), items = [];
    notes.forEach((n, i) => { if (set.has(n.id)) items.push({ note: JSON.parse(JSON.stringify(n)), index: i }); });
    return { at: new Date().toISOString(), items };
  }
  function pushDeletion(stack, rec, max = UNDO_DELETES) {
    const base = Array.isArray(stack) ? stack.filter(x => x && Array.isArray(x.items) && x.items.length) : [];
    if (!rec || !rec.items || !rec.items.length) return base;
    const out = base.concat([rec]);
    return out.slice(Math.max(0, out.length - max));
  }
  // Puts the notes of a record back at their old places (notes already present are skipped).
  function restoreDeletion(notes, rec) {
    const out = notes.slice(), have = new Set(out.map(n => n.id));
    for (const { note, index } of (rec && rec.items || []).slice().sort((a, b) => a.index - b.index)) {
      if (!note || have.has(note.id)) continue;
      out.splice(Math.max(0, Math.min(Number(index) || 0, out.length)), 0, normalizeNote(note)); have.add(note.id);
    }
    return out;
  }

  function markSent(notes, ids, info) {
    const set = new Set(ids);
    const at = (info && info.at) || nowIso();
    for (const n of notes) if (set.has(n.id)) {
      n.status = 'sent'; n.sentAt = at; n.sentAs = (info && info.as) || 'prompt-full'; n.sentFile = (info && info.file) || null; delete n.prevStatus; n.updated = at;
    }
    return notes;
  }

  function sortByPriority(notes) {
    return notes.map((n, i) => [n, i]).sort((a, b) => (PRIO_RANK[a[0].priority] ?? 1) - (PRIO_RANK[b[0].priority] ?? 1) || a[1] - b[1]).map(x => x[0]);
  }
  function move(notes, id, delta) {
    const i = notes.findIndex(n => n.id === id), j = i + delta;
    if (i < 0 || j < 0 || j >= notes.length) return notes;
    const out = notes.slice(); [out[i], out[j]] = [out[j], out[i]]; return out;
  }
  function moveBefore(notes, id, beforeId) {
    if (id === beforeId) return notes;
    const out = notes.slice(); const from = out.findIndex(n => n.id === id); if (from < 0) return notes;
    const [m] = out.splice(from, 1); const to = out.findIndex(n => n.id === beforeId);
    out.splice(to < 0 ? out.length : to, 0, m); return out;
  }

  // ---------------------------------------------------------------- generated texts
  // cfg: { appName, appDescription, version, docs:[string], location, rules:[string], deliver:[string], extraShort:[string] }
  function noteBlock(n, i, withMeta) {
    const meta = [n.type && n.type !== 'improvement' ? `type: ${n.type}` : null, n.area && `area: ${n.area}`, `priority: ${n.priority || 'normal'}`, `note id: ${n.id}`].filter(Boolean).join(' · ');
    const L = [`### ${i + 1}. ${n.title || '(untitled)'}`];
    if (withMeta) L.push(`_${meta}_`);
    L.push('', (n.details || '').trim() || '(no details given; ask what exactly is wanted)', '');
    return L;
  }

  function buildPrompt(notes, cfg, opts) {
    cfg = cfg || {}; const mode = (opts && opts.mode) || 'full';
    const app = cfg.appName || 'the application';
    const L = [];
    if (mode === 'short') {
      L.push(`# ${app} — follow-up changes (${today()}, from User notes)`, '');
      L.push(`Same project, rules and delivery steps as in the previous request${cfg.docs && cfg.docs.length ? ` (see ${cfg.docs[0]})` : ''}. Installed version now: **${cfg.version || '?'}**.`, '');
      L.push(`## Changes (${notes.length})`, '');
      notes.forEach((n, i) => L.push(...noteBlock(n, i, true)));
      L.push('## Deliver', '');
      for (const r of cfg.extraShort || ['Bump the version, build, install and verify as usual.', 'Extend the tests for the new behaviour.']) L.push(`- ${r}`);
      L.push('- Report per change: what you did, how you verified it, anything not done.');
      return L.join('\n');
    }
    L.push(`# ${app} — change request from User notes (${today()})`, '');
    L.push(`Your task is to improve **${app}** and deliver a new, installed version.${cfg.appDescription ? ' ' + cfg.appDescription : ''} Installed version now: **${cfg.version || '?'}**.`, '');
    L.push('## Before you change anything', '');
    let k = 1;
    if (cfg.docs && cfg.docs.length) L.push(`${k++}. Read ${cfg.docs.map(d => '`' + d + '`').join(' and ')}.`);
    if (cfg.location) L.push(`${k++}. ${cfg.location}`);
    L.push(`${k++}. Read how the affected part works today before changing it. Keep existing behaviour and saved data compatible (migrate data if you change a format).`);
    L.push(`${k++}. If a request below is ambiguous, ask before building. Never guess on anything that deletes, moves or rewrites files.`, '');
    L.push(`## Requested changes (${notes.length})`, '');
    notes.forEach((n, i) => { L.push(...noteBlock(n, i, true)); L.push('Done when: it works in the installed app, is covered by a test where it can be automated, and is described in the release notes of your report.', ''); });
    if (cfg.rules && cfg.rules.length) { L.push('## Rules (mandatory)', ''); for (const r of cfg.rules) L.push(`- ${r}`); L.push(''); }
    if (cfg.deliver && cfg.deliver.length) { L.push('## Delivery', ''); for (const r of cfg.deliver) L.push(`- ${r}`); L.push(''); }
    L.push('## Report back', '');
    L.push('For each numbered change: what you did, how you verified it (what you checked vs. what you expect), and anything not done. Keep "checked" and "expected" apart. The notes are then marked Done in User notes.');
    return L.join('\n');
  }

  // Plain user feedback: what I want changed, worded for a person, grouped by kind.
  function buildFeedback(notes, cfg) {
    cfg = cfg || {};
    const groups = [
      ['fix', 'Things that are not working right'],
      ['adjustment', 'Things I would like adjusted'],
      ['improvement', 'New things I would like'],
      ['question', 'Questions'],
    ];
    const L = [`Feedback on ${cfg.appName || 'the app'}${cfg.version ? ' ' + cfg.version : ''} (${today()})`, ''];
    let n = 0;
    for (const [type, heading] of groups) {
      const list = notes.filter(x => (x.type || 'improvement') === type);
      if (!list.length) continue;
      L.push(heading + ':', '');
      for (const x of list) {
        n++;
        const tags = [x.area, x.priority === 'high' ? 'important' : x.priority === 'low' ? 'low priority' : null].filter(Boolean).join(', ');
        L.push(`${n}. ${x.title || '(untitled)'}${tags ? ` (${tags})` : ''}`);
        const d = (x.details || '').trim();
        if (d) L.push(...d.split('\n').map(line => '   ' + line));
        L.push('');
      }
    }
    L.push('Thank you!');
    return L.join('\n');
  }

  // ---------------------------------------------------------------- done-list report (what was worked on)
  function buildDoneReport(notes, cfg) {
    cfg = cfg || {};
    const L = [`# ${cfg.appName || 'App'} — work done (${today()})`, ''];
    const versions = [...new Set(notes.map(n => n.doneIn).filter(Boolean))];
    L.push(`${notes.length} task${notes.length === 1 ? '' : 's'}${versions.length ? ` · done in ${versions.join(', ')}` : ''}${cfg.version ? ` · installed version ${cfg.version}` : ''}`, '');
    notes.forEach((n, i) => {
      L.push(`## ${i + 1}. ${n.title || '(untitled)'}`);
      const meta = [TYPE_LABEL[n.type] && n.type !== 'improvement' ? TYPE_LABEL[n.type] : null, n.area ? `area: ${n.area}` : null, n.priority !== 'normal' ? `priority: ${n.priority}` : null,
        `created ${String(n.created).slice(0, 10)}`, n.sentAt ? `sent ${String(n.sentAt).slice(0, 10)}` : null, n.doneIn ? `done in ${n.doneIn}` : null].filter(Boolean).join(' · ');
      L.push(`_${meta}_`, '');
      const d = (n.details || '').trim();
      if (d) L.push(d, '');
    });
    return L.join('\n');
  }
  function esc(t) { return String(t).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;'); }
  // Small Markdown subset -> HTML (headings, "- " lists, full-line _italic_, **bold**, paragraphs). Everything else is escaped text.
  function mdToHtml(text) {
    const out = []; let para = [], list = null;
    const inline = t => esc(t).replace(/\*\*(.+?)\*\*/g, '<b>$1</b>').replace(/`([^`]+)`/g, '<code>$1</code>');
    const flush = () => { if (para.length) { out.push('<p>' + para.map(inline).join('<br>') + '</p>'); para = []; } if (list) { out.push('<ul>' + list.map(x => '<li>' + inline(x) + '</li>').join('') + '</ul>'); list = null; } };
    for (const raw of String(text).split(/\r?\n/)) {
      const line = raw.replace(/\s+$/, '');
      let m;
      if (!line.trim()) { flush(); continue; }
      if ((m = /^(#{1,4})\s+(.*)$/.exec(line))) { flush(); out.push(`<h${m[1].length}>${inline(m[2])}</h${m[1].length}>`); continue; }
      if ((m = /^\s*[-*]\s+(.*)$/.exec(line))) { if (para.length) { const l = list; list = null; flush(); list = l; } (list = list || []).push(m[1]); continue; }
      if ((m = /^_(.+)_$/.exec(line.trim()))) { flush(); out.push('<p class="meta">' + inline(m[1]) + '</p>'); continue; }
      if (list) flush();
      para.push(line);
    }
    flush();
    return out.join('\n');
  }
  function buildDoneReportHtml(text, title) {
    return '<!doctype html><html><head><meta charset="utf-8"><title>' + esc(title || 'Work done') + '</title><style>' +
      'body{font-family:Georgia,"Times New Roman",serif;font-size:11.5pt;line-height:1.45;color:#111;margin:0;padding:18mm 16mm;}' +
      'h1{font-size:17pt;margin:0 0 4pt;}h2{font-size:12.5pt;margin:14pt 0 2pt;page-break-after:avoid;}h3{font-size:11.5pt;}' +
      'p{margin:0 0 6pt;}p.meta{color:#555;font-style:italic;font-size:9.5pt;}ul{margin:0 0 6pt 16pt;padding:0;}code{font-family:Consolas,monospace;font-size:10pt;}' +
      '@page{margin:12mm;}@media print{body{padding:0;}}' +
      '</style></head><body>' + mdToHtml(text) + '</body></html>';
  }

  // 1.5.0: text size of the notes window (percent of the host's size), for the optional size control.
  const FONT_SIZES = [80, 90, 100, 110, 120, 130, 140, 150, 160];
  function clampFontSize(v) {
    const n = Number(v);
    if (!Number.isFinite(n)) return 100;
    return FONT_SIZES.reduce((best, s) => Math.abs(s - n) < Math.abs(best - n) ? s : best, 100);
  }
  /** One step smaller (dir < 0), larger (dir > 0) or back to 100 % (dir === 0). */
  function stepFontSize(v, dir) {
    if (dir === 0) return 100;
    const i = FONT_SIZES.indexOf(clampFontSize(v));
    return FONT_SIZES[Math.min(FONT_SIZES.length - 1, Math.max(0, i + (dir > 0 ? 1 : -1)))];
  }

  return { MODULE_VERSION, FONT_SIZES, clampFontSize, stepFontSize, STATUSES, STATUS_LABEL, STATUS_HINT, PRIORITIES, TYPES, TYPE_LABEL, uid, normalizeNote, migrate, setQueued, queued, baseStatus, matchesFilter, tickMany, selectMany, bulkUpdate, removeNotes, UNDO_DELETES, deletion, pushDeletion, restoreDeletion, markSent, sortByPriority, move, moveBefore, buildPrompt, buildFeedback, buildDoneReport, mdToHtml, buildDoneReportHtml };
});
