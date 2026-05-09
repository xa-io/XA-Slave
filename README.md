# XA Slave

A Dalamud plugin for FINAL FANTASY XIV that automates repetitive multi-character workflows - relogging, world travel, chat announcements, housing checks, and more. Works alongside **XA Database** to collect and push character data hands-free.

- View all our utilities & plugins here: https://aethertek.io/

## Key Features

- **AutoRetainer Tasks** - Run pre-processing, post-processing, bailout handling, sync cadence gates, one-time startup recovery after plugin reloads/updates, and optional collection steps from one panel, including a workshop-only `Check FC Chest For Gil` pass that syncs Company Chest gil into XA Database before AutoRetainer continues.
- **Save to XA Database** - Push data to XA Database with optional cadence-gated login collection and a built-in task log for collection/save debugging.
- **Auto-Glam Weather** - Configure per-weather glamour plate lists, then randomly apply valid class/job and plate choices when the active weather changes.
- **City Chat Flooder** - Send announcements across selected worlds and cities with looping and delay controls.
- **Xagman** - Coordinate Tony / Franchise Owner FC trading across same-PC or LAN clients with `/xa db ...` Dropbox queueing, `Give` / `Take` / `Balance` / `TopUp` routing, supplier matching, role-aware trade recovery, and shared task completion actions. Includes item-list import/export, standby queue control, optional FC-return cleanup, and owner-side reconciliation so empty give passes are skipped, `Give N` retries do not loop, and owners are not sent home while give/request work remains. `TopUp` refills owners up to the configured amount without taking surplus back. While Xagman is running, XA Slave requires `Fix /target Command` internally so `/target` recovery works even when the XA Mods toggle is saved off or SimpleTweaks is unavailable, and Xagman sets/clears trade focus targets through Dalamud directly instead of `/focustarget`.
- **Monthly Relogger** - Cycle through characters with AutoRetainer integration, XA Database-backed rank and personal-plot visibility, failure highlighting, optional per-character actions, and shared task completion actions.
- **Shared Task Completion Options** - `Monthly Relogger`, `Prep Logistics`, `FC Permissions Updater`, `Check Duplicate Plots`, `Return Alts To Homeworlds`, `Refresh Sub/Bell/Chest`, and `Xagman` all share the same `Task Options on Complete` footer with `Logout`, `Kill Game`, and `Enable AR Multi Mode`. `Kill Game` always uses XA's hard logout + close-client flow even if `Instant Logout` is disabled in XA Mods.
- **Prep Logistics** - Relog selected characters, see available main-inventory space, move them to a target world or location, and finish through the shared task completion actions.
- **Auto-Accept FC Invites** - Monitor for FC invitations, automatically accept them, wait for a configurable period, and optionally leave again through the built-in floater-assist flow.
- **FC Permissions Updater** - Review FC rosters with member-rank and FC-rank visibility before running shared permissions updates and the shared completion actions.
- **Check Duplicate Plots** - Scan characters for duplicate housing plots and optionally rerun follow-up actions with the shared completion actions.
- **Screenshot-Safe Character Lists** - `Monthly Relogger`, `Prep Logistics`, `FC Permissions Updater`, `Check Duplicate Plots`, `Return Alts To Homeworlds`, `Refresh Sub/Bell/Chest`, and `Xagman` can anonymize visible character and world labels with one shared `Anonymize` toggle. Changing that checkbox in any task list immediately carries across the others, the same setting is exposed as the `Anonymize Character Lists` XA Mod, the mass-character table columns can be resized with the widths preserved through saved table settings, and the scrollable task lists now keep their header rows pinned while you move through long character lists.
- **Return Alts To Homeworlds** - Send characters back to their home worlds with the shared action flow and shared completion actions.
- **Refresh Sub/Bell/Chest** - Refresh workshop and bell interactions with optional prep actions, region filters, a bell-only mode, safer menu recovery, a workshop-side Company Chest gil sync into XA Database after the bell path finishes, and shared completion actions.
- **Field Operations** - Eureka tools for instance tracking and Logos Manipulator automation. `Instance Hunter` provides per-zone baselines, live instance ID display, Rodney controls, duty-ready commence handling, alert previews, and automatic baseline rollover. `Logogram Creator` brings the AutoLogoAction flow into XA Slave with favorites, recipe locks, queue automation, manipulator-aware tabs, favorite overlays, and a floating cancel control that stops the active run and clears queued plates.
- **Window Renamer** - Rename the FFXIV game window with an optional custom title, process-ID prefix, and current-character suffix.
- **Auto Open Moogle Mail** - Provides queued Letter List actions for taking attachments, batch-deleting opened letters, batch-deleting opened NPC letters, and requesting delivery with explicit viewer and confirmation cleanup between queued letters plus an in-window Stop control while a queue is active.
- **XA Mods** - Searchable mod manager grouped by category, with persistent collapse state, enabled-only filtering, bulk disable, presets, clipboard import/export, inline help, and `/xa xamods` navigation.

  | Game Mods | Graphic Mods | Player Mods | Plugin Mods | Eureka Mods |
  | --- | --- | --- | --- | --- |
  | Allow Multiple Game Instances | Ignore Minimum Window Size | Anti-AFK | Anonymize Character Lists | Instance ID |
  | Cancel Login Cooldown | Hide Game Objects | Auto Duty Commence | Teleport Helper | Field Operations Entry Command |
  | Display MSQ Progress | Custom Resolutions | Automate Expert Delivery | Force PeepingTom | Logogram Creator |
  | Disable Title Screen Movie | Disable Background Rendering | Refuse Trade Request |  |  |
  | Auto Display IDs | Low Resolution | Reveal Undiscovered Areas |  |  |
  | Display Network Latency | Special Rendering Modes | Clear Teleportation Lock |  |  |
  | Custom Timestamp Format |  | Auto Leave Duty |  |  |
  | Lock Game Window In Combat |  | Auto Merge |  |  |
  | Notify When Friend Is Near |  | Custom Sight Distance |  |  |
  | Better Cast Bar |  | Doze & Sit Anywhere |  |  |
  | Better Duty Finder |  | Infinite Sprint |  |  |
  | Fix /target Command |  | Item Commands |  |  |
  | Skip Cutscenes |  | XA Peep |  |  |
  | Hide Unnecessary Popups |  | Show Titles As Playernames |  |  |
  | Dalamud Notifications Suck |  | Show Traveler World Names |  |  |
  | Better Highlight Potential Targets |  |  |  |  |
  | Prevent Game Exiting From Lobby Errors |  |  |  |  |
  | Close Lobby Errors |  |  |  |  |
  | Bailout ESC Menu |  |  |  |  |
  | Skip Dialogue |  |  |  |  |
  | Display Actual Queue Position |  |  |  |  |
  | Copy Item Name For All |  |  |  |  |
  | Expanded Player Right-Click Menu Search |  |  |  |  |
  | Live Anonymous Mode |  |  |  |  |
  | Better Inventory Mover |  |  |  |  |
  | Better Company Chest |  |  |  |  |
  | Auto Open Moogle Mail |  |  |  |  |
  | Enable Item Icon In Shops |  |  |  |  |

- **Plugin Operations** - Manage plugin startup behavior, verbose task logging, the version display in the main XA Slave window title, and titlebar favourites. Custom favourites can open panels, load XA Mod presets, toggle XA Mods, drive `Special Rendering Modes` presets, fire `Sit now` / `Doze now`, run `All XA Mods Off`, or stop all automated tasks and disconnect Xagman. Resolution favourites stay dim and print an error unless `Custom Resolutions` is enabled, `Kill Game` auto-enables `Instant Logout` when selected, and `Show Updates` opens the standalone version-history window with the current release notes highlighted.
- **Export Data** - Export multi-character tables from AutoRetainer, Lifestream, and XA Database into timestamped TSV or CSV snapshots, or overwrite the same fixed file path when `Overwrite fixed file path` is enabled.
- **Repo List** - Review commonly required custom plugin repositories with installer/settings shortcuts, plugin presence checks, and copy-to-clipboard repo URLs.
- **IPC Calls Available** - Review the IPC integrations XA Slave can talk to, along with cached/live availability checks for supported plugins and the XA Slave provider channels other plugins can call, including `XASlave.ExecuteCommand` for the shipped `/xa` command surface plus simple direct-call examples such as `XASlave.ExecuteCommand("xamods")` shown directly in the XA Slave provider block.
- **Commands** - Review the current XA slash-command surface in compact grouped tables for `General`, `Game Mods`, `Graphic Mods`, `Player Mods`, `Plugin Mods`, `Eureka Mods`, and more, with a top search bar that filters by command text, setting names, descriptions, and notes, shipped per-toggle on/off coverage for the main XA Mods sections, `/xa equip` coverage behind `Item Commands`, built-in `/xa db ...` Dropbox queue coverage including `inv` main-bag sweeps, and a direct `/xa commands` navigation path into that page.
- **Support Diagnostics** - The Debug / Test panel ships in public builds but stays hidden until `/xa debug` is typed. Once shown, it remains visible across plugin reloads until `/xa debug` is typed again, which lets support request targeted user-side tests without a separate private build.
- **Priority Tasks** - Long-running automation tasks share one active-task lock with cross-panel stop controls, pulsing menu status, and clearer DTR visibility.
- **XA Mods Native Hooks** - 50+ built-in game and player QoL hooks for multi-instance handling, login/queue cleanup, menu and duty recovery, inventory actions, Return/logout shortcuts, rendering, camera controls, teleport-lock recovery, and other local client utilities. Startup keeps safety hooks early, defers heavier hooks and optional data until after core load or first use, and restores live rendering, UI visibility, and nameplate privacy on unload.

## Commands

The in-plugin `References > Commands` page is the full index for command descriptions and notes. The same XA command surface can also be used over IPC through `XASlave.ExecuteCommand`.

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
| `/xa debug` | Toggle the hidden Debug / Test menu for support diagnostics; the shown state persists until toggled off. |
| `/xa preset list` | List saved XA Mods presets. |
| `/xa preset load <name>` | Load a saved XA Mods preset, including the supported subsettings captured for the enabled mods. |
| `/xa preset save <name>` | Save the current XA Mods selection and the supported subsettings for the enabled mods as a preset. |
| `/xa updates` | Open the version history window. |
| `/xa xamods` or `/xa mods` | Open `Utility > XA Mods`. |

### Game Mods

| Command | Purpose |
| --- | --- |
| `/xa anonymous on/off` | Toggle `Live Anonymous Mode`. |
| `/xa companychest on/off` | Toggle `Better Company Chest` page defaults, right-click store/recover moves, quantity prompt confirmation, and the exchangeable-item gil-value display. |
| `/xa chocobocutscene on/off` | Toggle `Skip Cutscenes` > `Skip Feeding Chocobo`. |
| `/xa closeerrors on/off` | Toggle `Close Lobby Errors` for supported disconnect and lobby error popups. |
| `/xa copyitemname on/off` | Toggle `Copy Item Name For All`. |
| `/xa castbar on/off` | Toggle `Better Cast Bar` and its slidecast marker. |
| `/xa dalamudnotifs on/off` | Toggle `Dalamud Notifications Suck` for selected Dalamud toast categories. |
| `/xa displayids on/off` | Toggle `Auto Display IDs` for item, action, target, weather, zone, and map IDs. |
| `/xa dutyfinder on/off` | Toggle `Better Duty Finder` inline setting buttons. |
| `/xa friendnear on/off` | Toggle `Notify When Friend Is Near`; alerts are local XA Slave system/toast messages only. |
| `/xa gamerestore` | Disable the current Game Mods toggles. |
| `/xa highlighttargets on/off` | Toggle `Better Highlight Potential Targets`; waits about 5 seconds plus 30 stable frames and a brief stable hover, then repaints hovered potential-target outlines to the selected native backend color. |
| `/xa hidepopups on/off` | Toggle `Hide Unnecessary Popups`. |
| `/xa inventorymover on/off` | Toggle `Better Inventory Mover`; the quick-move modifier is configurable in the XA Mods panel. |
| `/xa latency on/off` | Toggle `Display Network Latency` in the DTR bar. |
| `/xa lockcombat on/off` | Toggle `Lock Game Window In Combat`. |
| `/xa logincooldown on/off` | Toggle `Cancel Login Cooldown`. |
| `/xa mooglemail on/off` | Toggle `Auto Open Moogle Mail` Letter List actions. |
| `/xa msqprogress on/off` | Toggle `Display MSQ Progress`. |
| `/xa multiinstance on/off` | Toggle `Allow Multiple Game Instances`. |
| `/xa playersearch on/off` | Toggle `Expanded Player Right-Click Menu Search`. |
| `/xa preventlobbyexit on/off` | Toggle `Prevent Game Exiting From Lobby Errors`. |
| `/xa queueposition on/off` | Toggle `Display Actual Queue Position`. |
| `/xa shopicons on/off` | Toggle `Enable Item Icon In Shops`. |
| `/xa skipcutscenes on/off` | Toggle `Skip Cutscenes`. |
| `/xa skipdialogue on/off` | Toggle `Skip Dialogue`. |
| `/xa targetfix on/off` | Toggle `Fix /target Command`; Xagman temporarily requires this fallback while it is running. |
| `/xa timestampseconds on/off` | Toggle `Custom Timestamp Format` for chat timestamp seconds. |
| `/xa titlemovie on/off` | Toggle `Disable Title Screen Movie`. |

### Graphic Mods

| Command | Purpose |
| --- | --- |
| `/xa bgpause on/off` | Toggle `Disable Background Rendering`. |
| `/xa customres on/off` | Toggle `Custom Resolutions`. |
| `/xa hideobjects on/off` | Toggle `Hide Game Objects`. |
| `/xa lowres on` | Enable `Low Resolution` with the saved panel scale. |
| `/xa lowres <scale>` | Set and enable `Low Resolution` scale. |
| `/xa lowres off` | Disable `Low Resolution` after forcing the live 3D resolution scale to render once at `1.00` without changing the saved slider value. |
| `/xa minwindow on/off` | Toggle `Ignore Minimum Window Size`; when enabled XA lowers the live minimum to `250x200`, corrects undersized restore or maximize results after the window changes, and when disabled XA restores the normal game minimum floor even if `Custom Resolutions` remains enabled. |
| `/xa res <width>x<height>` | Apply a custom client resolution at or above the guarded `250x200` floor. |
| `/xa res add <width>x<height>` | Add a saved custom-resolution button. |
| `/xa res remove <width>x<height>` | Remove a saved custom-resolution button. |
| `/xa resrestore` | Disable the current Graphic Mods toggles. |
| `/xa specialrender on/off` | Toggle `Special Rendering Modes`; UI visibility controls remain available when the optional world-fade helper cannot be resolved, and `Hide Chat` is blocked while AutoRetainer Multi Mode is active. |

### Player Mods

| Command | Purpose |
| --- | --- |
| `/xa antiafk on/off` | Toggle `Anti-AFK`; while enabled XA refreshes the local AFK timer every 2 minutes. |
| `/xa dutycommence on/off` | Toggle `Auto Duty Commence`. |
| `/xa equip <itemId>` | Equip an item by ID. |
| `/xa doze` | Trigger Doze Anywhere while `Doze & Sit Anywhere` is enabled. |
| `/xa expertdelivery on/off` | Toggle `Automate Expert Delivery`. |
| `/xa itemcommands on/off` | Toggle `Item Commands`. |
| `/xa leaveduty on/off` | Toggle `Auto Leave Duty` (`/xa autoleaveduty` is also accepted). |
| `/xa automerge on/off` | Toggle `Auto Merge`. |
| `/xa peep [on/off/clear]` | Open XA Peep's small list, toggle its tracker, or clear its stored history; its history window can sort by count, player, last seen, or total time. |
| `/xa playerrestore` | Disable the current Player Mods toggles. |
| `/xa refusetrade on/off` | Toggle `Refuse Trade Request`. |
| `/xa revealmap on/off` | Toggle `Reveal Undiscovered Areas`. |
| `/xa sightdistance on/off` | Toggle `Custom Sight Distance`. |
| `/xa sit` | Trigger Sit Anywhere while `Doze & Sit Anywhere` is enabled. |
| `/xa sitdoze on/off` | Toggle the master `Doze & Sit Anywhere` hook. |
| `/xa sprint on/off` | Toggle `Infinite Sprint`. |
| `/xa sprintdelay <seconds>` | Set the `Infinite Sprint` movement-start delay. |
| `/xa teleportlock on/off` | Toggle `Clear Teleportation Lock`. |
| `/xa titlesasplayernames on/off` | Toggle `Show Titles As Playernames`; prefix titles move before the player name and suffix titles move after it. |
| `/xa travelerworlds on/off` | Toggle `Show Traveler World Names`; visible traveler and wanderer names show `Name@HomeWorld` locally and hide the FC/travel tag, with an XA Mods option to disable in duties. |

### Plugin Mods

| Command | Purpose |
| --- | --- |
| `/xa peepingtom on/off` | Toggle `Force PeepingTom`. |
| `/xa teleporthelper on/off` | Toggle `Teleport Helper`; the default No response rejects aetheryte-ticket teleport prompts. |
| `/xa anonchars on/off` | Toggle `Anonymize Character Lists`. |
| `/xa pluginrestore` | Disable the current Plugin Mods toggles. |

### Eureka Mods

| Command | Purpose |
| --- | --- |
| `/xa eurekaid on/off` | Toggle the live `Instance ID` display surface and optional DTR output. Use `Field Operations` -> `Eureka Instance Hunter` for the farming loop. |
| `/xa eurekarestore` | Disable the current Eureka Mods toggles. |
| `/xa fe <entry>` | Queue a supported Eureka `Field Operations Entry Command` entry, such as `/xa fe pagos`: Anemos, Pagos, Pyros, or Hydatos. |
| `/xa fieldentrycommand on/off` | Toggle `Field Operations Entry Command`. |

### XA Movement Commands

| Command | Purpose |
| --- | --- |
| `/xa movingcheatersmart` | Mount and path to the current map flag with fly/ground selection based on zone flight unlock. |
| `/xa movingcheaterfly` | Mount and path to the current map flag with flying when available, falling back to ground movement. |
| `/xa movingcheaterwalk` | Mount and ground-path to the current map flag. |
| `/xa interact` | Interact with the current target. |
| `/xa leaveduty` | Run the direct Leave Duty action; `/xa leaveduty on/off` still controls `Auto Leave Duty`. |
| `/xa recommendedgear` | Open Character, open Recommended Gear, equip the recommendation, then close the related windows. |
| `/xa stopmovement` | Stop the current vnav path. |
| `/xa pathtotargetinteract` | Ground-path to the current target and interact once in range. |
| `/xa pathsmartinteract` | Smart-path to the current target, mount/fly when useful, dismount, and interact. |


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
