# Changelog

All notable changes to ClaudeMon are documented here. Each version below maps to a
GitHub release; the release notes are taken from these entries.

## [0.9.1] - 2026-06-28

### Changed
- **Installer now enables "Run at Windows startup" by default** — the startup checkbox in
  the installer is pre-checked on every install (previously it was only checked on a first
  install and unchecked on upgrades), so ClaudeMon starts with Windows out of the box. You
  can still untick it during setup to opt out.

### Fixed
- **Installer no longer hangs when ClaudeMon is already running** — upgrading over a running
  instance could stall the installer on a non-cancellable "closing applications" step,
  because the Windows Restart Manager can't close ClaudeMon's windowless tray process. The
  installer now stops the running instance itself (and waits for it to exit) instead of
  relying on the Restart Manager, so upgrades complete cleanly and the app relaunches.

## [0.9.0] - 2026-06-27

### Added
- **Time-to-limit estimate (burn rate)** — when your 5-hour usage is rising, the flyout now
  projects how long until you hit 100% at the current rate (e.g. **"~35m to limit"**),
  computed from the recent slope of your usage history. It shows **"—"** when usage is flat
  or declining, when there isn't enough recent history, or when you wouldn't reach the cap
  before the window resets — so the estimate is only shown when it's meaningful.
- **Usage trend sparkline** — the click-through flyout now draws a compact sparkline of
  recent 5-hour usage, so you can see at a glance whether you're climbing fast or leveling
  off. Samples are recorded to a small rolling history file under `%LocalAppData%\ClaudeMon`
  (pruned by age and count, so it never grows without bound) and survive restarts. The
  sparkline appears once there are at least two samples; the history holds only utilization
  percentages and timestamps — no account or token data.
- **Diagnostic logging + "View logs"** — ClaudeMon now writes timestamped diagnostics (poll
  results, connection/sign-in/offline status changes, and the token-refresh lifecycle) to a
  rolling, size-bounded log file at `%LocalAppData%\ClaudeMon\logs\claudemon.log` (capped at
  ~1 MB with a single rotated backup, so it never grows without bound). A new **View logs**
  tray-menu item opens it. Logging is best-effort and never interrupts the app, and **token
  values are never written to the log**.

## [0.8.0] - 2026-06-27

### Added
- **Automatic token refresh — stay signed in without the CLI** — ClaudeMon now renews its
  own Claude Code OAuth access token instead of relying on whichever client last refreshed
  it. When the on-disk token is expired (or about to be), it uses the saved **refresh
  token** to obtain a new access token, then writes the renewed token back to
  `~/.claude/.credentials.json` (atomically, preserving the file's structure) so the CLI
  and VS Code extension benefit too. This fixes the common case where ClaudeMon falsely
  showed **"sign-in expired"** during idle periods (e.g. overnight) for users who
  authenticate mainly through the VS Code extension. The sign-in-expired state now appears
  **only** when no valid sign-in can be obtained — the refresh token is missing, rejected,
  or itself expired. Token values are never logged.

### Fixed
- **Hover flyout no longer renders compressed/overlapping on high-DPI displays** — the tray
  hover popup drew DPI-scaled text into a fixed-pixel box, so on displays scaled above 100%
  the title, usage rows, and status line could collapse and overlap (most visibly on the
  first launch right after an update). The flyout now scales its whole layout from the
  current display DPI and sizes itself to fit its content, so it lays out cleanly at 100%,
  125%, 150%, and 200% scaling.

## [0.7.0] - 2026-06-23

### Changed
- **Clear sign-in-expired state** — when your Claude Code sign-in expires, ClaudeMon now
  shows an actionable **"Sign-in expired — run Claude Code to refresh"** message in the
  tray tooltip and the click-through flyout instead of leaving the last (stale) usage
  percentages on screen. The taskbar usage display likewise replaces the number with a
  neutral **"—"** marker rather than a stale percentage. The **About** dialog now also
  shows the current connection status (Connected / Sign-in expired / Offline). Normal
  display returns automatically once you re-authenticate in Claude Code.

## [0.6.0] - 2026-06-20

### Added
- **Update notifications** — ClaudeMon now checks GitHub for newer releases (daily and
  on demand) and lets you know when one is available. A new **"Download update
  (vX.Y.Z)…"** tray-menu item opens the release page, and a one-time notification fires
  per new version (no nagging). A **"Check for updates"** menu item runs an on-demand
  check, and a **"Check for updates automatically"** toggle (Settings → General, on by
  default) controls the periodic check. Network failures are handled silently; the
  check is unauthenticated and never installs anything on its own.

## [0.5.0] - 2026-06-19

### Added
- **Taskbar: optionally show the 7-day usage too** — a new "Also show 7-day usage
  (5hr / 7day)" toggle (Settings → Taskbar Display) shows the weekly percentage next
  to the 5-hour one, slash-separated (e.g. `42 / 18`). Off by default, so the taskbar
  looks unchanged unless you opt in. Each number is colored for its own usage level
  (under the Auto preset), and the overlay widens automatically so nothing clips.
  Applies live, without restarting.

## [0.4.0] - 2026-06-19

### Added
- **Settings: 7-day warning threshold** — choose the weekly-usage percentage that
  triggers the 7-day warning notification (previously only editable by hand in
  `config.json`).
- **Settings: "Notify when the rate limit resets"** — toggle the reset notification
  from the Settings dialog (previously only editable by hand in `config.json`).

## [0.3.0] - 2026-06-17

### Added
- **Taskbar text colors** — choose the color of the "Claude" label and the usage
  percentage shown on the taskbar (Settings → Taskbar Display). Presets: White, Black,
  Light gray, Dark gray. The percentage also supports **Auto** (the usage-level
  green/yellow/orange/red coloring, still the default). Fixes the label being
  invisible on light-colored taskbars. Applies live, without restarting.

## [0.2.0] - 2026-06-14

### Added
- **Taskbar usage display** — the 5-hour usage percentage can now be shown directly
  on the Windows taskbar (a small "Claude" label above a color-coded number), next to
  the clock. Colors match the tray-icon thresholds. Toggle it in Settings; on by
  default, and it turns on/off live without restarting.

### Changed
- The installer now stops a running ClaudeMon before upgrading and automatically
  relaunches it afterward.
- The installer version is derived from the application assembly, so there is a single
  source of truth for the version number.

## [0.1.1] - 2026-05-31

### Fixed
- Settings form now shows the correct alert panel (threshold vs progressive) when
  opened, matching the saved configuration (#1).

## [0.1.0] - 2026-05-23

- Initial release.
