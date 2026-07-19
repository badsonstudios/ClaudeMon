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
│   └── UI/                         #   IconRenderer, FlyoutPanel, SettingsForm
├── tests/ClaudeMon.Tests/          # xUnit tests (mirrors src; uses InternalsVisibleTo)
├── installer/                      # Inno Setup script + build.sh
└── .claude/                        # Claude config (context, skills, agents)
```

## Layers & responsibilities

- **UI** (`UI/`) — `IconRenderer` draws the 16×16 tray icon (number + threshold color),
  `FlyoutPanel` shows usage details, `SettingsForm` edits `AppSettings`.
- **Monitoring** (`Monitoring/`) — `UsageMonitor` polls on an interval and surfaces usage;
  `LocalUsageMonitor` drives the local-transcript scanner on its own timer (same
  Start/Pause/Resume shape); `AlertManager` decides when to notify (threshold vs.
  progressive modes); `LimitDisplay` turns the API's `limits[]` buckets into display rows
  (legacy 5h/7d fallback included), `TrayTooltip` composes the tray hover text under the
  127-char `NotifyIcon` cap, and `LocalCostText` composes the flyout's "Today: ~$…" cost
  line — all pure and unit-tested, consumed by the UI layer.
- **Services** (`Services/`) — `ClaudeApiClient` calls the Anthropic usage API;
  `CredentialReader` loads the OAuth token from `~/.claude/.credentials.json`;
  `TokenRefresher` renews it; `UpdateChecker`/`UpdateInstaller` drive in-app updates;
  `SessionEvents` wraps lock/unlock notifications; `BrowserLauncher` is the sole
  http(s)-only gate for opening URLs in the browser; `Logger` writes the daily logs;
  `LocalUsageStore` + `JsonlUsageParser` incrementally scan Claude Code's transcripts
  (`~/.claude/projects/**/*.jsonl`, per-file byte offsets, dedupe by message/request id)
  into per-day cost/token aggregates cached at `%LocalAppData%\ClaudeMon\local-usage.json`;
  `PricingTable` resolves model ids against the embedded `Resources/model-pricing.json`
  (list prices; unknown models stay unpriced rather than guessing).
- **Configuration** (`Configuration/`) — `ConfigManager` persists `AppSettings` as JSON and
  manages the "Start with Windows" registry entry.
- **Models** (`Models/`) — immutable `record` types for settings and API payloads.

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
