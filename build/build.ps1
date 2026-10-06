<#
  Peergos Snap build: tests -> bridge -> Java runtime -> app publish -> bundle -> self-test -> installer
  -> source snapshot -> SHA256SUMS.  Output goes to dist\ (never overwritten).

  .\build\build.ps1              # development build (allows uncommitted changes, no source zip)
  .\build\build.ps1 -Release     # release build: refuses uncommitted changes, adds the source zip
  .\build\build.ps1 -Upload      # also run a real upload in the self-test (needs PEERGOS_SNAP_TEST_LINK)
  .\build\build.ps1 -StageOnly   # development: only the packaged, self-tested app in out\stage (no installer,
                                 # nothing in dist\, so the version's installer name stays free for the release)
#>
param([switch]$Release, [switch]$Upload, [switch]$SkipTests, [switch]$StageOnly)
if ($Release -and $StageOnly) { throw "-Release builds the installer: leave out -StageOnly" }
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$Root = (Resolve-Path "$PSScriptRoot\..").Path
Set-Location $Root

. "$PSScriptRoot\deps.ps1"   # pinned third-party downloads: URLs + SHA256

function Step($t) { Write-Host "`n== $t" -ForegroundColor Cyan }
function Invoke-Git { $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'; $o = & git.exe @args 2>$null; $code = $LASTEXITCODE; $ErrorActionPreference = $old; if ($code -ne 0) { throw "git $args failed" }; $o }
function Run($exe, [string[]]$argv) { & $exe @argv; if ($LASTEXITCODE -ne 0) { throw "$exe failed ($LASTEXITCODE)" } }

$csproj = [xml](Get-Content "$Root\src\PeergosSnap\PeergosSnap.csproj")
$Version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version }).Version
Write-Host "Peergos Snap $Version" -ForegroundColor Green

if ($Release) {
  $dirty = Invoke-Git status --porcelain
  if ($dirty) { throw "Release builds need a clean working tree:`n$($dirty -join "`n")" }
}
$Dist = "$Root\dist"
New-Item -ItemType Directory -Force $Dist | Out-Null
$SetupName = "PeergosSnap-Setup-$Version.exe"
if (-not $StageOnly -and (Test-Path "$Dist\$SetupName")) { throw "$SetupName already exists. Installers are never overwritten: bump the version." }

# ---------- tools ----------
$Jdk = @("C:\Program Files\Microsoft\jdk-25.0.3.9-hotspot") + (Get-ChildItem "C:\Program Files\Microsoft\jdk-25*" -Directory -ErrorAction SilentlyContinue | ForEach-Object FullName) |
  Where-Object { Test-Path "$_\jmods\java.base.jmod" } | Select-Object -First 1
if (-not $Jdk) { throw "JDK 25 with jmods not found" }
# The runtime shipped in the installer must match the OpenJDK source attached to the release.
if (-not (Select-String -Path "$Jdk\release" -SimpleMatch "SOURCE=`".:git:$JdkSourceCommit" -Quiet)) {
  throw "The JDK in $Jdk was not built from OpenJDK source $JdkSourceCommit; update `$JdkSourceCommit and the source entry in build\deps.ps1."
}
$Iscc = @("C:\Program Files (x86)\Inno Setup 6\ISCC.exe", "C:\Program Files\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $Iscc) { throw "Inno Setup 6 not found" }

# ---------- third-party downloads (pinned, checksummed) ----------
Step "Dependencies"
$Cache = "$Root\build\.cache"
New-Item -ItemType Directory -Force $Cache | Out-Null
function Fetch($name) {
  $d = $Deps[$name]
  $file = Join-Path $Cache $d.File
  if (-not (Test-Path $file) -or (Get-FileHash $file -Algorithm SHA256).Hash -ne $d.Sha256) {
    Write-Host "download $($d.Url)"
    & curl.exe -sSL --fail -o "$file.part" $d.Url
    if ($LASTEXITCODE -ne 0) { throw "download failed: $($d.Url)" }
    $h = (Get-FileHash "$file.part" -Algorithm SHA256).Hash
    if ($d.Sha256 -and $h -ne $d.Sha256) { throw "checksum mismatch for $($d.File): $h" }
    Move-Item -Force "$file.part" $file
  }
  $file
}
$PeergosJar = Fetch 'PeergosJar'
$FfmpegZip = Fetch 'FfmpegZip'

# ---------- tests ----------
if (-not $SkipTests) {
  Step "Tests"
  Run dotnet @('test', "$Root\tests\PeergosSnap.Tests\PeergosSnap.Tests.csproj", '-c', 'Release', '--nologo', '-v', 'q')
  Push-Location "$Root\src\PeergosSnap\usernotes"; try { Run node @('usernotes.test.js') } finally { Pop-Location }
}

# ---------- bridge ----------
Step "Peergos bridge"
$Stage = "$Root\out\stage"
if (Test-Path "$Root\out") { Remove-Item -Recurse -Force "$Root\out" }
$Classes = "$Root\out\bridge-classes"
New-Item -ItemType Directory -Force $Classes, "$Stage\bridge" | Out-Null
$BridgeSources = Get-ChildItem "$Root\bridge\src\snap\bridge" -Filter *.java | ForEach-Object FullName
Run "$Jdk\bin\javac.exe" (@('-nowarn', '-XDsuppressNotes', '-encoding', 'UTF-8', '-cp', $PeergosJar, '-d', $Classes) + $BridgeSources)
Run "$Jdk\bin\jar.exe" @('--create', '--file', "$Stage\bridge\peergos-snap-bridge.jar", '--date', '2026-01-01T00:00:00Z', '-C', $Classes, '.')
Copy-Item $PeergosJar "$Stage\bridge\Peergos.jar"

# ---------- Java runtime (jlink) ----------
Step "Java runtime"
$Modules = 'java.base,java.compiler,java.desktop,java.instrument,java.management,java.naming,java.net.http,java.scripting,java.security.jgss,java.sql,jdk.httpserver,jdk.jfr,jdk.net,jdk.unsupported,jdk.crypto.cryptoki,jdk.zipfs'
Run "$Jdk\bin\jlink.exe" @('--module-path', "$Jdk\jmods", '--add-modules', $Modules, '--strip-debug', '--no-man-pages', '--no-header-files',
  '--compress', 'zip-6', '--output', "$Stage\runtime")

# ---------- app ----------
Step "Publish app"
Run dotnet @('publish', "$Root\src\PeergosSnap\PeergosSnap.csproj", '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
  '-p:PublishReadyToRun=true', '-o', "$Stage", '--nologo', '-v', 'q')

# ---------- FFmpeg ----------
Step "FFmpeg"
$Ff = "$Root\out\ffmpeg"
Expand-Archive -Force $FfmpegZip $Ff
$FfDir = Get-ChildItem $Ff -Directory | Select-Object -First 1
New-Item -ItemType Directory -Force "$Stage\tools\ffmpeg" | Out-Null
Copy-Item "$($FfDir.FullName)\bin\ffmpeg.exe" "$Stage\tools\ffmpeg\"
Copy-Item "$($FfDir.FullName)\LICENSE.txt" "$Stage\tools\ffmpeg\LICENSE.txt"

# ---------- licences ----------
Step "Licences"
New-Item -ItemType Directory -Force "$Stage\licenses" | Out-Null
Copy-Item "$Root\LICENSE" "$Stage\licenses\LICENSE-PeergosSnap.txt"
Copy-Item "$Root\THIRD-PARTY-NOTICES.md" "$Stage\licenses\THIRD-PARTY-NOTICES.md"
Copy-Item "$Root\bridge\LICENSE" "$Stage\licenses\LICENSE-bridge-AGPL-3.0.txt"
Copy-Item "$Root\bridge\LICENSE" "$Stage\bridge\LICENSE.txt"
Copy-Item "$($FfDir.FullName)\LICENSE.txt" "$Stage\licenses\LICENSE-FFmpeg-GPL-3.0.txt"
Copy-Item -Recurse "$Stage\runtime\legal" "$Stage\licenses\openjdk-legal"
$wv = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.web.webview2" -Directory | Sort-Object Name | Select-Object -Last 1
if (Test-Path "$($wv.FullName)\LICENSE.txt") { Copy-Item "$($wv.FullName)\LICENSE.txt" "$Stage\licenses\LICENSE-WebView2-SDK.txt" }
$dotnetRoot = Split-Path (Get-Command dotnet).Source
if (Test-Path "$dotnetRoot\LICENSE.txt") { Copy-Item "$dotnetRoot\LICENSE.txt" "$Stage\licenses\LICENSE-dotnet-MIT.txt" }
if (Test-Path "$dotnetRoot\ThirdPartyNotices.txt") { Copy-Item "$dotnetRoot\ThirdPartyNotices.txt" "$Stage\licenses\ThirdPartyNotices-dotnet.txt" }

# ---------- privacy: no build-machine paths in shipped binaries ----------
Step "Privacy check"
$needles = @($Root, ($Root -replace '\\', '/'), "\Users\$env:USERNAME\", "/Users/$env:USERNAME/") | Select-Object -Unique
foreach ($f in Get-ChildItem $Stage -Recurse -File -Include 'PeergosSnap*.dll', 'PeergosSnap*.exe', '*.pdb', 'peergos-snap-bridge.jar', '*.js', '*.html', '*.css', '*.json') {
  $bytes = [IO.File]::ReadAllBytes($f.FullName)
  $texts = @([Text.Encoding]::ASCII.GetString($bytes), [Text.Encoding]::Unicode.GetString($bytes))
  foreach ($n in $needles) { foreach ($t in $texts) { if ($t.IndexOf($n, [StringComparison]::OrdinalIgnoreCase) -ge 0) { throw "privacy: '$n' found in $($f.FullName)" } } }
}
if (Get-ChildItem $Stage -Filter 'PeergosSnap*.pdb') { throw "privacy: debug symbols must not be shipped" }
Write-Host "no local paths or user names in the shipped app files"

# ---------- self-test of the packaged app ----------
Step "Self-test (packaged app)"
$report = "$Root\out\selftest.txt"
$st = @('--selftest', '--report', $report); if ($Upload) { $st += '--upload' }
$p = Start-Process -FilePath "$Stage\PeergosSnap.exe" -ArgumentList $st -Wait -PassThru
Get-Content $report | ForEach-Object { Write-Host "  $_" }
if ($p.ExitCode -ne 0) { throw "self-test failed" }

if ($StageOnly) {
  Write-Host "`nDone: $Stage (no installer: -StageOnly)" -ForegroundColor Green
  return
}

# ---------- installer ----------
Step "Installer"
Run $Iscc @('/Q', "/DAppVersion=$Version", "/DStageDir=$Stage", "/DOutDir=$Dist", "$Root\installer\PeergosSnap.iss")

# ---------- source snapshot ----------
if ($Release) {
  Step "Source snapshot"
  $zip = "$Dist\PeergosSnap-Source-$Version.zip"
  if (Test-Path $zip) { throw "$zip exists (never overwritten)" }
  Invoke-Git archive --format=zip "--prefix=PeergosSnap-$Version/" -o $zip HEAD | Out-Null
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $inZip = [IO.Compression.ZipFile]::OpenRead($zip).Entries | Where-Object { $_.Name } | ForEach-Object { $_.FullName.Substring("PeergosSnap-$Version/".Length) } | Sort-Object
  $inGit = Invoke-Git ls-tree -r --name-only HEAD | Sort-Object
  if (Compare-Object $inZip $inGit) { throw "source zip does not match git ls-tree" }
  Write-Host "source zip: $($inGit.Count) files, matches HEAD"
}

# ---------- checksums ----------
Step "SHA256"
$sums = Get-ChildItem $Dist -File | Where-Object { $_.Name -like "*$Version*" -and $_.Extension -in '.exe', '.zip' } |
  ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name }
[IO.File]::WriteAllText("$Dist\SHA256SUMS-$Version.txt", ($sums -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
$sums | ForEach-Object { Write-Host "  $_" }
Write-Host "`nDone: $Dist\$SetupName" -ForegroundColor Green
