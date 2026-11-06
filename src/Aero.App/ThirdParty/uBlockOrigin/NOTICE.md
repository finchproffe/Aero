# uBlock Origin in Aero

Aero can download and install the official Chromium build of **uBlock Origin 1.72.2**
as a separate browser extension in Aero's WebView2 profile.

- Upstream project: https://github.com/gorhill/uBlock
- Upstream release: https://github.com/gorhill/uBlock/releases/tag/1.72.2
- Upstream release commit: `3d370ea`
- Upstream author and maintainer: Raymond Hill and uBlock Origin contributors
- Upstream license: GNU General Public License version 3
- Official Chromium archive: `uBlock0_1.72.2.chromium.zip`
- Archive SHA-256: `d104cac4e1f48d76b1c3ff88aede2eaa8269814ae06cc9c850d27afb5274e533`
- Aero integration: verified download, safe extraction, profile installation, toolbar pinning,
  fallback handling, and an Aero-owned launcher UI

The downloaded uBlock Origin package is not modified, relicensed, or represented as
original Aero code. It remains a separate GPL-3.0 component. Its complete corresponding
source is available from the upstream project and exact release link above.

Aero's native blocker is independent Aero code and is used only as a fallback when the
uBlock Origin extension is unavailable or cannot be installed.

A copy of the GPL-3.0 license is included in `LICENSE.txt`.
