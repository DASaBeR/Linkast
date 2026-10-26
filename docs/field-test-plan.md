# Field test plan (real users and real hardware)

These checks could not be confirmed on the development machine, which has one wired network connection, no physical webcam (only the Iriun phone-camera driver) and no access to a real streaming account. They need real users with real hardware. Each one maps to an open item in [roadmap.md](roadmap.md) or [webcam-checklist.md](webcam-checklist.md).

## How to report

For every test, send:

- **Result:** Pass or Fail, with what you saw if it failed.
- **Diagnostics:** the text from **Copy diagnostics** in Linkast. It removes stream keys automatically.
- **Your setup:** Windows version, CPU, GPU, and the camera or network adapter used.
- **Duration** for timed tests.

Never paste a stream key into a report.

---

## A. Streaming (from Milestone 1)

### A1. 30-minute stream to a real platform over RTMPS
- **Setup:** A Twitch or YouTube account. Use an `rtmps://` URL (YouTube: `rtmps://a.rtmps.youtube.com/live2`).
- **Steps:**
  1. Stream your desktop or a game at 1080p60 with your normal encoder for 30 minutes.
  2. Watch the platform's own stream health page.
- **Expected:**
  - Linkast stays **LIVE** with 0 reconnects.
  - Encoded frame rate stays close to the target.
  - The upload figure matches your bitrate.
  - The platform reports a healthy stream with no "bad connection" warnings.
  - The video plays back correctly.

### A2. Pulled network cable
- **Steps:**
  1. Go live.
  2. After 1 minute, unplug the Ethernet cable (or turn Wi-Fi off) for about 10 seconds, then reconnect.
- **Expected:**
  - Linkast shows **Reconnecting…** and a network problem, then returns to **LIVE** without your help.
  - The reconnect count goes up by 1 or more.
  - Stopping afterwards is clean.

### A3. Game capture compatibility
For each game, try the three capture modes and note whether the stream shows the game or a black screen:

- Entire display (Windows Graphics Capture)
- Entire display with **Use Desktop Duplication** ticked
- Single window

| Game | Graphics API (DX11 / DX12 / Vulkan / OpenGL) | Display mode (windowed / borderless / exclusive fullscreen) | Display capture | Desktop Duplication | Window capture | Anti-cheat? | Notes |
|---|---|---|---|---|---|---|---|
| | | | | | | | |

**Expected:** Borderless and windowed work in all modes. Results for exclusive fullscreen are what we need to learn.

When a single-window capture of an exclusive-fullscreen game is black, also check:

- A yellow hint, "The game may capture as black", appears on the Stream page within a few seconds, and stays after you switch back to Linkast.
- **Capture this display instead** asks first, warning that notifications and other windows on that display may appear on stream. After **Yes**, the stream restarts with the game visible.
- Switching the game between fullscreen and windowed while capturing the display shows *Reconnecting* briefly, then the stream continues.

### A4. Audio/video sync (clap test)
- **Steps:**
  1. Stream with your microphone on and the webcam visible.
  2. Clap clearly on camera at the start, then again after 10 and 30 minutes.
  3. Watch the recording on the platform.
- **Expected:** The clap sound lines up with the hands meeting, with no visible offset that grows over time.

---

## B. Webcam Studio (from Webcam Phase 1)

Use a **real USB webcam or a laptop's built-in camera**, not a phone app.

### B1. USB and built-in cameras are found and work
- **Steps:**
  1. Open the Webcam tab and pick your camera.
  2. Tick **Camera on**.
- **Expected:**
  - The camera's name is readable.
  - The live preview appears within a few seconds.
  - **Camera active** is shown.
  - The details line shows a sensible resolution and about 30 fps.

### B2. Unplug and replug while live
- **Steps:**
  1. Go live with the webcam on.
  2. Unplug the USB camera and wait 10 seconds.
  3. Plug it back in.
- **Expected:**
  - The stream stays **LIVE** the whole time.
  - The webcam area goes empty and Linkast shows the camera as unavailable.
  - The camera comes back by itself after it is plugged in again, with no click needed.
  - No error dialogs appear.

### B3. Camera already used by another app
- **Steps:**
  1. Open the camera in Teams, Zoom, Discord or the Windows Camera app first.
  2. Then turn the camera on in Linkast.
- **Expected:** One of two results, both acceptable:
  - (a) The picture works and Linkast says another app controls the camera, so resolution and hardware settings are locked.
  - (b) A clear "being used by another app" message, and the picture appears automatically once the other app releases the camera.

  Linkast must not freeze or crash.

### B4. Hardware controls (exposure, focus, white balance)
- **Steps:**
  1. With the camera on, open **Advanced image and camera settings**.
  2. Untick **Auto** for exposure, focus and white balance one at a time, and move each slider.
  3. Restart Linkast.
- **Expected:**
  - Controls your camera supports are enabled and visibly change the preview.
  - Controls it doesn't support are greyed out with "Not supported by this camera".
  - Your values are restored after restarting.

### B5. Choosing a specific resolution
- **Steps:**
  1. In **Resolution**, choose each available option, for example 640×480, 1280×720 and 1920×1080.
  2. Repeat while live.
- **Expected:**
  - The details line shows the chosen size.
  - The preview keeps working.
  - Choosing an unusual mode never crashes Linkast; at worst it falls back to Automatic with a message.
  - While live, the stream continues.

### B6. Windows camera privacy setting off
- **Steps:**
  1. Open Windows **Settings › Privacy & security › Camera** and turn off **Let desktop apps access your camera**.
  2. Turn the camera on in Linkast.
  3. Turn the setting back on.
- **Expected:**
  - Linkast clearly says Windows is blocking camera access and where to change it.
  - Streaming without the camera still works.
  - After the setting is re-enabled, the camera works again, by itself or after toggling **Camera on**.

### B7. Long stream with the webcam (stability)
- **Steps:** Stream for 60 minutes with the webcam on, a preset applied, and the webcam moved a few times while live.
- **Expected:**
  - No reconnects caused by the webcam.
  - The preview and the stream keep updating.
  - Linkast's memory use stays stable; check Task Manager at the start and the end.

---

## D. Background effects (from Phase 3)

Use a real webcam with a real person in front of it. Report your GPU, and the status line under **Background** (for example "Running on GPU (DirectML) · 10 ms per frame · 30 masks/s").

### D1. Edges, hair and movement
- **Steps:**
  1. Turn on **Blur**, then **Remove**, then **Image**.
  2. In each mode, move your head quickly, raise a hand, turn sideways, and run a hand through your hair.
- **Expected:**
  - Your outline follows you without obvious holes or halos.
  - Hair edges look natural, not cut out with scissors.
  - The background doesn't flicker into the person or the person into the background.

### D2. Long stream with an effect
- **Steps:** Stream for 60 minutes with Blur on while gaming.
- **Expected:**
  - The status stays "Running".
  - The stream shows no dropped frames caused by the effect.
  - The game's frame rate drops by only a few percent compared with no effect.
  - Linkast's memory is stable in Task Manager.

### D3. Other GPUs and no GPU
- **Steps:**
  1. On a PC with an AMD GPU, an Intel GPU, a laptop with switchable graphics, and a PC without a dedicated GPU, turn on Blur.
  2. Then untick **Use the graphics card**.
- **Expected:**
  - With the box ticked, the status shows "GPU (DirectML)", or says the CPU is used because the GPU isn't available.
  - Unticked, it shows "CPU".
  - The picture works either way.
  - Report the ms per frame and masks/s for both.

### D4. Dark room
- **Steps:** In a dim room, apply the **Low Light** preset, or raise **Low-light boost** in Advanced.
- **Expected:**
  - The picture becomes clearly brighter without washed-out highlights.
  - It doesn't pump brighter and darker when you move.
  - Turning on a lamp makes the boost back off.

### D5. Model file missing or damaged
- **Steps:**
  1. Close Linkast and rename `models\rvm_mobilenetv3_fp32.onnx`, then turn on Blur.
  2. Then put back a different file with the same name.
- **Expected:**
  - A clear message that the model is missing (or damaged/not the expected version) and to reinstall.
  - The camera keeps working without the effect.
  - Streaming is not affected.

### D6. Older Windows 10
- **Steps:** On Windows 10 (1903 to 22H2), turn on Blur.
- **Expected:** Either "GPU (DirectML)", or a CPU fallback with a note. Never a crash. Report the Windows build and what the status says.

---

## E. Advanced webcam features (from Phase 4)

Background is in [reports/webcam-phase-4.md](reports/webcam-phase-4.md). Use a real webcam with a real person in front of it.

### E1. Auto-framing with a real person
- **Steps:**
  1. Turn on **Keep my face in frame**.
  2. Sit still, then lean left and right, stand up and sit down, and briefly leave the picture.
  3. Try Face size small and large, and Speed slow and fast.
- **Expected:**
  - Small head movements don't move the picture.
  - Bigger moves are followed smoothly, without jumps or zoom "breathing".
  - When you leave, the picture holds for about 2 seconds, then slowly zooms out.
  - The status says "Following your face" while it tracks.
  - Report glasses, hats, side lighting or more than one person if tracking struggles.

### E2. Voice-reactive effects
- **Steps:**
  1. Turn on **React to my voice** with each style. Use the microphone chosen on the Stream tab.
  2. Talk normally, whisper, and stay silent with a fan or game sound in the background.
  3. Adjust **Threshold** until silence doesn't trigger it.
- **Expected:**
  - The effect follows your voice with no visible delay.
  - It doesn't flicker on and off between words.
  - With **Microphone** also on in the stream, Task Manager shows no extra load compared with the effect alone.

### E3. Two or more different cameras
- **Steps:**
  1. Connect two webcams, for example a face cam and a desk cam.
  2. **Add camera**, give each layer its own camera, and place both.
  3. Go live for 30 minutes.
- **Expected:**
  - Both appear in the preview and the stream at the chosen places.
  - Unplugging one leaves the other and the stream running.
  - Report the CPU use shown in Task Manager.

### E4. Vertical streaming to a real platform
- **Steps:**
  1. Choose the **Vertical (9:16)** layout, then go live to a platform that accepts vertical video (for example YouTube Shorts live or TikTok live through RTMP).
  2. While live, switch to Landscape and confirm the restart prompt.
- **Expected:**
  - The platform shows a 1080×1920 stream with the game on top and the camera below.
  - Switching asks first, then restarts within a few seconds.
  - Report the platform's ingest health messages.

### E5. Long stream with every effect
- **Steps:** Stream for 60 minutes while gaming, with two cameras, background blur, an animated border and the voice effect on.
- **Expected:**
  - No reconnects or dropped frames caused by the effects.
  - The game's frame rate drops by only a few percent.
  - Linkast's memory is stable in Task Manager.

---

## F. Installer and updates (from Phase 5)

### F1. Install on a clean PC
- **Steps:**
  1. On a PC or user account that never had Linkast, download `LinkastApp-win-Setup.exe` from the release page and run it.
  2. If SmartScreen warns, choose More info › Run anyway.
- **Expected:**
  - The install needs no admin prompt and finishes in seconds.
  - Linkast starts, and there are Desktop and Start-menu shortcuts and an entry in Settings › Apps.
  - Report the Windows version, and what SmartScreen or your antivirus said.

### F2. Update from the previous beta
- **Steps:**
  1. With an older beta installed, start Linkast after a new release is published.
  2. Look at the bottom-right corner, then press **Update now**.
- **Expected:**
  - Text such as "Version 0.2.0-beta.2 is ready to update" appears, with no pop-up.
  - Nothing happens until you press the button.
  - After pressing it, Linkast restarts as the new version, with your settings, stream key, cameras and layouts kept.
  - **Update now** is greyed out while you are live.

### F3. Offline start
- **Steps:** Start Linkast with the network cable unplugged or Wi-Fi off. Then press **Check for updates**.
- **Expected:**
  - Start-up is normal, with no error about updates.
  - The button shows "Couldn't check for updates…" as text only.

### F4. Uninstall
- **Steps:** Uninstall from Settings › Apps.
- **Expected:**
  - The app and shortcuts are removed.
  - `%LOCALAPPDATA%\Linkast` (settings, encrypted key, logs) is kept, as the beta notes say.

### F5. Antivirus and corporate PCs
- **Steps:** Install and go live with your antivirus active (Defender, or others such as Bitdefender, Kaspersky or ESET).
- **Expected:** No quarantine of `Linkast.exe`, `Update.exe` or `ffmpeg.exe`. Report any warnings verbatim.

### F6. Stream key privacy
- **Steps:** While live, open Task Manager › Details, add the *Command line* column, and look at `ffmpeg.exe`.
- **Expected:** The stream key is not visible; only a random placeholder appears.

---

## G. Linkast Camera and phone webcams (from Phase 6)

Needs Windows 11. Background is in [reports/webcam-phase-6.md](reports/webcam-phase-6.md).

### G1. Video-call apps
- **Steps:**
  1. On the Webcam tab, press **Set up Linkast Camera…** and approve the Windows prompt.
  2. In each of Zoom, Microsoft Teams (new), Discord, Google Meet (in Chrome and in Edge) and OBS, choose **Linkast Camera** as the camera.
  3. Turn Blur on in Linkast.
- **Expected:**
  - Each app shows your Linkast picture, with blur and auto-framing.
  - Linkast's card says "In use by an app".
  - Report any app that doesn't list the camera, shows black, or shows the picture stretched.

### G2. Linkast closed or camera off
- **Steps:**
  1. During a call on the Linkast Camera, turn the chosen camera off in Linkast.
  2. Then close Linkast, then start it again.
- **Expected:**
  - "Camera is off", then "Linkast is not running", then your picture again.
  - The call app never loses its camera.

### G3. Updates and removal
- **Steps:**
  1. After a Linkast update, use the Linkast Camera; if the card asks, press **Repair or update**.
  2. Later press **Remove from this PC**.
- **Expected:**
  - Each step asks for permission once.
  - After removal, call apps no longer list the camera.

### G4. Phone as webcam
- **Steps:** Follow [phone-as-webcam.md](phone-as-webcam.md) with the Windows 11 connected camera, DroidCam or Camo, over Wi-Fi and USB. Stream for 30 minutes with a background effect.
- **Expected:**
  - The phone appears as a camera in Linkast and all effects work.
  - Report the app, connection, delay and any freezes.

---

## C. Network adapter selection (from Phase 2)

Background is in [reports/network-phase-2.md](reports/network-phase-2.md). Linkast only binds its own stream to the chosen network and never changes Windows network settings, so these tests are safe to run with a VPN connected.

### C1. Stream over a phone or 5G modem while gaming on Ethernet (the core scenario)
- **Setup:** Ethernet connected. A phone tethered over USB, or a USB 5G modem, **with mobile data working**: check in a browser with Ethernet unplugged, or confirm Windows shows the network as "Internet".
- **Steps:**
  1. In **Network for your stream**, pick the phone or modem.
  2. Press **Test connection** with your real server URL.
  3. Go live for 15 minutes while playing an online game.
- **Expected:**
  - Test connection reports the phone or modem's name and address, and says Windows would otherwise use Ethernet.
  - While live, "Network interface actually used" shows the phone with **✓ verified: your chosen network**.
  - The upload shows on the phone's data counter, not on Ethernet.
  - The game's ping stays as it is on Ethernet.

### C2. Unplug the phone or modem while live
- **Steps:**
  1. During C1, unplug the USB cable for about 20 seconds.
  2. Plug it back in.
- **Expected:**
  - Linkast shows **Reconnecting…** with "'<name>' is not connected".
  - It never switches to Ethernet. The stream doesn't appear on the platform from your home connection.
  - It goes **LIVE** again on the phone after it is reconnected, if within the reconnect budget of about 2–4 minutes.

### C3. Wi-Fi as the stream network
- **Steps:** Repeat C1 with a Wi-Fi adapter as the stream network and Ethernet for the game.
- **Expected:** Same as C1. The list shows the adapter as **Wi-Fi**.

### C4. Built-in mobile broadband (laptop SIM / WWAN)
- **Steps:** On a laptop with a SIM slot, repeat C1.
- **Expected:** The adapter is listed as **Mobile broadband** and streaming works as in C1.

### C5. VPN interaction
- **Steps:**
  1. With a VPN connected that is set to block traffic outside the tunnel (a kill switch), pick a non-VPN network and press Test connection.
  2. Repeat with the kill switch off.
- **Expected:**
  - With the kill switch on, a clear failure mentioning the network and that a VPN can block other networks.
  - With it off, success through the chosen network.
  - Linkast never changes the VPN.

### C6. IPv6-only or IPv6-preferred network
- **Steps:** Use a mobile network that gives IPv6 (many 5G carriers do) and test an ingest that has IPv6, for example YouTube.
- **Expected:** Test connection succeeds and shows an IPv6 local address on the chosen adapter, or a clear "has no IPv4/IPv6 connection" message.

### C7. Remembered network not connected at start-up
- **Steps:**
  1. Choose the phone, close Linkast, and unplug the phone.
  2. Start Linkast and press **Go live**.
- **Expected:**
  - The network shows "(not connected)" and stays selected.
  - Go live reports it is not connected, retries within the budget, then stops.
  - It never streams over another network.
