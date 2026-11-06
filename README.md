# Aero

Aero is an experimental Windows browser focused on speed, resource transparency, and user control.

> Project status: pre-alpha. This repository is not ready for daily browsing or security-sensitive use.

## Product direction

Aero should feel instant, quiet, and understandable:

- no news feed, ads, account requirement, or telemetry by default;
- lazy session restore and aggressive sleeping of background tabs;
- clear per-site resource controls;
- visible tab temperature instead of a hidden process manager;
- temporary isolated tabs for risky links and secondary accounts.

## Current prototype

The starter application includes:

- a native WinUI 3 window;
- Microsoft WebView2 rendering;
- address/search parsing;
- back, forward, refresh, and home controls;
- a persistent WebView2 profile under `%LOCALAPPDATA%\Aero\UserData`;
- a clean split between UI code and browser-independent core logic.

## Stack

- C# and .NET 10
- WinUI 3 / Windows App SDK 2.3.1
- Microsoft Edge WebView2 Runtime
- Windows 10 version 1809 or newer; Windows 11 is the primary target

## Build

1. Install Visual Studio 2026 Community.
2. In Visual Studio Installer, enable Windows application development and Windows App SDK / WinUI tooling.
3. Install the .NET 10 SDK.
4. Open `Aero.sln`.
5. Select `x64` and run `Aero.App`.

Or from the command line:

```powershell
dotnet build Aero.sln -c Debug -p:Platform=x64
```

## Repository map

```text
src/Aero.App       Native Windows UI and WebView2 host
src/Aero.Core      Browser-independent domain logic
tests/             Smoke-test console projects (`dotnet run --project tests/<name>`)
.github/            Contribution and issue templates
```

## License

Copyright (c) 2026 finchproffe. Aero is licensed under the [Mozilla Public License 2.0](LICENSE). The Microsoft WebView2 Runtime is a separate Microsoft dependency; see [NOTICE.md](NOTICE.md).

## Near-term milestone

The next milestone is `0.1.0`: multiple tabs, session restore, downloads, history, bookmarks, and crash-safe state persistence.
