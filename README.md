# Peergos Snap

A Windows 11 tray app for **pictures and region videos**, shared through **[Peergos](https://peergos.net)**
end-to-end-encrypted storage or pasted straight from the clipboard. It works like Gyazo or ShareX, but is built for Peergos.

- **Live, non-freezing selection.** The screen keeps moving while you drag, so games, Second Life and videos stay
  live. The picture is taken the instant you release the mouse (a delay is optional). A click without dragging
  takes the window under the pointer.
- **Region video (MP4 or WebM)** with a floating Stop · Pause · Cancel bar that never shows in the video. You can
  also click the tray icon once to start and again to stop. A short dialog then offers *Upload & copy link*,
  *Copy video*, *Save as…* or *Discard*.
- **Two outputs, switched in the tray menu.** *Secret link* uploads the capture to Peergos and copies a link.
  *Media to clipboard* copies the picture or video file itself, ready for Ctrl+V.
- **Two Peergos modes.**
  - **Shared writable folder link (no login).** Uploads into a folder someone shared with
    `https://<host>/secret/<owner>/<id>#<key>`. You get a read-only link to **just that file**.
  - **Account.** Logs in, uploads to a folder in your home and creates a new secret link for each capture.
- **Safe fallback.** If an upload fails, the capture goes to the clipboard instead. Every capture is also kept in
  a local captures folder, and you can mirror all captures to a folder of your choice.
- **Always know what happened.** After every capture, a notification card (never captured itself) says whether
  you now have the link, the picture/video, or an error with its reason on the clipboard.
- **Colour schemes and font size** for every window, including User notes, with Windows 11 Fluent controls.
- **Global hotkeys**, which you can change: Ctrl+Shift+1 takes a picture, Ctrl+Shift+2 starts or stops a video.
  You can add hotkeys for pause and for switching the output.
- **Self-contained.** FFmpeg, a Java runtime, the Peergos client and the .NET runtime are all bundled, so nothing
  needs to be on PATH.
- **User notes** (tray menu). Keep a list of wanted changes while you test, then turn them into an AI prompt or
  user feedback.

## Install

Download `PeergosSnap-Setup-x.y.z.exe` from [Releases](https://github.com/anderlejan/peergos-snap/releases) and
check it against `SHA256SUMS-x.y.z.txt`. It installs per user to `%LOCALAPPDATA%\Programs\Peergos Snap`, with
optional autostart. You can choose "all users" in the installer to use Program Files instead.

Next, right-click the tray icon → **Settings → Peergos**, paste the writable folder link and click **Test connection**.

Guides:
- [docs/USER-GUIDE.md](docs/USER-GUIDE.md): usage, settings, hotkeys, files, troubleshooting
- [docs/PEERGOS-INTEGRATION.md](docs/PEERGOS-INTEGRATION.md): how the upload and link protocol works
- [CHANGELOG.md](CHANGELOG.md)

## Build

You need Windows 11, the .NET 10 SDK, the Microsoft OpenJDK 25 (with jmods), Inno Setup 6, Node.js (for the
User-notes tests) and git.

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
