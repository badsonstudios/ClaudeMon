# Changelog

All notable changes to ClaudeMon are documented here. Each version below maps to a
GitHub release; the release notes are taken from these entries.

## [0.27.0] - Unreleased

### Changed
- **Service-incident notifications: tidied up and the design decision written down** — no change
  to what you see. The service-status event no longer carries the previous status, which nothing
  had used since the "already told you about this incident" memory started surviving restarts,
  and the reason that memory is keyed on how *severe* an incident is rather than on which
  incident it is now lives next to the code: Anthropic's status page doesn't publish an incident
  identifier on the endpoint ClaudeMon reads, and its overall severity is a roll-up across every
  open incident, so there is no single incident for the notification to be tied to. The one
  accepted consequence — if the service recovers and breaks again entirely while ClaudeMon isn't
  looking, the second incident's balloon can be swallowed, though the flyout still shows it — is
  documented and pinned by tests. (#150)
- **ClaudeMon now identifies itself the same way on every request it makes.** The usage API, the
  GitHub update check, and the installer download each sent a different `User-Agent`, and the
  OAuth token refresh sent none at all. All four now send one `ClaudeMon/<version>` built in a
  single place, so an install's traffic reads as one app at one version rather than three. Headers
  only — nothing about polling, updating, or signing in changed. (#141)

### Fixed
- **Windows no longer run off the bottom of a short screen** — the fit-to-monitor clamp added to
  the Settings window now covers every window in the app. About, the update prompt, the update
  download progress window and the Usage & costs window all size themselves from their content,
  so on a short display or at a large scale factor any of them could put its buttons somewhere
  below the taskbar. They now cap themselves to the monitor's working area (scrolling, or in the
  resizable window's case simply opening smaller) and slide back on screen if a relayout grows
  them past the bottom edge. No change on a screen where they already fit. (#153)
- **CSV export follows the sort you're looking at** — clicking a column header re-sorted the
  Usage & costs tables on screen, but the exported file still came out in the default
  cost-descending order, so the spreadsheet didn't match the window it came from. Export now
  writes each table in that table's own current order (the two sort independently, and so does
  the file's model and project section), with the Total row still last. (#119)
- **The time-to-limit estimate now recovers within a couple of polls after a 5-hour reset**
  instead of showing a dash for half an hour. The estimate reads the trend from the last 30
  minutes of samples, and after a reset that half hour still contained the pre-reset numbers —
  so a window that dropped from 49% to 2% looked like usage *falling* fast, and both the flyout
  line and the taskbar readout's element showed `—` until the old samples aged out, even though
  usage was climbing again the whole time. Samples from before the current window began are now
  left out of the trend, so once the new window has three readings of its own the estimate comes
  back as a real projection (or `safe` when the next reset beats it). With fewer than three it
  still honestly shows `—`, and when Anthropic doesn't tell us when the window resets nothing
  changes. (#160)
- **Claude Opus 5 usage is priced, so today's cost stops being a floor** — the bundled price table
  had no row for `claude-opus-5`, so every token from it was counted but costed at nothing. If
  that was your main model the flyout read `Today: ≥$172` — a floor, not a figure — and the
  Usage & costs chart hatched the day as incomplete. Opus 5 is now priced at its published list
  rates (input, output, and the separate 5-minute / 1-hour cache-write and cache-read rates), so
  a day whose models are all known reads as a real number again. The rest of the table was
  audited against Anthropic's published pricing page at the same time; every other current model
  was already there and correct. (#161)
- **Saving Settings keeps alert and budget values it doesn't show** — the alert thresholds and
  budget caps were still rebuilt from the dialog's controls alone, the way the taskbar settings
  were before #143. Nothing is lost today (every one of those values has a control), but they are
  now layered onto your saved settings too, so a future setting stored alongside them can't be
  quietly reset the next time you click OK. (#155)

### Internal
- **Second coverage pass over the logic layer, and a higher bar to keep it there.** The classes
  the previous pass left out are now tested — the taskbar reading and DPI scaling helpers, the
  local-usage snapshot and breakdown records, the credentials file model, and the push
  notifier's fire-and-forget path — along with the `UsageMonitor` branches that only happen when
  things go wrong: a token the server rejects after the refresh also fails, a poll cancelled by
  shutdown, and a refreshed token that can't be written back because another Claude client
  rotated it first. The CI coverage gate moves from 85%/84% to 90%/87% (measured 96.0% line /
  91.4% branch). No behaviour changes. (#146)

## [0.26.0] - 2026-08-09

### Added
- **Middle-click the taskbar readout to cycle what it shows** — deciding mid-session that you'd
  rather be watching the weekly number than the session one meant opening Settings for a change
  you'd reverse ten minutes later. Now a **middle-click** on the readout (or **Ctrl+left-click**,
  for mice without a usable middle button) switches it to the next metric — session % → weekly %
  → time to limit → reset countdown → back around — and the readout flashes the name of the one
  you landed on so the gesture explains itself. Plain left-click still opens the flyout, and
  cycling on any monitor's readout moves them all. The gesture is a shortcut over the same
  Settings toggles rather than a setting of its own, so Settings always shows what you're looking
  at and the choice survives a restart. If your readout shows several metrics at once, that
  layout is a stop on the ring in its own right — the first click collapses onto its leftmost
  element, each one after that advances, and one lap brings back exactly what you had (flashed as
  `custom`), so cycling is a temporary focus rather than an edit that eats your layout. Tick the
  toggles in Settings and those become the layout it returns to. The Bar style cycles the two
  metrics it can draw as a bar — the bar itself stays put, both bars at once is a layout of its
  own, and the two time toggles it has no way to display are left exactly as you set them. While
  the Settings dialog is open it owns the readouts as its live preview, so the gesture stands
  down until you close it. (#71, #156)
- **Estimated time to limit on the taskbar readout** — the burn-rate projection the flyout has
  always shown ("~1h 23m to limit") is now available as a readout element of its own (`~1h 23m`),
  tilde-marked to tell an estimate from the reset countdown's known clock time, and showing a
  neutral `—` rather than a fabricated number when your usage is flat or the window resets first.
  Off by default; it is the third stop on the click-to-cycle ring above, and has its own toggle
  on the Taskbar settings tab. (#71)
- **Anthropic service status in the flyout** — when Claude starts misbehaving, the first
  question is "is it me, or is it down?". ClaudeMon now reads Anthropic's public status page on
  the same schedule it already polls usage, and shows a single coloured line in the flyout —
  `⚠ Anthropic: Partial System Outage` — whenever the page reports anything other than
  operational, using the status page's own wording. Click the line to open the status page.
  While everything is healthy nothing is shown at all: the flyout looks exactly as it did. The
  line also appears in the sign-in-expired state, where "is it me or is it down?" is precisely
  the question a failing sign-in raises. An optional **Notify on Anthropic service incidents**
  setting (Alerts tab, **off** by default) raises one balloon when an incident starts, and
  another only if it gets worse — never one per poll — and respects Snooze like every other
  alert. There is no new timer and no extra polling: the status ride-alongs the usage poll, so
  it also pauses while the workstation is locked. A status page that can't be reached is
  silently ignored — the status of the status page is not itself an alert — and the last known
  state stays on screen. (#132)

### Changed
- **The time-to-limit readout now says `safe` when you'll make it to the reset** — the estimate
  showed the same "—" for three very different situations: you're burning too slowly to hit 100%
  before the window resets (good news), there isn't enough recent history for a projection yet,
  and the trend is flat or declining. Enabling the element mid-session and seeing only a dash
  read as "broken" when it actually meant "you're fine". The reset-beats-projection case now
  shows `safe` on the taskbar readout (and `safe (resets first)` in the flyout's 5-hour line
  where the pace forecast — which already carries the same news for a live window — has no
  window data to draw on), while "—" is reserved for the states with genuinely nothing to
  show. The projection math itself is unchanged. (#158)
- **Usage API requests now identify ClaudeMon.** Every poll of the Anthropic usage endpoint
  previously went out with no `User-Agent` at all; it now sends `ClaudeMon/<version>`, along
  with `Accept: application/json` and the `anthropic-beta: oauth-2025-04-20` header that Claude
  Code attaches to its own OAuth-authenticated calls. Deliberately honest identification rather
  than impersonating the Claude Code CLI — live testing found the endpoint's rate limit is keyed
  on your token, not on who claims to be asking, so there is nothing to gain by pretending.
  Nothing about polling, alerts, or the 401/429 paths changed. (#136)

### Fixed
- **Service-incident notifications no longer repeat after a restart** — the "already told you
  about this incident" state now persists with your settings, so restarting ClaudeMon during a
  long Anthropic incident stays quiet instead of raising the same balloon again. An incident that
  gets worse still notifies, and recovery re-arms it for the next one. (#138)
- **The Settings window can no longer run off the bottom of your screen** — it sized itself
  purely from the tab you were on and grew downwards, so on a short display (or at a large scale
  factor) switching from a short tab to a long one could push OK and Cancel below the desktop
  with no way to reach them. The window now slides up to stay inside the working area of the
  monitor it is on, caps its height there, and scrolls whatever still doesn't fit — and while it
  is scrolling, the mouse wheel scrolls the dialog instead of quietly changing whichever
  dropdown or spinner you last clicked. On a screen where it already fitted, nothing
  changes. (#139)
- **Saving settings can no longer crash ClaudeMon when its settings folder can't be created.**
  The rest of the save path was already best-effort — if the file is locked or unwritable the
  in-memory settings still serve the session and the next save retries — but creating the
  `%LOCALAPPDATA%\ClaudeMon` folder sat outside that guard, so an ACL denial, a
  controlled-folder-access block, or a full disk threw straight out into whichever settings
  dialog or background poll happened to trigger the save. It is now handled the same quiet,
  best-effort way as the write itself. (#145)
- **Saving Settings no longer resets taskbar options the dialog doesn't show** — OK rebuilt the
  taskbar display settings from the controls alone, so anything stored there without a control of
  its own was quietly dropped on every save. It is now layered onto your saved settings, the way
  the notification settings already were. (#143)
- **Settings that fail to load or save now say so in the log.** Those paths are deliberately
  best-effort — a corrupt config quietly becomes defaults, an unwritable one is retried on the
  next save — but they left no trace at all, so "my settings didn't stick" was undiagnosable.
  Both now record the reason and the file involved in the daily log (**View logs** in the tray
  menu). The behaviour itself is unchanged, and a successful save still logs nothing. (#148)

## [0.25.0] - 2026-08-09

### Fixed
- **The taskbar readout no longer floats on top of fullscreen apps** — when a game, a
  fullscreen RDP session, or any other app covers a monitor edge to edge, that monitor's
  readout now hides along with the taskbar it lives on, and returns within about a second of
  the app closing or losing focus. Previously the readout re-asserted itself above
  everything, so a machine with quiet usage could stamp a permanent "0%" over your game (or
  over a fullscreen remote-desktop view of another machine, where it masquerades as the
  remote machine's UI and is genuinely confusing). Readouts on other monitors are unaffected,
  and a maximized window — which leaves the taskbar visible — never hides it. (#123)

## [0.24.0] - 2026-08-04

### Added
- **A cost-over-time chart in the Usage & costs window** — a new **Chart** tab plots what each day
  cost across the timeframe you picked, so you can finally tell steady daily spend from one
  expensive afternoon. The tables answered "where did the money go?" but not "how is it trending?":
  a 30-day total of $400 looks identical whether it was $13 a day or a single $380 Tuesday. Tables
  stays the default tab and the window opens exactly the size it always did — the chart is one
  click away when you want it, and out of the way when you don't. It follows the timeframe
  selector, grows with the window, and days whose cost is a floor because an unpriced model
  contributed are drawn hatched, matching the "≥$x" the tables already show. Everything is
  computed from usage ClaudeMon has already scanned — nothing is re-read and no numbers change.
  (#113)
- **Drill into a model or a project in the Usage & costs window** — select a row in *By model* and
  the project table becomes "Projects using claude-fable-5", showing how that model's spend splits
  across your projects; select a row in *By project* and the model table becomes "Models used in
  C:\Projects\ClaudeMon". Previously the two tables were independent lists: you could see that one
  model cost $12 and one project cost $8, but never how much of the model's spend came from which
  project. The drill-down follows the timeframe you picked, sorts by any column like the main
  tables, and its Total row sums to exactly the row you selected. Selecting the Total row (or
  clicking "Show all", which appears next to the heading) goes back to the full picture. It reads
  the usage data ClaudeMon already keeps, so nothing is rescanned and no numbers change. (#112)
- **Sort the Usage & costs tables by clicking a column header** — click any header to sort by that
  column and click it again to reverse, so you can finally ask "which project burns the most
  cache-write tokens?" or read the list alphabetically instead of always by cost. The two tables
  sort independently, an arrow marks the column each one is sorted by, and the sort sticks when you
  switch timeframe. Sorting is on the real numbers behind the cells, so 1.2M correctly outranks
  900K, and the Total row always stays at the bottom. Both tables still open in cost order, as
  before. (#111)
- **ClaudeMon now has its own app icon** — a clay tile with a white "C". It appears on the
  taskbar button and in Alt-Tab for every ClaudeMon window (update prompts, Settings, About,
  the usage breakdown), on the installer, on the Start-menu shortcut, and in Add/Remove
  Programs. Previously all of these showed the generic Windows placeholder icon, which made an
  open ClaudeMon window impossible to pick out of a crowded taskbar. The tray icon itself is
  unchanged — it's still drawn live from your usage. (#108)

### Fixed
- **The update window no longer hides on a monitor you aren't using** — it now opens centred on
  the monitor holding the window you were last working in, instead of always on the primary
  monitor. On a multi-monitor setup an update prompt could appear on a screen you weren't
  looking at and simply go unnoticed. Placement still never follows the mouse cursor, so the
  behaviour fixed in #88 is preserved; when the foreground window can't be identified (the
  desktop is focused, the window is minimised, or it's one of ClaudeMon's own) it falls back to
  the primary monitor as before. (#108)
- **The update prompt and its download window stay on the same monitor** — clicking "Get the
  update" no longer sends the progress window to a different screen than the prompt you clicked
  it on. (#108)
- **The update window is centred correctly on mixed-DPI multi-monitor setups** — a window moved
  onto a monitor with a different scale factor is resized by Windows *after* its position was
  worked out, which could leave the dialog off-centre or straddling the monitor edge. Placement
  is now re-run until the window is observed to actually be centred where it should be. Same
  class of bug as the flyout fix in #104. (#108)

### Changed
- **The Usage & costs window can now be resized and maximised** — drag any edge or corner and
  both tables grow with it, sharing the extra height between them, while the Model/Project
  column takes up the extra width so long project paths stop being truncated. Previously the
  window was locked to a fixed size with two 150-pixel table viewports, so a month of usage
  across several projects meant scrolling a small box on an otherwise empty screen. The window
  can't be dragged smaller than the point where the table headers and buttons stop being
  usable, and it doesn't yet remember its size between sessions. (#110)
- **Development only — CI now measures code coverage.** Every push and pull request publishes a
  per-class coverage summary, uploads the full HTML report, and fails the build if the logic
  layer (everything except the desktop-bound WinForms code, which can't run headless) drops
  below 85% line / 84% branch. Nothing about the app itself changed. (#102)

## [0.23.0] - 2026-07-22

### Fixed
- **Flyout no longer straddles two monitors** — on multi-monitor setups the usage flyout could
  be drawn half on one monitor and half on the next, especially when the monitors run different
  scale factors (moving onto a differently-scaled monitor resizes the flyout *after* its
  position was computed, pushing it past the edge). Placement is now re-clamped with the final
  size, and the flip-below path — used when the taskbar is at the top — clamps to the bottom
  edge instead of spilling onto the monitor below. The flyout always renders entirely on one
  monitor. (#104)
- **Settings label no longer overlaps its spinner** — on the Taskbar tab, the secondary
  monitor's position label could run underneath its numeric control when Windows renders text
  larger than usual (e.g. the "Make text bigger" accessibility setting). The row is now named
  **Position**, matching the primary row — it only appears under *Show on secondary monitors*,
  which carries the context — and numeric-row labels truncate with "…" instead of ever
  overlapping their control. (#105)

### Changed
- **Taskbar readout clicks open the flyout on that monitor** — clicking the usage readout on a
  secondary monitor's taskbar now opens the flyout right above that readout, instead of always
  jumping to the primary monitor's tray corner. (#104)

## [0.22.1] - 2026-07-19

### Fixed
- **Crash when opening the usage flyout** — clicking the taskbar readout or the tray icon could
  kill the app outright with a `TimeSpan overflowed` error instead of showing usage. The
  time-to-limit projection divides the remaining headroom by the recent usage trend, and on a
  quiet machine that trend is flat to within floating-point error — a slope so close to zero
  that the projection came out as a number too large to represent, which threw. Such a
  projection carries no information anyway, so it is now reported as "no estimate" (`—`) like
  any other case where the trend can't support one. Most likely to have hit a fresh install on
  a lightly-used machine. (#100)

## [0.22.0] - 2026-07-19

### Added
- **Per-model weekly alerts** — the weekly warning now covers **every** weekly bucket the API
  reports, not just the overall 7-day one. Max plans carry a separate weekly cap per model
  (e.g. "Weekly (Fable)") that can run out *before* the overall weekly; until now ClaudeMon
  showed those caps in the flyout but never alerted on them, so a model could lock out with no
  warning. Each bucket is tracked independently — one alert never masks another, each fires
  once per window and re-arms when that bucket resets — and escalates to a critical alert past
  the near-cap percentage or when Anthropic itself flags the bucket critical. Reuses the
  existing **Weekly (7-day) warning at** and **Critical alert near the limit at** settings; no
  new options. (#98)

### Fixed
- **Simultaneous alerts are no longer lost** — Windows shows one balloon at a time, so when two
  alerts fired on the same poll (say the pace warning and a weekly warning) only the last was
  ever seen, while both were marked as already-alerted and never repeated. Alerts raised
  together are now combined into a single notification. (#98)

## [0.21.0] - 2026-07-19

### Added
- **Usage & costs window** — a new tray-menu item opens per-model and per-project
  cost/token breakdown tables for Today / last 7 days / last 30 days, computed locally
  from the Claude Code transcripts (cache reads/writes shown separately, dated model
  variants merged into one row, totals row included), with **CSV export**. Projects are
  shown by their real working-directory path, read from the transcripts' `cwd` field and
  cached locally alongside the usage aggregates — still nothing beyond usage numbers,
  model ids, timestamps, and that path is ever read, and nothing leaves the machine. (#74)
- **Budget alerts** — optional daily and weekly (Mon–Sun) estimated-cost caps in
  Settings → Alerts, with graduated notifications at 50% / 80% / 95%. Each threshold
  fires once per period (surviving restarts), a jump across several thresholds fires only
  the highest, both budgets crossing at once combine into one balloon, and snoozed alerts
  defer rather than vanish. Clearly labeled as estimates at API list prices — ClaudeMon
  monitors, it never blocks anything. (#74)

### Fixed
- **Settings save no longer clears an active snooze** — saving any settings change while
  notifications were snoozed silently discarded the snooze; it now survives. (#74)

## [0.20.0] - 2026-07-19

### Added
- **Local cost & burn-rate estimates in the flyout** — a new line shows what today's Claude
  Code usage would cost at API list prices, e.g. `Today: ~$4.20 · 1.8M tokens · ~$1.10/hr
  (est.)`, with the `$/hr` figure computed over the last 30 minutes so a heavy session's
  burn is visible while it happens. Computed entirely on your machine from Claude Code's
  own transcript files (`~/.claude/projects/**/*.jsonl`) — the same source tools like
  ccusage use — read incrementally (only new bytes per pass; a large existing history is
  skipped past on first scan), with cache reads/writes priced at their separate rates and
  duplicate streaming entries deduplicated. Only token counts, model ids, and timestamps
  are read — never conversation content — and nothing leaves the machine. Costs are
  clearly marked as estimates: local transcripts can't see usage from claude.ai or other
  devices, so these totals are intentionally separate from the rate-limit percentages. A
  model the bundled pricing table doesn't know shows tokens with cost as `—` instead of
  guessing. The line appears only when transcripts exist. (#73)

## [0.19.1] - 2026-07-19

### Fixed
- **Visible progress through the whole update** — on a fast connection the update download
  finished in under a second, so the progress window was a barely-visible flash and the
  silent install that followed showed nothing at all: dead air until ClaudeMon restarted.
  The window now stays up after the download, switching to an **"Installing — ClaudeMon
  will close and restart itself"** state with an activity bar, and disappears only when the
  installer actually restarts the app — continuous feedback from click to relaunch. If the
  installer can't start or aborts, the window says so instead of lingering. (#94)

## [0.19.0] - 2026-07-19

### Added
- **Snooze notifications** — a new tray-menu item quiets alert balloons for 30 minutes,
  1 hour, 3 hours, or until the next 5-hour reset, for when you're deliberately burning
  quota and don't need reminding. Polling and the tray/taskbar readouts keep updating; the
  menu shows the active snooze ("Alerts snoozed — 42m left") with a **Resume alerts** item;
  the snooze survives an app restart. Alerts aren't lost, just held: if you're still past a
  threshold when the snooze ends, that alert fires on the next poll. (#14)

## [0.18.1] - 2026-07-18

### Fixed
- **Dialogs open on the primary monitor** — the update prompt (popped by a background
  timer) appeared on whichever monitor the mouse cursor happened to be on; all app dialogs
  (update, download, About, Settings) now always open centered on the primary monitor,
  where the tray lives. (#88)
- **"View release notes" opens the full releases page** — the update dialog's link now goes
  to the releases index (all versions, newest first) instead of the single offered release,
  so nothing is missed when several versions ship between updates. (#89)

## [0.18.0] - 2026-07-18

### Changed
- **Clickable GitHub link in the About dialog** — About ClaudeMon is now a themed dialog
  (matching the update windows) instead of a plain message box, and the
  github.com/badsonstudios/ClaudeMon line at the bottom is a real link that opens the
  repository in your browser. Same content as before: version, description, and current
  monitor status. (#86)

## [0.17.0] - 2026-07-18

### Added
- **"View release notes" link in the update dialog** — the "new version available" window
  now links to the offered release's GitHub page, so you can read what changed before
  choosing Get / Ignore / Skip. The link opens your browser without closing the dialog.
  (#84)

## [0.16.0] - 2026-07-18

### Added
- **Optional % sign on the taskbar readout** — a new "Show % sign after percentages"
  setting renders the taskbar percentages as `42% · 17%` instead of the compact default
  `42 · 17`, so the readout is self-explanatory at a glance. Off by default (the current
  look is unchanged); Numbers style only, with the live overlay preview reflecting the
  toggle immediately. The waiting/sign-in markers and the reset countdown are unaffected.
  (#80)

## [0.15.0] - 2026-07-18

### Added
- **Smart polling: pause while the workstation is locked** — ClaudeMon no longer polls the
  usage API while your session is locked (previously it kept burning API calls all night and
  showed stale numbers until the next tick after unlock). On unlock it resumes and refreshes
  **immediately**, so the tray icon and readout are current within seconds of sitting back
  down. The daily update check pauses too and, because pausing resets its 24-hour countdown,
  an overdue check now runs on unlock — without this, a machine locked at least once a day
  would never check for updates again after startup. Lock/unlock transitions are logged so
  gaps in the poll log are explainable. (#69)

## [0.14.1] - 2026-07-18

### Fixed
- **Idle 5-hour window no longer shows "resetting..." indefinitely** — when the 5-hour
  window expires and no new Claude usage starts, the API keeps reporting the old, past reset
  time; ClaudeMon rendered that as a perpetual "resetting..." (tooltip/flyout) and "now"
  (taskbar countdown), looking broken for hours. An expired idle window is now shown as a
  distinct state: **"resets on next use"** in the tooltip and flyout, **"idle"** in the
  taskbar countdown. The pace coloring and the bar's time tick no longer treat an idle
  window as 100% elapsed (they fall back to absolute-level coloring with no tick), and pace
  alerts don't fire on the stale reading. A genuine reset moment may briefly show the same
  state and recovers on the next poll. (#61)

## [0.14.0] - 2026-07-18

### Added
- **All quota buckets in the flyout, including per-model weekly caps** — the usage API
  reports more limits than the classic 5-hour/7-day pair; the flyout now renders one bar per
  reported limit. Max plans have a separate weekly cap per model (e.g. **Weekly (Fable)**)
  that can be closer to exhaustion than the overall weekly — previously invisible in
  ClaudeMon, now shown with its own percentage, bar, and reset countdown. Unknown future
  bucket types render generically from the API's own metadata instead of being dropped (and
  are logged once so they're diagnosable). Responses without the new data fall back to
  exactly the old two-bar display. (#67)
- **Severity-tinted bars** — each limit carries Anthropic's own severity judgment; a limit
  flagged *critical* draws its flyout bar red and *warning* at least orange, on top of the
  usual pace/absolute coloring. Severity only ever raises urgency, and is display-only:
  alerts, the tray icon, and the taskbar readout behave exactly as before. (#67)
- **Tray tooltip shows the tightest per-model weekly** — hovering the tray icon now includes
  a compact line like `Fable wk: 84% (2d 3h)` when a model-scoped weekly cap exists. (#67)

## [0.13.0] - 2026-07-18

### Added
- **One-click and fully automatic updates** — **Get the update** (and the tray's **Download
  update** item) now downloads the new version's installer in-app with a progress window,
  verifies it against the release's published SHA-256 checksum, and installs it silently: no
  installer wizard, no SmartScreen/Defender popup (the in-app download carries no
  Mark-of-the-Web), no UAC prompt. ClaudeMon relaunches on the new version and confirms with
  an "updated to vX" notification. A new **Install updates automatically** setting (Updates
  tab, off by default) runs the same flow hands-free when the daily check finds a release.
  If anything fails — download, checksum, installer — ClaudeMon falls back to opening the
  release page as before. (#63)
- Releases now publish a `.sha256` checksum next to the installer; the in-app updater refuses
  to run an installer it can't verify.

### Fixed
- **Updates preserve your "run at Windows startup" choice** — silent updates pin the
  installer's startup task to your current setting, and the interactive installer now defaults
  the checkbox to your actual state on upgrades (and actually disables startup when you untick
  it, which previously left it on). Fresh installs still default to on. (#63)

## [0.12.1] - 2026-07-18

### Fixed
- **Taskbar readout now appears on its own after a reboot** — when ClaudeMon starts with
  Windows, it could launch before Explorer had created the taskbar; the readout then never
  appeared until something poked it (like opening Settings). ClaudeMon now listens for the
  shell's `TaskbarCreated` broadcast and also retries every 2 seconds while no taskbar has
  been found, so the readout shows up within a couple of seconds of the taskbar being
  available — no interaction needed. Explorer restarts and monitor hotplug behave as
  before, and the log now records when taskbar enumeration comes up empty (and when it
  recovers), so future reports are diagnosable from a single boot's log. (#62)

## [0.12.0] - 2026-07-12

### Added
- **"Auto (match taskbar)" text color** — the taskbar readout's label and percentage color
  dropdowns gain an **Auto (match taskbar)** option that picks a contrasting color from the
  Windows taskbar theme: light text on a dark taskbar, dark text on a light one. It re-evaluates
  live when the Windows light/dark mode changes (within a few seconds, no restart), previews
  live in Settings, and the existing fixed presets, the number's **Auto (usage level)** mode,
  and the defaults are all unchanged. (#15)
- **In-app update window with Get / Ignore / Skip-this-version** — when a check finds a newer
  release, ClaudeMon now opens a small themed window (light/dark to match the app theme,
  DPI-aware) instead of the easy-to-miss tray balloon: **Get the update** opens the release
  page, **Ignore** dismisses until the next check, and **Skip this version** stops automatic
  prompts for that exact version — persisted across restarts — until something newer ships.
  A manual **Check for updates** always shows the window, even for a skipped version, and the
  **Download update (vX)…** menu item stays available either way. The daily automatic check,
  the startup check, and the **Check for updates automatically** toggle are unchanged. (#42)
- **Waiting indicator on the taskbar readout** — when no usage reading is available (the app
  just started, or the API is rate limited / erroring before anything was fetched), the taskbar
  readout now shows the "Claude" label with a waiting "…" instead of rendering nothing at all,
  so it's clear the readout is alive and will update. Applies from the moment the readout
  appears (including taskbars added later via monitor hotplug), honours the size, color, and
  position settings, and is replaced by real numbers on the next successful poll. Once a
  reading exists, rate limiting keeps showing the last known numbers, as before; the
  sign-in-expired "—" marker still takes priority. (#56)
- **Position nudge for the primary monitor's taskbar readout** — a new **Position** option on
  the Settings **Taskbar** tab nudges the primary readout left (−) or right (+) in pixels, for
  when the exact tray anchoring crowds something near the clock or you simply want more gap.
  It is independent of the secondary-monitor nudge (now labelled **Secondary position**),
  previews live while the dialog is open, and reverts on Cancel. 0 (the default) keeps the
  exact anchoring, so existing installs are visually unchanged until adjusted. (#39)

### Changed
- **Minimum polling interval raised to 2 minutes** — polling the usage API every minute made
  the refresh fail every other request, so the **Check usage every** setting now starts at
  **2 minutes** instead of 1. An existing 1-minute preference is treated as 2 automatically
  (both live and in the Settings dialog); the other intervals are unchanged.
- **Daily log files with 7-day retention** — diagnostics now write to one file per day
  (`logs/claudemon-YYYY-MM-DD.log`) instead of a single rolling `claudemon.log`, so **View
  logs** opens today's activity rather than a week-long wall of interleaved entries. Files
  older than 7 days are deleted automatically on startup and at midnight rollover — including
  the old `claudemon.log`/`claudemon.log.1` from earlier versions, so existing installs
  converge without a migration. The date rolls at local midnight without a restart, and if
  nothing has been logged yet today, **View logs** opens the most recent day's file. The
  per-file size cap remains as a backstop, and logging stays best-effort: IO failures never
  reach the app. (#54)

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
