# Contributing to Linkast

Thanks for helping. Linkast aims to stay small, reliable and easy to use, so please open an issue to discuss a feature before writing a large change.

## Ground rules

- **Scope.** Read [docs/roadmap.md](docs/roadmap.md). Features outside the current milestone, such as scenes, plugins, cloud services or kernel drivers, are unlikely to be accepted.
- **No secrets in logs.** Anything that can contain a stream key must pass through `Redactor`. Add a test when you touch logging or diagnostics.
- **No shell commands.** FFmpeg is launched with `ProcessStartInfo.ArgumentList`. User-entered text must never be placed inside a filter graph string.
- **Health is measured, not assumed.** Do not mark a stream healthy because a process is running.
- **License.** By contributing you agree your contribution is licensed under GPL-3.0-or-later. New C# files start with `// SPDX-License-Identifier: GPL-3.0-or-later`. New dependencies must be GPLv3-compatible; list them in `THIRD-PARTY-NOTICES.md`.

## Workflow

1. Build with zero warnings: `dotnet build Linkast.slnx -c Release`. Warnings are errors.
2. Run unit tests: `dotnet test Linkast.slnx`.
3. For engine, audio or networking changes, also run the integration tests against the local ingest. See the README.
4. Describe in the pull request how you tested, including hardware for capture or encoder changes.

## Reporting capture or encoder problems

Use **Copy diagnostics** in the app and paste the result into the issue. It contains versions, encoder probe results and recent log lines, with stream keys removed. Please also include the game, its display mode (windowed, borderless, exclusive fullscreen) and your GPU.
