<p align="center">
  <img src=".github/brand/aero-icon.svg" width="112" alt="Aero logo">
</p>

<h1 align="center">Aero</h1>

<p align="center">
  A fast, quiet Windows browser that assumes info-stealers exist.
</p>

<p align="center">
  <img alt="Windows 10/11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-0B0C0E?logo=windows11&logoColor=white">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-0B0C0E?logo=dotnet&logoColor=white">
  <img alt="WebView2" src="https://img.shields.io/badge/engine-WebView2-0B0C0E?logo=microsoftedge&logoColor=white">
  <img alt="License MPL-2.0" src="https://img.shields.io/badge/license-MPL--2.0-0B0C0E">
  <img alt="Status pre-alpha" src="https://img.shields.io/badge/status-pre--alpha-C2410C">
</p>

> **Pre-alpha.** Aero is not ready for daily browsing or for accounts you cannot afford to lose. It has not been independently audited.

## Why Aero

- **Instant and quiet.** No news feed, ads, account requirement or telemetry. Background tabs sleep, resource use is visible per tab, and sessions restore lazily.
- **You stay in control.** Per-site permissions, built-in Shield blocker, workspaces, tab groups, split view, containers and private windows.
- **Built against session theft.** Most account takeovers today don't need your password: a stealer copies the browser's cookies and replays your sessions. Aero is designed so that the usual way of doing that stops working.

## Protection against info-stealers

Info-stealers (Lumma, RedLine, Vidar, StealC and their forks) run as the signed-in user and go for the same few things. Here is how a default install of each browser holds up against them.

| Stealer technique | Firefox¹ | Chromium-based app² | Chrome 127+ | **Aero** |
|---|:---:|:---:|:---:|:---:|
| Copy the cookie database and decrypt it as the user | ❌ | ❌ | ✅ | ✅ |
| Copy saved passwords and decrypt them as the user | ❌ | ❌ | ⚠️³ | ✅ |
| Relaunch the browser with a debugging port and dump every cookie | — | ❌ | ✅⁴ | ✅ |
| Read the browser's memory or inject code into it | ❌ | ❌ | ❌⁵ | ⚠️ |
| Malware running as Administrator or SYSTEM | ❌ | ❌ | ❌ | ❌ |

✅ blocked by default · ⚠️ partly blocked · ❌ not blocked · — not a practical vector

<sub>¹ Default profile without a Primary Password: `cookies.sqlite` is plain text and `logins.json` decrypts with `key4.db` offline.<br>
² A Chromium browser or WebView2 app that relies on DPAPI alone: any process of the user can decrypt the key in `Local State`.<br>
³ Chrome began its app-bound encryption rollout with cookies; how far it covers passwords depends on the version.<br>
⁴ Since Chrome 136 the debugging switches are ignored for the default profile.<br>
⁵ Code injection into Chrome is the publicly documented way around its app-bound encryption.<br>
Based on publicly documented defaults; other browsers change between releases, so check current versions before relying on this table.</sub>

### How Aero does it

| Layer | What it does |
|---|---|
| **Cookie vault** | Persistent cookies never stay in WebView2's on-disk Cookies database. Aero turns them into in-memory session cookies and keeps the real copy in an AES-256-GCM vault. |
| **App-bound key** | The vault key is wrapped by `AeroAppBound`, a tiny LocalSystem service shipped in the MSIX. It unwraps the key only for `Aero.exe` from its own package folder. A stealer can copy the files but cannot open them. |
| **Windows Hello (opt-in)** | Settings → *Protect site sign-ins with Windows Hello* additionally requires your face, fingerprint or PIN on every launch. |
| **Aero Vault** | Saved passwords sit behind Windows Hello, this PC's TPM and your Windows account at once. See [Aero Vault](#aero-vault) below. |
| **Launch guard** | Environment variables and registry policies that would start WebView2 with a debugging port, another runtime or another profile are removed or refused before the engine starts. |
| **Process guard** | Other processes of the user cannot open `Aero.exe` to read or write its memory or start threads in it, and cannot reset that rule without admin rights. Remote DLL loads and legacy injection points are blocked. |

```mermaid
sequenceDiagram
    participant A as Aero.exe
    participant S as AeroAppBound (SYSTEM)
    A->>S: wrapped vault key (named pipe)
    S->>S: is the caller Aero.exe from the package folder?
    S->>S: unwrap with SYSTEM DPAPI and the caller's SID
    S-->>A: vault key
    Note over A: cookies go back to WebView2 in memory only
```

The service starts on demand and exits after a minute idle: about 60 ms on a cold start (in parallel with WebView2 startup), 1 ms when warm, 8.5 MB while running, nothing between launches.

## Aero Vault

<p align="center"><b>Your passwords, locked three times over and never left open.</b></p>

Aero Vault is Aero's own password manager. It does not use the Chromium password store that stealers know how to read, and it is built so that a copied vault file or a screen recorder gets nothing, and a stealer on the PC cannot open the vault without you.

|   | |
|---|---|
| 🔐 **Three keys, all required** | The vault key opens only with your **Windows Hello** (face, fingerprint or PIN, held by a non-exportable key), only on **this PC** (Node Lock: a key kept in the TPM by the Microsoft Platform Crypto Provider) and only for **your Windows account** (DPAPI). A vault file copied to another machine is noise. |
| ⏱️ **Never left unlocked** | There is no "unlocked for 15 minutes" state. Every fill or reveal gets its own short-lived key lease that is destroyed right after. Secrets live in locked, unmanaged memory and are zeroed before release. |
| 🪟 **Reveal outside the browser** | *Show password* runs in a separate one-shot process, the Aero Security Broker: an HMAC-SHA256-authenticated channel, a hardened process with loader monitoring, and a password window hidden from screen capture. The browser process never sees the revealed password. |
| 🎯 **Fills only where it belongs** | Autofill reaches the page through a WebView2 shared buffer bound to the exact origin and document. Aero Vault refuses to fill on IP addresses, local hosts, punycode look-alike domains and suspiciously complex domain names. |
| 🧱 **One store, no duplicates** | WebView2's built-in password saving is off, so there is no second copy in the `Login Data` file that stealers grab. The optional kernel driver can additionally shield the broker. |

```mermaid
flowchart LR
    P[Password] --> V[(Aero Vault)]
    V --- H[Windows Hello<br/>it is really you]
    V --- T[Node Lock TPM key<br/>this PC]
    V --- D[DPAPI<br/>this Windows account]
    V -->|reveal| B[Security Broker<br/>separate hardened process]
    V -->|fill| W[Exact origin and document only]
```

### Attribution: Vault is Powered by Aero

Every source file of Aero Vault starts with the notice **"Vault is Powered by Aero"**. Forks and derivative works must keep it: MPL-2.0 section 3.4 forbids removing or altering notices in the source code. If your product ships Aero Vault or code derived from it, credit it in your product and documentation as **Vault is Powered by Aero**.

### What Aero does not protect against yet

- **Injection into a running Aero.** Like Chrome's app-bound encryption, this layer stops file theft, not code that is already inside the browser.
- **WebView2's own processes.** While Aero runs, cookies live in memory in the WebView2 network process, which other processes of the user can still read. Chromium crashes if those processes are locked down from user mode.
- **Administrator-level malware.** Nothing in user mode survives it. The optional kernel driver (`Aero.Security.Driver`) currently protects only the password broker.
- **Development builds.** Without the MSIX there is no key service, so the cookie vault stays off unless Windows Hello protection is turned on.

Found a weakness? See [SECURITY.md](SECURITY.md).

## Build

1. Install Visual Studio 2026 with the Windows application development workload and the Windows App SDK / WinUI tools.
2. Install the .NET 10 SDK.
3. Open `Aero.sln`, select `x64` and run `Aero.App`.

```powershell
dotnet build Aero.sln -c Debug -p:Platform=x64
```

Smoke tests are console projects: `dotnet run --project tests/<name> -p:Platform=x64`. The signed MSIX and installer are built with `BUILD_AERO_MSIX.cmd` and `BUILD_AERO_INSTALLER.cmd`.

## Repository map

```text
src/Aero.App                Windows UI and WebView2 host
src/Aero.Core               Browser-independent domain logic
src/Aero.WebView            WebView2 environment, launch guard, controller lifecycle
src/Aero.Navigation         Navigation identity, timing and completion policy
src/Aero.Search             Address bar resolution and suggestions
src/Aero.Shield             Native request blocker
src/Aero.Storage            Atomic settings, history, bookmarks and sessions
src/Aero.Downloads          Download naming, paths and deduplication
src/Aero.Extensions         Extension catalog and permission risk
src/Aero.Security           Vaults, process guard, app-bound key client
src/Aero.Security.Broker    Hardened process that reveals passwords
src/Aero.Security.AppBound  LocalSystem key service for the cookie vault
src/Aero.Security.Driver    Optional kernel driver for the broker
src/Aero.Installer          WinUI installer
.github/brand               Logo sources (SVG)
tests/                      Smoke-test console projects
```

## License

Copyright (c) 2026 finchproffe. Aero is licensed under the [Mozilla Public License 2.0](LICENSE). The Microsoft WebView2 Runtime is a separate Microsoft dependency; see [NOTICE.md](NOTICE.md). The Aero name and logo are not covered by the code license.
