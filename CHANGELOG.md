# Changelog

All notable changes to ClaudeMon are documented here. Each version below maps to a
GitHub release; the release notes are taken from these entries.

## [0.11.0] - 2026-07-11

### Added
- **Compose the taskbar readout: session / weekly / time-left toggles** — the single "Also show
  7-day usage" toggle is replaced by three independent toggles under *Taskbar display*: **Show
  session (5-hour) usage** (on by default), **Show weekly (7-day) usage**, and **Show time left
  to reset** — a compact countdown (e.g. `1h 23m`) that ticks down live between polls. Any
  combination works, in both readout styles: in **Numbers** the enabled elements render
  dot-separated; in **Bar** the session/weekly toggles pick which bars draw (the countdown is
  Numbers-only — the bar already marks time with its tick). An existing "Also show 7-day usage"
  opt-in migrates to the weekly toggle automatically. (#50)
- **Taskbar readout Size setting** — a new **Size** option on the Settings **Taskbar** tab
  scales the taskbar readout on top of the monitor's DPI scaling: any percentage from 25% to
  150% (arrow keys step by 5). Since the Per-Monitor-V2 change the readout scales with display
  scaling (1.5× larger at 150%), which some found too prominent — sub-100% sizes bring back a
  more compact readout. Applies to both styles and every monitor's overlay, previews live, and
  reverts on Cancel. Enlargement is capped by the taskbar height so the readout never clips;
  100% (the default) renders exactly as before. (#46, #49)

### Fixed
- **Settings window cramped at high display scaling** — the Settings dialog laid its rows,
  padding, and controls out in fixed pixels while its fonts scaled with the display DPI, so on
  displays scaled above 100% the text crowded the fixed spacing. Every layout metric now scales
  with the window's DPI, so the dialog keeps its proportions at 100/125/150/200% scaling; at
  100% it is pixel-identical to before. (#40, #41)

### Changed
- **Settings dialog is now tabbed** — the single stacked scroll of sections is reorganized into
  four tabs: **General** (start with Windows, poll interval), **Alerts** (notifications and
  thresholds), **Taskbar** (the readout's style, colors, and placement), and **Updates** (automatic
  update checks, moved out of General). Every setting works exactly as before — sub-options still
  collapse with their parent toggle, taskbar changes still preview live and revert on Cancel — and
  the window sizes itself to the active tab. The tab headers are drawn to match the app's dark/light
  theme and scale with the display DPI. Groundwork for upcoming settings (#42, #39). (#43)
- **Per-monitor DPI awareness** — the app is now Per-Monitor-V2 DPI aware (previously
  system-DPI-aware). Each window — the Settings dialog, the hover flyout, and the taskbar
  readout — scales crisply for the monitor it is on and re-scales live when moved between
  monitors with different scaling, instead of being bitmap-stretched. This also makes taskbar
  readout placement accurate on mixed-DPI multi-monitor setups. (#41)

## [0.10.0] - 2026-06-29

### Added
- **Selectable taskbar style — Numbers or Bar** — a new **Style** option in Settings switches the
  taskbar readout between the original stacked **Numbers** (`5hr · 7day`) and a compact **Bar +
  time tick**: a horizontal usage bar with faint hour/day dividers and a bright "now" tick marking
  how far through the reset window you are. Fill past the tick means you're burning faster than the
  clock. The bar is pace-coloured (green→red by usage-vs-time, matching the tray icon and flyout).
  Defaults to **Numbers**, so existing installs look unchanged until you opt in. Switches live, no
  restart.
- **Bar width** — a new **Bar width** option (Compact / Standard / Wide / Extra wide) sizes the bar
  style; wider bars give the dividers and time tick more room to read. Only applies to the **Bar**
  style. The label/percentage colour options apply only to the **Numbers** style, and the dialog
  enables each set for the style it affects.
- **Taskbar usage display on secondary monitors (opt-in)** — a new **Show on secondary
  monitors** option in Settings shows the usage readout on every monitor's taskbar, not just
  the primary one (on setups where Windows shows the taskbar on all displays). It's off by
  default. One
  overlay is maintained per taskbar and the set follows the display layout live, so plugging
  in or unplugging a monitor (or toggling "show taskbar on all displays") adds or removes the
  readout automatically — no restart needed. Each overlay re-finds its taskbar continuously,
  so it also survives an Explorer restart. On secondary taskbars (whose clock is a windowless
  surface with no queryable bounds) the readout reserves estimated space so it sits just left
  of the clock instead of over it.
- **Secondary-monitor readout position** — a new **Position** setting (under *Show on secondary
  monitors*) nudges the readout left or right (in pixels) on secondary monitors to fine-tune the
  spacing from the clock, whose width on those taskbars can only be estimated. The primary
  monitor is anchored exactly to its tray and is unaffected. Both the toggle and the position
  preview live as you change them, and revert if you cancel the dialog.

### Changed
- **Alerts are now pace-aware** — the old fixed-percentage alert modes (Warning/Critical
  thresholds and Progressive every-10%) are replaced by a model built around usage-vs-time-left.
  The primary **pace early-warning** fires when your usage relative to how far through the 5-hour
  window you are means you're on track to run out before it resets (with a configurable
  sensitivity: Early / Balanced / Late). A separate absolute **near-cap backstop** still fires a
  critical "almost out" alert near the limit (default 90%) regardless of pace, and the weekly
  (7-day) warning is unchanged. This matches the tray icon and flyout, which already colour by
  pace. Existing configs fall back to the new defaults automatically.
- **Settings window redesigned** — the dialog was rebuilt as flat sections (a section header over
  a hairline divider) with consistent spacing and a single aligned label→control column, replacing
  the boxy, unevenly-spaced GroupBox layout. Notification options now live under an **Alerts**
  section alongside the new pace settings.
- **Installer enables "Run at Windows startup" by default** — the startup checkbox in the
  installer is pre-checked on every install (previously it was only checked on a first install and
  unchecked on upgrades), so ClaudeMon starts with Windows out of the box. You can still untick it
  during setup to opt out.
- **Live preview of taskbar appearance in Settings** — changing any of the visual taskbar settings
  (show on taskbar, style, bar width, also-show-7-day, label/percentage colours, secondary monitors,
  position) now updates the real taskbar readout immediately while the dialog is open, so you can
  see each choice before committing. Cancelling the dialog reverts to the saved appearance.
- **Modern Settings window that follows the Windows theme** — the app now follows the Windows
  light/dark setting (matching window chrome + themed controls), so the Settings dialog is light on
  a light Windows and dark on a dark one. The dialog was modernised further: boolean options are
  **toggle switches** instead of checkboxes, and a section's sub-options **collapse** when its master
  toggle is off (so the dialog only shows what's relevant and shrinks to fit). The section headers,
  toggle switches, numeric spin buttons, dialog buttons, and title bar all themed to match.

### Fixed
- **Taskbar bar tick is readable on a light taskbar** — the bar style's time-in-window tick was a
  plain light line that washed out on a light-themed taskbar. It now detects the taskbar theme and
  draws the tick with a contrasting halo (a dark mark with a light outline on light taskbars; a
  light mark with a dark outline on dark ones), so it reads on either.
- **Installer no longer hangs when ClaudeMon is already running** — upgrading over a running
  instance could stall the installer on a non-cancellable "closing applications" step, because the
  Windows Restart Manager can't close ClaudeMon's windowless tray process. The installer now stops
  the running instance itself (and waits for it to exit) instead of relying on the Restart Manager,
  so upgrades complete cleanly and the app relaunches.

### Notes
- Positioning is best-effort; on mixed-DPI multi-monitor setups the placement on secondary
  taskbars may be slightly off (the app is system-DPI-aware) — use the horizontal-position
  setting to compensate.

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
