# Reference Client Notes

Date: 2026-05-17

These notes record what was checked before the first x-tunnel Client overview redesign. The referenced projects are used for interaction and product-boundary research only. No GPL implementation code is copied into this repository.

## Current GitHub Snapshot

Checked with `gh repo view` on 2026-05-17:

| Project | Latest release | License | Default branch | Relevant UX pattern |
| --- | --- | --- | --- | --- |
| 2dust/v2rayN | 7.21.3, published 2026-05-10 | GPL-3.0 | master | Avalonia desktop shell, tray-first controls, status bar, profile table |
| clash-verge-rev/clash-verge-rev | v2.4.7, published 2026-03-21 | GPL-3.0 | dev | Home dashboard cards, traffic panels, system proxy/TUN switch panels |
| chen08209/FlClash | v0.8.92, published 2026-02-02 | GPL-3.0 | main | Cross-platform proxy mode and TUN UX |
| hiddify/hiddify-app | v4.1.1, published 2026-03-05 | other | main | Import/subscription and diagnostics-heavy proxy UX |

## v2rayN Findings

Inspected shallow clone under `%TEMP%\xtunnel-research\v2rayN`.

- `v2rayN.Desktop/App.axaml` keeps the tray menu operational: show/hide, copy proxy command, system proxy modes, subscription update, and exit are available without opening the main window.
- `v2rayN.Desktop/Views/StatusBarView.axaml` keeps system proxy, routing, TUN, selected server, inbound listeners, and speed display visible at the bottom of the main window.
- `v2rayN.Desktop/Views/MainWindow.axaml` favors dense operational panes: profiles remain next to logs/proxies/connections instead of hiding all runtime detail behind modal pages.
- `ServiceLib/ViewModels/StatusBarViewModel.cs` centralizes tray/status commands and display state, so the UI can stay consistent across tray, status bar, and main window.

Implication for x-tunnel Client: the first screen should expose connection state, active profile, local proxy endpoints, listener state, channel health, recent logs, and recovery actions without requiring users to inspect raw JSON.

## Clash Verge Rev Findings

Inspected shallow clone under `%TEMP%\xtunnel-research\clash-verge-rev`.

- `src/pages/home.tsx` composes the home page from compact profile, proxy, mode, traffic, test, and system cards.
- `src/components/shared/proxy-control-switches.tsx` treats system proxy and TUN as first-class controls, and shows unavailable TUN state with repair/install affordances.
- `src/components/layout/layout-traffic.tsx` keeps small live upload/download metrics visible in the application shell.

Implication for x-tunnel Client: system proxy/PAC/TUN should be visible as mode state, not buried as a setting; traffic and channel health should be summarized as small dashboard metrics with raw details still available for diagnostics.

## Applied In This Iteration

- Replaced the raw-JSON-only Overview with a dashboard header, profile/core/proxy summaries, fixed-size metric tiles, listener rows, channel rows, recent logs, and a compact runtime details pane.
- Kept profile and diagnostics tabs intact so the existing workflows remain reachable.
- Preserved raw status/log text for debugging while making common state scannable for daily use.
- Added a copyable, redacted profile summary so the profile list/detail workflow supports quick diagnostics without exposing tokens or secret references.
- Added a profile-page startup shortcut so the selected node can be saved for auto-connect from the same workflow where it is tested and edited.
- Added copyable filtered logs to keep troubleshooting output available after narrowing the runtime log view.
- Added copyable subscription update results so aggregate subscription refresh outcomes can be pasted into troubleshooting notes.
- Added a one-click test-and-select-fastest profile action to reduce the common latency-check then choose-node workflow.
- Added Overview quick navigation and made the header diagnostics action open the Diagnostics page before refreshing checks.
- Added Overview proxy-mode quick actions for Off, System, and PAC so common mode changes are available without opening the mode combo box.
- Added an Overview network-test shortcut that opens Diagnostics and runs the existing direct/proxy connectivity test.
- Added an Overview network-test summary so the latest direct/proxy connectivity result is visible without reading the Diagnostics log block.
- Added a dedicated GUI smoke screenshot for the Overview network-test summary state.
