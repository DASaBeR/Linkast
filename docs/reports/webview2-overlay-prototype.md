# WebView2 overlay capture: prototype result

**Question:** can Linkast draw a web overlay (alerts from Reymit, Streamlabs, StreamElements and similar) into the stream with WebView2, the browser engine that ships with Windows, instead of bundling Chromium (CefSharp, +70–90 MB)?

**Method:**
- WebView2 is hosted on a `Windows.UI.Composition` visual (no visible window), with `CoreWebView2CompositionController` and a transparent background.
- The visual is captured with `GraphicsCaptureItem.CreateFromVisual` and a free-threaded `Direct3D11CaptureFramePool`.
- Each frame is copied into a staging texture and read back as BGRA, then converted from premultiplied to straight alpha, which is what the layer pipe expects.
- The page was an animated, semi-transparent alert at 1920 × 1080. The run lasted 20 seconds on the development PC (12 logical cores, NVIDIA RTX 3070).

**Result:**

| Measure | Value | Needed |
|---|---|---|
| Frames delivered | 58.9 fps (56–61 every second) | 30 |
| Readback and alpha conversion | 7.9 ms per frame | under 33 ms |
| CPU, Linkast plus its WebView2 processes | 4.8 % of all cores, at 60 fps | low |
| Private memory | 278 MB | acceptable |
| Alpha in a captured frame | 85.7 % fully transparent, 14.2 % partial (soft shadow), 0.2 % opaque | transparency kept |

**Decision:** pass. Overlays use WebView2. Linkast stays small, MP4/H.264 alert videos play, and Windows keeps the engine up to date. Overlays are rendered at 30 fps, which halves the cost measured here. CefSharp stays the fallback if a future WebView2 change breaks visual capture.
