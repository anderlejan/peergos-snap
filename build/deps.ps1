# Pinned third-party binaries bundled into Peergos Snap. Every entry has a fixed URL and SHA256.
# Their licences and the matching source code are listed in THIRD-PARTY-NOTICES.md; the release also carries
# the corresponding source archives (see build\fetch-sources.ps1).
$Deps = @{
  PeergosJar = @{
    # Official Peergos release (AGPL-3.0), built from Peergos/web-ui v1.35.1 (commit 3a9b822f2df2a5a3df393b849ae1ecd5ddbf5f29,
    # Peergos submodule d735c3b6e2b94654d754876a2fad8f6fb12a5156).
    Url = 'https://github.com/Peergos/web-ui/releases/download/v1.35.1/Peergos.jar'
    File = 'Peergos-1.35.1.jar'
    Sha256 = 'BD84E5DCAB25CD87925E7B8D49EFF8E74B3E9CE9FF9C68041DF0CA2740197743'
  }
  FfmpegZip = @{
    # FFmpeg n8.1.3, GPL static build by BtbN/FFmpeg-Builds tag autobuild-2026-09-26-13-03
    # (build scripts commit 58cc05f33c20e3ead0ce876b72531ab482d0f981).
    Url = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-09-26-13-03/ffmpeg-n8.1.3-win64-gpl-8.1.zip'
    File = 'ffmpeg-n8.1.3-win64-gpl-8.1.zip'
    Sha256 = 'D20EF03F0F4161453B9F46A72471EB5370F56B6410FE8BCA14A4B0220C6C924A'
  }
}

# Corresponding source for the bundled GPL/AGPL binaries (attached to every GitHub release).
$Sources = @(
  @{ Url = 'https://ffmpeg.org/releases/ffmpeg-8.1.3.tar.xz'; File = 'ffmpeg-8.1.3.tar.xz' }
  @{ Url = 'https://github.com/BtbN/FFmpeg-Builds/archive/58cc05f33c20e3ead0ce876b72531ab482d0f981.tar.gz'; File = 'FFmpeg-Builds-58cc05f3-build-scripts.tar.gz' }
  @{ Url = 'https://github.com/Peergos/web-ui/archive/3a9b822f2df2a5a3df393b849ae1ecd5ddbf5f29.tar.gz'; File = 'peergos-web-ui-1.35.1-source.tar.gz' }
  @{ Url = 'https://github.com/Peergos/Peergos/archive/d735c3b6e2b94654d754876a2fad8f6fb12a5156.tar.gz'; File = 'peergos-d735c3b6-source.tar.gz' }
)
