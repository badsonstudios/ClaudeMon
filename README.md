# ClaudeMon

A Windows system tray application that monitors your Claude AI usage for Claude Max subscribers. It polls the Anthropic API for 5-hour and 7-day rate limit utilization, displays usage as a color-coded tray icon, and sends desktop notifications when approaching limits.

![System Tray](images/system_tray.png)

The usage percentage can also be shown directly on the taskbar:

![Taskbar usage display](images/taskbar.png)

## Features

- **Real-time usage tracking** - Monitors both 5-hour and 7-day usage windows
- **Color-coded tray icon** - Green, yellow, orange, or red based on current utilization
- **Taskbar usage display** - Optional always-visible percentage on the taskbar, color-coded to match the tray icon (on by default; toggle in Settings). Can also show the 7-day usage alongside the 5-hour one (`5hr / 7day`).
- **Desktop notifications** - Alerts when usage crosses configurable thresholds
- **Two alert modes**:
  - **Threshold** - Warning and critical notifications at set percentages
  - **Progressive** - Notifications every 10% starting from a configurable level
- **Reset countdown** - Shows time remaining until rate limits reset
- **Runs at startup** - Optional Windows startup registration

## Installation

Download the latest installer from the [Releases](https://github.com/badsonstudios/ClaudeMon/releases/latest) page.

The installer will optionally configure ClaudeMon to start with Windows.

### Requirements

- Windows 10 or later
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
- An active [Claude Max](https://claude.ai) subscription with Claude Code configured

### Credentials

ClaudeMon reads your existing Claude Code OAuth token from `~/.claude/.credentials.json`. No additional setup is needed if you already use Claude Code.

## Settings

![Settings](images/settings.png)

| Setting | Description |
|---------|-------------|
| **Check usage every** | Polling interval (1, 3, 5, or 10 minutes) |
| **Warning / Critical thresholds** | One-time notifications at set percentages (5-hour usage) |
| **Progressive alerts** | Recurring notifications every 10% above a starting level (5-hour usage) |
| **7-day warning** | Notification when 7-day (weekly) usage crosses this percentage |
| **Show usage on the Windows taskbar** | Show the usage percentage on the taskbar, next to the clock (on by default) |
| **Also show 7-day usage (5hr / 7day)** | Also display the 7-day percentage next to the 5-hour one, slash-separated (off by default) |
| **Taskbar text colors** | Color of the "Claude" label and the percentage number (presets; the number can stay Auto / usage-level) |
| **Enable desktop notifications** | Master toggle for all alerts |
| **Notify when the rate limit resets** | Notify when your 5-hour limit resets to full capacity |
| **Start with Windows** | Launch ClaudeMon at login |

## Building from Source

```bash
# Clone the repo
git clone https://github.com/badsonstudios/ClaudeMon.git
cd ClaudeMon

# Build
dotnet build

# Run
dotnet run --project src/ClaudeMon

# Run tests
dotnet test
```

### Building the Installer

Requires [Inno Setup 6](https://jrsoftware.org/isdownload.php).

```bash
# Publish and build installer
bash installer/build.sh
```

The installer will be created in the `dist/` folder.

## License

MIT
