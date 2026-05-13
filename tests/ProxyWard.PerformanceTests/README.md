# ProxyWard Performance Tests

This project runs a local NBomber comparison between:

- `clean-yarp-*`: a minimal ASP.NET Core + YARP reverse proxy with no ProxyWard middleware.
- `proxyward-worst-case-*`: ProxyWard configured for audit mode, bounded JSON inspection, queued SQLite audit writes, path/host/private-network/command rules, redaction, hidden and blocked tool policy states, telemetry activities/metrics, and a stale DB-backed schema version for `tools/list` drift checks.
- `*-tools-list-gzip`: same `tools/list` schema surface with a gzip-encoded upstream response body, covering ProxyWard's response decoding path before schema inspection.

Run from the repository root:

```powershell
dotnet run -c Release --project .\tests\ProxyWard.PerformanceTests -- --rate 50 --warmup 5 --duration 30
```

Useful options:

- `--rate 100`: injected requests per second for each individual scenario run.
- `--warmup 10`: warmup duration in seconds.
- `--duration 60`: measured duration in seconds.
- `--include-tools-list false`: run only the `tools/call` comparison.
- `--artifacts artifacts/performance`: output directory for NBomber reports, with one report subfolder per scenario, plus runtime SQLite files.

Scenarios run sequentially to reduce cross-scenario CPU contention. Compare the generated NBomber reports by scenario name.

NBomber prints its license notice at runtime; verify the package license fits your intended use before using these results in an organizational workflow.
