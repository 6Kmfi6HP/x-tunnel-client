# x-tunnel Client

Windows desktop client for `x-tunnel`, implemented as a separate C# / Avalonia repository. The GUI manages profiles, secrets, system proxy/PAC state, diagnostics, and a supervised `x-tunnel.exe` sidecar. It does not embed the Go tunnel core in the GUI process.

## Repository Boundary

- This repository: Windows GUI, tray, profile storage, sidecar supervisor, system proxy/PAC, diagnostics, update manifest validation, and packaging scripts.
- Sibling `x-tunnel` repository: Go core and control API.
- Debug builds auto-detect `..\x-tunnel\build\x-tunnel.exe` when present.
- Release packaging copies the core into `core\x-tunnel.exe` inside the client artifact.

## Features

- Single-instance Windows desktop app with tray menu and color-coded top feedback for success, warning, and error messages.
- Global status bar for runtime state, active profile, proxy endpoints, traffic, channel health, core version, and current issue, with GUI smoke coverage for every status field.
- Overview dashboard with runtime, traffic, channel, listener, reconnect, proxy, copyable local proxy address, color-coded network-test summary with last-run time, copyable status snapshot, quick proxy-mode actions, one-click network test, copyable recent-log, status/stats detail, and runtime-metrics panels with GUI smoke coverage, and quick navigation to operational pages.
- Profiles page with search/filter, saved/name/endpoint sorting, profile summary counts, startup-profile shortcut, copyable redacted profile summaries, core configs, and validation issue reports, one-click test-and-select-fastest, fastest-profile selection, clearable forward-endpoint latency badges, structured form editing, advanced JSON editing, core-backed format/check, field issue list, secret validation, and local port checks.
- Subscriptions page with search/filter, saved/name/updated/status sorting, saving sources, updating selected or all subscriptions, copyable source summaries and update results, updating profiles by name, and tracking aggregate/list-item fetch/cache status.
- Logs page with runtime log level, text or `/regex/` filtering, visible filter-state badges and summaries, invalid-regex feedback, and copyable filtered output.
- Diagnostics page with one-click run-all checks, one-click test-result clearing, preset/custom direct/local-proxy HTTP tests that require successful HTTP status codes, local listen-port status badge, copyable port details, clearable direct/proxy network results, clearable forward route status chips with freshness text, copyable diagnostics summary including current test results, copyable diagnostics report, copyable network and forward-test results, profile forward endpoint TCP tests, redacted export, and log access.
- Settings page with core executable auto-detect, visible and copyable core path status, one-click core version check, copyable local folder summary, and app-data, profile, log, and runtime folder shortcuts.
- Tray menu for connect, disconnect, restart, copy proxy address, subscription update, diagnostics, log folder access, restore proxy, and quit.
- SQLite profile/settings/subscription storage.
- DPAPI-protected profile secrets.
- Runtime config generation with `token_ref` replacement.
- Offline config check through `x-tunnel.exe -check-config`.
- Offline config format through `x-tunnel.exe -format-config`.
- Sidecar launch using `-control`, `-ready-file`, and `-control-token-file`.
- Control API client for version/capabilities, health, status, logs, stats, config check/format, and runtime stop.
- Supervisor state machine: stopped, starting, running, degraded, stopping, faulted, recovering.
- WinINET system proxy mode with previous-setting restore.
- Local PAC server mode.
- Startup registry integration, minimized startup, delayed auto-connect, and selected-profile auto-connect.
- Redacted diagnostics zip export.
- Update manifest and checksum validation primitives.
- Subscription fetch, validation, and diffing.
- TUN/helper UI/data-path placeholder without unsafe route/DNS changes.

## Build

Use the local SDK installed by the development task:

```powershell
$dotnet = "$env:USERPROFILE\.dotnet-sdk\dotnet.exe"
& $dotnet build XTunnelClient.sln
& $dotnet test XTunnelClient.sln
```

Build a portable artifact and bundle the sibling Go core:

```powershell
.\scripts\build.ps1 -Configuration Release -Runtime win-x64
```

The portable zip is written under `artifacts\`.

## Development Smoke

Build the Go core first:

```powershell
cd ..\x-tunnel
go build -o build\x-tunnel.exe .\cmd\x-tunnel
go test ./...
```

Then from this repository:

```powershell
$dotnet = "$env:USERPROFILE\.dotnet-sdk\dotnet.exe"
& $dotnet test XTunnelClient.sln
& $dotnet run --project src\XTunnelClient.App\XTunnelClient.App.csproj
```

The real-core smoke test starts a local Go server, launches a GUI-managed sidecar, waits for a live channel through the control API, and stops it.

Run the desktop GUI smoke after a debug build:

```powershell
.\scripts\gui-smoke.ps1
```

The GUI smoke launches the app with an isolated `XTUNNEL_CLIENT_HOME`, starts a local x-tunnel server, controls the Overview, Profiles, Subscriptions, and Diagnostics tabs through Windows UI Automation, verifies all status-bar fields before and after connect/disconnect including traffic and channel summaries, overview quick navigation, proxy-mode quick actions, Overview Save, copyable local proxy address, Overview network-test shortcut, summary, and last-run time, copyable Overview status, connected Overview recent logs/runtime status+stats details plus their copy actions, copyable runtime metrics from the core control API, and header diagnostics navigation, validates and formats the selected profile through the GUI, copies a redacted profile summary and core JSON config, verifies the startup-profile shortcut plus clearing/restoring it, runs selected-profile and visible-profile endpoint latency checks, verifies endpoint sorting and one-click test-and-select-fastest, verifies Diagnostics Run Checks refresh feedback, listen-port status, and copied port details, verifies network-test target presets and invalid-target feedback, runs the network test against a local HTTP 204 endpoint, verifies direct/proxy/forward route status chips and forward last-run text, verifies copying and clearing the network-test result, connects and restarts through the GUI, verifies the connected HTTP proxy route returns HTTP 204 through the GUI and that the connected direct/proxy result copies correctly, checks the selected profile's forward endpoint, verifies copying and clearing the forward-test result, verifies one-click Diagnostics Run All repopulates network and forward-test results, verifies one-click Diagnostics Clear Tests resets network and forward state, verifies copying the diagnostics summary with current test results and JSON report, exports diagnostics zips from Diagnostics and Logs and checks that `report.json` exists inside them, verifies the Logs-page diagnostics refresh button, disconnects through the GUI, verifies Settings auto-connect reflects the profile startup shortcut, verifies Settings core path auto-detect status, copies the core path and local folder summary, saves settings, verifies the custom network-test target and URL survive a GUI restart, verifies regex/text/invalid-regex log filtering and filter badges, copies filtered logs, saves one subscription, updates it and then all subscriptions from a local feed, verifies copying the subscription source summary and update result, verifies subscription search/clear, updated sorting, summary text, and deletion, verifies profile search/clear behavior, verifies copying the selected profile issue report after an invalid JSON save, imports profile JSON from the clipboard, duplicates and deletes that imported profile through the GUI, saves `artifacts\gui-smoke-overview.png`, `artifacts\gui-smoke-overview-network.png`, `artifacts\gui-smoke-overview-runtime.png`, `artifacts\gui-smoke-diagnostics.png`, `artifacts\gui-smoke-diagnostics-narrow.png`, `artifacts\gui-smoke-settings.png`, `artifacts\gui-smoke-logs-regex.png`, `artifacts\gui-smoke-logs-invalid-regex.png`, `artifacts\gui-smoke-logs.png`, `artifacts\gui-smoke-subscriptions.png`, `artifacts\gui-smoke.png`, and `artifacts\gui-smoke-profiles-narrow.png` for visual layout review, then clears endpoint test results through the GUI.
If `..\x-tunnel\build\x-tunnel.exe` is missing, the script builds it from the sibling core repository first.
