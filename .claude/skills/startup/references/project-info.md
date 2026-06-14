# Project Info — ClaudeMon

## Purpose

ClaudeMon is a Windows system tray app for **Claude Max subscribers** who want at-a-glance
visibility into their Claude AI usage. It polls the Anthropic API for 5-hour and 7-day
rate-limit utilization, shows a color-coded tray icon, and fires desktop notifications as
usage approaches configurable thresholds. It reuses the existing Claude Code OAuth token, so
no extra credential setup is required for users who already run Claude Code.

## Status

- **Current phase:** Early release (v0.1.0), actively developed.
- **What works today:** Tray icon with live 5h/7d usage, color thresholds, threshold +
  progressive alert modes, desktop notifications, reset countdown, "Start with Windows",
  settings UI, Inno Setup installer.
- **In progress / next up:** Taskbar usage display with a settings toggle — see issue #3.

## Key documentation

- `README.md` — user-facing overview, install, settings, build instructions.
- GitHub Issues — feature/bug tracking (e.g. #3 taskbar display).

## Links

- **Repo:** https://github.com/badsonstudios/ClaudeMon
- **Releases:** https://github.com/badsonstudios/ClaudeMon/releases
