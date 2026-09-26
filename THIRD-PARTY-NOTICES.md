# Third-party notices

Peergos Snap is licensed under the **GNU General Public License v3.0 or later** (see `LICENSE`).
The Java bridge in `bridge/` links against Peergos and is licensed under the **GNU Affero General Public
License v3.0** (see `bridge/LICENSE`); GPL-3.0 section 13 permits combining it with the GPL-3.0 app.

The repository contains **no third-party source code or binaries**. The build script downloads the pinned
components below (fixed URL + SHA256 in `build/deps.ps1`) and bundles them unmodified into the installer.
Every GitHub release also carries the **corresponding source code** of the bundled GPL/AGPL binaries
(`build/fetch-sources.ps1`), so the release itself fulfils GPL-3.0 / AGPL-3.0 section 6.

| Component | Version | Licence | Bundled as | Source |
|---|---|---|---|---|
| Peergos | 1.35.1 (web-ui 3a9b822f, Peergos d735c3b6) | AGPL-3.0 | `bridge\Peergos.jar` (official release file) | https://github.com/Peergos/web-ui/tree/v1.35.1 · https://github.com/Peergos/Peergos/tree/d735c3b6e2b94654d754876a2fad8f6fb12a5156 · attached to each release |
| FFmpeg (static, GPL variant) | n8.1.3, BtbN autobuild-2026-09-26-13-03 | GPL-3.0-or-later (with GPL-compatible libraries: x264, x265, libvpx, …) | `tools\ffmpeg\ffmpeg.exe` | https://ffmpeg.org/releases/ffmpeg-8.1.3.tar.xz · build scripts https://github.com/BtbN/FFmpeg-Builds/tree/58cc05f33c20e3ead0ce876b72531ab482d0f981 · attached to each release |
| OpenJDK runtime (Microsoft Build of OpenJDK, jlink image) | 25.0.3 | GPL-2.0 with Classpath Exception | `runtime\` | https://github.com/openjdk/jdk · licence texts in `runtime\legal\` and `licenses\openjdk-legal\` |
| .NET runtime + WPF/WinForms (self-contained) | 10.0 | MIT | app folder | https://github.com/dotnet/runtime · https://github.com/dotnet/wpf |
| Microsoft WebView2 SDK (`Microsoft.Web.WebView2` NuGet: loader + managed wrapper) | 1.0.3650.58 | BSD-3-Clause | `Microsoft.Web.WebView2.*.dll`, `WebView2Loader.dll` | https://www.nuget.org/packages/Microsoft.Web.WebView2 |
| Microsoft Edge WebView2 Runtime | part of Windows 11 | Microsoft (OS component, **not** redistributed) | – | – |

Build-time only (not shipped): xUnit (Apache-2.0), Microsoft.NET.Test.Sdk (MIT), Inno Setup (Inno Setup licence;
the generated installer carries no restriction), JDK tools.

`src/PeergosSnap/usernotes/` is the reusable "User notes" module (version 1.4.0), included unchanged and licensed
with this project under GPL-3.0-or-later.

Peergos.jar is the unmodified official release; it contains the Peergos code and the libraries Peergos itself
bundles, each under its own licence as listed in the Peergos repository (`lib/` and its licence files).
The tray/app icon is original artwork drawn by `build/make-icon.ps1` (GPL-3.0-or-later with the project).

## Written offer

For three years after each release, and for as long as the release is offered, the complete corresponding
source code of every bundled GPL/AGPL component is available as release assets at
https://github.com/anderlejan/peergos-snap/releases and on request via
https://github.com/anderlejan/peergos-snap/issues.
