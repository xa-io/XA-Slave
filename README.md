# XA Slave

A Dalamud plugin for FINAL FANTASY XIV that automates repetitive multi-character workflows - relogging, world travel, chat announcements, housing checks, and more. Works alongside **XA Database** to collect and push character data hands-free.

- [XA Slave features and usage](https://aethertek.io/plugins/xa-slave.html) · [All utilities and plugins](https://aethertek.io/)

## Key Features

- **Character Automation** - Relog character rosters, prepare travel, return alts home, and run recurring tasks with AutoRetainer integration. Shared task controls provide progress, results, cancellation, and completion actions.
- **FC & Housing Management** - Manage FC invitations and permissions, check housing plots, refresh workshops, bells, and chests, and handle furniture tasks across your characters.
- **Xagman Item Transfers** - Coordinate collection and restocking across characters, worlds, and participating clients. Configure item policies, review stock forecasts, and rotate collectors through fixed-world, Server Matching, or Outside Network Helper runs.
- **XA Mods** - Over 60 searchable quality-of-life mods covering gameplay, UI, graphics, inventory, player utilities, and plugin integrations. Browse categorized settings, filter enabled mods, and save or share presets.
- **Field Operations** - Hunt and track Eureka instances, configure instance alerts, and automate Logos Manipulator work with favorites, recipe selection, and crafting queues.
- **Chat & Notifications** - Send city announcements across selected worlds with timing and loop controls, configure player alerts, and optionally log chat, messages, and emotes to `/xllog` by category, including private chat.
- **Data & Exports** - Save character data to XA Database on demand or during login collection. Export roster information to CSV, TSV, TXT, or JSON, with optional database and Lifestream details.
- **Plugin Tools** - Customize startup behavior and quick actions, browse plugin repositories and the command reference, and access task controls, update history, and IPC integrations.

Open `/xa` to browse the task panels, or `/xa mods` to search the available mods and their settings.

## Feature Directory

Open `/xa` and choose a panel from the left menu. Select characters and review the panel's requirements before starting an automated task; use its progress and cancellation controls while it runs.

<details>
<summary>Tasks, tools, and reference panels</summary>

| Section | Panel | Purpose |
| --- | --- | --- |
| Automated Tasks | AutoRetainer Helper | Configure pre/post-processing around AutoRetainer cycles, data collection, and stuck ship-result menu recovery. |
| Automated Tasks | Save to XA Database | Save the current character's collected data to XA Database. |
| City Shenanigans | Auto-Glam Weather | Apply configured glamour plates for weather conditions and jobs. |
| City Shenanigans | City Chat Flooder | Schedule announcements across selected worlds, with delay, pause, and loop controls. |
| FC Relations | Xagman | Coordinate item collection and restocking; see the guide below. |
| FC Relations | Monthly Relogger | Visit selected characters and perform configured FC and housing maintenance actions. |
| FC Relations | Prep Logistics | Move selected characters to a chosen world and optional location using Lifestream. |
| FC Relations | Auto-Accept FC Invites | Accept invitations, wait, then leave the FC. This task can leave your current FC; review its settings before starting. |
| FC Relations | FC Permissions Updater | Update member-rank permissions across selected FCs; each character must have Master rank. |
| FC Relations | Check Duplicate Plots | Find shared estate/apartment records and refresh housing data to clear stale duplicates. |
| FC Relations | Return Alts To Homeworlds | Relog selected characters and return visiting characters to their homeworlds. |
| FC Relations | Refresh Sub/Bell/Chest | Refresh submarine, retainer, and company chest information. House entry requires Lifestream door-path and enter-house settings. |
| Field Operations | Instance Hunter | Search selected Eureka zones for a different instance, using per-zone baselines and alerts. |
| Field Operations | Logogram Creator | Manage Logos Manipulator recipes, favorites, and crafting queues. |
| Utility | XA Mods | Browse the categorized mod list below, configure options, and manage presets and hotkeys. |
| Utility | Window Renamer | Customize the game window title, with optional process ID and current-character name. |
| Utility | Plugin Operations | Configure startup, window placement, logging, interface behavior, and title-bar favorites. |
| Utility | Plugin Builder | Create custom windows with pages, information panels, and buttons from other plugins; early access. |
| Reference | Export Data | Export character rosters to CSV, TSV, TXT, or JSON, with optional database and Lifestream details. |
| Reference | Repo List | Browse plugin repositories. |
| Reference | IPC Calls Available | Browse integration calls for external tools and plugins. |
| Reference | Commands | Search slash commands and their usage notes. |
| Reference | Splash Screen | View the plugin's introduction screen. |
| Reference | Debug / Test | Access diagnostics when the debug menu is enabled. |

</details>

## XA Mods

Open `/xa mods`, search by name, and select a mod to see its options and status. Use enabled-only filtering, saved presets, and per-mod hotkeys to manage your setup. Availability depends on the current game version and required integrations.

<details>
<summary>Mod list by category</summary>

### Game Mods

| Mod | Purpose |
| --- | --- |
| Allow Multiple Game Instances | Allow additional game clients to launch. |
| Cancel Login Cooldown | Clear the temporary character-select login cooldown. |
| Clean Chat | Hide selected login announcements and recognized RMT advertising. |
| Close Lobby Errors | Confirm supported lobby error dialogs and close matching NoKillPlugin error panels. |
| Custom Mouse | Resize, recolor or replace the game cursor with local artwork. |
| Dalamud Log Cleaner | Archive and clear the active Dalamud log manually or automatically at a configured size threshold. |
| Dalamud Log Disabler | Filters selected plugins' output to Dalamud's log (/xllog window and the on-disk log file) by log level. |
| Display Actual Queue Position | Expands queue displays with position and ETA information. |
| Export Dalamud Logs | Save this client's active Dalamud log to the plugin exports folder. |
| Fix /target Command | Selects the closest targetable actor when the game's `/target` command cannot resolve a visible player or NPC name. |
| Lock Game Window In Combat | Prevents the game window from being moved while the local character is in combat. |
| Prevent Game Exiting From Lobby Errors | Prevent lobby error countdowns from closing the client. |
| PVP Hotbar Disabler | Show, hide, or toggle hotbars in PvP through supported commands. |
| Replace Unowned Mount Hotbars | Use Mount Roulette for hotbar mounts the current character has not unlocked. |
| Skip Cutscenes | Skips standard cutscenes and optional content categories, with per-zone allow/block controls. |
| Skip Dialogue | Automatically advance supported dialogue windows. |

### UI Mods

| Mod | Purpose |
| --- | --- |
| Anonymous Mode | Masks visible player nameplates locally with deterministic `Firstname Lastname` aliases. |
| Auto Display IDs | Adds item, action, target, weather, zone, and map IDs to supported local UI surfaces. |
| Bailout ESC Menu | Close the System menu when it remains open past the configured timer. |
| Better Cast Bar | Restyles the local cast bar and adds a slidecast readiness marker. |
| Better Duty Finder | Adds inline duty-finder setting buttons to Contents Finder and Raid Finder. |
| Better Highlight Potential Targets | Change the potential-target hover highlight color. |
| Copy Item Name For All | Adds inventory context-menu actions to copy base or glamour item names. |
| Custom Timestamp Format | Adds seconds to chat timestamps. |
| Dalamud Notifications Suck | Hides selected Dalamud toast notification categories before they draw. |
| Display MSQ Progress | Expands Scenario Tree with remaining main-scenario count and completion percentage. |
| Display Network Latency | Shows the detected game-server ping in the DTR bar. |
| Enable Item Icon In Shops | Replaces placeholder shop row icons with the actual item icons when the shop addon refreshes. |
| Expanded Player Right-Click Menu Search | Adds FFLogs, Lodestone, and Lalachievements shortcuts to player context menus. |
| Inspect Outfit Try-on | Adds Try On All and Stop to a player inspection and saves outfit history. |

### Graphic Mods

| Mod | Purpose |
| --- | --- |
| Custom Resolutions | Applies saved or typed client sizes from the panel or `/xa res <width>x<height>` / `/xa res <width> <height>`. |
| Disable Background Rendering | Pause background rendering, with periodic refreshes and optional minimized-only behavior. |
| Disable Title Screen Movie | Prevents the title screen idle movie from starting. |
| Hide Game Objects | Locally hides selected object categories, with optional player distance limits and duty and territory guards. |
| Hide Unnecessary Popups | Closes tutorial and recommendation popups as they appear. |
| Ignore Minimum Window Size | Lowers the client minimum window size and re-syncs rendering after restore or maximize. |
| Low Resolution | Reduce the 3D rendering scale. |
| No UI Fade | Suppresses common black, white, and event UI fade transitions. |
| Special Rendering Modes | Reveals world fade and UI visibility tools. Turning this off restores all world/UI surfaces. |

### Player Mods

| Mod | Purpose |
| --- | --- |
| Alert When Typing In Combat | Plays a local sound and toast when ChatLog is focused during combat. |
| Anti-AFK | Refresh the local AFK timer periodically. |
| Auto Busy Status | Uses Busy status to block unsolicited player trades. |
| Auto Duty Commence | Automatically confirms duty commencement prompts. |
| Auto Food Consume | Keeps your selected food's Well Fed buff active when it is safe to use food. |
| Auto Leave Duty | Leaves a completed duty automatically after combat and blocking duty UI states clear. |
| Auto Merge | Merges incomplete main-bag stacks after opening the inventory. |
| Auto Open Moogle Mail | Exposes Moogle Mail cleanup actions on the Letter List. |
| Auto Restore Furniture | Move placed furniture to the storeroom or inventory, or stored furniture to inventory, in batches. |
| Auto Sort Items | Sorts normal inventory and the current armoury chest. |
| Automate Expert Delivery | Automates eligible Expert Delivery hand-ins with confirmation, inventory-progress, and configurable seal-cap checks. |
| Better Company Chest | Adds FC chest defaults, right-click quick move, quantity prompt handling, and exchangeable gil-value display. |
| Better Inventory Mover | Adds configurable-modifier right-click moves between open inventory, retainer, saddlebag, and premium saddlebag windows. |
| Clear Teleportation Lock | Retry teleporting when blocked by the local teleport lock. |
| Custom Sight Distance | Overrides camera distance, angle, FoV, and optional collision limits. |
| Doze & Sit Anywhere | Lets you trigger Sit and Doze from the panel or `/xa sit` / `/xa doze` without nearby furniture. |
| Estate Teleportation Context Menu | Add estate selection to supported player menus, including party members, for friends whose home world is your current world. Off by default; open Social > Friend List to load friends if needed. The game controls destinations and access; the existing Friend List menu is unchanged. |
| Hide Nameplate Status Icons | Hide selected player status icons beside names. |
| Infinite Sprint | Reapplies Sprint only while the local character is moving, with a configurable movement-start delay. |
| Item Commands | Adds `/xa equip <itemId>`. |
| Notify When Friend Is Near | Shows a local XA Slave alert when a configured player appears nearby. |
| Refuse Trade Request | Refuses incoming trade requests automatically. |
| Remove FC Tag | Hide the FC or visitor tag on player nameplates. |
| Reveal Undiscovered Areas | Reveal undiscovered map areas locally. |
| Show Blacklisted Playername In Party | Shows blocked party-list Unknown names as the blocked player name. |
| Show Titles As Playernames | Moves visible player titles into the player name line. |
| Show Traveler World Names | Shows visible wanderer, traveler, and voyager nameplates as Name@HomeWorld. |
| View Single Item | Adds View Single Item to supported player and item menus, with Find item search in the preview window. |
| XA Nearby | Compact nearby player list with direction, distance and optional radar lines. |
| XA Peep | XA target tracker with a small cached list and full history window. |

### Plugin Mods

| Mod | Purpose |
| --- | --- |
| Anonymize Character Lists | Forces screenshot-safe aliases in XA Slave character-list tables. |
| ARealmRecorded All Zones | Allow additional Duty Recorder content types in ARealmRecorded; zones without a Duty Finder entry remain unsupported. |
| Force PeepingTom | Keeps Peeping Tom target tracking active in PvP matches. |
| Plugin Guard | Protect selected plugins from disable commands and unload requests. |
| Teleport Helper | Handles SelectYesNo prompts that ask about using an aetheryte ticket for teleporting. |

### Eureka Mods

| Mod | Purpose |
| --- | --- |
| Field Operations Entry Command | Adds `/xa fe <entry>` for Eureka entries. |
| Instance ID | Shows the live Eureka instance ID while you are inside Anemos, Pagos, Pyros, or Hydatos. |

</details>

## Xagman

Open `FC Relations > Xagman` to coordinate item transfers between **Tony** collectors/suppliers and **Franchise Owner** characters. Configure your characters, meetup location, and item policies, then start the participating clients. Supports fixed-world meetups, Server Matching, character rotation, and collection or restocking runs.

**Outside Network Helper** lets two players coordinate transfers without sharing a peer network. Import each other's rosters, set the same meetup, and use the in-game tell and gil-trade coordination. Both sides need compatible versions; green-value targets are unavailable in this mode.

**Beta:** Monitor Xagman while it runs. It requires AutoRetainer, Lifestream, XA Database, Dropbox, and vnavmesh.

<details>
<summary>How to use Xagman</summary>

### Before you start

Install and enable AutoRetainer, Lifestream, XA Database, Dropbox, and vnavmesh on the participating clients. Use compatible XA Slave versions, confirm that selected characters can reach the meetup, and leave enough inventory space and travel gil. Monitor the run while Xagman is in beta.

### Roles and meetup

| Role | What it does |
| --- | --- |
| **Tony** | Waits at the meetup, receives surplus items, supplies requested stock, and rotates to another selected Tony when needed. |
| **Franchise Owner** | Relogs through selected owner characters, travels to Tony, gives or receives items according to the Shared Item List, and checks the resulting inventory. |

For a **fixed-world** run, choose one meetup world and location for the participating clients. For **Server Matching**, configure a meetup world for each participating data center and a shared location. Tony follows the required routes while owners stay within their own data center.

### Configure item policies

Set the **Franchise Owner Shared Item List** to describe what each owner should give or receive. Amounts apply per character; NQ and HQ are separate selections.

| Policy | Amount greater than zero | Amount `0` |
| --- | --- | --- |
| **Give** | Give up to this many items. | Give all matching stock. |
| **Take** | Receive this many additional items during the run. | Take all matching supply the active Tony can safely offer. |
| **Balance** | Give or receive until the owner holds this amount. | Give all matching stock. |
| **TopUp** | Receive enough to reach this amount, without giving away surplus. | Do nothing. |

For example, an owner holding 30 items with `Balance 100` requests 70. With `Take 100`, that same owner requests 100 additional items. An owner holding 150 with `TopUp 100` keeps all 150.

Ordinary items come from the four main inventory bags; shards, crystals, and clusters use the crystal inventory. Retainers, saddlebags, equipped gear, and other stored items are not ordinary trade stock.

Optional **if Subs** and **if Retainers** policies use AutoRetainer registration. A matching submarine policy takes priority, followed by a matching retainer policy, then the ordinary policy. Unknown registration skips the conditional item group. Connected runs also support green-item seal and FC-credit targets, which apply additional gear protections and are unavailable in Outside Network Helper.

### Start a connected run

1. Open `FC Relations > Xagman` on each client, assign Tony or Franchise Owner roles, and connect the participating peers.
2. Select the characters for each role and configure the meetup or Server Matching destinations.
3. Set the owner Shared Item List. Use the Tony Search Item List when selecting collectors or suppliers by stock.
4. Use **Pull XA Database Info** to refresh character snapshots, then **Select Matching Items** or select characters manually. Review available stock and inventory-space forecasts before starting; forecasts are estimates.
5. Start Tony with **Start Tony (N)**, then use **Start All Peers** to start the connected owners. Watch the task status as characters relog, travel, and trade.
6. Review **Tony Order**, **Franchise Owner Order**, and the task log for completed, failed, skipped, or unfinished characters.

**Prioritize Characters Giving Items First** optionally collects surplus across the connected roster before starting a separate restocking pass. Enable it on every participating Franchise Owner client when using it; mixed settings or incompatible peers block that run. Leave it off for the normal combined collection-and-supply flow.

### Outside Network Helper

Use this mode when two players on separate machines cannot share the peer network. It coordinates through in-game tells and separate gil trades.

1. Enable **Outside Network Helper** on both sides and choose the Tony and Franchise Owner roles.
2. Exchange the selected character rosters through the clipboard, import your partner's roster, and configure the same meetup world and location.
3. Configure owner item policies and select the participating characters. Give Tony stock for any requested supplies and enough free space for collection.
4. Keep at least 5,000 gil on Tony, or the higher configured minimum. Each owner needs at least 2 gil for failure signals.
5. Start both sides. Tony queues nearby owners and invites one with a 1-gil trade. The owner gives collection items, sends its supply request by tell, and confirms with 1 gil. Tony supplies available items and sends a separate final 1 gil. Xagman manages these signals automatically.
6. Watch the results as owners verify their inventory, return home, and advance. Even an empty supply request completes the handshake.

A Tony unable to continue can signal rotation with 2 gil; the owner retains outstanding work for the next Tony. An owner unable to receive more signals failure with 2 gil and advances after returning home. Both sides must use compatible versions. Green-value targets and connected collection-first scheduling are unavailable in this mode.

### Inventory space and rotation

Select multiple Tonys to allow rotation when stock or space runs out. **Sell When Inventory Is Full** can recover space using the configured seller before continuing. In Outside Network Helper, successful selling resumes with the same Tony; otherwise the run can fall back to rotation. Uncertain cleanup stops the run for review.

**Use XA NPC seller (bypass AR sell rules)** sells subaquatic salvage directly and bypasses AutoRetainer's item-selection rules. Leave it off to use AutoRetainer's configured selling rules. Review this choice before enabling automatic selling.

### Stop and review

- **Stop** stops the current client.
- **Stop All Peers** stops connected clients while preserving their results.
- **Stop All Clients and Results** also clears retained Tony and Franchise Owner orders.
- Review retained results and logs before starting a new run or clearing them. A stopped or failed task does not mean every character finished.

Waiting can mean that Xagman is awaiting a meetup, an available Tony, travel completion, or the active trade partner. Check the current task and partner status before intervening. If the run enters Error, inspect the logged reason, resolve the travel, stock, space, or connection issue, and confirm that outstanding travel and trades have stopped before restarting. Inventory reconciliation determines completion; a finished trade animation alone is not proof that every requested item moved.

</details>

## Commands

The in-plugin `Reference > Commands` page contains the full command index and usage notes. Commands are also available through `XASlave.ExecuteCommand` IPC.

<details>
<summary>General</summary>

| Command | Purpose |
| --- | --- |
| `/xa win <name>` | Toggle a saved Plugin Builder window by its configured command name; for example, `/xa win Test1`. |
| `/xa xagman mini` or `/xa xagman m` | Toggle the compact Xagman progress/results window. It displays status without controlling the run. |
| `/xa db subsupplies <days>` | Clear the Dropbox item queue, then queue supplies for four submarines using 9 Ceruleum Tanks and 1.33 Magitek Repair Kits per submarine per day, rounding totals up. For example, `30` requests 1,080 tanks and 160 kits. Accepts positive fractional days; invalid input leaves the queue unchanged. Queues available local stock and reports shortages; without a trade partner, use `/xa db begin` later. |
| `/xa npcsell <subloot&#124;itemId,itemId,...>` | Sell all matching subaquatic salvage from the four main bags to a nearby NPC, independently of AutoRetainer sell rules. Use `subloot` for IDs 22500-22507 or a comma-separated subset; `/xa npcsell stop` stops a manual sale. Stops for gil limits, errors, or lack of progress. |
| `/xa` | Toggle the XA Slave window. |
| `/xa allrestore` | Disable every top-level XA Mod toggle. |
| `/xa commands` | Open `Reference > Commands`. |
| `/xa db <itemId:qty ...>` | Add Dropbox trade items from local inventory, then attempt to start trading. Positive quantities keep the existing "queue up to this many" behavior. A negative quantity means "keep this many and queue the rest" across the item's combined local NQ/HQ count; for example, if item `10155` has 5,000 available, `10155:-1080` queues 3,920 and leaves 1,080. XA retains the existing NQ-first queue order, saturates additive queue totals instead of wrapping, skips a zero-item start, and reports the exact start outcome. |
| `/xa db inv` | Add every eligible item from `Inventory1` through `Inventory4`, then attempt to start trading and report the exact start outcome. |
| `/xa db clear` | Clear the current Dropbox item queue. |
| `/xa db begin` | Start trading the queued Dropbox items: promotes your current player target to focus target if needed, then kicks Dropbox's trade queue. |
| `/xa db request <itemId:qty ...>` | Print the missing quantities still needed locally as a ready-to-run `/xa db ...` command. |
| `/xa db <shortcut>` | Build missing crystal-fill commands with `shards`, `crystals`, `clusters`, `shards+crystals`, `crystals+clusters`, or `shards+crystals+clusters`. |
| `/xa db subloot` | Shortcut for `/xa db 22500:99999 ... 22507:99999` (item IDs 22500-22507); adds those items from local inventory and reports their total vendor gil value in chat. Trading starts when a player is targeted/focus-targeted; without a partner the queue is retained and the result says trading did not start, so `/xa db begin` can be used later. |
| `/xa db subgil <gil-value>` or `/xa dbsub <gil-value>` | Clear the Dropbox queue, then queue a minimum-overflow mixture of locally held subaquatic salvage (item IDs 22500-22507) whose vendor value is the smallest reachable total at or above the positive gil target. The result reports selected item count, value, overflow/shortfall, and the exact start outcome. Invalid input or a clear failure stops additions; without a partner the selected entries remain queued for a later `/xa db begin`. |
| `/xa debug` | Toggle the Debug / Test menu and save its visibility setting; enabling it opens the diagnostics panel. |
| `/xa sort` | Trigger Auto Sort Items > Sort now using saved settings. Requires Auto Sort Items enabled. |
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
| `/xa clearlog` | Archive and clear the active Dalamud log file; keeps up to 10 MiB in the old log. Does not clear the `/xllog` display. |
| `/xa devlogs` | Export the active Dalamud log to the plugin exports folder. |
| `/xa devlogs folder` | Open the plugin exports folder. |
| `/xa hotbar on/off` | Toggle `PVP Hotbar Disabler`; omit on/off for status. Disabling it shows the hotbars it controls. |
| `/xa hotbar <1-10> [on/off/toggle]` | Show, hide, or toggle a hotbar directly while `PVP Hotbar Disabler` is enabled; omit the action to toggle. |
| `/hotbar display <1-10> [on/off]` | Handle blocked game hotbar commands in PvP while `PVP Hotbar Disabler` is enabled. Requires the English unavailable error, login, and a visible main hotbar; omit on/off to toggle. |
| `/xa chocobocutscene on/off` | Toggle `Skip Cutscenes` > `Skip Feeding Chocobo`. |
| `/xa closeerrors on/off` | Toggle `Close Lobby Errors`. |
| `/xa disablelogs on/off` | Toggle `Dalamud Log Disabler`, filtering selected plugins' output to the Dalamud log (/xllog and the log file) by log level (e.g. keep Warning/Error/Fatal, blacklist Info/Debug/Verbose). Expand the Plugin list in XA Mods to access plugin checkboxes, filtering and bulk selection. |
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
| `/xa res <width>x<height>` or `/xa res <width> <height>` | Apply a custom client resolution at or above the guarded `250x200` floor, such as `/xa res 1280 720`. |
| `/xa res reset` | Restore the client size captured when XA first enabled `Custom Resolutions`; the feature remains enabled and saved presets are unchanged. |
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
| `/xa autobusy on/off` | Toggle `Auto Busy Status`; preserves manually set Busy. Omit on/off for status. |
| `/xa autofood on/off` | Toggle `Auto Food Consume`; choose food and NQ/HQ quality in XA Mods first. `/xa food` is an alias; omit on/off for status. |
| `/xa viewitem` | Open `View Single Item` with item search visible. Enable the mod for searching and previews. |
| `/xa outfits [player]` | Open saved outfit history, optionally filtered by player or world. Enable `Inspect Outfit Try-on` and inspect players to save outfits; use Try On or Remove per entry. Keeps the 500 most recently seen outfits. |
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
| `/xa mail` | Open Moogle Mail directly; no mail automation toggle required. |
| `/xa mooglemail on/off` | Toggle `Auto Open Moogle Mail` Letter List actions. |
| `/xa nearby [on/off]` | Show or hide a searchable nearby-player list with distance, targeting, and player actions. |
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
- **XA Database:** [XA Database](https://github.com/xa-io/XA-Database) provides character snapshots for data collection, exports, and Xagman planning.

## This Plugin is in Development

This means that there are still features being implemented and enhanced. Suggestions and feature requests are welcome via GitHub issues or by visiting the Discord server for direct support.

## Installation

Native inventory, furniture, sorting, nearby-player and try-on features require a matching supported game and Dalamud build. Their compatibility checks reject unsupported native bindings.

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
