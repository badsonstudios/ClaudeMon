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
(`COVERAGE_MIN_LINE` / `COVERAGE_MIN_BRANCH`, currently 85% line / 84% branch against a
measured 87.0% / 86.2%).

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

## Expectations

- Test the testable layers — `Configuration`, `Monitoring`, `Services`, and pure UI helpers
  like `IconRenderer`. Existing tests cover `ConfigManager`, `UsageMonitor`, `AlertManager`,
  `ClaudeApiClient`, `CredentialReader`, and `IconRenderer`.
- The test project uses `InternalsVisibleTo`, so internal types are fair game to test.
- Test files mirror the type under test: `<Type>Tests.cs`.
- Verify behavior before marking work done; report failures honestly. UI-only changes that
  can't be unit-tested should be exercised by running the app.
