# User notes module (1.5.1)

A small, reusable "User notes" window: testers keep a list of wanted changes, tick some, and turn them into a
**prompt** for an AI coding session (Full or Short) or plain **user feedback**. Statuses (open → queued → sent → done)
change only by hand. Done notes can be listed, printed or saved as PDF.

| File | Purpose |
|---|---|
| `usernotes-core.js` | pure logic (statuses, migration, prompts, feedback, done report); browser + Node |
| `usernotes-ui.js` | the window (global `UserNotesUI`) |
| `usernotes.css` | styles; uses the host's CSS variables (`--bg`, `--fg`, `--acc`, …) |
| `usernotes-store.js` | JSON storage for Electron hosts (not used by Peergos Snap, which stores notes in C#) |
| `usernotes.test.js` | tests: `node usernotes.test.js` |

In Peergos Snap the module runs unchanged inside WebView2 (`notes-host/`); `UI/NotesWindow.cs` provides the
storage, clipboard, print and the prompt texts. Those texts contain nothing about the developer; a source location
appears only if the user sets one in Settings → General.

Licensed with Peergos Snap under GPL-3.0-or-later.
