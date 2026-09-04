# Testing — ClaudeMon

## Frameworks

- **Unit:** xUnit (`tests/ClaudeMon.Tests`), with `coverlet.collector` for coverage.
- **UI / E2E:** None. The tray icon, flyout, and notifications require a real Windows
  desktop session and are verified manually.

## Running tests

```bash
dotnet test                      # run the whole suite
dotnet test --filter <Name>      # run a subset
```

Run long suites in the background and check results when notified.

## Coverage

CI collects coverage on every push and PR, publishes a per-class summary to the Actions job
summary, uploads the HTML/Cobertura report as the `coverage-report` artifact, and fails the
build if the **logic layer** drops below the gate in `.github/workflows/ci.yml`
(`COVERAGE_MIN_LINE` / `COVERAGE_MIN_BRANCH`, currently 90% line / 87% branch against a
measured 95.4% / 90.4% — re-measure and re-state both numbers together, or the comment in
`ci.yml` and this line drift apart again).

Reproduce it locally — same settings file, same pinned ReportGenerator version as CI:

```bash
dotnet tool restore                                   # once, per clone
dotnet test -c Release --collect:"XPlat Code Coverage" \
            --settings coverage.runsettings \
            --results-directory coverage/raw
dotnet reportgenerator "-reports:coverage/raw/**/coverage.cobertura.xml" \
                       "-targetdir:coverage/report" \
                       "-reporttypes:Html;TextSummary"
cat coverage/report/Summary.txt                       # or open coverage/report/index.html
```

Use `-c Release` — CI does, and a Debug build reports a percent or so lower (more coverable
lines survive without optimisation), so a Debug run can look like a regression that isn't one.

**What the gate covers.** `coverage.runsettings` excludes the desktop-bound code — Forms and
Controls, `TaskbarOverlayManager`/`TaskbarOverlayWindow`, the Win32/registry wrappers
(`SystemTheme`, `TaskbarEnumerator`, `SystemSessionEvents`), and the `Program`/`TrayApplication`
entry points — because none of it can run in a headless test host. Without that filter the
headline number is ~43%, dominated by code nobody can ever cover, and a real regression in the
logic layer disappears into the noise.

What remains is `Configuration`, `Models`, `Monitoring`, `Services`, and the pure UI helpers that
were deliberately extracted out of the forms (`*Layout`, `*Metrics`, `*Placement`, `*Text`,
`IconRenderer`, `Sparkline`, `DpiScale`, …) — the code this project expects to be tested.

Only add a class to the exclusion list if it genuinely needs a live desktop or a message loop.
"It has no tests yet" is not a reason — that is precisely what the gate exists to notice.

One class sits permanently low and is *not* excluded: `ListViewSortIndicator` (~15%). Its guard
clause is unit-tested, but the rest is a conversation with a live comctl32 header control
(`LVM_GETHEADER`, then `HDM_GET/SETITEMW` against the HWND) and needs a realized `ListView`, so
it is verified by using the Usage & costs window. It stays in the measurement deliberately — it
is small, and excluding it would hide any *new* untested code someone adds to it.

**Never declare a `const` whose type comes from WinForms** (`TextFormatFlags`, `AnchorStyles`,
`Keys`, …) — use `static readonly` instead. A `const`'s type is baked into metadata as a constant,
and Mono.Cecil resolves that type when coverlet rewrites the assembly. `System.Windows.Forms` isn't
in the test project's output (it comes from the shared framework), so the resolve throws, coverlet
abandons **the whole of `ClaudeMon.dll`**, and the gate reports **0% / 0%** — not a partial drop.
The failure is silent apart from a warning buried in `--diag` output, and an exclusion filter
cannot save you: the module is rewritten whether or not the offending type is instrumented.
Found the hard way in #113; the diagnosis is `dotnet test --diag:log.txt` then
`grep "Unable to instrument module" log.datacollector.*.txt`.

## Expectations

- Test the testable layers — `Configuration`, `Monitoring`, `Services`, and pure UI helpers
  like `IconRenderer`. Existing tests cover `ConfigManager`, `UsageMonitor`, `AlertManager`,
  `ClaudeApiClient`, `CredentialReader`, and `IconRenderer`.
- The test project uses `InternalsVisibleTo`, so internal types are fair game to test.
- Test files mirror the type under test: `<Type>Tests.cs`.
- Verify behavior before marking work done; report failures honestly. UI-only changes that
  can't be unit-tested should be exercised by running the app.
