'use strict';
/* User notes module — storage for the Electron main process (Node).
 * Keeps notes in one JSON file (atomic writes, rotating backups) and saves generated texts as .md files.
 * Part of the reusable "User notes" module: see README.md in this folder.
 */
const fs = require('fs');
const path = require('path');
const core = require('./usernotes-core');

function readJson(file) {
  try { return JSON.parse(fs.readFileSync(file, 'utf8').replace(/^﻿/, '')); }
  catch (e) { if (e.code === 'ENOENT') return null; try { fs.copyFileSync(file, file + '.corrupt-' + Date.now()); } catch {} return null; }
}
function writeAtomic(file, data) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const tmp = file + '.tmp';
  fs.writeFileSync(tmp, JSON.stringify(data, null, 1), 'utf8');
  for (let i = 0; ; i++) { try { fs.renameSync(tmp, file); return; } catch (e) { if (i > 6) throw e; const t = Date.now() + 60; while (Date.now() < t) { /* wait */ } } }
}
function stamp() { const d = new Date(); const p = n => String(n).padStart(2, '0'); return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}_${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`; }

class UserNotesStore {
  /** @param {string} file JSON file; @param {{backups?:number, seed?:object[]}} opts */
  constructor(file, opts = {}) {
    this.file = file;
    const existed = fs.existsSync(file);
    if (existed && opts.backups !== 0) this._backup(opts.backups || 15);
    const raw = readJson(file);
    this.data = core.migrate(raw || { notes: existed ? [] : (opts.seed || []) });
    this.existed = existed;
    this.timer = null;
    if (!existed || !raw || raw.version !== 2) this.flush();
  }
  _backup(keep) {
    const dir = path.join(path.dirname(this.file), 'backups');
    fs.mkdirSync(dir, { recursive: true });
    const base = path.basename(this.file, '.json');
    fs.copyFileSync(this.file, path.join(dir, `${base}-${stamp()}.json`));
    const old = fs.readdirSync(dir).filter(f => f.startsWith(base + '-') && f.endsWith('.json')).sort();
    while (old.length > keep) { try { fs.unlinkSync(path.join(dir, old.shift())); } catch {} }
  }
  list() { return this.data.notes; }
  save(list) {
    if (!Array.isArray(list)) throw new Error('notes must be a list');
    this.data.notes = list.map(core.normalizeNote);
    clearTimeout(this.timer); this.timer = setTimeout(() => this.flush(), 300);
    return this.data.notes;
  }
  flush() { clearTimeout(this.timer); writeAtomic(this.file, this.data); }
}

/** Save a generated prompt / feedback text as <folder>/<kind>-<date_time>.md and return the path. */
function saveText(folder, text, kind) {
  fs.mkdirSync(folder, { recursive: true });
  const safe = String(kind || 'prompt').replace(/[^a-z0-9-]/gi, '') || 'prompt';
  const file = path.join(folder, `${safe}-${stamp()}.md`);
  fs.writeFileSync(file, String(text || ''), 'utf8');
  return file;
}

/**
 * Wire the store to IPC. Channels: usernotes:get, usernotes:save, usernotes:saveText.
 * @param {Electron.IpcMain} ipcMain
 * @param {{store: UserNotesStore, folder: () => string}} o
 */
function registerIpc(ipcMain, o) {
  ipcMain.handle('usernotes:get', () => o.store.list());
  ipcMain.handle('usernotes:save', (_e, list) => o.store.save(list));
  ipcMain.handle('usernotes:saveText', (_e, text, kind) => ({ file: saveText(o.folder(), text, kind) }));
}

module.exports = { UserNotesStore, saveText, registerIpc, core };
