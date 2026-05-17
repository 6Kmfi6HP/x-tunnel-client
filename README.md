# x-tunnel Client

Windows desktop client for `x-tunnel`, implemented as a separate C# / Avalonia repository. The GUI manages profiles, secrets, system proxy/PAC state, diagnostics, and a supervised `x-tunnel.exe` sidecar. It does not embed the Go tunnel core in the GUI process.

## Repository Boundary

- This repository: Windows GUI, tray, profile storage, sidecar supervisor, system proxy/PAC, diagnostics, update manifest validation, and packaging scripts.
- Sibling `x-tunnel` repository: Go core and control API.
- Debug builds auto-detect `..\x-tunnel\build\x-tunnel.exe` when present.
- Release packaging copies the core into `core\x-tunnel.exe` inside the client artifact.

## Features

- Single-instance Windows desktop app with tray menu.
- Global status bar for runtime state, active profile, proxy endpoints, core version, and current issue.
- Overview dashboard with runtime, traffic, channel, listener, reconnect, proxy, color-coded network-test summary, copyable status snapshot, quick proxy-mode actions, one-click network test, recent-log, raw-detail panels, and quick navigation to operational pages.
- Profiles page with search/filter, saved/name/endpoint sorting, profile summary counts, startup-profile shortcut, copyable redacted profile summaries, one-click test-and-select-fastest, fastest-profile selection, clearable forward-endpoint latency badges, structured form editing, advanced JSON editing, core-backed format/check, field issue list, secret validation, and local port checks.
- Subscriptions page with search/filter, saved/name/updated/status sorting, saving sources, updating selected or all subscriptions, copyable update results, updating profiles by name, and tracking aggregate/list-item fetch/cache status.
- Logs page with runtime log level, text or `/regex/` filtering, visible filter summaries, and copyable filtered output.
- Diagnostics page with preset/custom direct/local-proxy HTTP tests, direct/proxy/forward route status chips, copyable diagnostics summary, copyable network and forward-test results, profile forward endpoint TCP tests, redacted export, and log access.
- Settings page with core executable auto-detect, visible core path status, app-data, profile, log, and runtime folder shortcuts.
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

The GUI smoke launches the app with an isolated `XTUNNEL_CLIENT_HOME`, starts a local x-tunnel server, controls the Overview, Profiles, Subscriptions, and Diagnostics tabs through Windows UI Automation, verifies overview quick navigation, proxy-mode quick actions, Overview network-test shortcut and summary, copyable Overview status, and header diagnostics navigation, copies a redacted profile summary, verifies the startup-profile shortcut, runs visible-profile endpoint latency checks, verifies endpoint sorting and one-click test-and-select-fastest, verifies network-test target presets, runs the network test against a local HTTP 204 endpoint, verifies direct/proxy/forward route status chips, verifies copying the network-test result, verifies the connected HTTP proxy route, checks the selected profile's forward endpoint, verifies copying the forward-test result, verifies copying the diagnostics summary, connects and disconnects through the GUI, verifies Settings core path auto-detect status, verifies regex/text log filtering and copying filtered logs, updates one subscription and then all subscriptions from a local feed, verifies copying the subscription update result, verifies subscription search/clear, updated sorting, and summary text, verifies profile search/clear behavior, saves `artifacts\gui-smoke-overview.png`, `artifacts\gui-smoke-overview-network.png`, `artifacts\gui-smoke-diagnostics.png`, `artifacts\gui-smoke-settings.png`, `artifacts\gui-smoke-logs-regex.png`, `artifacts\gui-smoke-logs.png`, `artifacts\gui-smoke-subscriptions.png`, and `artifacts\gui-smoke.png` for visual layout review, then clears endpoint test results through the GUI.
If `..\x-tunnel\build\x-tunnel.exe` is missing, the script builds it from the sibling core repository first.
