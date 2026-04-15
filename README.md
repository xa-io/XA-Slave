# XA Slave

A Dalamud plugin for FINAL FANTASY XIV that automates repetitive multi-character workflows - relogging, world travel, chat announcements, housing checks, and more. Works alongside **XA Database** to collect and push character data hands-free.

- View all our utilities & plugins here: https://aethertek.io/

## Key Features

- **AutoRetainer Tasks** - Run pre-processing, post-processing, bailout handling, sync cadence gates, and optional collection steps from one panel, including a workshop-only `Check FC Chest For Gil` pass that syncs Company Chest gil into XA Database before AutoRetainer continues.
- **Save to XA Database** - Push data to XA Database with optional cadence-gated login collection and a built-in task log for collection/save debugging.
- **Auto-Glam Weather** - Configure per-weather glamour plate lists, then randomly apply valid class/job and plate choices when the active weather changes.
- **City Chat Flooder** - Send announcements across selected worlds and cities with looping and delay controls.
- **Xagman** - Coordinate cross-client Tony / Franchise Owner FC trading with configurable same-PC or LAN hub support, `/xa db ...` Dropbox queueing, `Give` / `Take` / `Balance` / `TopUp` routing, item-list imports and exports, supplier matching with an optional `Ignore Gil in Select Matching Items` filter, standby queue control, optional FC-return cleanup, safer trade recovery, and shared task completion actions. `TopUp` refills owners up to the configured amount without taking any surplus back.
- **Monthly Relogger** - Cycle through characters with AutoRetainer integration, XA Database-backed rank and personal-plot visibility, failure highlighting, optional per-character actions, and shared task completion actions.
- **Shared Task Completion Options** - `Monthly Relogger`, `Prep Logistics`, `FC Permissions Updater`, `Check Duplicate Plots`, `Return Alts To Homeworlds`, `Refresh Sub/Bell/Chest`, and `Xagman` all share the same `Task Options on Complete` footer with `Logout`, `Kill Game`, and `Enable AR Multi Mode`. `Kill Game` always uses XA's hard logout + close-client flow even if `Instant Logout` is disabled in XA Mods.
- **Prep Logistics** - Relog selected characters, see available main-inventory space, move them to a target world or location, and finish through the shared task completion actions.
- **Auto-Accept FC Invites** - Monitor for FC invitations, automatically accept them, wait for a configurable period, and optionally leave again through the built-in floater-assist flow.
- **FC Permissions Updater** - Review FC rosters with member-rank and FC-rank visibility before running shared permissions updates and the shared completion actions.
- **Check Duplicate Plots** - Scan characters for duplicate housing plots and optionally rerun follow-up actions with the shared completion actions.
- **Return Alts To Homeworlds** - Send characters back to their home worlds with the shared action flow and shared completion actions.
- **Refresh Sub/Bell/Chest** - Refresh workshop and bell interactions with optional prep actions, region filters, a bell-only mode, safer menu recovery, a workshop-side Company Chest gil sync into XA Database after the bell path finishes, and shared completion actions.
- **Window Renamer** - Rename the FFXIV game window with an optional custom title, process-ID prefix, and current-character suffix.
- **XA Mods** - Searchable mod manager grouped by category, with persistent collapse state, a live enabled counter, `Show Only Enabled`, `Disable All Mods`, preset save/load, clipboard `Export` / `Import`, inline help, and direct `/xa xamods` navigation.

  | Game Mods | Graphic Mods | Player Mods | Plugin Mods |
  | --- | --- | --- | --- |
  | Allow Multiple Game Instances | Disable Background Rendering | Automate Expert Delivery | Force PeepingTom |
  | Cancel Login Cooldown | Custom Resolutions | Clear Teleportation Lock |  |
  | Close Lobby Errors | Hide Game Objects | Custom Sight Distance |  |
  | Copy Item Name For All | Ignore Minimum Window Size | Doze & Sit Anywhere |  |
  | Display Actual Queue Position | Low Resolution | Infinite Sprint |  |
  | Display MSQ Progress | Special Rendering Modes | Instant Logout |  |
  | Expanded Player Right-Click Menu Search |  | Refuse Trade Request |  |
  | Hide Unnecessary Popups |  | Reveal Undiscovered Areas |  |
  | Live Anonymous Mode |  |  |  |
  | Prevent Game Exiting From Lobby Errors |  |  |  |
  | Skip Cutscenes |  |  |  |
  | Skip Cutscenes Feeding Chocobo |  |  |  |
  | Skip Dialogue |  |  |  |

  Extra controls include section restore commands like `/xa allrestore`, `/xa gamerestore`, `/xa resrestore`, `/xa playerrestore`, and `/xa pluginrestore`, plus slash-command coverage for the shipped top-level XA Mods, custom-resolution shortcuts, and `Infinite Sprint` delay control. `Close Lobby Errors` also covers the stuck-logout `3102` dialog, and `Low Resolution` now temporarily switches unsupported DLSS runtime scaling to AMD FSR while the feature is active so the forced scale continues to apply.
- **Plugin Operations** - Manage plugin startup behavior, verbose task logging, task-menu section state, and other shared XA Slave behavior/settings.
- **Export Data** - Export multi-character tables from AutoRetainer, Lifestream, and XA Database into timestamped TSV or CSV snapshots.
- **Repo List** - Review commonly required custom plugin repositories with installer/settings shortcuts, plugin presence checks, and copy-to-clipboard repo URLs.
- **IPC Calls Available** - Review the IPC integrations XA Slave can talk to, along with cached/live availability checks for supported plugins and the XA Slave provider channels other plugins can call, including `XASlave.ExecuteCommand` for the shipped `/xa` command surface plus simple direct-call examples such as `XASlave.ExecuteCommand("xamods")` shown directly in the XA Slave provider block.
- **Commands** - Review the current XA slash-command surface in compact grouped tables for `General`, `Game Mods`, `Graphic Mods`, `Player Mods`, and `Plugin Mods`, with a top search bar that filters by command text, setting names, descriptions, and notes, shipped per-toggle on/off coverage for the main XA Mods sections, built-in `/xa db ...` Dropbox queue coverage including `inv` main-bag sweeps, and a direct `/xa commands` navigation path into that page.
- **Splash Screen** - Return to the default XA Slave landing page with the website, Discord, and first-time guidance without needing the ctrl-click unselect shortcut.
- **Priority Tasks** - Long-running automation tasks share one active-task lock with cross-panel stop controls, pulsing menu status, and clearer DTR visibility.

- **XA Mods Native Hooks** - Adds multi-instance launch-lock cleanup, character-select login cooldown clearing, Scenario Tree MSQ progress display, actual queue-position ETA hooks, object-hide controls, inventory item-name copy actions, Special Rendering Modes controls, custom sight-distance sliders, teleport-lock recovery, and a checkbox-gated hard logout seam built on the native contents-finder request path.

## Commands

The in-plugin `References > Commands` page is the full source of truth for descriptions and notes. The same XA command surface can also be driven over IPC through `XASlave.ExecuteCommand` by passing the text you would normally enter after `/xa`. The main shipped `/xa` surface includes:

### General

| Command | Purpose |
| --- | --- |
| `/xa` | Toggle the XA Slave window. |
| `/xa allrestore` | Disable every top-level XA Mod toggle. |
| `/xa commands` | Open `References > Commands`. |
| `/xa db <itemId:qty ...>` | Queue Dropbox trade items from local inventory and start trading. |
| `/xa db inv` | Queue all eligible items from `Inventory1` through `Inventory4` and start trading. |
| `/xa db clear` | Clear the current Dropbox item queue. |
| `/xa db request <itemId:qty ...>` | Print the missing quantities still needed locally as a ready-to-run `/xa db ...` command. |
| `/xa db <shortcut>` | Build missing crystal-fill commands with `shards`, `crystals`, `clusters`, `shards+crystals`, `crystals+clusters`, or `shards+crystals+clusters`. |
| `/xa preset list` | List saved XA Mods presets. |
| `/xa preset load <name>` | Load a saved XA Mods preset. |
| `/xa preset save <name>` | Save the current XA Mods selection as a preset. |
| `/xa xamods` | Open `Utility > XA Mods`. |

### Game Mods

| Command | Purpose |
| --- | --- |
| `/xa anonymous on/off` | Toggle `Live Anonymous Mode`. |
| `/xa chocobocutscene on/off` | Toggle `Skip Cutscenes Feeding Chocobo`. |
| `/xa closeerrors on/off` | Toggle `Close Lobby Errors`. |
| `/xa copyitemname on/off` | Toggle `Copy Item Name For All`. |
| `/xa gamerestore` | Disable the current Game Mods toggles. |
| `/xa hidepopups on/off` | Toggle `Hide Unnecessary Popups`. |
| `/xa logincooldown on/off` | Toggle `Cancel Login Cooldown`. |
| `/xa msqprogress on/off` | Toggle `Display MSQ Progress`. |
| `/xa multiinstance on/off` | Toggle `Allow Multiple Game Instances`. |
| `/xa playersearch on/off` | Toggle `Expanded Player Right-Click Menu Search`. |
| `/xa preventlobbyexit on/off` | Toggle `Prevent Game Exiting From Lobby Errors`. |
| `/xa queueposition on/off` | Toggle `Display Actual Queue Position`. |
| `/xa skipcutscenes on/off` | Toggle `Skip Cutscenes`. |
| `/xa skipdialogue on/off` | Toggle `Skip Dialogue`. |

### Graphic Mods

| Command | Purpose |
| --- | --- |
| `/xa bgpause on/off` | Toggle `Disable Background Rendering`. |
| `/xa customres on/off` | Toggle `Custom Resolutions`. |
| `/xa hideobjects on/off` | Toggle `Hide Game Objects`. |
| `/xa lowres <scale>` | Set and enable `Low Resolution`. |
| `/xa lowres off` | Disable `Low Resolution`. |
| `/xa minwindow on/off` | Toggle `Ignore Minimum Window Size`. |
| `/xa res <width>x<height>` | Apply a custom client resolution. |
| `/xa res add <width>x<height>` | Add a saved custom-resolution button. |
| `/xa res remove <width>x<height>` | Remove a saved custom-resolution button. |
| `/xa resrestore` | Disable the current Graphic Mods toggles. |
| `/xa specialrender on/off` | Toggle `Special Rendering Modes`. |

### Player Mods

| Command | Purpose |
| --- | --- |
| `/xa doze` | Trigger Doze Anywhere. |
| `/xa expertdelivery on/off` | Toggle `Automate Expert Delivery`. |
| `/xa instantlogout on/off` | Toggle `Instant Logout`. |
| `/xa killgame` | Hard logout, then close the client. |
| `/xa logout` | Trigger XA's hard logout seam. |
| `/xa playerrestore` | Disable the current Player Mods toggles. |
| `/xa refusetrade on/off` | Toggle `Refuse Trade Request`. |
| `/xa revealmap on/off` | Toggle `Reveal Undiscovered Areas`. |
| `/xa sightdistance on/off` | Toggle `Custom Sight Distance`. |
| `/xa sit` | Trigger Sit Anywhere. |
| `/xa sitdoze on/off` | Toggle `Doze & Sit Anywhere`. |
| `/xa sprint on/off` | Toggle `Infinite Sprint`. |
| `/xa sprintdelay <seconds>` | Set the `Infinite Sprint` movement-start delay. |
| `/xa teleportlock on/off` | Toggle `Clear Teleportation Lock`. |

### Plugin Mods

| Command | Purpose |
| --- | --- |
| `/xa peepingtom on/off` | Toggle `Force PeepingTom`. |
| `/xa pluginrestore` | Disable the current Plugin Mods toggles. |

## Dependencies

- **Optional:** [XA Database](https://github.com/xa-io/XA-Database) - For Save to XA Database task and IPC data collection

## This Plugin is in Development

This means that there are still features being implemented and enhanced. Suggestions and feature requests are welcome via GitHub issues or by visiting the Discord server for direct support.

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
