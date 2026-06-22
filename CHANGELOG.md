# Changelog

All notable changes to ClaudeMon are documented here. Each version below maps to a
GitHub release; the release notes are taken from these entries.

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
