# XA Slave

A Dalamud plugin for FINAL FANTASY XIV that automates repetitive multi-character workflows — relogging, world travel, chat announcements, housing checks, and more. Works alongside **XA Database** to collect and push character data hands-free.

## Key Features

- **Save to XA Database** — Push data to XA Database with optional cadence-gated login collection steps.
- **Monthly Relogger** — Cycle through characters with AutoRetainer integration, retries, failure highlighting, and optional per-character actions.
- **AutoRetainer Tasks** — Run pre-processing, post-processing, bailout handling, sync cadence gates, and optional collection steps from one panel.
- **City Chat Flooder** — Send announcements across selected worlds and cities with looping and delay controls.
- **Check Duplicate Plots** — Scan characters for duplicate housing plots and optionally rerun follow-up actions.
- **Prep Logistics** — Relog selected characters, move them to a target world or location, and optionally enable AR multi or logout after the run.
- **Return Alts To Homeworlds** — Send characters back to their home worlds with the shared action flow.
- **Utility Controls** — Manage plugin startup behavior, task logging, and window renaming.

## Commands

| Command | Description                  |
| ------- | ---------------------------- |
| `/xa`   | Toggle the XA Slave window   |

## Dependencies

- **Optional:** [XA Database](https://github.com/xa-io/XA-Database) — For Save to XA Database task and IPC data collection

## This Plugin is in Development

This means that there are still features being implemented and enhanced. Suggestions and feature requests are welcome via github issues or by visiting the discord server for direct support.

## Installation

1. Install [FFXIVQuickLauncher](https://github.com/goatcorp/FFXIVQuickLauncher) and enable Dalamud in its settings. You must run the game through FFXIVQuickLauncher for plugins to work.
2. Open Dalamud settings by typing `/xlsettings` in game chat.
3. Go to the "Experimental" tab.
4. In the "Custom Plugin Repositories" section, paste the following URL:

   ```text
   https://raw.githubusercontent.com/xa-io/MyDalamudPlugins/master/pluginmaster.json
   ```

5. Click "Save".
6. Open the plugin installer with `/xlplugins`, go to "All Plugins", and search for **XA Slave**.

## Support

- Discord server: <https://discord.gg/g2NmYxPQCa>
- Open an issue on the relevant GitHub repository for bugs or feature requests.
- [XA Slave Issues](https://github.com/xa-io/XA-Slave/issues)

## License

[AGPL-3.0-or-later](LICENSE)
