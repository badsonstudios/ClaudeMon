# Architecture — ClaudeMon

## Structure

```
ClaudeMon/
├── src/ClaudeMon/                  # the app
│   ├── Program.cs                  #   entry point; single-instance mutex, app bootstrap
│   ├── TrayApplication.cs          #   wires together tray icon, monitor, alerts, UI
│   ├── Models/                     #   data records (AppSettings, UsageResponse, CredentialData)
│   ├── Services/                   #   CredentialReader, ClaudeApiClient (Anthropic API)
│   ├── Monitoring/                 #   UsageMonitor (polling), AlertManager, LimitDisplay, TrayTooltip
│   ├── Configuration/              #   ConfigManager (load/save settings, startup registry)
│   ├── Resources/                  #   embedded/committed assets (model-pricing.json, ClaudeMon.ico)
│   └── UI/                         #   IconRenderer, FlyoutPanel, SettingsForm
├── tests/ClaudeMon.Tests/          # xUnit tests (mirrors src; uses InternalsVisibleTo)
├── installer/                      # Inno Setup script + build.sh
├── tools/                          # one-off dev utilities, NOT part of the build
│   └── icon/                       #   generate-claudemon-icon.ps1 → Resources/ClaudeMon.ico
└── .claude/                        # Claude config (context, skills, agents)
```

## Layers & responsibilities

- **UI** (`UI/`) — `IconRenderer` draws the 16×16 tray icon (number + threshold color),
  `FlyoutPanel` shows usage details, `SettingsForm` edits `AppSettings`. `DialogPlacement`
  owns where windows open — pure `CenterIn` math plus `PlaceStable`, which re-centers until
  the window is observed to actually be centered (a move onto a differently-scaled monitor
  resizes it afterwards under Per-Monitor-V2); `ForegroundMonitor` resolves which monitor the
  user is working on from the foreground window, never the mouse cursor (#88, #108). The
  static app icon is a committed asset (`Resources/ClaudeMon.ico`, wired via
  `<ApplicationIcon>`), distinct from the live tray icon `IconRenderer` draws.
- **Monitoring** (`Monitoring/`) — `UsageMonitor` polls on an interval and surfaces usage;
  `LocalUsageMonitor` drives the local-transcript scanner on its own timer (same
  Start/Pause/Resume shape); `AlertManager` decides when to notify (pace-aware 5-hour alerts
  plus per-bucket weekly alerts — overall and per-model — each with its own fired/hysteresis
  state, all coalesced into one balloon per poll since `NotifyIcon` shows only one);
  `LimitDisplay` turns the API's `limits[]` buckets into display rows and, via
  `WeeklyAlertTargets`, into the weekly buckets `AlertManager` alerts on — one source for
  both, so they can't disagree (legacy 5h/7d fallback included); `TrayTooltip` composes the tray hover text under the
  127-char `NotifyIcon` cap, and `LocalCostText` composes the flyout's "Today: ~$…" cost
  line — all pure and unit-tested, consumed by the UI layer. `ServiceStatusText` composes the
  flyout's Anthropic-service line (null while healthy, so a healthy service draws nothing) and
  `ServiceStatusAlerts` is the pure incident-start/escalation decision behind the optional
  outage notification, settings gate (opt-in toggle, notifications switch, snooze) included.
  `LimitWindowTracker` is the pure state machine behind the correlated limit log (#184):
  one observation in → (sample, finalized window records, new state) out, windows keyed by
  (kind, scope model) with server-authoritative `resets_at` boundaries; `LimitLogRecorder`
  glues it to the poll (called by `UsageMonitor` on the success path only) and appends via
  `Services/LimitLogStore` (per-month, never-pruned JSONL under
  `%LocalAppData%\ClaudeMon\limit-log\` plus an atomic `state.json`, the only piece the app
  ever reads back — startup and memory stay O(1) in log size).
- **Services** (`Services/`) — `ClaudeApiClient` calls the Anthropic usage API;
  `ServiceStatusClient` reads the public status page (`status.claude.com/api/v2/status.json`,
  unauthenticated, silent on any failure) with a pure `Parse`, fetched by `UsageMonitor` on the
  usage poll's own cadence rather than a second timer;
  `CredentialReader` loads the OAuth token from `~/.claude/.credentials.json`;
  `TokenRefresher` renews it; `UpdateChecker`/`UpdateInstaller` drive in-app updates;
  `SessionEvents` wraps lock/unlock notifications; `BrowserLauncher` is the sole
  http(s)-only gate for opening URLs in the browser; `Logger` writes the daily logs;
  `LocalUsageStore` + `JsonlUsageParser` incrementally scan Claude Code's transcripts
  (`~/.claude/projects/**/*.jsonl`, per-file byte offsets, dedupe by message/request id)
  into per-(day, project, model) cost/token cells (30-day retention, versioned cache at
  `%LocalAppData%\ClaudeMon\local-usage.json`) serving the flyout snapshot, the breakdown
  queries, and the budget totals; `PricingTable` resolves model ids against the embedded
  `Resources/model-pricing.json` (list prices; unknown models stay unpriced rather than
  guessing); `BreakdownCsv`/`BreakdownSort`/`BreakdownDrill`/`ProjectDisplay` are pure helpers
  for the Usage & costs window — `BreakdownSort` orders the table rows and, since #119, the
  export's rows too, so the CSV matches the on-screen sort, and `BreakdownDrill` (moved out of
  `UI/` for the same reason in #168) slices the model × project pairs for both the drilled
  tables and the drilled export; `Monitoring/BudgetAlerts` is the pure 50/80/95% once-per-period alert ladder
  (state persisted in `AppSettings.BudgetAlertState`).
- **Configuration** (`Configuration/`) — `ConfigManager` persists `AppSettings` as JSON and
  manages the "Start with Windows" registry entry.
- **Models** (`Models/`) — immutable `record` types for settings and API payloads (including
  `ServiceStatus`, the status page's overall indicator and its wording).

## Patterns & conventions

- WinForms event-driven app; `TrayApplication` is the composition root (no DI container).
- Settings are immutable `record`s with `init` setters + `[JsonPropertyName]` mappings.
- Internals are exposed to the test project via `InternalsVisibleTo`, so prefer testable
  internal types over reaching into UI.
- Single instance enforced by a global named mutex in `Program.cs`.

## Data flow

`UsageMonitor` polls `ClaudeApiClient` (authed via `CredentialReader`) on the configured
interval → parses `UsageResponse` → updates the tray icon via `IconRenderer` and feeds
`AlertManager`, which raises desktop notifications when thresholds are crossed. User changes
in `SettingsForm` are persisted by `ConfigManager` and applied to the running monitor.
