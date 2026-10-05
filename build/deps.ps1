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

# The Java runtime is a jlink image of the Microsoft Build of OpenJDK 25.0.3+9 (GPL-2.0 with Classpath Exception),
# built by Microsoft from microsoft/openjdk-jdk25u at this commit (the SOURCE line of the JDK's "release" file).
# build.ps1 refuses a JDK built from another commit, so the attached source always matches the shipped runtime.
$JdkSourceCommit = '7a05ec815bae'

# Corresponding source for the bundled GPL/AGPL binaries (attached to every GitHub release), pinned like the
# binaries: build\fetch-sources.ps1 refuses an archive with another SHA256.
$Sources = @(
  @{ Url = 'https://ffmpeg.org/releases/ffmpeg-8.1.3.tar.xz'; File = 'ffmpeg-8.1.3.tar.xz'
     Sha256 = '7138D28C96D9D3E3AF4EE3D8CAD72741F8FFB40DA90C1112235DEA3ECD3178A3' }
  @{ Url = 'https://github.com/BtbN/FFmpeg-Builds/archive/58cc05f33c20e3ead0ce876b72531ab482d0f981.tar.gz'; File = 'FFmpeg-Builds-58cc05f3-build-scripts.tar.gz'
     Sha256 = '197A00FF1C4955AB1CDF157CF434F27E6F3C8A0E2345EE512CD3B8DB72364833' }
  @{ Url = 'https://github.com/Peergos/web-ui/archive/3a9b822f2df2a5a3df393b849ae1ecd5ddbf5f29.tar.gz'; File = 'peergos-web-ui-1.35.1-source.tar.gz'
     Sha256 = 'AA156326EE3AC07BEDADF998B6DE93DBFB292CB7E61B78DE3EEAD6D933997507' }
  @{ Url = 'https://github.com/Peergos/Peergos/archive/d735c3b6e2b94654d754876a2fad8f6fb12a5156.tar.gz'; File = 'peergos-d735c3b6-source.tar.gz'
     Sha256 = '385BC4D2C869F1DF2AADDF0B41195A6169AF2B42005EDDE8257B2A0499347CCF' }
  @{ Url = 'https://github.com/microsoft/openjdk-jdk25u/archive/7a05ec815bae26c959a47577a4490630822712a1.tar.gz'; File = 'openjdk-jdk25u-7a05ec815bae-source.tar.gz'
     Sha256 = '41C4B163D90E7BF2D2AF1F344D5C193A646DCAE22FF138117ADF77056ED3AD7B' }
)
