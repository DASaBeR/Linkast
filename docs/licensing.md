# Licensing

## Linkast

Linkast is licensed under **GPL-3.0-or-later**. Every source file carries `// SPDX-License-Identifier: GPL-3.0-or-later`.

GPL was chosen because the FFmpeg builds that include x264 are themselves GPL. Licensing Linkast under GPL-3.0-or-later makes distributing Linkast together with such a build straightforward, the same model OBS Studio uses.

## FFmpeg

Linkast ships FFmpeg **9.0.1 "essentials_build" from gyan.dev**, configured with `--enable-gpl --enable-version3` and statically linked with x264, x265 and about 40 other libraries. That makes the binary **GPL-3.0-or-later**.

Linkast runs FFmpeg as a separate executable, communicating only through command-line arguments, pipes and local sockets. It does not link against FFmpeg's libraries.

**How each release complies:**

1. **Pinned build.** `tools/fetch-ffmpeg.ps1` downloads the build from its versioned GitHub release and checks the SHA-256 of the archive and of `ffmpeg.exe`. Changing the build means changing the pin and re-running the integration tests against it.
2. **Licenses and build information** ship in the app's `ffmpeg\` folder:
   - `LICENSE` (GPL v3);
   - `README.txt`, the build's library versions;
   - `BUILD-INFO.txt`, with the configure line, hashes, and the source commit `n9.0.1` / `bf1b838f2a`.
3. **Corresponding source** is assembled by `tools/fetch-ffmpeg-sources.ps1` from `tools/ffmpeg-sources.json`:
   - FFmpeg's release tarball;
   - every statically linked library at the exact version or commit the build reports.

   It is published with each release as `ffmpeg-<version>-corresponding-source.zip`. MSYS2-provided libraries come as MSYS2 source packages, which hold the upstream source plus the patches that were built. The release workflow fails if any library can't be fetched.
4. **Remaining gaps, stated in `SOURCES.txt`:**
   - cairo "1.18.5", libgme "0.6.6" and libva "2.25.0" are unreleased git snapshots, and the build didn't record their commits;
   - bzip2, xz and zlib come from MSYS2 packages whose versions the build doesn't record;
   - gyan.dev builds with media-autobuild_suite but doesn't publish the exact suite revision.

   These are closed by building FFmpeg ourselves from these pinned sources, which also cuts the library list to what Linkast uses (planned; see the roadmap).

**Why "essentials".** It includes everything Linkast uses: gfxcapture, ddagrab, the zmq command filter, NVENC/AMF/QSV/Media Foundation, x264, AAC and TLS for RTMPS. That was verified by the full integration suite. It is less than half the size of "full" (98 MB against 212 MB for `ffmpeg.exe`) and links fewer libraries.

## Codecs and patents

H.264 and AAC are covered by patent pools in some jurisdictions. Open-source projects such as OBS and FFmpeg ship H.264/AAC encoders, and hardware encoders (NVENC, AMF, Quick Sync) are licensed through the GPU vendor. Software x264 is distributed as source and binaries by many projects. This is not legal advice. Anyone redistributing Linkast commercially should obtain their own review.

## Dependencies

See [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md). All shipped dependencies are MIT or Apache-2.0, both compatible with GPL-3.0.
