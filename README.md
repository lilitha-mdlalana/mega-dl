# MegaDl

A bare-bones C# / .NET 10 + Avalonia port of [MegaBasterd](https://github.com/tonikelope/megabasterd), Windows first.
Scope: downloading (with per-folder grouping), clipboard watcher, smart proxy, and settings (theme, font, download folder). Nothing else.

> Status: **Phase 0 (foundations)**. The crypto and download engine work and are tested; the desktop UI runs on a
> simulated engine. See [`docs/PORT_PLAN.md`](docs/PORT_PLAN.md) for the roadmap.

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
dotnet test                                    # crypto, chunk layout, link parsing

dotnet run --project src/MegaDl.Desktop        # the UI (simulated downloads for now)

# real MEGA, public file links:
dotnet run --project src/MegaDl.Console -- info "https://mega.nz/file/<id>#<key>"
dotnet run --project src/MegaDl.Console -- get  "https://mega.nz/file/<id>#<key>" ./downloads
```

Tip: in a Debug build of the desktop app, press **F12** for Avalonia DevTools (a visual tree inspector, like browser devtools).

## License

GPL-3.0, inherited from MegaBasterd because this is a derivative work. See `LICENSE`.

Do not use this software in any way that violates [MEGA's terms of use](https://mega.io/terms).
