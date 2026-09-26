'use strict';
// Self-contained tests for the User notes module (Node, no Electron): node usernotes.test.js
const fs = require('fs'); const path = require('path'); const os = require('os');
const C = require('./usernotes-core');
const { UserNotesStore, saveText } = require('./usernotes-store');
let failed = 0;
const check = (name, cond) => { if (!cond) failed++; console.log((cond ? 'PASS ' : 'FAIL ') + name); };

const m = C.migrate({ notes: [{ id: '1', title: 'Old', status: 'prompted', promptedAt: '2026-01-01T00:00:00Z' }, { title: 'X', status: '??' }] });
check('migrate prompted -> sent', m.notes[0].status === 'sent' && m.notes[0].sentAt);
check('unknown status -> open', m.notes[1].status === 'open');
const n = C.normalizeNote({ status: 'done' }); C.setQueued(n, true); const q = n.status; C.setQueued(n, false);
check('tick queues, untick restores', q === 'queued' && n.status === 'done');
const list = ['A', 'B'].map((t, i) => C.normalizeNote({ id: t, title: t, type: i ? 'fix' : 'improvement', priority: i ? 'high' : 'normal' }));
list.forEach(x => C.setQueued(x, true));
const cfg = { appName: 'App', version: '1.0', rules: ['R1'], deliver: ['D1'], docs: ['doc.md'] };
const full = C.buildPrompt(list, cfg, { mode: 'full' }), short = C.buildPrompt(list, cfg, { mode: 'short' });
check('full prompt has rules', full.includes('R1') && full.includes('D1') && full.includes('### 2. B'));
check('short prompt is shorter, no rules', !short.includes('R1') && short.length < full.length);
check('feedback groups fixes first', C.buildFeedback(list, cfg).indexOf('not working right') < C.buildFeedback(list, cfg).indexOf('New things'));
C.markSent(list, ['A', 'B'], { as: 'prompt-short', file: 'f.md' });
check('markSent', list.every(x => x.status === 'sent' && x.sentAs === 'prompt-short'));
// 1.1.0
check('version 1.4.0', C.MODULE_VERSION === '1.4.0');
const b = ['p', 'q', 'r'].map(id => C.normalizeNote({ id, title: id }));
C.setQueued(b[0], true); C.setQueued(b[1], true);
C.bulkUpdate(b, ['p', 'q'], { type: 'fix', area: 'UI', priority: 'high' });
check('bulk: type/area/priority, ticks kept', b.slice(0, 2).every(x => x.type === 'fix' && x.area === 'UI' && x.priority === 'high' && x.status === 'queued') && b[2].type === 'improvement');
C.bulkUpdate(b, ['p', 'q'], { status: 'done' }, { version: '9.9' });
check('bulk: status done unticks, records version', b.slice(0, 2).every(x => x.status === 'done' && x.doneIn === '9.9' && !x.prevStatus));
check('bulk: remove', C.removeNotes(b, ['p', 'r']).map(x => x.id).join() === 'q');
const t3 = ['a', 'b', 'c'].map(id => C.normalizeNote({ id, title: id }));
C.tickMany(t3, 'all'); const allQ = t3.every(x => x.status === 'queued');
C.setQueued(t3[1], false); C.tickMany(t3, 'invert');
check('tick all / invert', allQ && t3[1].status === 'queued' && t3[0].status === 'open' && t3[2].status === 'open');
C.tickMany(t3, 'none'); check('tick none', t3.every(x => x.status === 'open'));
const dn = C.normalizeNote({ id: 'd', status: 'done' }); C.setQueued(dn, true);
check('ticked done note stays under Done filter, not under Not done', C.matchesFilter(dn, 'done') && !C.matchesFilter(dn, 'active') && C.matchesFilter(dn, 'queued'));
const sel = new Set(['a']); C.selectMany(sel, t3, 'invert'); check('selectMany invert', !sel.has('a') && sel.has('b') && sel.has('c'));
const done = [C.normalizeNote({ id: 'x', title: 'Area fix', details: 'Area can be changed.\n- item <b>', status: 'done', doneIn: '0.4.0', area: 'UI' })];
const rep = C.buildDoneReport(done, { appName: 'App' });
check('done report lists title and version', rep.includes('## 1. Area fix') && rep.includes('done in 0.4.0'));
const html = C.buildDoneReportHtml(rep, 'T');
check('done report html escapes and formats', html.includes('<h2>1. Area fix</h2>') && html.includes('&lt;b&gt;') && !html.includes('<b>') && html.includes('<li>'));
const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'un-'));
const s1 = new UserNotesStore(path.join(dir, 'n.json')); s1.save(list); s1.flush();
check('store round trip', new UserNotesStore(path.join(dir, 'n.json')).list().length === 2);
check('saveText', fs.readFileSync(saveText(path.join(dir, 'out'), 'x', 'feedback'), 'utf8') === 'x');
// 1.4.0: deleted notes come back at their old places; the last 5 deletions are kept
{
  const ns = ['a', 'b', 'c', 'd', 'e'].map(id => C.normalizeNote({ id, title: id.toUpperCase() }));
  const rec = C.deletion(ns, ['b', 'd']);
  const after = C.removeNotes(ns, ['b', 'd']);
  check('deletion record keeps copies and places', rec.items.length === 2 && rec.items[0].index === 1 && rec.items[1].index === 3 && rec.items[0].note !== ns[1]);
  check('restore puts them back where they were', C.restoreDeletion(after, rec).map(n => n.id).join('') === 'abcde');
  check('restore skips notes that are already there', C.restoreDeletion(ns, rec).length === 5);
  let stack = [];
  for (let i = 0; i < 7; i++) stack = C.pushDeletion(stack, C.deletion(ns, [ns[i % 5].id]));
  check('only the last 5 deletions are kept', stack.length === C.UNDO_DELETES && C.UNDO_DELETES === 5 && stack[4].items[0].note.id === 'b');
  check('empty or broken records are ignored', C.pushDeletion([null, { items: [] }], { items: [] }).length === 0);
  const q = C.normalizeNote({ id: 'q', title: 'Q' }); C.setQueued(q, true);
  const r2 = C.restoreDeletion([], C.deletion([q], ['q']));
  check('a queued note comes back queued', r2[0].status === 'queued');
}
console.log(failed ? `${failed} failed` : 'all passed');
process.exit(failed ? 1 : 0);
