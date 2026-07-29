# XA Slave

A Dalamud plugin for FINAL FANTASY XIV that automates repetitive multi-character workflows - relogging, world travel, chat announcements, housing checks, and more. Works alongside **XA Database** to collect and push character data hands-free.

- View all our utilities & plugins here: https://aethertek.io/

## Key Features

- **AutoRetainer Tasks** - Run pre-processing, post-processing, bailout recovery, startup recovery, sync cadence gates, and optional collection from one panel. Includes a workshop-only FC Chest gil sync to XA Database before AutoRetainer continues.
- **Save to XA Database** - Push character data into XA Database on demand or through cadence-gated login collection, with task logs for save/debug visibility.
- **Auto-Glam Weather** - Pick valid glamour plates from per-weather lists and apply them automatically when the active weather changes.
- **City Chat Flooder** - Send announcements across selected worlds and cities with loop and delay controls.
- **Xagman** - Coordinate automated item transfers between Tony collectors/suppliers and Franchise Owner characters. Supports fixed-world, Server Matching, and Outside Network Helper runs, item policies, forecasts, peer coordination, recovery, and Dropbox trade reconciliation.

   <details>
   <summary>How to use Xagman</summary>

   Open `XA Slave > FC Relations > Xagman` to coordinate repeatable item transfers between
   Franchise Owner characters and one or more Tony characters. Xagman handles relogging, travel,
   partner selection, Dropbox trade queues, inventory reconciliation, Tony rotation, and retained
   results.

   > **Beta:** Do not leave Xagman unmonitored.

   Xagman requires AutoRetainer, Lifestream, XA Database, Dropbox, and vnavmesh.

   ### Roles and item policies

   | Role | Responsibility |
   | --- | --- |
   | **Tony** | Receives owner surplus, supplies owner shortages, and rotates when stock or inventory space runs out. |
   | **Franchise Owner** | Runs the configured Shared Item List against each selected owner character. |

   Ordinary exact-item work treats NQ and HQ separately and uses only `Inventory 1` through
   `Inventory 4`. Retainer, saddlebag, market, equipped, Armoury, and other stored quantities do not
   become ordinary tradable stock.

   | Policy | Amount greater than zero | Amount `0` |
   | --- | --- | --- |
   | `Give` | Give up to that amount from each applicable owner. | Give all matching owner stock. |
   | `Take` | Receive one additional batch of that amount during the run. | Take all matching supply the active Tony can safely offer. |
   | `Balance` | Give or receive until the owner ends at that amount. | Give all matching owner stock. |
   | `TopUp` | Receive only the shortage needed to reach at least that amount; never return surplus. | Do nothing. |

   The same Item ID and quality can have ordinary, `if Subs`, and `if Retainers` policies. A matching
   submarine policy wins first, then a matching retainer policy, then the ordinary fallback. These
   conditions use AutoRetainer registration only; they do not make retainer-held items tradable.
   Unknown registration skips that conditional item group instead of applying a potentially unsafe
   fallback.

   Connected-peer runs can also use `Green Item GC Seals` and
   `Green Item FC Credits / Rank Progress` as aggregate `TopUp` targets. These targets apply stricter
   gear, container, gearset, binding, glamour, materia, and AutoRetainer protection checks and remain
   unavailable in Outside Network Helper.

   ### Run types

   | Run type | Use it when |
   | --- | --- |
   | **Fixed world** | Selected participating clients use one configured meetup world and location. |
   | **Server Matching** | Owners span multiple data centers. Configure one meet world per data center (called a server in the Xagman UI) plus one shared location; Tony sweeps the required routes while each owner stays within its own data center. |
   | **Outside Network Helper** | Two different players on separate machines cannot share the peer network. Exchange selected rosters by clipboard and coordinate the owner-to-Tony transfer through the in-game one-gil start/done handshake. This mode supports `Give` and `Balance` surplus only; it does not supply owners or use collection-first scheduling. |

   Multiple Tonys can rotate as they fill or deplete. Connected Server Matching keeps replacement
   Tonys within the active region, while fixed-world runs use the combined selected pool. Optional
   Tony selling-when-full is limited to supported ARR city or hamlet routes; normal rotation remains
   the fallback.

   ### Basic connected workflow

   1. Assign each client the Tony or Franchise Owner role, then connect the participating peers.
   2. Select the characters and configure either a fixed meetup or Server Matching destination.
   3. Configure the Franchise Owner Shared Item List and, when useful, the Tony Search Item List.
   4. Use `Pull XA Database Info`, then `Select Matching Items` or select the characters manually.
   5. Start Tony with `Start Tony (N)`. At the meetup, Tony signals the connected owners to begin;
      `Start All Peers` can rebroadcast the current run when needed.
   6. Use the peer controls to stop the run or clear retained results, then review the Tony Order and
      Franchise Owner Order output.

   `Prioritize Characters Giving Items First` is an optional, default-off Franchise Owner setting that
   appears with conditional policies. When every participating Franchise Owner client enables it and
   advertises compatible conditional-policy support, Xagman completes one global
   `Give`/`Balance`-surplus collection pass before restarting the roster for
   `Take`/`Balance`-deficit/`TopUp` restocking. All owners off keeps the normal combined flow. Mixed
   preferences, missing conditional policies, or incompatible protocols block startup; a frozen run
   stops safely if an expected peer disappears or goes stale.

   ### Planning, controls, and safety

   - `Pull XA Database Info` refreshes and saves the logged-in character before reloading snapshots.
   While logged out, it uses the last saved data.
   - Forecasts show expected collection space, stock, and shortages. They are advisory; live
   inventory changes and reconciliation decide whether a trade completed.
   - `Add Item` searches the current tradeable Lumina Item sheet. XA Database supplies character
   ownership, stock, AutoRetainer registration, matching, and forecast data.
   - Ctrl+click a character name to send `/ays relog FirstLast@World` for that saved row while Xagman
   and the shared task runner are idle.
   - `Refuse Trades When Idle` optionally reuses XA's Refuse Trade Request protection while preserving
   the saved manual preference around Dropbox auto-accept windows.
   - The normal Xagman `Stop` control stops one client. `Stop All Peers` stops connected clients and
   preserves their results. `Stop All Clients and Results` also clears retained Tony and Franchise
   Owner orders.
   - Routes, destinations, participating peers, and collection-first phases are pinned for the active
   run. Unknown, stale, unreachable, mismatched, or unsafe state fails closed instead of silently
   advancing, rerouting, or treating a coordination message as proof of a trade.

   </details>

- **Monthly Relogger** - Cycle through characters with AutoRetainer support, XA Database rank and plot visibility, optional per-character actions, and shared completion actions. A 300-second login timeout recovers safely and continues if a character cannot load. Results flag failed or unfinished characters, show timing and ETA, and remain available until cleared. Characters missing AutoRetainer data are marked `(not found in AR)` and skipped.
- **Shared Task Completion Options** - Major task panels share one completion footer for `Logout`, `Kill Game`, and `Enable AR Multi Mode`. `Kill Game` uses XA's hard logout and close-client flow even when `Instant Logout` is disabled.
- **Prep Logistics** - Relog selected characters, check main-inventory space, move them to a target world or location, and finish with shared completion actions.
- **Auto-Accept FC Invites** - Accept FC invitations automatically, wait for a configured period, and optionally leave again through the floater-assist flow.
- **FC Permissions Updater** - Review FC rosters with member-rank and FC-rank visibility before applying shared permission updates.
- **Check Duplicate Plots** - Scan characters for duplicate housing plots and optionally rerun follow-up actions with the shared completion flow.
- **Return Alts To Homeworlds** - Send characters back to their home worlds with the shared task action flow.
- **Refresh Sub/Bell/Chest** - Refresh workshop and bell interactions with prep actions, region filters, bell-only mode, safer menu recovery, optional Company Chest gil sync, and shared completion actions.
- **Field Operations** - Eureka tools for instance tracking and Logos Manipulator automation. `Instance Hunter` handles per-zone InstanceID, Rodney controls, duty-ready commence, alerts, and rollover until new instances are found. `Logogram Creator` adds favorites, recipe selection, stock scanning, queue and extraction automation, overlays, a floating cancel control, separate `Static Catalog` and `Live Stock Cache` status, and a `Retry Catalog Load` action when packaged catalog data cannot load.
- **Window Renamer** - Rename the FFXIV game window with a custom title, process-ID prefix, or current-character suffix.
- **Auto Open Moogle Mail** - Queue Letter List actions for taking attachments, deleting opened letters, deleting opened NPC letters, and requesting delivery, with cleanup between letters and an in-window Stop control.
- **XA Mods** - Searchable mod manager with categorized sections, persistent collapse state, enabled-only filtering, bulk disable, presets, clipboard import/export, inline help, and `/xa xamods` navigation.

  <details>
  <summary>XA Mods</summary>

  | Game Mods | UI Mods | Graphic Mods | Player Mods | Plugin Mods | Eureka Mods |
  | --- | --- | --- | --- | --- | --- |
  | Allow Multiple Game Instances | Anonymous Mode | Custom Resolutions | Anti-AFK | Anonymize Character Lists | Field Operations Entry Command |
  | Cancel Login Cooldown | Auto Display IDs | Disable Background Rendering | Auto Duty Commence | ARealmRecorded All Zones | Instance ID |
  | Close Lobby Errors | Bailout ESC Menu | Disable Title Screen Movie | Auto Leave Duty | Force PeepingTom |  |
  | Display Actual Queue Position | Better Cast Bar | Hide Game Objects | Auto Merge | Teleport Helper |  |
  | Fix /target Command | Better Duty Finder | Hide Unnecessary Popups | Auto Open Moogle Mail |  |  |
  | Lock Game Window In Combat | Better Highlight Potential Targets | Ignore Minimum Window Size | Automate Expert Delivery |  |  |
  | Prevent Game Exiting From Lobby Errors | Copy Item Name For All | Low Resolution | Better Company Chest |  |  |
  | Replace Unowned Mount Hotbars | Custom Timestamp Format | No UI Fade | Better Inventory Mover |  |  |
  | Skip Cutscenes | Dalamud Notifications Suck | Special Rendering Modes | Clear Teleportation Lock |  |  |
  | Skip Dialogue | Display MSQ Progress |  | Custom Sight Distance |  |  |
  | Dalamud Log Disabler | Display Network Latency |  | Doze & Sit Anywhere |  |  |
  |  | Enable Item Icon In Shops |  | Infinite Sprint |  |  |
  |  | Expanded Player Right-Click Menu Search |  | Item Commands |  |  |
  |  |  |  | Notify When Friend Is Near |  |  |
  |  |  |  | Alert When Typing In Combat |  |  |
  |  |  |  | Refuse Trade Request |  |  |
  |  |  |  | Reveal Undiscovered Areas |  |  |
  |  |  |  | Show Blacklisted Playername In Party |  |  |
  |  |  |  | Show Titles As Playernames |  |  |
  |  |  |  | Show Traveler World Names |  |  |
  |  |  |  | XA Peep |  |  |

  </details>

- **Plugin Operations** - Manage startup behavior (including Open Plugin on Load and Custom Resolution on Plugin Load, which force-resizes the game window to a saved width/height with an optional Ignore Minimum Window Size sub-option), verbose logging, titlebar favourites, version display, update history, and quick actions such as presets, rendering presets, Sit/Doze, All XA Mods Off, task stop, Xagman disconnect, and Kill Game.
- **Export Data** - Export AutoRetainer, Lifestream, and XA Database tables to timestamped TSV/CSV files or overwrite a fixed path for automation.
- **Repo List** - Review all plugins from the referenced repositories in one sortable table with group, author, plugin status, installer/settings shortcuts, and copy-to-clipboard repo actions.
- **IPC Calls Available** - Check supported IPC integrations, live/cached plugin availability, XA Slave provider channels, and direct examples such as `XASlave.ExecuteCommand("xamods")`.
- **Commands** - Browse the current `/xa` command surface in searchable grouped tables for general commands, XA Mods categories, Dropbox queueing, movement helpers, and item commands.
- **Support Diagnostics** - `/xa debug` reveals the hidden Debug / Test panel for support-guided checks and keeps it visible across reloads until toggled off.
- **Priority Tasks** - Long-running automation tasks share one active-task lock, cross-panel stop controls, pulsing menu status, and clearer DTR visibility.
- **XA Mods Native Hooks** - 50+ local QoL hooks cover multi-instance handling, login/queue cleanup, menu and duty recovery, inventory actions, return/logout shortcuts, rendering, camera controls, teleport-lock recovery, and other client utilities. Startup prioritizes safety hooks, defers heavier work, and restores live rendering, UI visibility, and nameplate privacy on unload.

## Commands

The in-plugin `Reference > Commands` page is the full index for command descriptions and notes. The same XA command surface can also be used over IPC through `XASlave.ExecuteCommand`.

<details>
<summary>General</summary>

| Command | Purpose |
| --- | --- |
| `/xa` | Toggle the XA Slave window. |
| `/xa allrestore` | Disable every top-level XA Mod toggle. |
| `/xa commands` | Open `Reference > Commands`. |
| `/xa db <itemId:qty ...>` | Queue Dropbox trade items from local inventory and start trading. |
| `/xa db inv` | Queue all eligible items from `Inventory1` through `Inventory4` and start trading. |
| `/xa db clear` | Clear the current Dropbox item queue. |
| `/xa db begin` | Start trading the queued Dropbox items: promotes your current player target to focus target if needed, then kicks Dropbox's trade queue. |
| `/xa db request <itemId:qty ...>` | Print the missing quantities still needed locally as a ready-to-run `/xa db ...` command. |
| `/xa db <shortcut>` | Build missing crystal-fill commands with `shards`, `crystals`, `clusters`, `shards+crystals`, `crystals+clusters`, or `shards+crystals+clusters`. |
| `/xa db subloot` | Shortcut for `/xa db 22500:99999 ... 22507:99999` (item IDs 22500-22507); queues those items from local inventory and reports their total vendor gil value in chat. Trading starts automatically when a player is targeted/focus-targeted; otherwise use `/xa db begin`. |
| `/xa debug` | Toggle the hidden Debug / Test menu for support diagnostics; the shown state persists until toggled off. |
| `/xa preset list` | List saved XA Mods presets. |
| `/xa preset load <name>` | Load a saved XA Mods preset, including the supported subsettings captured for the enabled mods. |
| `/xa preset save <name>` | Save the current XA Mods selection and the supported subsettings for the enabled mods as a preset. |
| `/xa updates` | Open the version history window. |
| `/xa xamods` or `/xa mods` | Open `Utility > XA Mods`. |

</details>

<details>
<summary>Game Mods</summary>

| Command | Purpose |
| --- | --- |
| `/xa chocobocutscene on/off` | Toggle `Skip Cutscenes` > `Skip Feeding Chocobo`. |
| `/xa closeerrors on/off` | Toggle `Close Lobby Errors`. |
| `/xa disablelogs on/off` | Toggle `Dalamud Log Disabler`, filtering selected plugins' output to the Dalamud log (/xllog and the log file) by log level (e.g. keep Warning/Error/Fatal, blacklist Info/Debug/Verbose). |
| `/xa gamerestore` | Disable the current Game Mods toggles. |
| `/xa lockcombat on/off` | Toggle `Lock Game Window In Combat`. |
| `/xa logincooldown on/off` | Toggle `Cancel Login Cooldown`. |
| `/xa multiinstance on/off` | Toggle `Allow Multiple Game Instances`. |
| `/xa preventlobbyexit on/off` | Toggle `Prevent Game Exiting From Lobby Errors`. |
| `/xa queueposition on/off` | Toggle `Display Actual Queue Position`. |
| `/xa skipcutscenes on/off` | Toggle `Skip Cutscenes`. |
| `/xa skipdialogue on/off` | Toggle `Skip Dialogue`. |
| `/xa targetfix on/off` | Toggle `Fix /target Command`. |

</details>

<details>
<summary>UI Mods</summary>

| Command | Purpose |
| --- | --- |
| `/xa anonymous on/off` | Toggle `Anonymous Mode`. |
| `/xa castbar on/off` | Toggle `Better Cast Bar` and its slidecast marker. |
| `/xa copyitemname on/off` | Toggle `Copy Item Name For All`. |
| `/xa dalamudnotifs on/off` | Toggle `Dalamud Notifications Suck` for selected Dalamud toast categories. |
| `/xa displayids on/off` | Toggle `Auto Display IDs` for item, action, target, weather, zone, and map IDs. |
| `/xa dutyfinder on/off` | Toggle `Better Duty Finder` inline setting buttons. |
| `/xa highlighttargets on/off` | Toggle `Better Highlight Potential Targets`; waits about 5 seconds plus 30 stable frames and a brief stable hover, then repaints hovered potential-target outlines to the selected native backend color. |
| `/xa latency on/off` | Toggle `Display Network Latency` in the DTR bar. |
| `/xa msqprogress on/off` | Toggle `Display MSQ Progress`. |
| `/xa playersearch on/off` | Toggle `Expanded Player Right-Click Menu Search`. |
| `/xa shopicons on/off` | Toggle `Enable Item Icon In Shops`. |
| `/xa timestampseconds on/off` | Toggle `Custom Timestamp Format` for chat timestamp seconds. |
| `/xa uirestore` | Disable the current UI Mods toggles. |

</details>

<details>
<summary>Graphic Mods</summary>

| Command | Purpose |
| --- | --- |
| `/xa bgpause on/off` | Toggle `Disable Background Rendering`. |
| `/xa customres on/off` | Toggle `Custom Resolutions`. |
| `/xa hidepopups on/off` | Toggle `Hide Unnecessary Popups`. |
| `/xa hideobjects on/off` | Toggle `Hide Game Objects`. |
| `/xa lowres on` | Enable `Low Resolution` with the saved panel scale. |
| `/xa lowres <scale>` | Set and enable `Low Resolution` scale. |
| `/xa lowres off` | Disable `Low Resolution` after forcing the live 3D resolution scale to render once at `1.00` without changing the saved slider value. |
| `/xa minwindow on/off` | Toggle `Ignore Minimum Window Size`; when enabled XA lowers the live minimum to `250x200`, corrects undersized restore or maximize results after the window changes, and when disabled XA restores the normal game minimum floor even if `Custom Resolutions` remains enabled. |
| `/xa nouifade on/off` | Toggle `No UI Fade` for common black, white, and event UI fade transitions. |
| `/xa res <width>x<height>` | Apply a custom client resolution at or above the guarded `250x200` floor. |
| `/xa res add <width>x<height>` | Add a saved custom-resolution button. |
| `/xa res remove <width>x<height>` | Remove a saved custom-resolution button. |
| `/xa resrestore` | Disable the current Graphic Mods toggles. |
| `/xa specialrender on/off` | Toggle `Special Rendering Modes`; UI visibility controls remain available when the optional world-fade helper cannot be resolved, and `Hide Chat` is blocked while AutoRetainer Multi Mode is active. |
| `/xa titlemovie on/off` | Toggle `Disable Title Screen Movie`. |

</details>

<details>
<summary>Player Mods</summary>

| Command | Purpose |
| --- | --- |
| `/xa antiafk on/off` | Toggle `Anti-AFK`; while enabled XA refreshes the local AFK timer every 2 minutes. |
| `/xa companychest on/off` | Toggle `Better Company Chest` page defaults, right-click store/recover moves, quantity prompt confirmation, and the exchangeable-item gil-value display. |
| `/xa dutycommence on/off` | Toggle `Auto Duty Commence`. |
| `/xa equip <itemId>` | Equip an item by ID. |
| `/xa doze` | Trigger Doze Anywhere while `Doze & Sit Anywhere` is enabled. |
| `/xa expertdelivery on/off` | Toggle `Automate Expert Delivery`. |
| `/xa friendnear on/off` | Toggle `Notify When Friend Is Near`; alerts are local XA Slave system/toast messages only. |
| `/xa typingcombat on/off` | Toggle `Alert When Typing In Combat`; warns with a local toast and configurable tone when ChatLog is focused during combat. |
| `/xa inventorymover on/off` | Toggle `Better Inventory Mover`; the quick-move modifier is configurable in the XA Mods panel. |
| `/xa itemcommands on/off` | Toggle `Item Commands`. |
| `/xa leaveduty on/off` | Toggle `Auto Leave Duty` (`/xa autoleaveduty` is also accepted). |
| `/xa automerge on/off` | Toggle `Auto Merge`. |
| `/xa mooglemail on/off` | Toggle `Auto Open Moogle Mail` Letter List actions. |
| `/xa peep [on/off/clear]` | Open XA Peep's small list, toggle its tracker, or clear its stored history; turning XA Peep off also hides the compact window if it is open. Its history window can sort by count, player, last seen, or total time. |
| `/xa playerrestore` | Disable the current Player Mods toggles. |
| `/xa refusetrade on/off` | Toggle `Refuse Trade Request`. |
| `/xa revealmap on/off` | Toggle `Reveal Undiscovered Areas`. |
| `/xa blacklistedparty on/off` | Toggle `Show Blacklisted Playername In Party`; blacklisted party-list `Unknown ##` rows show the matched blacklist name in red local text. |
| `/xa sightdistance on/off` | Toggle `Custom Sight Distance`. |
| `/xa sit` | Trigger Sit Anywhere while `Doze & Sit Anywhere` is enabled. |
| `/xa sitdoze on/off` | Toggle the master `Doze & Sit Anywhere` hook. |
| `/xa sprint on/off` | Toggle `Infinite Sprint`. |
| `/xa sprintdelay <seconds>` | Set the `Infinite Sprint` movement-start delay. |
| `/xa teleportlock on/off` | Toggle `Clear Teleportation Lock`. |
| `/xa titlesasplayernames on/off` | Toggle `Show Titles As Playernames`; prefix titles move before the player name and suffix titles move after it, with optional Honorific custom-title support in XA Mods. |
| `/xa travelerworlds on/off` | Toggle `Show Traveler World Names`; visible Wanderer, Traveler, and Voyager names show `Name@HomeWorld` locally and hide the FC/travel tag, with XA Mods options to disable in duties or add `Name @ HomeWorld` spacing. |

</details>

<details>
<summary>Plugin Mods</summary>

| Command | Purpose |
| --- | --- |
| `/xa peepingtom on/off` | Toggle `Force PeepingTom`. |
| `/xa recordallzones on/off` | Toggle `ARealmRecorded All Zones`, letting ARealmRecorded record every content type (Event, Eureka, Carnivale, and the rest). |
| `/xa teleporthelper on/off` | Toggle `Teleport Helper`; the default No response rejects aetheryte-ticket teleport prompts. |
| `/xa anonchars on/off` | Toggle `Anonymize Character Lists`. |
| `/xa pluginrestore` | Disable the current Plugin Mods toggles. |

</details>

<details>
<summary>Eureka Mods</summary>

| Command | Purpose |
| --- | --- |
| `/xa eurekaid on/off` | Toggle the live `Instance ID` display surface and optional DTR output. Use `Field Operations` -> `Eureka Instance Hunter` for the farming loop. |
| `/xa eurekarestore` | Disable the current Eureka Mods toggles. |
| `/xa fe <entry>` | Queue a supported Eureka `Field Operations Entry Command` entry, such as `/xa fe pagos`: Anemos, Pagos, Pyros, or Hydatos. |
| `/xa fieldentrycommand on/off` | Toggle `Field Operations Entry Command`. |

</details>

<details>
<summary>XA Movements</summary>

| Command | Purpose |
| --- | --- |
| `/xa movingcheatersmart` | Mount only when needed and path to the current map flag with fly/ground selection based on zone flight unlock. |
| `/xa movingcheaterfly` | Mount only when needed and path to the current map flag with flying when available, falling back to ground movement. |
| `/xa movingcheaterwalk` | Mount only when needed and ground-path to the current map flag. |
| `/xa interact` | Interact with the current target. |
| `/xa leaveduty` | Run the direct Leave Duty action; `/xa leaveduty on/off` still controls `Auto Leave Duty`. |
| `/xa recommendedgear` | Open Character, open Recommended Gear, equip the recommendation, then close the related windows. |
| `/xa stopmovement` | Stop the current vnav path. |
| `/xa pathtotargetinteract` | Ground-path to the current target and interact once in range. |
| `/xa pathsmartinteract` | Smart-path to the current target, mount/fly when useful, dismount, and interact. |

</details>

## Dependencies

- **Xagman requires:** AutoRetainer, Lifestream, XA Database, Dropbox, and vnavmesh.
- **XA Database:** [XA Database](https://github.com/xa-io/XA-Database) provides snapshots for Save to XA Database, IPC collection, and Xagman's character ownership, stock matching, AutoRetainer data, and forecasts. Xagman item lookup itself uses Lumina.

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
