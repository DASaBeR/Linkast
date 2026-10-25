# Using your phone as a webcam

A modern phone camera is usually far better than a laptop webcam. Linkast works with any phone app that makes the phone appear as a normal Windows camera. Pick it on the **Webcam** tab like any other camera; every Linkast feature works with it: looks, background effects, auto-framing, multiple cameras and the Linkast Camera.

Linkast doesn't need its own phone app for this. A companion app would be a separate project and isn't planned.

## Options

| Option | Phones | Connection | Status in Linkast |
|---|---|---|---|
| **Windows 11 built-in** ("Use mobile device as a connected camera") | Android with *Link to Windows* | Wi-Fi or Bluetooth pairing | Expected to work (it appears as a normal camera); field test G4 |
| **Iriun Webcam** | Android, iPhone | Wi-Fi or USB | **Verified** (development camera) |
| **DroidCam** | Android, iPhone | Wi-Fi or USB | Expected; field test G4 |
| **Camo** (Reincubate) | iPhone, Android | USB or Wi-Fi | Expected; field test G4 |

## Setting up (example: Windows 11 built-in)

1. On the PC: **Settings › Bluetooth & devices › Mobile devices**. Turn on *Allow this PC to access your mobile devices* and add your phone.
2. On the phone: install or update *Link to Windows* and accept the camera permission.
3. In Linkast's **Webcam** tab, press **Refresh** and pick the phone's camera.

For Iriun, DroidCam or Camo: install the phone app and its Windows companion, start both, then pick the camera in Linkast.

## Tips for a good picture

- **Use USB if the app offers it.** It is more stable and has less delay than Wi-Fi, and it charges the phone.
- **Mount the phone in landscape** at eye level. Use the rear camera for the best quality, with the phone's own screen preview turned off if the app allows.
- **Choose the resolution in the phone app** (1080p is plenty). Linkast picks the best matching format automatically; **Resolution** in the Webcam tab lists what the phone offers.
- **Keep the phone cool and on power** for long streams; phones slow down when they get hot.
- **If the picture freezes** when the phone locks or the app goes to the background, keep the phone app in the foreground. Linkast keeps streaming, shows the camera as unavailable, and continues when the picture returns.

## Known behaviour

- **When the phone app isn't sending,** some apps (Iriun, for example) show their own "please start the app" picture. Linkast shows whatever the camera sends.
- **Delay.** Phone cameras over Wi-Fi add some delay (typically 100–300 ms). If your voice and picture drift apart on stream, a USB connection helps.
