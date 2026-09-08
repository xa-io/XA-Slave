using System.Collections.Generic;
using System.Numerics;
using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace XASlave.Windows;

/// <summary>
/// Standalone version-history window - displays changelog entries as collapsible tree nodes.
/// Opened via <c>/xa updates</c> or the ⬆ Show Updates toggle in Plugin Operations.
/// </summary>
public sealed class UpdatesWindow : Window
{
    private const string UpdatesWindowTitle = "XA Slave - Updates";
    private static float UiScale => ImGuiHelpers.GlobalScale;
    private static float UiScaleSafe => ImGuiHelpers.GlobalScale;

    private bool firstDraw = true;

    private readonly List<VersionEntry> versions = new()
    {
        new VersionEntry
        {
            Header = "v0.0.0.44 - 2026-09-08",
            Lines =
            [
                "Dropbox Queue / Treasure",
                "- `/xa dbsub <gil>` queues the lowest reachable treasure value at or above the target. Shortages queue all available treasure and report the missing amount; empty inventory does not start a trade.",
                "- Treasure selection uses local NQ and HQ subaquatic salvage. Results show queue entries, item count, selected value, and any overflow or shortfall.",
                "- Gil targets accept plain numbers, commas, or underscores. Invalid, zero, and negative targets show usage without changing the queue.",
                "- Negative `/xa db` quantities queue local stock above the requested reserve. For example, `/xa db 1234:-500` queues stock above 500. NQ and HQ stock are combined, with NQ queued first; a reserve at or above local stock queues nothing. Positive and negative quantities can be mixed.",
                "- Queue entries remain additive. A negative quantity assumes that item was not already queued. A current player target can start trading automatically; otherwise the queue waits for `/xa db begin`.",
                "",
                "Commands / Window Compatibility",
                "- `XASlave.ExecuteCommand` now supports the full `/xa` command surface with accurate `OK:` or `ERROR:` results, including opening the main window, Update History, and treasure queueing.",
                "- `/xa res` accepts spaced dimensions such as `720 300` alongside `720x300`. While Custom Resolutions is enabled, `/xa res reset` restores the client size captured when it was enabled, preserving the toggle and saved presets.",
                "- Window Renamer preserves custom titles, PID prefixes, and character suffixes while supporting XIVWindowResizer. Compatibility problems are shown in the panel, with up to three automatic retries and an Apply Now retry.",
                "- Restore Default temporarily restores the original game-window title.",
                "",
                "Debug / XA Abuse - AutoRetainer",
                "- Added Expand All Retainers, Collapse All Retainers, Expand All Deployables, and Collapse All Deployables. Open AutoRetainer and the relevant tab once before using these controls.",
                "- Added Show Alert Deployables Only for unused submarine slots, unchecked submarines on characters enabled for automation, enabled submarines not on voyages, and suboptimal submarine builds.",
                "- Added Show Only Enabled and Show Only Disabled for characters on both tabs. Disabled also includes characters with an unchecked retainer or submarine, so partially selected characters can appear in either view.",
                "- Added Show Missing FC Address for Deployables characters without an FC-house registration in Lifestream. A private-house registration alone still counts as missing an FC address.",
                "- Alert and Missing FC Address filters offer refresh controls; Show All Characters restores the full list. Filters affect the display only and preserve character settings and automation.",
                "",
                "Xagman Recovery / Results",
                "- Relog retries now run preflight before another attempt. Standby rotation signals apply only to the interrupted Tony, preventing delayed signals from rotating its replacement.",
                "- Update all participating Tony and Franchise Owner clients together; older clients cannot use the revised standby-rotation coordination.",
                "- Logs and character results survive internal sequences, region changes, Tony rotations, and standby resumes. Notices explain when history is cleared or older entries are omitted.",
                "- Added regional roster logging, login attempts, confirmed successful logins, and specific character failure reasons.",
                "- Insufficient teleport gil and unattuned destinations are detected immediately, recorded, and sent through failure recovery. Missing FC housing remains normal behavior.",
                "- Live AutoRetainer roster checks promptly reject confirmed missing login targets. Unavailable or unreadable roster data retains normal retry handling.",
                "- Improved Tony failure recovery and replacement selection within the active Server Matching region, with visible errors when safe recovery or an eligible replacement is unavailable.",
                "- Improved Xagman reconciliation and Outside Network Helper (ONH) cancellation handling so cancelled or halted subtasks are recorded as unsuccessful. Recovery may still encounter issues; monitor character results.",
                "",
                "Database / Export Data",
                "- Corrected Treasure, Repair Kits, and Ceruleum Tanks totals from XA Database inventories. Manual database pulls now wait for a fresh, confirmed save before reloading.",
                "- Export Data produces proper JSON and extension-appropriate delimited output, rejects unsupported extensions, and asks before overwriting an existing file.",
                "",
                "Tasks / Saved Settings",
                "- Monthly Relogger stops or marks characters incomplete when required duty-exit or homeworld-return steps fail.",
                "- Refresh Sub/Bell/Chest options and FC Floater timing settings persist across reloads.",
                "- Improved Dropbox queueing, failed trade-start reporting, IPC handling, disconnected-client handling, and task cancellation.",
                "- Task starts are rejected when another task is running or there are no steps to perform, preserving current work and auto-collection.",
                "",
                "Reference / Debug Diagnostics",
                "- Debug builds now show the Debug / Test menu automatically, without first using `/xa debug`.",
                "- Added Reference > IPC Calls > Dalamud Client State diagnostics for login/zone flags and logout type/code.",
                "- Added Debug > XA Abuse > Dalamud DLL Bypass Checker as a read-only local diagnostic.",
                "- Added optional logging of delivered chat, system/error messages, and emotes to `/xllog`, including message type, sender, text, and handled state. Enable Log Chat, Messages and Emotes to /xllog in Plugin Operations; it defaults off, applies immediately, and saves across reloads. Xagman error detection remains active when logging is off.",
                "",
                "Reliability / Bundled Data",
                "- Improved configuration upgrades, handling of invalid or newer configurations, saving settings, plugin startup, shutdown, and cleanup.",
                "- Improved stability when accessing game data and windows, reduced work that can stall the interface, and contained panel errors within the affected panel.",
                "- Eureka Instance Hunter's Use Current Zone reads the instance ID in the background and reports progress or failure.",
                "- Fixed Eureka Instance Hunter duty exits getting stuck at the duty menu or confirmation. It now uses the same Leave action as Player State Checker (D), waits for the confirmation, and continues only after leaving the duty.",
                "- Auto Refuse Trade reports partial availability and retries unavailable functionality when re-enabled.",
                "- Preserved the bundled Eureka Logogram Creator catalogs for Logograms, item contents, and Logos Actions.",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.43 - 2026-08-01",
            Lines =
            [
                "XA Mods",
                "- Disable All Mods is now locked and grayed out until Ctrl is held. Its tooltip explains that Ctrl+click is required before clearing every enabled XA Mod.",
                "",
                "Player Mods & Debug",
                "- Added Leave Duty Quick beside the existing Debug control, with controller support and confirmation of the leave-duty prompt.",
                "- Auto Leave Duty now supports controller input while retaining its saved settings, selected delay, safety checks, and status.",
                "",
                "Window & Plugin Compatibility",
                "- Window Renamer now keeps the exact native FINAL FANTASY XIV title while XIVWindowResizer is loaded, visibly pauses only the live rename, and reapplies the user's unchanged custom/PID/character title after XIVWindowResizer unloads.",
                "",
                "Moogle Mail & IPC",
                "- IPC Calls live pulls now show the same XASlave.IsBusy state exposed to other plugins, including the full pending Auto Open Moogle Mail operation.",
                "- The Letter List overlay's Take all action now honors the saved Delete all when finished option, matching Claim Attachments in XA Mods.",
                "",
                "Xagman",
                "- Added Select Current Character to both Xagman role tables. It selects the logged-in character without clearing other selections or changing filters.",
                "- Prioritize Characters Giving Items First now applies only to Franchise Owners with submarine- or retainer-specific item policies. Standard policies retain the normal combined trade flow.",
                "- HQ selection is available only for items that support HQ. Invalid HQ selections in saved or shared orders are rejected.",
                "- Elemental shards, crystals, and clusters now use the player's dedicated Crystals inventory for live counts, XA Database matching, forecasts, Dropbox supply, finite-Take baselines, and post-trade reconciliation. Every non-crystal exact item remains limited to Inventory 1-4.",
                "- Crystal capacity uses each element's dedicated 9,999-unit pouch slot and does not consume or advertise main-bag slots.",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.42 - 2026-07-28",
            Lines =
            [
                "Eureka Logogram Creator",
                "- Eureka Logogram Creator includes its required catalogs in stable and testing packages and reports missing, invalid, or empty catalog data.",
                "- Added separate Static Catalog readiness and load-error reporting. Recipes and Logos Actions no longer look silently empty after a catalog failure, and Refresh All Pages or Retry Catalog Load can retry without restarting XA Slave.",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.41 - 2026-07-28",
            Lines =
            [
                "Patch 7.55 Beta",
                "- Eureka Logogram Creator handles unavailable game data safely when reading Logogram and Logos Action stock.",
                "",
                "Game & Player Mods",
                "- Added Replace Unowned Mount Hotbars, disabled by default. Unowned mount shortcuts use Mount Roulette without changing saved hotbars or owned mount shortcuts.",
                "- Added opt-in Alert When Typing In Combat for the focused ChatLog: local multi-beep and toast alerts with 30-second/exact cooldown controls, 16 pitches, volume, 1-10 beeps, preview, saved-list/startup restore, and `/xa typingcombat on|off`; it sends no chat.",
                "- Improved combat window locking, object hiding, and shop overlays. Fixed dialogue skipping after toggles, Sit/Doze options, and leave-duty confirmation; reduced background work when chat rendering is disabled.",
                "",
                "Xagman Policies & Coordination",
                "- Shared Item policies support Give, Take, Balance, and TopUp with default, submarine, or retainer conditions. Submarine rules take priority over retainer rules, then defaults. Unknown AutoRetainer registration blocks those conditional policies; available stock is limited to Inventory 1-4.",
                "- Added Green Item GC Seals and Green Item FC Credits / Rank Progress targets. Safe green gear must pass trade, binding, collectability, glamour, materia, gearset, AutoRetainer, container, and peer checks; seals use expert-delivery rewards, while FC value is item level x1.5 NQ or x3 HQ.",
                "- Green-item targets share Tony supply, reserve exact-item requests first, and can contribute to both value targets. Saved exact-item requests are preserved when upgrading, and participating clients must use compatible versions. Green-item targets do not yet work reliably and are not supported by Outside Network Helper.",
                "- Prioritize Characters Giving Items First collects surplus items before distributing requested items. All participating Franchise Owners must enable the option; disabling it retains the normal trade flow.",
                "- Collection and restocking stop when participating clients are unavailable, incompatible, cancelled, or have conflicting settings. Peer status and forecasts show collection priority and expected stock after collection.",
                "- Ctrl+click either character table relogs the exact saved character through `/ays relog` only while Xagman and the shared task runner are safe and idle; anonymization never changes the command key.",
                "- Select Matching Items uses Inventory 1-4 only. Retainers Only, Subs Only, Without Retainers, and Without Subs refresh AutoRetainer registration and preserve the established item-need, Region, Search, and Tony visibility rules without counting retainer stock.",
                "- Added Refuse Trades When Idle, disabled by default. It allows Xagman trades while preserving the user's trade-refusal preference when a run ends, stops, or fails.",
                "",
                "Xagman Inventory, Forecasts & Results",
                "- Stop All Clients and Results stops local and connected clients and clears retained orders; Stop All Peers remains stop-only. Add Item searches tradable items and uses each row's HQ selection.",
                "- Tony and Franchise Owner tables gained saved optional columns with clear chooser labels plus sortable Treasure, Kits, Tanks, Retainers, and Submarines values; hiding columns is presentation-only.",
                "- Pull XA Database Info performs logged-in Refresh + Save before rereading committed snapshots, while logged-out pulls use saved data. Unavailable, skipped, failed, pending, malformed, or wrong-character saves are reported and never labeled fresh.",
                "- Connected capacity forecasts separate Server Matching regions or use one fixed-world pool, calculate stack size/partial-stack headroom, distinguish single-item exact capacity from multi-item shared slots, retain low-incoming and unknown/stale warnings, and require real snapshots no more than 45 days old.",
                "- Improved Give, Balance, and Take forecasts, including shared supply, each owner's remaining need, receive capacity, and cached information before connecting or using ONH.",
                "- Finite Take requests track partial deliveries across Tony rotations and complete only after the requested quantity is received. Take 0, Balance, TopUp, and green-item targets retain their existing behavior.",
                "- Tony supply is labeled Need from Tony Pool and shows availability per Tony. Normal completion reports to peers, returns to the FC if configured, and disconnects before logout, game-close, or AutoRetainer actions. Collection-first runs stay connected until all participating owners acknowledge completion.",
                "",
                "Xagman Travel & Server Matching",
                "- Server Matching adds per-data-center meet worlds and a shared location, sweeps Aether/Crystal/Dynamis/Primal then NA/EU/JP/OCE, rotates multiple Tonys within a region, keeps owners on world travel only, marks exhausted-region owners skipped, retains results, and requires the same XA Slave version.",
                "- Collection and resupply handoffs stay within the active data center. Empty replacements rotate immediately; stocked replacements publish their location and wait up to 600 seconds for the owner.",
                "- Server Matching waits for a confirmed destination and verifies the character, world, and aetheryte before advancing. Owners can wait up to 600 seconds for a destination to become available.",
                "- Compound World-plus-aetheryte commands remain in flight through early idle, casting, loading, zoning, and missing player state: Tony gets up to 600 seconds for the world followed by a fresh 60-second local-teleport window without overlapping retries.",
                "- Travel eligibility uses each character's home world. NA/EU/JP characters can travel within their region or to OCE; OCE characters remain within OCE. Unknown or unreachable routes are rejected.",
                "- Expected logouts during cross-data-center travel and FC returns no longer trigger incorrect disconnect recovery.",
                "- Unresolved owners now finish with accurate skipped summaries instead of misleading Tony-finished lines; same-Tony return after selling can be accepted once the old call clears.",
                "",
                "Monthly Relogger, Tasks & Diagnostics",
                "- Monthly Relogger adds a 300-second login timeout, safe pre-flight recovery, red login failures, purple incomplete processing, persistent results with Clear results, AutoRetainer not-found markers, per-character duration, rolling average, remaining count, and ETA.",
                "- Refresh Sub/Bell/Chest and FC Permissions verify the intended character after relog, preventing tasks from running on the wrong character. Progress reporting and reload cleanup are more reliable.",
                "- Xagman rejects oversized or malformed peer messages. LAN hub connections do not authenticate peers.",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.40 - 2026-06-27",
            Lines =
            [
                "Plugin Operations",
                "- New `Custom Resolution on Plugin Load` option (under `Open Plugin on Load`) force-resizes the game window to a saved width and height each time the plugin loads.",
                "- The width, height, and the `Ignore Minimum Window Size` sub-option stay greyed out until the feature is enabled; the sub-option lowers the client minimum so sizes below 1024x720 hold instead of snapping back.",
                "- Resize on plugin load works without separately enabling Custom Resolutions in XA Mods.",
                "",
                "Game Mods",
                "- Skip Dialogue also skips Craft Leve turn-in dialogue.",
                "- `Close Lobby Errors` now catches lobby error `2021` (`World data could not be obtained. Please try logging in later.`).",
                "- `Auto Open Moogle Mail` now reports busy through `XASlave.IsBusy` while it claims attachments, so external automation can wait for mail collection to finish.",
                "- `Auto Open Moogle Mail` adds a `Delete all when finished` sub-option that automatically deletes all opened letters once Claim Attachments finishes collecting everything.",
                "",
                "Xagman",
                "- Franchise Owners now begin relogging and travelling to the meet location as soon as Tony advertises it, instead of waiting for Tony to reach the spot first, so they are already standing nearby when Tony calls ready.",
                "- When every Franchise Owner is relogging and none are ready to trade, Tony now uses the idle window to sell its inventory (when `Sell When Inventory Is Full` is enabled). If Tony hits the gil cap during idle selling, it runs the normal full-inventory rotation: return home (if selected), relog the next Tony, travel back to the meet location, and resume.",
                "- `Select Matching Items` now accounts for gil: Balance selects characters above or below the target, Give selects characters holding at least 1 gil when a give amount is set, Take selects characters when a non-zero amount is set, and TopUp selects only characters below the target. Gil is no longer ignored by default.",
                "- Connected Xagman clients now share which XA Slave version they are running as part of their peer presence.",
                "- If a connected, active Xagman client is running a different XA Slave version, the local Xagman run now halts automatically so out-of-sync clients do not trade against each other.",
                "",
                "Dalamud 15.0.2.2 Compatibility",
                "- Updated bundled SQLite dependencies to address a known vulnerability.",
                "- Eureka Logogram Creator overlay buttons no longer click through to the game controls behind them.",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.39 - 2026-06-04",
            Lines =
            [
                "Movement Commands",
                "- `/xa movingcheatersmart`, `/xa movingcheaterfly`, and `/xa movingcheaterwalk` now check whether the player is already mounted before sending Mount Roulette.",
                "- Already-mounted players now path directly to the current map flag instead of being dismounted before `/vnav flyflag` or `/vnav moveflag` runs.",
                "- Debug MovingCheater buttons behave consistently with the matching chat commands.",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.38 - 2026-06-03",
            Lines =
            [
                "Player Mods",
                "- Added optional Honorific support to `Show Titles As Playernames`.",
                "- The new `Support Honorific` sub-option is enabled by default and reads Honorific's resolved custom title through IPC before falling back to the native title line.",
                "- Empty Honorific titles stay empty, so XA does not re-add the default game title when Honorific is hiding or overriding the title.",
                "- Turning off XA Peep now also hides the compact XA Peep window if it is open.",
                "- IPC Calls Available and Debug `Check All IPC` now include Honorific availability.",
                "",
                "Dalamud API 15.0.2",
                "- Updated Eureka Logogram Creator for current Dalamud compatibility.",
                "- Automatic Export Data writes run in the background to reduce game stutters.",
                "- Improved startup of saved XA Mods, including chat timestamps, UI fades, queue position, cutscene skipping, sight distance, multiple game instances, and lobby-error controls.",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.37 - 2026-05-15",
            Lines =
            [
                "Xagman",
                "- Added `Sell When Inventory Is Full` for Tony runs.",
                "- When Tony's inventory fills in a supported meet zone, XA travels to the local vendor, waits for AutoRetainer to finish selling, and resumes Xagman.",
                "- After Tony sells items, resumed Franchise Owners path to Tony's vendor coordinate with vnav stop distance `2` before targeting Tony and starting the normal trade path.",
                "- Standby owners stay visible in Tony's queue while moving to that vendor coordinate, so Tony can call the first owner that reaches the sell location.",
                "- Owners now enter Queue Wait as soon as they reach Tony's sell coordinate, so they do not stay stuck as generic Traveling peers before Tony can call them.",
                "- Owner-side Tony lookup now accepts Tony's active sell-location peer and includes queued Paused owners in Tony's first-come queue.",
                "- Owners now publish their queue request before the pre-position approach finishes, so target/pathing delays do not keep ready clients out of Tony's normal first-come queue.",
                "- Peer presence now publishes live coordinates, the peer list shows them, and owners path to randomized coordinates near Tony before targeting him and closing the final trade gap.",
                "- After Tony calls an owner, the final live-coordinate approach tightens to `0.5` yalm and can repath by Tony's visible object if the current target is missing or stale.",
                "- Tony's NPC sell route now randomizes the destination within `0.5` yalm of the configured vendor coordinate instead of stacking every run on the exact same point.",
                "- Xagman peer connections now retry local hub listener startup while disconnected, so same-PC clients can recover from a transient listener gap instead of staying on `hub connection unavailable`.",
                "- If selling reaches the gil cap, XA closes the shop and follows the normal Tony rotation or completion settings.",
                "- Supported vendor meet locations are listed one per line in the tooltip and shown in green in the meet-location dropdown.",
                "- Selling is skipped at `990,000,000` gil or higher so Tony does not risk the `999,999,999` gil cap.",
                "- Unsupported zones, unavailable AutoRetainer/vnav IPC, or sell-cleanup failures fall back to the normal full-inventory behavior: return home, relog the next Tony, or finish with warnings if no Tony remains.",
                "",
                "AutoRetainer IPC",
                "- IPC Calls > AutoRetainer now shows the `AutoRetainer.PluginState.*` status-pull channels for busy state, retainer readiness, Multi Mode status, auto-login availability, RetainerSense, protected-item checks, and deployable readiness.",
                "- Debug / Test > Punish > AutoRetainer now has matching PluginState status buttons.",
                "- Added an `AR ItemSell` debug command button that sends `/ays itemsell`.",
                "",
                "Game Mods",
                "- `Close Lobby Errors` now closes NoKillPlugin's `No Kill Plugin Panel` if that plugin opens its auth-error settings panel during a monitored lobby Dialogue flow.",
                "- Close Lobby Errors monitors supported lobby dialogs for 10 seconds, allowing it to close related popups and a NoKill panel that appears shortly afterward.",
                "- Debug / Test > XA Abuse > Lobby Test shows lobby-dialog readiness, detected error text, title-menu visibility, the 10-second monitoring period, and NoKill panel state, with a manual close test button.",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.36 - 2026-05-13",
            Lines =
            [
                "Player Mods",
                "- Added `Show Blacklisted Playername In Party` under XA Mods > Player Mods.",
                "- Blacklisted party-list `Unknown ##` rows can now show the matched blacklist name in red local text.",
                "- Added `/xa blacklistedparty on|off`",
                "- `Show Traveler World Names` now has an `Add spacer` suboption.",
                "- Default output stays `Name@HomeWorld`; enabling `Add spacer` renders remote visitor names as `Name @ HomeWorld`.",
                "- Saved XA Mods presets now preserve both `Disable in duties` and `Add spacer` for this feature.",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.35 - 2026-05-13",
            Lines =
            [
                "Dalamud Notifications Suck",
                "- Fixed `Hide Penumbra/Glamourer/mod alerts` so hidden Penumbra import notifications no longer cancel active mod imports.",
                "- Matching Penumbra/Glamourer/mod-manager notifications remain visually suppressed.",
                "- Updated notification suppression and its help text so hiding alerts does not interrupt the originating plugin's operation.",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.34 - 2026-05-11",
            Lines =
            [
                "Player Mods",
                "- `Show Traveler World Names` now explicitly covers Wanderer, Traveler, and Voyager visitor labels.",
                "- Voyager visitors follow the same local Name@HomeWorld presentation as existing Wanderer and Traveler labels.",
                "- Visitor labels continue to use the player's home world and respect Disable in duties.",
                "",
                "XA Mods",
                "- Added a dedicated `UI Mods` section for UI overlays, context-menu helpers, nameplate presentation, DTR display helpers, and UI text tweaks.",
                "- Renamed `Live Anonymous Mode` to `Anonymous Mode` in visible UI and command/help surfaces while preserving the existing saved setting and preset key.",
                "- Added `/xa uirestore` for disabling the current UI Mods section.",
                "- Moved `Bailout ESC Menu` into UI Mods.",
                "- XA Mods now shows a pinned current-section bar when a category header has scrolled out of view, so the open category can be collapsed without scrolling back to its original header.",
                "",
                "Game Mods",
                "- Moved `Notify When Friend Is Near` from Game Mods to Player Mods.",
                "- Moved `Auto Open Moogle Mail`, `Better Company Chest`, and `Better Inventory Mover` from Game Mods to Player Mods.",
                "- `Skip Cutscenes` options are now grouped into collapsible Territory Gates, Cutscene Categories, Gold Saucer, and Detectable Skips sections.",
                "",
                "Graphic Mods",
                "- Added `No UI Fade` under XA Mods > Graphic Mods.",
                "- Moved `Disable Title Screen Movie` and `Hide Unnecessary Popups` into Graphic Mods.",
                "- No UI Fade suppresses common UI and event fade transitions.",
                "- Saved No UI Fade settings restore more reliably after plugin load.",
                "- Added `/xa nouifade on|off`, saved startup settings, titlebar favourites, and preset support.",

            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.33 - 2026-05-09",
            Lines =
            [
                "Player Mods",
                "- Added `Show Titles As Playernames` to move visible player titles into the name line without title brackets.",
                "- Prefix titles now render before the player name; suffix titles render after the player name.",
                "- `Show Traveler World Names` composes after title placement, so traveler labels append `@HomeWorld` to the title-adjusted name.",
                "- Added `/xa titlesasplayernames on|off` and saved startup settings.",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.32 - 2026-05-08",
            Lines =
            [
                "Release Highlights",
                "- Added Better Highlight Potential Targets with selectable highlight colors for hovered targets.",
                "- Added `Show Traveler World Names` so visible travelers and wanderers can show locally as Name@HomeWorld while home-world FC tags stay unchanged.",
                "- Expanded `Skip Cutscenes` with category gates, territory whitelist/blacklist controls, MSQ light-party auto-enable, Gold Saucer, PvP, Ocean Fishing, Inn, and buddy-feed options.",
                "- Added public support access to Debug / Test through `/xa debug`, while keeping the menu hidden until support asks a user to toggle it.",
                "- Added direct XA movement/support commands for selected Debug / Test actions.",
                "",
                "Cutscene, Camera, And Duty Fixes",
                "- Improved Auto Skip Cutscenes compatibility with current cutscenes, credits, continuation screens, and selection menus.",
                "- Custom Sight Distance follows camera changes while preserving distance and collision settings.",
                "- Better Duty Finder controls remain visible and usable when Contents Finder or Raid Finder opens or refreshes.",
                "",
                "Debug / Test And Movement Commands",
                "- `/xa debug` toggles the hidden Debug / Test menu in public builds and stays persistent across plugin reloads until toggled off again.",
                "- Removed the placeholder `Braindead Functions` section from Debug / Test.",
                "- Added `/xa movingcheatersmart`, `/xa movingcheaterfly`, `/xa movingcheaterwalk`, `/xa interact`, `/xa leaveduty`, `/xa recommendedgear`, `/xa stopmovement`, `/xa pathtotargetinteract`, and `/xa pathsmartinteract`.",
                "- `/xa leaveduty` with no arguments runs the direct leave-duty action; `/xa leaveduty on|off` still controls the existing Auto Leave Duty XA Mod toggle.",
                "",
                "Startup, Reload, And Cleanup",
                "- Reduced plugin startup delays from Xagman, local data initialization, Eureka catalogs, and saved Instant Return settings.",
                "- Plugin unload now restores live Low Resolution scale, Special Rendering Modes UI/world visibility, and nameplate privacy state before teardown.",
                "- Updated public wording and links to the Aethertek website.",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.31 - 2026-05-04",
            Lines =
            [
                "New XA Mods",
                "- Added `Display Network Latency`, `Lock Game Window In Combat`, `Notify When Friend Is Near`, `Better Cast Bar`, and `Better Duty Finder` to XA Mods > Game Mods.",
                "- Added `Dalamud Notifications Suck` to XA Mods > Game Mods for hiding selected Dalamud toast categories, including update alerts, plugin lifecycle chatter, plugin error/load alerts, mod-manager alerts, success/info notices, and warning/error notices.",
                "- The new Game Mods can be saved in XA Mod lists, restored on startup, used as titlebar favourites, and toggled from chat commands.",
                "",
                "Smoother Startup",
                "- Saved XA Mods activate gradually after plugin load, reducing startup delays and showing clearer activation status.",
                "",
                "Game Mod Polish",
                "- `Display Network Latency` now starts safely even when the DTR bar entry has to be reacquired after reload.",
                "- Display Network Latency updates more reliably and indicates when its server-bar entry is hidden in /xlsettings.",
                "- `Notify When Friend Is Near` keeps the toast simple: Friend nearby plus the player's name. It does not send in-game chat messages.",
                "- `Better Cast Bar` adds the local cast-bar restyle and slidecast marker controls.",
                "- `Better Duty Finder` now shows its inline Contents Finder and Raid Finder buttons in two compact rows above the normal duty window controls.",
                "- `Special Rendering Modes` now reports UI visibility and world fade support separately, and only disables world fade buttons when that helper is unavailable.",
                "- Debug / Test > XA Abuse now includes `Dalamud Test Notifications`, with three-per-row buttons that create real Dalamud toasts for each notification suppression category.",
                "",
                "Shop Icons And Xagman",
                "- Fixed item icons in the current FreeShop window layout.",
                "- Xagman targeting now uses built-in target and focus handling, including direct focus assignment and target recovery while Xagman is running.",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.30 - 2026-05-02",
            Lines =
            [
                "Dalamud API 15",
                "- Updated task handling and Infinite Sprint for Dalamud API 15 compatibility.",
                "",
                "Game Mods",
                "- Added `Disable Title Screen Movie` with `/xa titlemovie on|off`",
                "- `Disable Title Screen Movie` keeps the title-screen lobby idle timer reset so the idle intro movie does not start",
                "- Added `Auto Display IDs` with `/xa displayids on|off` for item, action, target, weather, zone, and map IDs",
                "- Auto Display IDs supports tooltip, action, target, weather, zone, and map IDs; its master toggle disables all ID displays.",
                "- Added `Custom Timestamp Format` with `/xa timestampseconds on|off`",
                "- Custom Timestamp Format uses `[HH:mm:ss]` by default and reduces unnecessary preview work.",
                "- Added `Better Inventory Mover` with a configurable Shift/Ctrl/Alt quick-move modifier and destination-aware context-menu moves",
                "- Added `Better Company Chest` with default-page, right-click store/recover, quantity prompt confirmation, and exchangeable-item gil-value display support",
                "- `Better Company Chest` now handles Free Company Chest context-menu withdrawals and prompt confirmation more reliably",
                "- Added `Auto Open Moogle Mail` with Letter List Take all, Delete all, Delete NPC, Request delivery, and Stop overlay actions",
                "- `Auto Open Moogle Mail` handles letter opening, attachment claiming, confirmation handling, viewer close, and cleanup deletes more safely.",
                "- Added `Enable Item Icon In Shops` with `/xa shopicons on|off`",
                "- Added `Field Operations Entry Command` with `/xa fe <entry>` for Eureka entries through Pier #1 and Rodney routing",
                "",
                "Player Mods",
                "- Added `Auto Duty Commence` with `/xa dutycommence on|off`",
                "",
                "Plugin Mods",
                "- Added `Teleport Helper` with `/xa teleporthelper on|off`",
                "- Teleport Helper answers the aetheryte-ticket confirmation, defaults to No, and can be configured to choose Yes.",
                "- Teleport Helper handles ticket prompts while other plugin tasks are busy.",
                "",
                "Commands",
                "- `/xa lowres on` now restores the saved Low Resolution slider value and `/xa lowres <scale>` sets and enables the feature from chat",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.29 - 2026-05-01",
            Lines =
            [
                "XA Mods",
                "- Disabling Low Resolution restores full 3D rendering resolution.",
                "- The saved Low Resolution slider value is preserved, so your chosen scale is still ready for the next enable",
                "- The full-scale disable pass is shared by the XA Mods toggle, `/xa lowres off`, presets, section restore, disable-all, and plugin unload",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.28 - 2026-05-01",
            Lines =
            [
                "XA Peep",
                "- Added `Log while in duty` so XA Peep can pause duty targeter alerts and history writes unless explicitly enabled",
                "- XA Peep History columns now sort by Count, Player, Last Seen, or Total, and the window reopens on Last Seen by default",
                "",
                "API 15 Compatibility",
                "- Updated Refuse Trade Request, Display Queue Position, and camera-related features for Dalamud API 15.",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.27 - 2026-04-29",
            Lines =
            [
                "Dalamud API 15",
                "- Updated XA Slave for Dalamud API 15 compatibility.",
                "",
                "XA Mods / Fixes",
                "- `Close Lobby Errors` now catches lobby error `5006` and visiting-character congestion error `3088`",
                "- Added `Fix /target Command` and `/xa targetfix on|off` to recover failed native `/target` lookups through closest matching game objects",
                "- Display MSQ Progress reports more accurate completion percentages.",
                "- Eureka Instance Hunter accepts the duty-entry confirmation after talking to Rodney.",
                "- `Refuse Trade Request` and Xagman Dropbox handoff paths are more reliable around requester names, local feedback, and trade timeouts",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.26 - 2026-04-23",
            Lines =
            [
                "Field Operations",
                "- Added `Eureka Instance Hunter` to auto-rejoin Rodney entries until XA finds a new public instance, with per-zone baselines, alert sounds, and optional DTR output",
                "- Added `Eureka Logogram Creator` to automate Logos action crafting with favorites, reorderable favorite buttons, overlay shortcuts, and live cancel controls",
                "",
                "XA Mods / Alerts",
                "- `Anti-AFK` now refreshes the local AFK timer every 2 minutes",
                "- `Skip Cutscenes` no longer sends repeated `Esc` input during normal zone transitions",
                "- `Unlock Expert Delivery` again has a configurable Grand Company rank-floor dropdown",
                "- XA Peep can now optionally print chat log notifications when a new player starts targeting you",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.25 - 2026-04-20",
            Lines =
            [
                "XA Mods",
                "- `Hide Unnecessary Popups` now has an opt-in `Also hide HowToNotice` subsetting",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.24 - 2026-04-19",
            Lines =
            [
                "Fixes",
                "- Reduced stutters while restoring saved XA Mods and windows after plugin updates.",
                "- Xagman owner sendoff now waits for a final two-step give/request check before the owner leaves",
                "- Xagman partial Tony resupply trades now keep the owner in the wait loop with the reduced remaining request",
                "- Improved Xagman task and log handling for peer start, stop, recall, and completion commands.",
                "- `Close Lobby Errors` now catches supported numeric error codes even when those codes appear inside longer dialog text",
                "",
                "Quality Of Life",
                "- `/xa lowres <scale>` now requires `Low Resolution` to already be enabled",
                "- Titlebar resolution favourites now stay dim and print the same enable-first error unless `Custom Resolutions` is enabled",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.23 - 2026-04-19",
            Lines =
            [
                "Fixes",
                "- Fixed the update-time reload hang path so Dalamud is less likely to stall when XA Slave reloads during plugin updates",
                "- Fixed the `Kill Game` titlebar favourite highlight so it refreshes correctly even while the main XA Slave window is collapsed",
                "- Xagman trading conflicts were hardened with owner-collection and queue-flow fixes so empty or already-satisfied owner passes are less likely to stall follow-up trading work",
                "- Xagman partial Tony resupply trades now keep the owner waiting with the reduced remaining request instead of yielding too early and missing Tony's immediate follow-up trade request",
                "- Xagman checks both outgoing items and outstanding requests before sending a Franchise Owner home after trading.",
                "",
                "New XA Mods / QoL",
                "- Added `Bailout ESC Menu` to close a stuck `SystemMenu` after the selected timeout",
                "- Added `Auto Leave Duty` to exit completed duties after a configurable delay once combat and blockers clear",
                "- Added `Instant Return` to skip the Return cast/cooldown path while leaving the in-game confirmation to the user",
                "- Added `Anti-AFK` with a local timer refresh cadence",
                "- Added `Auto Merge` to combine incomplete inventory stacks when the inventory window opens",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.22 - 2026-04-17",
            Lines =
            [
                "Fixes",
                "- Minimum window size enforcement now restores and keeps the normal game floor correctly when the XA override is turned off",
                "- Plugin unload now tears down more safely so update-time cleanup is less likely to freeze the client",
                "- `Anonymize Character Lists` now stays in sync between XA Mods and the shared task-list checkbox flow",
                "",
                "UI / Quality Of Life",
                "- XA Slave now follows Dalamud interface zoom across the main UI, update history, task tables, and XA Peep overlays",
                "- Mass-character task tables support resizable columns with saved widths.",
                "- The splash screen no longer duplicates a `What's New` section; use `Update History` for release notes instead",
                "",
                "XA Mods",
                "- XA Mods preset save/load and export/import now restore supported per-mod subsettings, not just the top-level toggle list",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.21 - 2026-04-16",
            Lines =
            [
                "XA Peep",
                "- XA Peep adds a compact target tracker, a separate history window, cumulative per-player counts, logout-safe cached history, and saved local history.",
                "- Live rows now support hover focus preview, left/right click actions, Ctrl+Left Click examine, Ctrl+Right Click adventurer plate, center-screen alerts, and configurable targeter cards/lines/dots",
                "- XA Peep alert sounds now play even when the game's own sound channel is muted, include selectable sound slots plus volume control, and the tracker now supports party/alliance/in-combat filters, auto-open on load, resize lock, and reload-safe startup",
                "",
                "Plugin Operations",
                "- Show Version in Window Title now defaults on until you turn it off",
                "- Kill Game titlebar selection now auto-enables XA Mods > Instant Logout, and custom titlebar favourites can now open panels, toggle any XA Mod, drive Special Rendering Modes UI presets, fire Sit / Doze actions, run All XA Mods Off, trigger Stop All Automated Tasks, and be added or removed as needed",
                "",
                "XA Mods / Other",
                "- Special Rendering Modes now uses stored toggle switches for the hide-chat, action-bar, target-info, nameplate, and keep-chat/keep-nameplate visibility presets, plus Restore All clears those saved toggles",
                "- Doze & Sit Anywhere keeps the simple master toggle flow with Sit now / Doze now buttons, those same actions can now be added as titlebar favourites, fixed Export Data paths can now overwrite the same TSV/CSV file, and the anonymize-character flows now use shared deterministic aliases across XA Slave",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.20 - 2026-04-15",
            Lines =
            [
                "Plugin Operations: Titlebar Favourite Buttons",
                "- Configurable quick-action buttons on the window title bar",
                "- Fixed actions: Kill Game (Ctrl+Shift gate), Disable All Mods, Load Mod List, AR Pre/Post toggles, Glam Weather toggle",
                "- Custom slots: up to 4 menu-nav favourites and 4 resolution shortcuts",
                "",
                "Other Changes",
                "- Lobby error auto-close now covers codes 90000, 90003\u201390005, 2002, 3050",
                "- Xagman TopUp item mode added (top quantity up to a threshold)",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.19 - 2026-04-14",
            Lines =
            [
                "FC Chest Gil Capture",
                "- AR post-processing can now capture FC chest gil - Check FC Chest For Gil targets, paths, interacts, saves, and closes the chest automatically",
                "- Workshop-only guard: chest capture only runs inside the company workshop when XA Database and vnav are both available",
                "- Refresh Sub/Bell/Chest workshop runs now also refresh FC chest gil in one pass",
                "",
                "Shared Task Completion",
                "- Task Options on Complete footer now consistent across all relogger-style task panels",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.18 - 2026-04-13",
            Lines =
            [
                "Xagman Networking",
                "- Xagman now supports a configurable hub address + port for same-PC or LAN peer setups",
                "- Keep 127.0.0.1 for one machine, or point clients at the host PC's LAN IP/name for cross-PC runs",
                "",
                "Dropbox Queue",
                "- XA Slave now owns the Dropbox queue flow with /xa db ..., /xa db clear, and the crystal request shortcuts",
                "- Xagman now uses the XA Dropbox flow directly; Dropbox itself is still required",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.17 - 2026-04-09",
            Lines =
            [
                "XA Mods",
                "- One grouped utility panel for game/graphics/player/plugin toggles",
                "- Presets + clipboard import/export: build a known-good mod set and reuse it across clients",
                "- Mass reset/restore tools are included (Disable All Mods + section restores) for quick recovery",
                "- XA Mods remain experimental; disable or reset a mod if it causes problems.",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.16 - 2026-04-06",
            Lines =
            [
                "- Xagman is now live in normal XA Slave releases",
                "- New splash screen, splash shortcut, repo list, and tidier menu layout",
                "- Window Renamer can append your current character name to the title bar",
                "- /journal task flows now save leve allowances for XA Database exports",
                "- Task logs only auto-open when Verbose Task Logging is enabled",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.15 - 2026-03-19",
            Lines =
            [
                "- Fixed: ESC (used in bailout) and other key presses now go only to the FFXIV game client, not your active Windows app",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.14 - 2026-03-18",
            Lines =
            [
                "- Window can be resized smaller",
                "- Automated Export Data is now a built-in Reference panel",
                "- Manual writes and Always On scheduling both support {timestamp} paths",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.13 - 2026-03-17",
            Lines =
            [
                "- Auto-Glam now uses per-weather plate lists",
                "- Monthly Relogger now shows current rank + personal plot and can check masters/personal only",
                "- Prep Logistics, Refresh Subs, and FC Permissions now share region filters and richer table data",
                "- XA Database rank + inventory data is now reused across the main FC task panels",
                "- Sidebar sections now collapse, persist, reopen on your last task, keep a cleaner center gap, and highlight both Save to XA + AutoRetainer activity; sidebar width is also adjustable",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.12 - 2026-03-15",
            Lines =
            [
                "- Auto-Glam now uses persisted class/job and plate lists with random picks",
                "- Refresh AR Subs/Bell now supports prep actions, bell-only mode, and safer recovery",
                "- Save to XA Database now has built-in logs and yields to FC relation tasks",
                "- Shared Check Every presets now extend up through 90 days",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.11 - 2026-03-09",
            Lines =
            [
                "- Added Open Journal support and improved personal/shared estate logging for XA Database task flows",
                "- AutoRetainer Tasks now include bailout options for stuck result windows",
                "- Added logout-on-completion support for mass-character task flows",
                "- New Prep Logistics task for moving selected characters to a target world and optional location through Lifestream",
                "- Monthly Relogger stale selection now uses a configurable slider instead of a fixed >20-day threshold",
                "- Added Verbose Task Logging for easier task debugging",
                "- Public builds no longer show the Debug / Test section",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.10 - 2026-03-08",
            Lines =
            [
                "- Added local tracking of the last XA Database save time.",
                "- AR Pre/Post and login collection now support 6\u201372hr cadence gates",
                "- Login collection now pauses and safely resumes AR when needed",
                "- Show Live Pulls now defaults off on every plugin load",
                "- Optional open-on-load setting can reopen XA Slave on load/login",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.9 - 2026-03-08",
            Lines =
            [
                "- Critical Fix: Resolved Post-AR Processing errors causing AR to get stuck in Post process when using Pre-AR Processing",
                "- Disabled logging by default; settings changes persist across loads",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.8 - 2026-03-05",
            Lines =
            [
                "AR Pre/Post Processing",
                "- Added data collection before and after AutoRetainer Multi Mode processing.",
                "- Pre-processing: suppress AR \u2192 collect data \u2192 un-suppress before retainers",
                "- Post-processing: collect data after retainers, before AR relogs",
                "",
                "New Features",
                "- Menu reorganized into colored sections: Tasks, FC, Reference",
                "- Refresh AR Subs/Bell \u2014 rotate chars, refresh sub console + bell",
                "- FC Permissions Updater \u2014 bulk-update FC rank permissions",
                "- Auto-Accept FC Invites \u2014 accept, wait, leave (FC floater assist)",
                "- Auto-Glam Weather \u2014 glamour plates based on weather conditions",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.6 - 2026-03-01",
            Lines =
            [
                "- Pre-flight: detects movie / char select / main menu before processing",
                "- Saddlebag/FC guards: graceful skip for missing features",
                "- Duty recovery uses bounded waits and retries to avoid getting stuck.",
                "- Movement: 0.5y stop distance, 1.0y interact, mount at >20y",
                "- Mount+path simultaneous, 2s dismount safety delay",
                "- IPC: XASlave.IsBusy + XASlave.RunTask + /xa run command",
                "- DTR bar always visible, debug header pinned while scrolling",
                "",
                "New Features",
                "- Check Duplicate Plots \u2014 detect & fix stale housing data",
                "- Return Alts To Homeworlds \u2014 relog & return world-visitors",
                "- City Chat Flooder \u2014 travel worlds/cities sending announcements",
            ],
        },
    };

    public UpdatesWindow()
        : base(UpdatesWindowTitle, ImGuiWindowFlags.None)
    {
        UpdateSizeConstraints(UiScaleSafe);
    }

    public override void PreDraw()
    {
        UpdateSizeConstraints(UiScale);
    }

    public override void Draw()
    {
        if (versions.Count == 0)
        {
            ImGui.TextDisabled("No version history available.");
            return;
        }

        var currentVersionIndex = versions.FindIndex(entry => HeaderMatchesRunningVersion(entry.Header));
        if (currentVersionIndex < 0)
            currentVersionIndex = 0;
        var currentVersionHeader = versions[currentVersionIndex].Header;

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Version History");
        ImGui.TextDisabled($"Installed build: XA Slave v{BuildInfo.Version}");
        ImGui.TextDisabled($"Current version notes: {currentVersionHeader}");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        for (var i = 0; i < versions.Count; i++)
        {
            var entry = versions[i];
            var isCurrentVersion = i == currentVersionIndex;

            using (ImRaii.PushId(i))
            {
                bool open;
                using (ImRaii.PushColor(
                           ImGuiCol.Text,
                           new Vector4(0.4f, 1.0f, 0.4f, 1.0f),
                           isCurrentVersion))
                {
                    if (firstDraw)
                        ImGui.SetNextItemOpen(isCurrentVersion, ImGuiCond.Always);
                    open = ImGui.CollapsingHeader(entry.Header);
                }

                if (open)
                {
                    ImGui.Indent(Scale(12f));
                    using (ImRaii.TextWrapPos(ImGui.GetContentRegionAvail().X + ImGui.GetCursorPosX() - Scale(12f)))
                    {
                        foreach (var line in entry.Lines)
                        {
                            if (string.IsNullOrWhiteSpace(line))
                            {
                                ImGui.Spacing();
                                continue;
                            }

                            var trimmed = line.TrimStart();

                            // Sub-header lines (no leading dash)
                            if (!trimmed.StartsWith("-"))
                            {
                                ImGui.TextColored(new Vector4(0.7f, 0.85f, 1.0f, 1.0f), trimmed);
                                continue;
                            }

                            // Bullet lines
                            var bulletText = trimmed.Length > 1 ? trimmed[1..].TrimStart() : string.Empty;
                            ImGui.TextUnformatted($"• {bulletText}");
                        }
                    }

                    ImGui.Unindent(Scale(12f));
                    ImGui.Spacing();
                }
            }
        }

        firstDraw = false;
    }

    public override void OnClose()
    {
        firstDraw = true;
    }

    private void UpdateSizeConstraints(float scale)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480f * scale, 320f * scale),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    private static float Scale(float value)
        => value * UiScale;

    private static bool HeaderMatchesRunningVersion(string header)
    {
        return string.Equals(GetVersionToken(header), $"v{BuildInfo.Version}", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetVersionToken(string header)
    {
        var trimmed = header?.Trim() ?? string.Empty;
        var separatorIndex = trimmed.IndexOf(" - ", StringComparison.Ordinal);
        if (separatorIndex > 0)
            return trimmed[..separatorIndex].Trim();

        var firstSpaceIndex = trimmed.IndexOf(' ');
        return firstSpaceIndex > 0
            ? trimmed[..firstSpaceIndex].Trim()
            : trimmed;
    }

    private sealed class VersionEntry
    {
        public string Header { get; init; } = string.Empty;
        public List<string> Lines { get; init; } = [];
    }
}
