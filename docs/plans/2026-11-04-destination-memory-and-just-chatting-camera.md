# Plan: per-platform destination memory + "Just chatting" camera missing

Source: tester report on Linkast 1.3.1 (log `linkast-20261104.log`).
Two independent tasks. Do them as two separate commits on `main` (no new branches).

Rules for the implementer (from the project owner):
- Commit and push directly on `main`.
- No AI attribution anywhere (no "Co-Authored-By", no tool names in commits, code, docs).
- Run tests with `tools/run-tests.ps1` (it saves the report and history). Do not run the live Kick test.
- Never launch Microsoft Edge. Add `// SPDX-License-Identifier: GPL-3.0-or-later` to any new file.
- Linkast is local-only: no cloud, no backend.

---

## Task 1 - Every Destination tab remembers its own values

### What happens today (verified in code)
- `MainViewModel` has ONE `ServerUrl` and ONE `StreamKey` for all platforms
  ([MainViewModel.cs:1024](../../src/Linkast.App/ViewModels/MainViewModel.cs#L1024),
  [MainViewModel.cs:1041](../../src/Linkast.App/ViewModels/MainViewModel.cs#L1041)).
- `OnSelectedPlatformChanged` ([MainViewModel.cs:962](../../src/Linkast.App/ViewModels/MainViewModel.cs#L962))
  overwrites `ServerUrl` with the preset server when a preset is picked. A custom URL typed before is lost.
- The stream key is one PasswordBox and one secret `destination.streamKey`
  ([DpapiSecretStore.cs:17](../../src/Linkast.App/Services/DpapiSecretStore.cs#L17)). Switching tab keeps showing
  the same key, so a Kick key is sent to YouTube, and typing a new key destroys the old one.
- Settings only store one `Platform`, `DestinationKind`, `ServerUrl`
  ([LinkastSettings.cs:97-110](../../src/Linkast.Core/Settings/LinkastSettings.cs#L97)).

### Target behaviour
- Platforms: Twitch, YouTube, Kick, Facebook, Custom. Each has its own saved server URL, delivery type
  (Direct / restream, only meaningful for Custom) and stream key.
- Switching tab: the current tab's values are kept, the new tab shows its own saved values (empty if none).
  Nothing is erased, ever, by switching.
- Presets (Twitch/YouTube/Kick/Facebook) default to the preset server. Custom starts empty.
- Survives restart. Keys stay DPAPI encrypted, never in settings.json, never in logs.
- The active platform is still the one used when going live.

### Design
1. **Core settings** (`Linkast.Core/Settings/LinkastSettings.cs`, `StreamSettings`):
   add an additive field, no schema bump needed:
   ```csharp
   public sealed record DestinationProfile
   {
       public string ServerUrl { get; init; } = string.Empty;
       public DestinationKind DestinationKind { get; init; } = DestinationKind.Direct;
   }
   public IReadOnlyDictionary<StreamPlatform, DestinationProfile> Destinations { get; init; }
       = new Dictionary<StreamPlatform, DestinationProfile>();
   ```
   Keep `Platform`, `DestinationKind`, `ServerUrl` as the ACTIVE values (old files and the rest of the code keep
   working). Make sure `SettingsSerializer` round-trips a dictionary keyed by enum (check its `JsonSerializerOptions`
   converters; if enum keys serialise badly, key by `string` platform name instead). Clamp/normalise on load
   (null dictionary -> empty).
2. **Secrets** (`DpapiSecretStore`): replace the single name with a per-platform name:
   `public static string StreamKeyNameFor(StreamPlatform p) => $"destination.streamKey.{p.ToString().ToLowerInvariant()}";`
   Keep the old `StreamKeyName` constant only for migration.
   **Migration**: in `InitializeAsync` ([MainViewModel.cs:1185](../../src/Linkast.App/ViewModels/MainViewModel.cs#L1185)),
   if the old `destination.streamKey` exists and the per-platform key for the saved active platform does not,
   copy it to the active platform's name, then `Set(old, null)` to remove it. No user may lose a saved key on update.
3. **ViewModel** (`MainViewModel.cs`, region "Platform" and "Destination"):
   - Track `_activePlatform` (the platform whose values are currently in `ServerUrl`/`StreamKey`).
   - Replace `OnSelectedPlatformChanged` logic (when `!_loading`):
     a. Store the current `ServerUrl` + `SelectedDestinationKind.Kind` into an in-memory
        `Dictionary<StreamPlatform, DestinationProfile> _profiles[_activePlatform]`.
        (The old key is already saved under its own name because `StreamKey` setter writes immediately.)
     b. Set `_activePlatform = value.Platform`.
     c. Load the new profile: `ServerUrl = profile?.ServerUrl` or, for a preset, `value.Server`;
        `SelectedDestinationKind = profile kind` (Direct for presets).
        For presets always use the preset server unless a saved override exists (see Kick note below).
     d. Load the key: `var key = _secrets.Get(StreamKeyNameFor(value.Platform)) ?? new Secret("")`, then set the
        `StreamKey` property WITHOUT writing it back (add a private `_switchingPlatform` guard checked in the
        setter next to `_loading`) and call `StreamKeyLoaded?.Invoke(this, key)` EVEN WHEN EMPTY so the view clears
        the PasswordBox. Also `Redactor.Shared.Register(key)` when non-empty.
   - `StreamKey` setter: save under `StreamKeyNameFor(_activePlatform)` (not the legacy name).
   - `CurrentSettings()` ([MainViewModel.cs:1511](../../src/Linkast.App/ViewModels/MainViewModel.cs#L1511)): write
     `Destinations` from `_profiles`, including the active platform's live values. `Platform/ServerUrl/DestinationKind`
     stay the active values.
   - `ApplyStreamSettings` ([MainViewModel.cs:1562](../../src/Linkast.App/ViewModels/MainViewModel.cs#L1562)):
     fill `_profiles` from `s.Destinations`; set `_activePlatform` from the resolved platform BEFORE keys load.
     Legacy files (no `Destinations`): seed `_profiles[active]` from the old `ServerUrl`/`DestinationKind`.
   - In `InitializeAsync` load the key of the active platform only for the PasswordBox, but register ALL stored
     platform keys with `Redactor.Shared` so a key of an inactive tab can never leak into a log.
   - OBS import ([MainViewModel.cs:165-174](../../src/Linkast.App/ViewModels/MainViewModel.cs#L165)): it sets
     `Platform` via `Detect` and a key. Route it through the same path: switch to that platform, put the URL into its
     profile, save the key under that platform's name.
   - The `nameof(...)` list that triggers autosave ([MainViewModel.cs:~1337](../../src/Linkast.App/ViewModels/MainViewModel.cs#L1337))
     already contains `SelectedPlatform`, `ServerUrl`, `SelectedDestinationKind`; nothing else needed as long as
     `CurrentSettings()` reads `_profiles`.
4. **View** (`MainWindow.xaml.cs` lines 31-36, 59-108): the `StreamKeyLoaded` handler must also update
   `StreamKeyPlain.Text` when the "Show" toggle is on (currently only the PasswordBox is set), and must not trigger
   `OnStreamKeyChanged` (the `_syncingKey` guard already does that). No XAML change is required: one PasswordBox and
   one URL box are reused and refilled per tab. (Do not create five boxes; refilling one is simpler and keeps
   automation names.)
5. **Kick server is hard-coded** ([StreamPlatforms.cs:28](../../src/Linkast.Core/StreamPlatforms.cs#L28)) to one
   Amazon IVS endpoint (`fa723fc1b171...`). Kick ingest hosts are per account, so this is wrong for other users.
   Small related fix, same commit or separate: make the Kick tab show an editable Server URL box (reuse the Custom
   box, prefilled with the default) and store the user's value in the Kick profile. Use
   `StreamPlatforms.Normalize` (already appends `/app` for `*.live-video.net`). If you skip this, leave a TODO note
   in the commit message body, not in code.

### Edge cases to handle
- Switching tab while live: `CanEditSettings` already blocks editing during a stream; keep that behaviour.
- Key typed then tab switched immediately: the setter already persisted it, so it must come back.
- Empty key on a tab: the status panel `IsDestinationReady` must read false for that tab.
- Settings imported/exported: secrets are never exported; export may include `Destinations` URLs only.
- `Detect(serverUrl)` on load must not override an explicit saved platform.

### Tests
- Core: serializer round trip of `Destinations`; legacy file (no `Destinations`) loads with the active profile seeded.
- App/Core logic test (add to the existing test project; look at how `MainViewModel` or secrets are tested today):
  1. Enter Kick key K, switch to Custom, enter URL U and key C, switch to Kick -> key K and Kick server shown,
     switch to Custom -> U and C shown.
  2. Restart (new VM from the same stores) -> both still there; the active tab is the last one used.
  3. Migration: only legacy `destination.streamKey` + `Platform=Kick` -> after load Kick has the key and the legacy
     entry is gone.
  4. Redactor masks keys of inactive platforms.

---

## Task 2 - Camera missing in the "Just chatting" scene

### Findings (code read; not reproduced on a machine, so verify step 0 first)
The Just chatting scene is created with the first camera placed large on a Linkast card background
([SceneLayouts.cs:316-325](../../src/Linkast.Core/Camera/SceneLayouts.cs#L316)). Two real defects can make the camera
vanish; the first is the most likely because it explains an *intermittent* report.

**Cause A (most likely): the scene's card background can be drawn ON TOP of the camera.**
- The card is a separate source with id `SceneMediaLayerId(scene.Id)`
  ([MainViewModel.Scenes.cs:379](../../src/Linkast.App/ViewModels/MainViewModel.Scenes.cs#L379)). It is never part of the
  stack order: `ShownOrder` ([MainViewModel.Stacking.cs:39-59](../../src/Linkast.App/ViewModels/MainViewModel.Stacking.cs#L39))
  lists only the game, cameras and overlays. The doc comment of `SceneStacking` says the background "is always below".
- The renderer decides the order in `GetOrderedKeys`
  ([MediaOutputScheduler.cs:279-322](../../src/Linkast.Platform.Windows/Rendering/MediaOutputScheduler.cs#L279)) with
  `SceneStacking.Resolve(stackOrder, presentList)`. `presentList` is the key order of `_sources`, a
  `ConcurrentDictionary`, whose enumeration order is hash-based (string hashing is randomised per process), NOT insertion
  order. `Resolve` places an unlisted key just above whichever present key precedes it in that list, or at the bottom
  if it is first. So depending on the process the card lands at the bottom (camera visible) or above the camera
  (camera hidden behind a full-screen card). That matches "sometimes the camera is not there".
- Only Just chatting has both a full-screen card and a camera by default (Starting soon / BRB / Ending have no camera),
  which is why only this scene shows it.

**Cause B: the camera was never placed in that scene.**
- `AddCamera` deliberately does NOT add a new camera to Just chatting, Starting soon, BRB or Ending
  ([WebcamStudioViewModel.cs:164-176](../../src/Linkast.App/ViewModels/WebcamStudioViewModel.cs#L164)).
- `RemoveCamera` removes the camera from every scene ([WebcamStudioViewModel.cs:198-201](../../src/Linkast.App/ViewModels/WebcamStudioViewModel.cs#L198)).
  If the user deleted `camera-1` and added a new one (or the first camera was added after scenes were created with
  `firstLayerId == null`, see [SettingsSerializer.cs:153](../../src/Linkast.Core/Settings/SettingsSerializer.cs#L153)),
  Just chatting has an empty `CameraPlacements` and nothing ever puts a camera there. The user has to know to tick
  "show in this scene".
- `OnLayerShownChanged` ([WebcamStudioViewModel.cs:286-296](../../src/Linkast.App/ViewModels/WebcamStudioViewModel.cs#L286))
  removes the camera from the *active* scene when its Shown toggle is turned off, silently.

### Step 0 - confirm which cause (10 minutes)
Add one log line at "Going live" (in `MainViewModel`, where the stream start log is written, near
[MainViewModel.cs:1960](../../src/Linkast.App/ViewModels/MainViewModel.cs#L1960)):
for each scene of the stream log `scene name: cameras [ids with placements], stack order`. Also log the final ordered
key list once in `MediaOutputScheduler.GetOrderedKeys` when it is rebuilt (debug level is fine). Reproduce with the
default setup (Gaming -> Just chatting) and restart the app 5 times: if the camera is hidden in some runs and the
log shows the card key after the camera key, it is Cause A. If `cameras []` is logged for Just chatting, it is Cause B.

### Fix for Cause A (do this regardless; it is cheap and removes the randomness)
Make the background always the bottom layer, deterministically:
- In `MainViewModel.BuildStack` ([MainViewModel.Stacking.cs:62-63](../../src/Linkast.App/ViewModels/MainViewModel.Stacking.cs#L62)),
  prepend every scene-media layer id of the stream's scenes (`StreamScenes(active)` where `scene.Media` has a file,
  same condition as `BuildSceneMedia`) in front of the merged order:
  `[.. mediaIds, .. SceneStacking.Merge(...)]`.
- Check the places that compare stacks so a media id at the front does not force restarts: `LiveStackFits`
  ([MainViewModel.LiveSource.cs:277](../../src/Linkast.App/ViewModels/MainViewModel.LiveSource.cs#L277)) and the code
  that builds `session.Stack`; `SceneStacking.Fits` ignores ids a scene does not list, so it should be fine, but run the
  scene-switch tests.
- Defence in depth in the engine: in `GetOrderedKeys`, make iteration of `presentList` deterministic (sort keys with
  `StringComparer.Ordinal`, or keep an insertion-ordered list next to `_sources`) so any other unlisted key (overlays
  added live) cannot land in a random place.
- Also make the preview consistent: `PreviewStack` ([MainViewModel.Stacking.cs:108](../../src/Linkast.App/ViewModels/MainViewModel.Stacking.cs#L108))
  must put the scene background below everything as well (the preview is not the stream; check both).

### Fix for Cause B (make the camera impossible to lose by accident)
- When a scene of kind `JustChatting` becomes active (or is created/loaded) and it has NO camera placement while at
  least one camera layer exists, place the first camera with the default Just chatting rectangle
  (`new NormalizedRect(0.2, 0.1, 0.6, 0.8)` landscape, `(0.05, 0.25, 0.9, 0.5)` vertical), using
  `LayerGeometry.FitInside` for the camera's aspect (same as `AddScene` does,
  [WebcamStudioViewModel.cs:514-522](../../src/Linkast.App/ViewModels/WebcamStudioViewModel.cs#L514)).
  Do it once at load time (a migration-style pass in `Webcam.Load`), not on every switch, so a user who deliberately
  hid the camera in that scene is not overridden after the first fix. Mark nothing in settings; the rule is
  "Just chatting never loads without a camera if a camera exists". If that is too opinionated, instead show a one-line
  hint in the scene editor: "No camera in this scene. Turn on Show for a camera." and leave data alone.
- `AddCamera`: also place a new camera in Just chatting scenes (large, centred) when that scene has no camera yet.

### Tests
- Core (`SceneStacking` / layout tests): `Resolve` with a media key not in `stored` and a `present` list in several
  permutations always yields media first once the stack contains it (test the `BuildStack` helper by extracting the
  pure ordering function if needed so it is testable without the VM).
- Engine-level (if a render test harness exists): two sources, card + camera, enumerate in both orders -> same draw order.
- Cause B: loading a settings file whose Just chatting scene has an empty `CameraPlacements` and one camera layer
  yields a placement; a user who removed the camera from Gaming still keeps Just chatting's camera.
- Manual: default setup, go live, switch Gaming -> Just chatting 10 times, restart app 5 times, camera always visible
  above the card.

---

## Order of work and delivery
1. Task 2 step 0 (logging) + Cause A fix, run tests, commit.
2. Task 2 Cause B, run tests, commit.
3. Task 1, run tests, commit.
After each task: `tools/run-tests.ps1`, then push to `main`. Report in the final message what was verified and what
was not (the camera bug could not be reproduced by the person who wrote this plan).

## Out of scope here (separate items already found in the same log)
- Capture hot-swap (Deadlock -> CS2) gives no error and no "first frame" log; add logging and fix the one-second
  capture flap in `UpdateWindowWatch` / `Resolve`.
- Audio/video sync after switching games: `ReplaceSources` only adds audio sources, never removes old ones.
