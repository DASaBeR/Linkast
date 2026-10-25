# Security policy

## Reporting a vulnerability

Please report security issues privately using GitHub's **Report a vulnerability** button on the repository's Security tab. Do not open a public issue. We aim to acknowledge reports within seven days.

Examples of in-scope issues:

- A stream key or other secret appearing in logs, diagnostics, the clipboard export, or crash output.
- Command or filter-graph injection through any user-controlled value.
- Another local user or process being able to read Linkast's audio pipes or hijack its local relay.
- Linkast connecting anywhere other than the stream destination and the GitHub releases update check (once at start and when you press Check for updates).
- The Linkast Camera component (loaded by Windows' camera service) or its setup helper being usable to run code with higher rights.

## Known limitations

- The stream key is never passed to FFmpeg: FFmpeg only sees a random placeholder, and Linkast's local relay inserts the real key into the RTMP connection. See [docs/security-review.md](docs/security-review.md).
- The stream key is saved encrypted with Windows DPAPI for the current Windows user (`%LOCALAPPDATA%\Linkast\secrets.dat`). Any program running as that same user can ask DPAPI to decrypt it; other users and other PCs cannot.
- Background effects run a local AI model. The model file is verified against a pinned SHA-256 before it is loaded; a modified file is refused. Linkast never downloads models at runtime.
- Webcam frames are processed locally, never logged and never written to disk. They reach FFmpeg through a current-user-only named pipe. Live layout commands use FFmpeg's `zmq` filter on a random loopback port for the duration of a stream; only filter commands are accepted there.
