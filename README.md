# Peergos Snap

A Windows 11 tray app for **pictures and region videos**, shared through **[Peergos](https://peergos.net)**
end-to-end-encrypted storage or pasted straight from the clipboard. It works like Gyazo or ShareX, but is built for Peergos.

- **Live, non-freezing selection.** The screen keeps moving while you drag, so games, Second Life and videos stay
  live. The picture is taken the instant you release the mouse (a delay is optional). A click without dragging
  takes the window under the pointer.
- **Region video (MP4 or WebM) with sound** – the sound Windows plays, only when there is any – and a floating
  Stop · Pause · Cancel bar that never shows in the video. You can also click the tray icon once to start and again
  to stop. A short dialog then offers *Upload & copy link*, *Copy video*, *Save as…* or *Discard*.
- **Direct to a friend.** Drop, paste or capture a picture (or send a video or any file) and it appears on your
  friend's screen within seconds, in a window where both of you can zoom, save, label, pin and star it. Each file is
  shared read-only with that one friend, who gets their own copy in their Peergos; Delete only ever removes your own
  copy.
- **Discard** any capture from its notification card: gone from this PC, Peergos, the clipboard and the history.
- **Two outputs, switched in the tray menu.** *Secret link* uploads the capture to Peergos and copies a link.
  *Media to clipboard* copies the picture or video file itself, ready for Ctrl+V.
- **Your Peergos account.** Sign in once (two-factor codes supported). Only the Peergos session is kept, never
  the password. Every capture gets its own short, read-only secret link, the same kind the Peergos web app creates:
  `https://peergos.net/secret/<owner>/<id>#<key>?open=true`.
- **Delay with a frozen screen.** Choose 1–5 s in the tray menu (or up to 60 s in Settings); menus opened during
  the countdown stay in the picture, because the screen freezes when it ends.
- **History manager.** Every capture with a preview, the app it came from, a label and its link, shown on this PC
  and in Peergos side by side. Find old links, and delete on this PC (Recycle Bin), in Peergos or both.
- **Safe fallback.** If an upload fails, the capture goes to the clipboard instead. Every capture is also kept on
  this PC (forever by default, in one folder per month, or only when the upload failed), and you can mirror all
  captures to a folder of your choice.
- **Always know what happened.** After every capture, a notification card (never captured itself) says whether
  you now have the link, the picture/video, or an error with its reason on the clipboard.
- **18 colour schemes** (dark, medium and bright) and a font size for every window, including the tray menu,
  Help and User notes, with Windows 11 Fluent controls.
- **Automatic updates** from GitHub releases (checked against their SHA256 list), plus *Check for the new version*
  in Settings.
- **Built-in Help** with a quick start (tray menu → Help, or F1 in Settings).
- **Global hotkeys**, which you can change: Ctrl+Shift+1 takes a picture, Ctrl+Shift+2 starts or stops a video.
  You can add hotkeys for pause and for switching the output.
- **Self-contained.** FFmpeg, a Java runtime, the Peergos client, NAudio and the .NET runtime are all bundled, so
  nothing needs to be on PATH.
- **User notes** (tray menu). Keep a list of wanted changes while you test, then turn them into an AI prompt or
  user feedback.

## Install

Download `PeergosSnap-Setup-x.y.z.exe` from [Releases](https://github.com/anderlejan/peergos-snap/releases) and
check it against `SHA256SUMS-x.y.z.txt`. It installs per user to `%LOCALAPPDATA%\Programs\Peergos Snap`, with
optional autostart. You can choose "all users" in the installer to use Program Files instead.

Next, right-click the tray icon → **Settings → Peergos**, enter your Peergos username and password and click
**Sign in**. Later versions install themselves automatically.

Guides:
- The complete user help is built into the app (tray menu → **Help**); its source is
  [src/PeergosSnap/help/index.html](src/PeergosSnap/help/index.html).
- [docs/PEERGOS-INTEGRATION.md](docs/PEERGOS-INTEGRATION.md): how sign-in, uploads and links work
- [CHANGELOG.md](CHANGELOG.md)

## Build

You need Windows 11, the .NET 10 SDK, the Microsoft Build of OpenJDK 25.0.3 (with jmods), Inno Setup 6, Node.js
(for the User-notes tests) and git. The build stops if the JDK was not built from the OpenJDK source pinned in
`build/deps.ps1`, because that source is attached to each release.

```powershell
.\build\build.ps1            # development build → dist\
.\build\build.ps1 -Release   # clean tree required; also writes the source zip
.\build\fetch-sources.ps1    # corresponding source of the bundled GPL/AGPL binaries (release assets)
```

The build runs the unit tests and the module tests. It then compiles the bridge, builds a trimmed Java runtime
with jlink and publishes the app. After adding FFmpeg and the licences, it runs `PeergosSnap.exe --selftest` on
the packaged app and builds the installer. Last, it writes `SHA256SUMS`.

Layout: `src/PeergosSnap` (C#/WPF app), `bridge/` (Java bridge to Peergos, AGPL-3.0), `installer/` (Inno Setup),
`build/` (scripts, pinned dependencies), `tests/` (xUnit).

## Licence

GPL-3.0-or-later. The bridge is AGPL-3.0 because it links against Peergos. The bundled components are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), and each release carries their source code.
