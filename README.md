# XA Slave

A Dalamud plugin for FINAL FANTASY XIV that automates repetitive multi-character workflows — relogging, world travel, chat announcements, housing checks, and more. Works alongside **XA Database** to collect and push character data hands-free.

- View all our utilities & plugins here: https://aethertek.io/

## Key Features

- **AutoRetainer Tasks** — Run pre-processing, post-processing, bailout handling, sync cadence gates, and optional collection steps from one panel.
- **Save to XA Database** — Push data to XA Database with optional cadence-gated login collection and a built-in task log for collection/save debugging.
- **Auto-Glam Weather** — Configure per-weather glamour plate lists, then randomly apply valid class/job and plate choices when the active weather changes.
- **City Chat Flooder** — Send announcements across selected worlds and cities with looping and delay controls.
- **Xagman** — Coordinate cross-client Tony / Franchise Owner FC trading over TCP peer connections with peer-aware startup, stationary meetups, `Give` / `Take` / `Balance` routing, Tony supplier search matching, standby / waiting-room queue control, safer Dropbox / targeting / timeout recovery, automatic Tony rotation and warning-based completion cleanup, plus run-order/progress UI and relog/travel safeguards. Available in normal Release builds.
- **Monthly Relogger** — Cycle through characters with AutoRetainer integration, XA Database-backed rank and personal-plot visibility, failure highlighting, and optional per-character actions.
- **Prep Logistics** — Relog selected characters, see available main-inventory space, move them to a target world or location, and optionally enable AR multi or logout after the run.
- **Auto-Accept FC Invites** — Monitor for FC invitations, automatically accept them, wait for a configurable period, and optionally leave again through the built-in floater-assist flow.
- **FC Permissions Updater** — Review FC rosters with member-rank and FC-rank visibility before running shared permissions updates.
- **Check Duplicate Plots** — Scan characters for duplicate housing plots and optionally rerun follow-up actions.
- **Return Alts To Homeworlds** — Send characters back to their home worlds with the shared action flow.
- **Refresh AR Subs/Bell** — Refresh workshop and bell interactions with optional prep actions, region filters, a bell-only mode, and safer menu recovery.
- **Window Renamer** — Rename the FFXIV game window with an optional custom title, process-ID prefix, and current-character suffix.
- **Plugin Operations** — Manage plugin startup behavior, verbose task logging, task-menu section state, and other shared XA Slave behavior/settings.
- **Export Data** — Export multi-character tables from AutoRetainer, Lifestream, and XA Database into timestamped TSV or CSV snapshots.
- **Repo List** — Review commonly required custom plugin repositories with installer/settings shortcuts, plugin presence checks, and copy-to-clipboard repo URLs.
- **IPC Calls Available** — Review the IPC integrations XA Slave can talk to, along with cached/live availability checks for supported plugins.
- **Splash Screen** — Return to the default XA Slave landing page with the website, Discord, and first-time guidance without needing the ctrl-click unselect shortcut.
- **Priority Tasks** — Long-running automation tasks share one active-task lock with cross-panel stop controls, pulsing menu status, and clearer DTR visibility.

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
   https://aethertek.io/x.json
   ```

5. Click "Save".
6. Open the plugin installer with `/xlplugins`, go to "All Plugins", and search for **XA Slave**.

## Support

- Discord server: <https://discord.gg/g2NmYxPQCa>
- Open an issue on the relevant GitHub repository for bugs or feature requests.
- [XA Slave Issues](https://github.com/xa-io/XA-Slave/issues)

## License

[AGPL-3.0-or-later](LICENSE)
