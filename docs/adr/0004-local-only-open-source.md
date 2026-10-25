# ADR 0004: Local-only, open-source product named Linkast

**Status:** Accepted

## Context

The original brief included an ASP.NET Core backend, a Next.js dashboard, PostgreSQL and cloud profile sync. The product owner decided the product will be a purely local Windows application, released as open source.

## Decision

- **Name:** Linkast ("link" plus "cast"). It ties the product to its network-link differentiator. "Lincast" was rejected because it reads as "Lincoln" and is less distinctive.
- **Scope:** no backend, website, database, accounts or telemetry. Profiles live in `%LOCALAPPDATA%\Linkast`, with export and import to a file. Updates come from GitHub Releases through an opt-in check.
- **License:** GPL-3.0-or-later, which matches the GPL FFmpeg builds that include x264.
- **Stack:** C#, .NET 10, WPF using the built-in Fluent theme, and FFmpeg as a supervised child process. Docker is used only as a development tool for the test ingest.

## Consequences

- Milestone 4 (cloud) was removed and milestones renumbered (see roadmap.md).
- No server costs or server-side security surface.
- Diagnostics are a user-initiated, redacted clipboard export instead of telemetry.
