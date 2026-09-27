# Downloads the corresponding source code of the bundled GPL/AGPL binaries into dist\sources\ so it can be
# attached to the GitHub release next to the installer (GPL-2.0 section 3 / GPL-3.0 section 6 / AGPL-3.0 section 6).
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
    Move-Item -Force "$f.part" $f
  }
  $lines += "{0}  {1}" -f (Get-FileHash $f -Algorithm SHA256).Hash.ToLower(), $s.File
}
[IO.File]::WriteAllText("$out\SHA256SUMS-sources.txt", ($lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
$lines
