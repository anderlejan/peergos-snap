# Downloads the corresponding source code of the bundled GPL/AGPL binaries into dist\sources\ so it can be
# attached to the GitHub release next to the installer (GPL-2.0 section 3 / GPL-3.0 section 6 / AGPL-3.0 section 6).
# Every archive must have the SHA256 pinned in build\deps.ps1.
$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path "$PSScriptRoot\..").Path
. "$PSScriptRoot\deps.ps1"
$out = "$Root\dist\sources"
New-Item -ItemType Directory -Force $out | Out-Null
$lines = @()
foreach ($s in $Sources) {
  $f = Join-Path $out $s.File
  if (-not (Test-Path $f)) {
    Write-Host "download $($s.Url)"
    & curl.exe -sSL --fail -o "$f.part" $s.Url
    if ($LASTEXITCODE -ne 0) { throw "download failed: $($s.Url)" }
    $got = (Get-FileHash "$f.part" -Algorithm SHA256).Hash
    if ($got -ne $s.Sha256) { Remove-Item -Force "$f.part"; throw "$($s.File): the download has SHA256 $got, not the one pinned in build\deps.ps1" }
    Move-Item -Force "$f.part" $f
  }
  $hash = (Get-FileHash $f -Algorithm SHA256).Hash
  if ($hash -ne $s.Sha256) { throw "dist\sources\$($s.File) has SHA256 $hash, not the one pinned in build\deps.ps1 - delete it and run this again" }
  $lines += "{0}  {1}" -f $hash.ToLower(), $s.File
}
[IO.File]::WriteAllText("$out\SHA256SUMS-sources.txt", ($lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
$lines
