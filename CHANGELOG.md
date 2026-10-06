# Changelog

## 2.5.0 – 2026-10-06

- **The direct window shows every file better.** Pictures zoom as in the history: the mouse wheel zooms where the
  pointer is, dragging moves the picture, and *View* (or a double-click) shows it full screen – the view follows the
  mouse, ← → go through the pictures and videos, *D* draws on it.
- **Videos play in the direct window.** The list and the preview show a still of each video with its length and
  picture size; ▶ (or Space) plays it there, with a bar to jump to any moment and the sound on or off.
- **What a file is, and what to do with it.** Under the preview: what it is (PNG picture, MP4 video, PDF document,
  ZIP archive …), its size, picture size or length, who sent it when, and where it is (your Peergos, this PC). Then
  *Open*, *Folder*, *Save as…*, *Copy*, *Draw on it*, *Open as folder* / *Unpack to…* (ZIP files), *Send to…*
  (another friend), *Get a link* (a copy in your own Peergos with a secret link) and *Delete*. Files without a
  preview show a card with their kind and size; a ZIP file lists what is inside.
- **Draw first.** A tick next to *Take picture* (also Settings → Direct and the tray menu) opens the drawing editor
  with the picture before it goes to your friend: *Send to … ✓*, *Send without drawing* or *Don't send*.
- **Draw on any picture of the direct window** – one you received or sent – and send the drawn copy back.
- **Folders to a friend.** *Send a folder…* (or drop a folder) sends it as one ZIP file named like the folder; the
  friend clicks *Open as folder* (unpacked next to its copy and opened in Explorer) or *Unpack to…*.
- **Explorer's right-click menu (optional).** Settings → General adds *Peergos Snap* to the right-click menu of files
  and folders: upload and copy the link, or send to a friend. A question always comes first – what, how much, to whom
  – so nothing leaves the PC by accident; several selected items get one question. On Windows 11 it is under *Show
  more options*.
- **Settings export and import.** Settings → General saves all or some settings in a file and reads them back –
  to keep them, to set up a friend's Peergos Snap, or to show an AI. The sign-in is never in the file; personal
  details (account name, friends, folders) only when ticked.
- **Direct to a friend in every mode** of the tray menu, with *Take a picture for …*, *Send files to …* and *Send a
  folder to …* for each friend (a submenu each with several friends).
- **How long cards stay**: Settings → Output → *Notification cards stay for* 4 to 30 seconds, or until closed.

## 2.4.0 – 2026-10-06

- **After a capture: copy it and upload it (the new default).** The picture or video is on the clipboard at once and is
  uploaded to Peergos at the same time; its card has *Copy link* for the secret link, and the history keeps the link.
  A failed upload always shows a card; the capture stays on the clipboard and on this PC. The other choices remain:
  *Upload and copy the secret link* and *Copy the picture only (no upload)*. An earlier choice is kept. The question
  after a recording offers *Upload & copy video* first; the output hotkey goes round all three choices.
- **Camera sound.** Taking a picture plays a short sound made for Peergos Snap: a telephoto lens focusing, then the
  shutter. Settings → Capture turns it off; *▶ Listen* plays it.
- **The tray menu, reorganised.** Three modes in the middle – *Pictures*, *Videos*, *Files* – decide what the top of the
  menu shows and what a left click on the icon does (a picture, a video, or *Upload files…*). Choosing a mode keeps the
  menu open. *After a picture ▸* / *After a video ▸* hold the output choice; *History…*, *Settings…* and *Delay ▸* are at
  the bottom, and *Open captures folder*, *User notes…* and *Help* under *Other ▸*. The menu is about half as long.
- **Fixed: the first right click on the tray icon showed no menu**; only the second one did.
- **Keep a copy on this PC: Never** (Settings → Files & history). Captures then stay only in Peergos, or only on the
  clipboard (pasting the file still works until Peergos Snap restarts). A capture whose upload failed is always kept,
  so nothing is lost.
- **History from the card.** The card after a capture has *History*: it opens the history with that capture selected,
  to label, view, draw on or delete it.
- **User notes** say "Paste this into an AI session" (User notes module 1.5.1).
- Help updated for all of the above.

## 2.3.0 – 2026-10-03

- **Draw on pictures.** Arrows, lines, boxes, ellipses, highlights, a free pen, text labels, numbered steps (1, 2, 3 …)
  and blur (pixelation that really removes what was under it), with colours, three sizes, undo/redo and zoom. Right
  after a capture: tray menu → *Take picture and draw on it*, *Draw on it…* in the after-picture question, or for every
  picture (Settings → Capture). From the history: *Draw* saves the drawing as a new capture; the original stays.
- **Upload files and folders** from the tray menu (*Upload files…*, *Upload a folder…*). Files get their own secret
  links (all copied, one per line); a folder is uploaded with its subfolders and gets one link. They appear in the
  history (filter *Uploaded files and folders*); the originals on the PC are only read, never changed or deleted.
  Hidden and system files are left out, folder names in any script (Cyrillic, Greek, Chinese …) arrive unchanged, and
  the name *Direct* inside PeergosSnap stays reserved for direct sharing.
- **History: full-screen view.** Double-click the preview (or *View*, or Space): the mouse wheel zooms where the pointer
  is, the view follows the mouse, a click zooms in or out, ← → go through the list, Esc closes.
- **History: buttons on top.** The actions sit above the preview in a compact bar with icons (*Copy link*, *Link*,
  *View*, *Open*, *Folder*, *Copy picture*, *Draw*, *Upload*, *Lock*, *Delete ▾*), and a right click on the list
  offers them too.
- **History: lock.** Locked entries (🔒, key L) are never deleted or removed: not by any delete, *Clean up*, clearing
  the history, or the automatic removal of old local copies. New filter *Locked*.
- **History: asking before a delete is optional** (default on): *Don't ask again* in the question, or Settings →
  Files & history → *Ask before deleting in the history*.
- **History: select with the mouse.** A tick box on every row adds or removes it from the selection; *☑ All* /
  *☐ None* at the top.
- **Fixed: deleting while not signed in to Peergos.** A delete that needs Peergos while you are signed out no longer
  half-runs: it says which files cannot be deleted in Peergos now and offers *Sign in to Peergos…*, *Delete on this PC
  only* (the entries stay for later) or Cancel. A refused delete says why and keeps the entry. The open history looks
  at Peergos again when you sign in or out, and shows *Peergos ?* while that is not known.
- **Fixed: signing out while direct sharing runs.** Signing out of Peergos (or switching the account) while direct
  sharing was running could freeze Peergos Snap. It now stops direct sharing and carries on.
- **Direct window: everything by default.** It opens with *All friends* and *All months*; choosing a friend or month
  narrows the list. *Send to* in the drop area chooses the receiver when several friends are shown.
- **One main folder in Peergos.** Direct sharing now uses *PeergosSnap/Direct/(friend)/(month)* inside the app's
  folder instead of *PeergosSnap-Direct* next to it. Files from 2.2 stay where they are and are still shown, labelled
  and deletable. A friend still on 2.2 sees new files only after their Peergos Snap updates. Because Direct is now
  inside PeergosSnap, direct sharing pauses with a clear message when PeergosSnap (or Direct, or the friend's folder
  there) is shared with someone or has a secret link: what you exchange would be visible there too.
- **Choose the Peergos folder by browsing** (Settings → Peergos → *Browse…*), or make a new one there.
- Help updated for all of the above.

## 2.2.0 – 2026-10-01

- **Direct to a friend.** Send a picture, video or any file straight to a friend's screen while you talk with them in
  another app: drop it on the direct window, paste it (Ctrl+V), choose files, or take a picture for them (tray menu →
  *Direct to a friend*). It appears on their side within seconds, in a window where both of you can zoom, save, label,
  pin and star it – labels, pins and stars are seen by both. What you send stays in your Peergos
  (`PeergosSnap-Direct/<friend>/<month>`); each file is shared read-only with that friend only – the folder is not
  shared and no link is created. The receiver's Peergos Snap copies it into their own Peergos (`…/<month>/received`),
  so each side has its own copy, and *Delete* only ever deletes your own. *Also keep received files on this PC*
  (Settings → Direct, on by default) downloads what arrives. Friends are
  added in the window (*Friends…*: friend requests sent and accepted there). The receiving side needs nothing but
  Peergos Snap running and signed in. New Settings page *Direct*.
- **Sound in videos.** Videos get the sound Windows plays while recording (on by default; tray menu → *Videos* →
  *Record sound*, or Settings → Capture). A recording during which nothing played gets no sound track. The microphone
  is not recorded.
- **Discard for pictures.** The card after a capture has *Discard*: the capture is removed from this PC (and the
  mirror folder), from Peergos (the link stops working), from the clipboard if it is still there, and from the
  history. *After a picture, ask what to do* (Settings → Capture, formerly *Ask before uploading a picture*) offers
  the same choices as after a recording, including *Discard*.
- **Discarded captures are deleted at once** by default, never kept (also videos, which went to the Recycle Bin in
  2.1). Settings → Files & history can bring the Recycle Bin back.
- **Keep a copy on this PC: only if the upload fails** (Settings → Files & history; default *Always*).
- **History: the Delete key** runs the default delete action, highlighted in the details: *From both* by default,
  which now also removes the entry from the history (Settings → Files & history can keep it, or choose another
  default). *From both* also works when the capture is only on one side.
- Help updated for all of the above.

## 2.1.0 – 2026-09-27

- **Delay before capturing, with a frozen screen.** Tray menu → *Delay* → 1–5 seconds (any length up to 60 s in
  Settings → Capture). A countdown card appears, and the tray icon shows the seconds. When it ends the whole screen
  freezes – menus and pop-ups opened during the countdown stay in the picture – and you select the area (or click
  the menu itself) on the frozen screen. Videos: the area is marked first and recording starts when the countdown
  ends. Cancel with the card, the tray icon or the hotkey. The old delay after releasing the mouse remains as
  *Extra wait* (Settings → Overlay).
- **Keep local copies forever by default.** Installs that still had the old 30-day default switch to "keep
  forever" once. If you set a number of days, older copies go to the Recycle Bin instead of being deleted.
- **Month folders.** Captures go into one folder per month (`2026-10`), or per day, per year or none (Settings →
  Files & history). The mirror folder uses the same folders. Captures from 2.0 lying directly in the captures folder
  are sorted into month folders once.
- **History manager** (tray menu → History…): every capture with a preview (videos too), time, the app it came from,
  size, label and link, and whether it is on this PC and/or in Peergos. It has search, filters (pictures, videos,
  only on this PC, only in Peergos, missing, labelled …), sorting (newest, oldest, name, size, app) and day groups.
  You can copy or open old links (links of earlier uploads are read back from Peergos), add labels, upload later,
  and delete from this PC (Recycle Bin), from Peergos (the file and its links) or both, one by one or several at
  once, always after an inline confirmation. You can also remove entries, remove entries whose files are gone, or
  clear the whole history; files are never touched by that, and removed entries do not come back.
- **User notes: text size** control in its header (A− · 100 % · A+, Ctrl+Plus / Ctrl+Minus / Ctrl+0),
  remembered; the notes window now looks like the rest of the app (User notes module 1.5.0).
- **Settings: categories in a column on the left**, with a new *Files & history* page.
- Help updated for all of the above.
- *Discard* after a recording moves the video to the Recycle Bin instead of deleting it.
- The release now also carries the source code of the bundled Java runtime (Microsoft Build of OpenJDK 25.0.3),
  and the build checks that the runtime was built from exactly that source.
- Fixed: Start menu → *Peergos Snap – Help* now opens the Help also when Peergos Snap was not running yet.


## 2.0.0 – 2026-09-27

**Sharing through a folder link was removed.** Links now always come from your Peergos account, in the short
form of the Peergos web app (`https://peergos.net/secret/<owner>/<id>#<key>?open=true`). The long links that 1.x
created for folder uploads (`https://peergos.net/#…/…/…/…`) are being disabled by Peergos.

- **Sign in once, password not stored.** Settings → Peergos signs in (with a two-factor code if the account uses
  one) and keeps only the Peergos session, encrypted for your Windows account, until you sign out. An account
  password stored by 1.x is used once to sign in automatically and is then deleted. If the session ends (for
  example after a password change), a card asks you to sign in again. A folder link stored by 1.x is kept but
  unused.
- **Show password:** an eye button next to the password field shows or hides what you typed.
- **Mouse pointer in videos:** besides Settings → Capture, the tray menu now has *Videos → Show mouse pointer*.
  The self-test checks that the option really shows or hides the pointer.
- **Automatic updates:** Peergos Snap checks the latest GitHub release at start and once a day. It downloads the
  installer, verifies it against the release's SHA256 list, installs it silently and starts again, never during a
  recording or upload. Settings → General: *Check for new versions automatically*, *Install new versions
  automatically* and **Check for the new version**.
- **The tray menu follows the colour scheme** (colours, font size, accent ticks, rounded corners).
- **18 colour schemes:** 6 dark, 6 of medium brightness and 6 bright ones that are soft on the eyes (no pure
  white), plus *Follow Windows*. All are checked for readable contrast; the Appearance list groups them.
- **Built-in Help** with a quick start and every feature, setting and common problem explained (tray menu →
  Help, F1 or the Help button in Settings, Start menu → Peergos Snap – Help). It shows the version number and
  nothing else about the app's making. `docs/USER-GUIDE.md` was replaced by it.
- Hotkeys are only registered again when they change.


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
