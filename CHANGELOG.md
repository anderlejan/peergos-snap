# Changelog

## 1.1.1 – 2026-09-26

First public release (contains everything from 1.1.0).

- Release builds no longer contain the build machine's folder paths: source paths are neutral and no debug
  symbols are shipped. The build fails if a local path ever appears in a shipped app binary.

## 1.1.0 – 2026-09-26 (internal build, not published)


- **Notifications you always see.** The app shows its own notification card (bottom right, never included in
  captures) instead of Windows balloon tips, which "Do not disturb" or notification settings could swallow
  silently. After every capture it says which of the outcomes happened: *link copied*, *picture/video copied to
  the clipboard*, *upload failed – copied instead* (with the reason), or *failed*. It includes a thumbnail and the
  buttons **Open link** and **Show file**, and shows upload progress. Warnings and errors are always shown; the
  Notifications setting only hides the success and progress cards.
- **Colour schemes and font size** (Settings → Appearance): Follow Windows, Peergos dark / light, Dark, Light,
  Midnight, Sepia, High contrast; font size 80–160 %. They apply to every window, including User notes, the
  recording bar, dialogs and notification cards, with the modern Windows 11 (Fluent) controls.
- **User notes reveal nothing about the developer.** Full prompts no longer contain any local path, name or
  repository address. A source location appears only if you enter one in Settings → General (it stays on this
  PC). Tests check this.
- Switching the output with its hotkey now confirms the new output on a card.

## 1.0.1 – 2026-09-26 (internal build, not published)

Fixes found while testing the 1.0.0 install (both came from User notes):
- Esc now always cancels the selection, including when the capture was started by a command (`--picture`,
  `--video`) and Windows would not give the overlay the keyboard focus. While the overlay is open, Esc works as
  a temporary global hotkey. A second start of the app also passes the foreground right to the running copy.
- The finish dialog's button read "Upload && copy link"; it now reads "Upload & copy link".

## 1.0.0 – 2026-09-26 (internal build, not published)

First build.

- Tray app for Windows 11 (.NET 10, WPF). Left-clicking the icon takes a picture, or starts/stops a video,
  depending on the setting. The right-click menu has capture, mode, output, Settings, User notes and Quit.
- Live transparent selection overlay: the screen does not freeze. Instant capture on mouse release, optional
  delay, click to pick a window, optional dim, and a size label.
- Pictures as PNG (default) or JPG. Region videos as MP4/H.264 (default) or WebM/VP9, recorded with the bundled
  FFmpeg. Pause/resume works through segments that are joined without re-encoding. A Stop/Pause/Cancel bar and a
  frame show next to the region; both are excluded from capture.
- After a recording, a dialog offers Upload & copy link, Copy video, Save as… or Discard. Closing it keeps the
  video locally only.
- Peergos upload through the official Peergos client (v1.35.1) on a bundled Java 25 runtime:
  - writable folder secret link, no login, with optional link password; returns a read-only capability link
    to the uploaded file;
  - account login; returns a new secret link for each capture.
- Output switch: secret link, or media to clipboard (bitmap + PNG + file for pictures, the file for videos).
- Fallback to the clipboard when an upload fails. Every capture is kept in a local captures folder (optional
  auto-cleanup), with an optional mirror folder.
- Global hotkeys (Ctrl+Shift+1 picture, Ctrl+Shift+2 video; you can also set pause and output switch). Conflicts
  are shown in Settings.
- Settings apply and save at once. Secrets are encrypted with DPAPI.
- User notes 1.4.0 module (WebView2) for testers: notes, prompts, feedback and a done list with print/PDF.
- Per-user Inno Setup installer with an autostart option; a running copy is closed on install and uninstall.
- `--selftest` checks the packaged app: FFmpeg, recording with pause, picture, bridge, and an optional live upload.
