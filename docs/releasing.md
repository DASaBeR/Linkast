# Releasing Linkast

Releases are built by GitHub Actions ([release.yml](../.github/workflows/release.yml)) and published by a maintainer. Installed copies see a release only after it is **published**, because drafts are invisible to the update check.

## Before tagging

1. Set `<Version>` in `Directory.Build.props` (SemVer, for example `0.2.0-beta.2` or `0.2.0`). A version with a `-suffix` is published as a pre-release.
2. Write `docs/release-notes/<version>.md`. It is shown in the installer and on the release page.
3. Check that `LinkastReleaseRepository` in `Directory.Build.props` is the repository that hosts the releases (currently `DASaBeR/Linkast`).
4. If FFmpeg changes:
   - update the pin in `tools/fetch-ffmpeg.ps1` and the library list in `tools/ffmpeg-sources.json` (from the build's `README.txt`);
   - run the integration suite with `LINKAST_FFMPEG` pointing at the new `ffmpeg.exe`.
5. Run the full test suite locally, including the integration tests against MediaMTX (see `tools/test-ingest`).

## Run the release

**From the Actions tab (recommended):** *Actions › Release › Run workflow*.
- **Version:** leave empty to use `<Version>` from `Directory.Build.props`, or type one such as `0.2.0-beta.2`.
- **Publish immediately:** on by default. Untick it to create a draft you check first.

Alternatively, push a tag (`git tag v0.2.0-beta.2` then `git push origin v0.2.0-beta.2`); a tag push always creates a draft.

The workflow:

1. works out the version and refuses one that was already released;
2. uses `docs/release-notes/<version>.md`, or writes short default notes if there is none;
3. builds the C++ camera components and the app with warnings as errors, and runs the unit tests;
4. downloads the previous release, so updates can be small deltas;
5. packages the app with the pinned FFmpeg and the hash-verified model, and builds the installer;
6. assembles FFmpeg's corresponding source, failing on any download error;
7. creates the release, tagged at the commit it built: published, or a draft. Versions with a `-suffix` are marked as pre-releases.

It takes roughly 30–60 minutes, mostly for the FFmpeg source bundle.

## Publish a draft

1. Open the draft on GitHub and download `LinkastApp-win-Setup.exe`.
2. Install it on a clean user account or a VM, start it, and go live to a test ingest.
3. Press **Publish release**. Installed copies show "Version x.y.z is ready to update" at their next start, or when the user presses **Check for updates**.

## Trying an update locally (no GitHub needed)

1. Build two versions:
   - `tools/publish.ps1 -Installer` for the first;
   - bump the version, then `tools/publish.ps1 -Installer -OutputRoot artifacts\feedtest` for the second.
2. Install the first with `artifacts\releases\LinkastApp-win-Setup.exe`.
3. Start it with the environment variable `LINKAST_UPDATE_SOURCE=<repo>\artifacts\feedtest\releases`. The footer shows the newer version, and **Update now** installs it and restarts.

## Install layout

| Path | Contents |
|---|---|
| `%LOCALAPPDATA%\LinkastApp\` | The app: `current\` and `Update.exe`. Removed on uninstall. |
| `%LOCALAPPDATA%\Linkast\` | Settings, encrypted stream key, logs and background images. Kept on uninstall. |

The installer is per-user and needs no admin rights. It creates Desktop and Start-menu shortcuts and an entry in *Settings › Apps*.

## Not done yet

- **Code signing.** Velopack can sign during `vpk pack` (`--signParams` or `--azureTrustedSignFile`). Until then Windows SmartScreen warns on first run.
- **A self-built FFmpeg**, to close the remaining source gaps (see [licensing.md](licensing.md)).
