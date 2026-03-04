# XA Slave

A Dalamud plugin for FINAL FANTASY XIV that automates repetitive multi-character workflows — relogging, world travel, chat announcements, housing checks, and more. Works alongside **XA Database** to collect and push character data hands-free.

## Key Features

- **Monthly Relogger** — Cycle through all your characters with AutoRetainer integration, sorted by Region/DC/World. Includes retry logic, character verification, and per-character data display (levels, Gil, FC, last login).
- **City Chat Flooder** — Travel through selected worlds and cities sending chat announcements. Supports looping, configurable delays, region/DC-based world selection grid, and pre-flight region validation.
- **Check Duplicate Plots** — Scan housing wards across characters for duplicate plot ownership.
- **Return Alts To Homeworlds** — Automatically travel characters back to their home worlds.
- **Save to XA Database** — Push character data to XA Database via IPC.
- **Pre-flight Checks** — Detects game state (cutscene, character select, duty, main menu) before running any task.
- **IPC Provider** — Exposes `XASlave.IsBusy` and `XASlave.RunTask` channels for other plugins to check status and trigger tasks.

## Commands

| Command | Description |
|---------|-------------|
| `/xa`  | Toggle the XA Slave window |

## Dependencies

- **Optional:** [XA Database](https://github.com/xa-io/XA-Database) — For Save to XA Database task and IPC data collection

## This Plugin is in Development

This means that there are still features being implemented and enhanced. Suggestions and feature requests are welcome via github issues or by visiting the discord server for direct support.

## Installation

1. Install [FFXIVQuickLauncher](https://github.com/goatcorp/FFXIVQuickLauncher) and enable Dalamud in its settings. You must run the game through FFXIVQuickLauncher for plugins to work.
2. Open Dalamud settings by typing `/xlsettings` in game chat.
3. Go to the "Experimental" tab.
4. In the "Custom Plugin Repositories" section, paste the following URL: `https://raw.githubusercontent.com/xa-io/MyDalamudPlugins/master/pluginmaster.json`
5. Click "Save".
6. Open the plugin installer with `/xlplugins`, go to "All Plugins", and search for **XA Slave**.

## Support
- Discord server: <https://discord.gg/g2NmYxPQCa>
- Open an issue on the relevant GitHub repository for bugs or feature requests.
- [XA Slave Issues](https://github.com/xa-io/XA-Slave/issues)

## License

[AGPL-3.0-or-later](LICENSE)
