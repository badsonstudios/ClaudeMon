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

## Expectations

- Test the testable layers — `Configuration`, `Monitoring`, `Services`, and pure UI helpers
  like `IconRenderer`. Existing tests cover `ConfigManager`, `UsageMonitor`, `AlertManager`,
  `ClaudeApiClient`, `CredentialReader`, and `IconRenderer`.
- The test project uses `InternalsVisibleTo`, so internal types are fair game to test.
- Test files mirror the type under test: `<Type>Tests.cs`.
- Verify behavior before marking work done; report failures honestly. UI-only changes that
  can't be unit-tested should be exercised by running the app.
