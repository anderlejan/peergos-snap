# Peergos Snap – user guide (1.1.0)

## First start

1. Install `PeergosSnap-Setup-1.0.0.exe` (keep "Start Peergos Snap when I sign in" ticked if you want it to start
   with Windows).
2. A camera icon appears in the tray. If it is hidden, pin it: open the taskbar's ^ area and drag the icon onto the
   taskbar.
3. Right-click the icon → **Settings… → Peergos**:
   - **Shared writable folder link.** Paste `https://peergos.net/secret/<owner>/<id>#<key>`, add the link password
     if it has one, then click **Test connection**. You should see "✓ Works – uploads go to /…".
   - or **My Peergos account**: server, username, password and a folder (default `PeergosSnap`).

## What happened? The notification card

After every capture a card appears at the bottom right of the screen for a few seconds. Warnings and errors stay
longer, and hovering keeps the card open. It is never included in your pictures or videos. It always tells you
which of these happened:

| Card | What is on your clipboard |
|---|---|
| **Link copied to the clipboard** (green) | the Peergos link (buttons: *Open link*, *Show file*) |
| **Picture / video copied to the clipboard** (green) | the picture itself, or the video file |
| **Upload failed – the picture is on the clipboard instead** (orange) | the picture/video; the card gives the reason |
| **… failed** (red) | nothing new; the reason is shown and the file stays in the captures folder |

While uploading, the card shows the progress. The *Show notifications* setting only hides the green and
progress cards; orange and red ones always appear.

## Taking pictures

- **Left-click the tray icon** (in Picture mode) or press **Ctrl+Shift+1**.
- The screen does **not** freeze: everything underneath keeps moving. Drag a rectangle; the picture is taken the
  moment you release the mouse.
- **Click without dragging** to take the window under the pointer (it is outlined while you hover).
- **Esc** or a right-click cancels.
- What happens next depends on the **Output** setting (tray menu):
  - *Secret link*: the picture uploads to Peergos and the link is copied. The card says "Link copied to the
    clipboard" and has an *Open link* button.
  - *Media to clipboard*: the picture itself is copied (as bitmap, PNG and file). Paste it with Ctrl+V.

## Recording videos

- Press **Ctrl+Shift+2**, choose *Record video* in the tray menu, or set *Tray click takes → Video* and left-click
  the tray icon.
- Drag the area. Recording starts on release. A dashed frame marks the area, and a small bar next to it shows the
  time with **■ Stop · ⏸ Pause · ✕ Cancel**. The frame and the bar never appear in the video, even for full-screen
  recordings.
- To stop, use **Stop** in the bar, **Ctrl+Shift+2**, or a left-click on the tray icon (the icon shows a red dot
  while recording).
- The finish dialog offers: **Upload & copy link** · **Copy video** (the file, for pasting into chats or Explorer) ·
  **Save as…** · **Discard**. You can also **▶ Play it first**. Closing the dialog or pressing Esc keeps the video
  only in the local captures folder, so nothing is uploaded.
- Videos have no sound in 1.0.0.

## When an upload fails

When you're offline, the link is wrong or the server is full, the app never loses the capture:
- it goes to the clipboard instead (setting *If the upload fails, copy …*, on by default);
- it is always saved in the **local captures folder** (`%LOCALAPPDATA%\PeergosSnap\Captures`; *Open captures
  folder* in the tray menu). Copies older than 30 days are deleted; set 0 days to keep them forever;
- the notification says why the upload failed.

## Settings (right-click → Settings…)

Changes are saved at once. Invalid values show a red note and are not saved until you fix them.

| Tab | Setting | Default |
|---|---|---|
| Peergos | Mode: shared writable folder link / account; link + link password; server, username, password, folder | folder link |
| Capture | Tray click takes a picture / starts or stops a video | picture |
| | Picture format png / jpg; video format mp4 / webm | png, mp4 |
| | Frame rate 15/24/30/60; quality (CRF 15–40); show the mouse pointer | 30, 23, on |
| | Ask before uploading a picture | off |
| Overlay | Darken outside the selection 0–70 % (0 = fully transparent) | 0 % |
| | Border colour; show size; click picks a window | #FF3B30, on, on |
| | Delay after release (ms) | 0 |
| Output | Secret link / media to clipboard | secret link |
| | Fallback to the clipboard; notifications | on, on |
| | Mirror every capture to a folder | off |
| | Delete local copies older than N days | 30 |
| Hotkeys | Picture, video start/stop, pause/resume, switch output | Ctrl+Shift+1, Ctrl+Shift+2, –, – |
| Appearance | Colour scheme (Follow Windows, Peergos dark/light, Dark, Light, Midnight, Sepia, High contrast); font size 80–160 % | Follow Windows, 100 % |
| General | Start with Windows; show "User notes…" in the tray menu; source location for AI prompts (optional); open log; licences | –, on, empty |

**Hotkeys:** click a box and press the keys you want. Backspace clears it. A red note means another program already
uses that combination.

Passwords and links are stored encrypted for your Windows account (DPAPI) in `%APPDATA%\PeergosSnap\settings.json`.

## User notes (for testers)

Tray menu → **User notes…** (or Settings → General → Open User notes).
- Add a note per wanted change, with a type (fix, adjustment, improvement, question), an area and a priority.
- **Tick** notes to queue them. Then **make a prompt**: *Full* gives everything a new AI coding session needs,
  including app, source location, rules and delivery; *Short* gives only the changes, for the same session.
  **User feedback** gives plain grouped text. The texts are saved as `.md` files in `%APPDATA%\PeergosSnap\prompts`.
- Statuses (open → queued → sent → done) change **only by hand**. After you actually sent a prompt, click
  **Mark these N as Sent**. When a change is delivered, set the note to Done.
- While notes are ticked, a bar under the list sets type, area, priority or status for all of them, or deletes them.
  **Ctrl+Z** (outside text fields) brings back the last 5 deletions.
- The **Done** filter lists finished work, which you can copy, save, print or save as PDF.
- Notes are stored in `%APPDATA%\PeergosSnap\user-notes.json`, with 15 rotating backups in `backups\`.
- Prompts contain no names or paths of whoever made the app. If you work on the source yourself, enter its location
  in Settings → General → *Source location for AI prompts*. Full prompts then name it (only on your PC).

## Command line

`PeergosSnap.exe --picture | --video | --settings | --notes | --quit` sends the command to the running copy (or
starts it). `--selftest --report file.txt [--upload]` checks the installation without touching your data.

## Files

| What | Where |
|---|---|
| Program | `%LOCALAPPDATA%\Programs\Peergos Snap` (bundled `tools\ffmpeg`, `runtime` (Java), `bridge` (Peergos)) |
| Settings, notes, prompts | `%APPDATA%\PeergosSnap` |
| Captures (local backup), log | `%LOCALAPPDATA%\PeergosSnap\Captures`, `…\logs\peergos-snap.log` |

## Troubleshooting

- **"Peergos not set up – copied instead"**: add the link or account in Settings → Peergos, then Test.
- **"The shared folder link is read-only"**: the owner must share the folder with a *writable* secret link.
- **Black video of a game**: some games in exclusive full screen cannot be captured by GDI. Run the game in
  borderless/windowed mode.
- **A hotkey does nothing**: Settings → Hotkeys shows whether another program owns it.
- Details of every step are in the log (Settings → General → Open log).
