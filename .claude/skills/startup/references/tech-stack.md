# Tech Stack — ClaudeMon

- **Language(s):** C# (nullable enabled, implicit usings)
- **Framework:** .NET 10 / Windows Forms (`net10.0-windows`, `UseWindowsForms`)
- **Build tool:** `dotnet` (MSBuild SDK-style projects)
- **Package manager:** NuGet
- **Database / storage:** None. User settings persist as JSON on disk via
  `ConfigManager`; "Start with Windows" uses the Windows registry `Run` key.
- **Testing:** xUnit (`tests/ClaudeMon.Tests`), with coverlet for coverage.
- **Hosting / deploy:** Desktop app distributed as an Inno Setup 6 installer.
- **CI/CD:** None configured yet (GitHub Actions could be added).

## Key dependencies

- **Windows Forms** — tray icon (`NotifyIcon`), settings form, notifications.
- **System.Text.Json** — settings serialization and Anthropic API responses.
- **Microsoft.Win32 registry** — startup registration.
- Test stack: `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`,
  `coverlet.collector`.

## Local run / build commands

```bash
dotnet build                         # build solution
dotnet run --project src/ClaudeMon   # run the tray app
dotnet test                          # run xUnit tests
bash installer/build.sh              # publish + build the Inno Setup installer (needs Inno Setup 6)
```
