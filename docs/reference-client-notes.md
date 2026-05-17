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
- Added GUI smoke coverage that confirms the Settings auto-connect checkbox reflects the profile-page startup shortcut.
- Added a selected-profile endpoint test action so one profile can be retested without running the full visible-profile batch.
- Added GUI smoke coverage for clearing and restoring the profile-page startup shortcut.
- Added copyable filtered logs to keep troubleshooting output available after narrowing the runtime log view.
- Added copyable subscription update results so aggregate subscription refresh outcomes can be pasted into troubleshooting notes.
- Added a one-click test-and-select-fastest profile action to reduce the common latency-check then choose-node workflow.
- Added Overview quick navigation and made the header diagnostics action open the Diagnostics page before refreshing checks.
- Added GUI smoke coverage for the Overview Diagnostics shortcut before returning to the Overview workflow.
- Added GUI smoke coverage for the Overview Subscriptions, Logs, and Settings shortcuts.
- Added Overview proxy-mode quick actions for Off, System, and PAC so common mode changes are available without opening the mode combo box.
- Added an Overview network-test shortcut that opens Diagnostics and runs the existing direct/proxy connectivity test.
- Added an Overview network-test summary so the latest direct/proxy connectivity result is visible without reading the Diagnostics log block.
- Added a dedicated GUI smoke screenshot for the Overview network-test summary state.
- Added a copyable Overview status snapshot for troubleshooting current profile, proxy, network-test, core, validation, and issue state.
- Made the Overview network-test summary a color-coded badge so ok, partial, pending, and failed states are faster to scan.
- Added `/regex/` runtime log filtering with a visible filter summary, matching v2rayN-style advanced log search while keeping plain text filtering.
- Added a dedicated GUI smoke screenshot for the active regex log-filter state.
- Added a Settings core executable status badge so users can immediately see whether the configured or auto-detected `x-tunnel.exe` path is usable.
- Added a copyable Diagnostics summary for quickly sharing OS, profile, proxy, and port-check context.
- Added Diagnostics route status chips for direct, local proxy, and selected-profile forward tests so connectivity state is scannable before reading raw result text.
- Changed the Diagnostics action bar to a wrapping toolbar so its many troubleshooting actions remain reachable on narrower windows.
- Added a Logs filter badge with invalid-regex and no-match states, following the v2rayN-style emphasis on visible regex log search feedback.
- Added a Diagnostics listen-port badge that surfaces selected-profile port availability from the existing core config port checks.
- Added Diagnostics refresh feedback with last-run text and stable smoke assertions for the Run Checks action.
- Added Overview network-test last-run text and copied it into the Overview status summary so users can judge result freshness.
- Added a Settings copy-core-path action so users can paste the configured or auto-detected `x-tunnel.exe` path into bug reports or terminals.
- Added a stable AutomationId and GUI smoke coverage for the Overview Save action.
- Added GUI smoke coverage for the Overview proxy-address copy action using a stable AutomationId.
- Added stable AutomationIds and smoke assertions for the status bar proxy mode, local proxy, core, and issue fields.
- Extended status bar smoke coverage to assert the core field changes on connect and returns to stopped on disconnect.
- Rebalanced the status bar columns so local proxy, core, and issue details get proportional width instead of being capped by narrow fixed columns.
- Added a Diagnostics Clear Network action that resets raw network results, route chips, and Overview network freshness text.
- Added a Diagnostics Clear Forward action that resets the selected-profile forward TCP result and route chip.
- Added a Diagnostics Copy Report action for sharing the redacted JSON report without exporting a zip.
- Added stable AutomationIds and smoke coverage for exporting the Diagnostics zip, including a `report.json` zip-entry check.
- Added GUI smoke coverage for the Logs-page Export Diagnostics entry point to keep both diagnostics export buttons verified.
- Added GUI smoke coverage for the Logs-page Refresh Diagnostics entry point.
- Added GUI smoke coverage for invalid network-test target feedback, including route-chip state, before the local HTTP 204 success path.
- Persisted the selected network-test target and URL with application settings so custom diagnostics targets survive explicit saves.
- Added GUI smoke coverage that saves a custom network-test target, restarts the app with the same data folder, and verifies the target and URL reload.
- Added narrow-window GUI smoke screenshots for Diagnostics and Profiles so wrapped layouts have visual artifacts at the minimum-width boundary.
- Split the Diagnostics network-test URL from its action buttons so the URL field keeps usable width while the actions wrap below it.
- Wrapped the Logs toolbar actions so diagnostics, filter, clear, and copy controls remain reachable as the window narrows.
- Wrapped the Subscription detail actions so update, save, and copy controls stay reachable at narrower widths.
- Wrapped the Settings local-folder actions so folder shortcuts remain visible in tighter windows.
- Wrapped the Diagnostics forward-test actions so test, copy, clear, and freshness controls remain reachable.
- Constrained the Settings core-path status detail in a remaining-width grid column so long paths trim instead of pushing the layout.
- Standardized the Profiles action toolbar spacing with WrapPanel item and line spacing for cleaner wrapped rows.
- Standardized the Profiles detail action toolbar spacing so edit, import, export, and copy commands wrap evenly.
- Split the Profiles listen/forward inputs from connection, fallback, and metrics controls so endpoint editing keeps more horizontal space.
- Standardized the Subscriptions list action toolbar spacing so new, update, save, and delete commands wrap evenly.
- Standardized the Overview control, network, and navigation button spacing with WrapPanel item and line spacing.
- Added a Diagnostics Run All action that refreshes the diagnostics report and reruns network plus selected-profile forward tests from one visible control.
- Tightened HTTP connectivity tests so non-2xx HTTP responses are shown as failed instead of route-ok false positives.
- Added selected-profile forward test freshness text so Diagnostics shows when the last TCP check ran or was cleared.
- Added a Diagnostics Copy Ports action for quickly sharing the current listen-port status and local endpoint details.
- Extended the copied Diagnostics summary with the current network and forward-test results after checks have run.
- Added GUI smoke coverage for Overview recent logs and runtime details after connecting through the sidecar.
- Added a Diagnostics Clear Tests action that resets network and selected-profile forward test state from one visible control.
- Added Overview copy actions for recent logs and runtime details, with GUI smoke clipboard checks after connecting.
- Added a Subscriptions Copy Source action for sharing selected subscription name, URL, interval, trust policy, and last update status.
- Added a Profiles Copy Config action for copying the selected profile core JSON without exposing the DPAPI secret value.
- Added a Profiles Copy Issues action for sharing selected-profile validation state and field issues after failed saves or checks.
- Added GUI smoke coverage for the Profiles Validate and Format actions through stable AutomationIds.
- Added GUI smoke coverage for importing a selected profile from clipboard JSON.
- Added a Settings Copy Folders action for sharing app-data, profile, log, runtime, and core paths.
- Added GUI smoke coverage for the Settings save action through a stable AutomationId.
- Added GUI smoke coverage for the Subscriptions detail Save Subscription action through a stable AutomationId.
- Added GUI smoke coverage for the runtime Restart control before the connected proxy-route test.
- Added an Overview Copy Metrics action backed by the x-tunnel sidecar control API metrics endpoint.
- Extended Overview Copy Runtime Details to include both control status and stats JSON.
- Added GUI smoke coverage for duplicating and deleting an imported profile.
- Added GUI smoke coverage for deleting a subscription through a stable AutomationId.
