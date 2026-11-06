# Aero smoke tests

The repository uses small executable smoke-test projects so architectural modules can be checked without starting the WinUI shell.

Current projects:

- `Aero.Search.SmokeTests`: URL/query classification, ranking and cancellation;
- `Aero.Navigation.SmokeTests`: navigation identity, stale completion and browser-check policy;
- `Aero.WebView.SmokeTests`: one-time event binding, rollback and detach;
- `Aero.Storage.SmokeTests`: atomic writes, snapshots, session persistence and recovery;
- `Aero.Shield.SmokeTests`: fast-lane blocking, strict mode and anti-bot exceptions;
- `Aero.Security.Broker.SmokeTests`: Broker hardening primitives;
- `Aero.Downloads.SmokeTests`: deduplication, safe filenames and path policy;
- `Aero.Extensions.SmokeTests`: manifests, registry and permission classification;
- `Aero.VaultBoundary.SmokeTests`: password JSON/Base64/script, backup, Reveal UI, CSV temp and WER exclusion regressions.

Run one with:

```powershell
dotnet run --project tests/Aero.Search.SmokeTests -p:Platform=x64
```
