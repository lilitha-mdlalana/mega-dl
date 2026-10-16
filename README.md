# MegaDl

A small, fast MEGA downloader for Windows (C# / .NET 10 + Avalonia).

Paste a MEGA file or folder link and it downloads — parallel chunked downloads with
resume, a folder picker for grabbing just the files you want, downloads grouped by
folder in the list, a clipboard watcher that offers to download links you copy, a smart
proxy that reroutes around MEGA's quota limit, and settings for theme, font and where
files land. Closing the window minimizes it to the tray — downloads keep running, and the
whole queue (including each file's chunk progress) survives an app restart. Nothing else —
no accounts, no uploads, no streaming server.

> Status: **Phases 0–5 done** (crypto, real downloads, the queue engine, folder links,
> folder grouping in the list, settings + clipboard watcher, smart proxy, tray/background
> running, queue persistence — all verified against real MEGA links). One thing left: a
> smoke test on an actual Windows machine (everything so far has only run on macOS).
> See [`docs/PORT_PLAN.md`](docs/PORT_PLAN.md) for the roadmap.

## Layout

```
MegaDl.slnx
├── src/MegaDl.Core       engine: crypto, MEGA API, transfers (no UI references)
├── src/MegaDl.Console    headless harness: `megadl parse|info|get <link>`
├── src/MegaDl.Desktop    Avalonia app (MVVM)
├── tests/MegaDl.Core.Tests
└── docs/PORT_PLAN.md
```

## Getting started

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build                                   # restores Avalonia etc. from NuGet
dotnet test                                    # crypto, chunk layout, link parsing, settings

dotnet run --project src/MegaDl.Desktop        # the app

# headless, real MEGA links:
dotnet run --project src/MegaDl.Console -- info "https://mega.nz/file/<id>#<key>"
dotnet run --project src/MegaDl.Console -- get  "https://mega.nz/file/<id>#<key>" ./downloads
```

Tip: in a Debug build of the desktop app, press **F12** for Avalonia DevTools (a visual tree inspector, like browser devtools).

## License

GPL-3.0 — this project builds on ideas and protocol details from
[MegaBasterd](https://github.com/tonikelope/megabasterd), also GPL-3.0. See `LICENSE`.

Do not use this software in any way that violates [MEGA's terms of use](https://mega.io/terms).
