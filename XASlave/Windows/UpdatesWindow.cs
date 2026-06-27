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
            Header = "v0.0.0.40 - 2026-06-27",
            Lines =
            [
                "Plugin Operations",
                "- New `Custom Resolution on Plugin Load` option (under `Open Plugin on Load`) force-resizes the game window to a saved width and height each time the plugin loads.",
                "- The width, height, and the `Ignore Minimum Window Size` sub-option stay greyed out until the feature is enabled; the sub-option lowers the client minimum so sizes below 1024x720 hold instead of snapping back.",
                "- Reuses the same custom-resolution engine as XA Mods, so the on-load resize works without separately enabling Custom Resolutions in XA Mods.",
                "",
                "Game Mods",
                "- `Skip Dialogue` now also skips Craft Leve turn-in dialogue by hooking the `CraftLeveTalk` Lua handler alongside the existing Talk, SystemTalk, ShortTalk, and Guildleve handlers.",
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
                "- Updated `Microsoft.Data.Sqlite` to `10.0.9` with an explicit `SQLitePCLRaw.bundle_e_sqlite3` `3.0.3` reference so strict builds no longer report a vulnerable SQLite transitive package.",
                "- Eureka Logogram Creator favorites and automation overlays now keep Atk collision inhibition enabled so their invisible buttons no longer click through to the native UI behind them.",
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
                "- Debug `MovingCheater` buttons now use the same command methods as the matching `/xa movingcheater*` commands.",
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
                "- Updated Eureka logogram addon text cleanup to use `AtkValueType.ConstString` instead of the obsolete `String8` alias.",
                "- Export Data automatic writes now run in the background so the framework tick does not perform synchronous JSON, SQLite, and file-output work.",
                "- Saved startup restore for hook-heavy XA Mods now prepares Custom Timestamp Format, No UI Fade, Queue Position Display, Auto Skip Cutscenes, and Custom Sight Distance hook surfaces outside the framework tick.",
                "- Custom Timestamp Format now prepares its saved-startup hook during plugin load so the first post-load activation tick only enables an already-created hook.",
                "- Allow Multiple Game Instances now runs its launch-lock handle cleanup outside the deferred startup queue.",
                "- Cancel Login Cooldown now prepares its lobby hook outside the deferred startup framework tick before enabling it.",
                "- Prevent Game Exiting From Lobby Errors now prepares its lobby error hook outside the deferred startup framework tick before enabling it.",
                "- Reload validation confirmed the XA deferred startup warnings for Allow Multiple Game Instances, Cancel Login Cooldown, and Auto Skip Cutscenes no longer appear.",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.37 - 2026-05-15",
            Lines =
            [
                "Xagman",
                "- Added `Sell When Inventory Is Full` for Tony runs.",
                "- When Tony fills inventory in a supported meet zone, XA paths Tony to the local vendor, sends `/ays itemsell`, waits for AutoRetainer item selling to finish, runs CharacterSafeWait, and resumes Xagman.",
                "- After Tony sells items, resumed Franchise Owners path to Tony's vendor coordinate with vnav stop distance `2` before targeting Tony and starting the normal trade path.",
                "- Standby owners stay visible in Tony's queue while moving to that vendor coordinate, so Tony can call the first owner that reaches the sell location.",
                "- Owners now enter Queue Wait as soon as they reach Tony's sell coordinate, so they do not stay stuck as generic Traveling peers before Tony can call them.",
                "- Owner-side Tony lookup now accepts Tony's active sell-location peer and includes queued Paused owners in Tony's first-come queue.",
                "- Owners now publish their queue request before the pre-position approach finishes, so target/pathing delays do not keep ready clients out of Tony's normal first-come queue.",
                "- Peer presence now publishes live coordinates, the peer list shows them, and owners path to randomized coordinates near Tony before targeting him and closing the final trade gap.",
                "- After Tony calls an owner, the final live-coordinate approach tightens to `0.5` yalm and can repath by Tony's visible object if the current target is missing or stale.",
                "- Tony's NPC sell route now randomizes the destination within `0.5` yalm of the configured vendor coordinate instead of stacking every run on the exact same point.",
                "- Xagman peer connections now retry local hub listener startup while disconnected, so same-PC clients can recover from a transient listener gap instead of staying on `hub connection unavailable`.",
                "- If NPC item selling hits the gil cap message, XA closes Shop with `callback Shop true -1` and falls back to the normal Tony full-inventory rotation/completion path.",
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
                "- The NoKill panel close is scoped to the known `NoKillPlugin` / `No Kill Plugin` runtime instance and uses reflection to set `Gui.ConfigWindow.Visible` false.",
                "- The Close Lobby Errors monitor now waits for addon:Dialogue to contain a supported lobby/networking marker such as `90002`, then opens a 10 second monitor window for popup confirmation and NoKill panel cleanup.",
                "- The NoKill panel close runs on a throttled framework update during that Dialogue-triggered 10 second window, so it can catch the panel if it opens shortly after the lobby dialog appears.",
                "- Debug / Test > XA Abuse > Lobby Test now shows whether Dialogue is visible, ready, supported, which marker/text was seen, whether `_TitleMenu` is visible, whether the 10 second monitor window is active, whether the NoKill window is active, and includes a manual close test button.",
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
                "- Plugin-owned dismiss callbacks are scrubbed before XA suppresses matching notifications, so Penumbra import/upload flow can continue.",
                "- Updated the XA Mods help text to call out that matching notifications are hidden without firing plugin-owned dismiss callbacks.",
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
                "- The runtime rewrite still uses home-world mismatch and preserves the existing Disable in duties behavior.",
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
                "- The toggle suppresses common middle-back, white fade in/out, and event fade in/out UI transitions through native hooks.",
                "- Saved `No UI Fade` restores now run through the post-load XA Mod activation phase after the core startup pass instead of the immediate deferred startup queue.",
                "- Added `/xa nouifade on|off`, startup restore, titlebar favourite, preset, Commands, README, and startup-status coverage.",

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
                "- Added `/xa titlesasplayernames on|off` plus XA Mods, Commands, README, and startup-status coverage.",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.32 - 2026-05-08",
            Lines =
            [
                "Release Highlights",
                "- Added `Better Highlight Potential Targets` with selectable native highlight colors and stable-client arming before repainting hovered potential targets.",
                "- Added `Show Traveler World Names` so visible travelers and wanderers can show locally as Name@HomeWorld while home-world FC tags stay unchanged.",
                "- Expanded `Skip Cutscenes` with category gates, territory whitelist/blacklist controls, MSQ light-party auto-enable, Gold Saucer, PvP, Ocean Fishing, Inn, and buddy-feed options.",
                "- Added public support access to Debug / Test through `/xa debug`, while keeping the menu hidden until support asks a user to toggle it.",
                "- Added direct XA movement/support commands for selected Debug / Test actions.",
                "",
                "Cutscene, Camera, And Duty Fixes",
                "- `Auto Skip Cutscenes` now resolves current Lua cutscene handlers before hooking `PlayCutScene`, `PlayStaffRoll`, and `PlayToBeContinued`.",
                "- `Auto Skip Cutscenes` now handles PointMenu completion through the current agent lifecycle path.",
                "- `Custom Sight Distance` now tracks active camera changes through `SetActiveCamera` while preserving the existing distance and collision controls.",
                "- `Better Duty Finder` now tracks Contents Finder and Raid Finder through addon lifecycle draw, refresh, and finalize events, so its inline controls survive the current window lifecycle.",
                "- `Better Duty Finder` no longer depends on the stale addon value guard that could hide the overlay on current builds.",
                "",
                "Debug / Test And Movement Commands",
                "- `/xa debug` toggles the hidden Debug / Test menu in public builds and stays persistent across plugin reloads until toggled off again.",
                "- Removed the placeholder `Braindead Functions` section from Debug / Test.",
                "- Added `/xa movingcheatersmart`, `/xa movingcheaterfly`, `/xa movingcheaterwalk`, `/xa interact`, `/xa leaveduty`, `/xa recommendedgear`, `/xa stopmovement`, `/xa pathtotargetinteract`, and `/xa pathsmartinteract`.",
                "- `/xa leaveduty` with no arguments runs the direct leave-duty action; `/xa leaveduty on|off` still controls the existing Auto Leave Duty XA Mod toggle.",
                "",
                "Startup, Reload, And Cleanup",
                "- Xagman peer auto-start, local SQLite schema setup, Eureka Logogram data loading, and Instant Return restore now avoid blocking plugin construction.",
                "- Plugin unload now restores live Low Resolution scale, Special Rendering Modes UI/world visibility, and nameplate privacy state before teardown.",
                "- Remaining obsolete Dalamud scale warnings were cleaned up by moving affected windows to `ImGuiHelpers.GlobalScale`.",
                "- Public text and metadata were cleaned up to use neutral built-in/plugin wording and the public Aethertek site.",
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
                "- Heavy startup work for chat timestamps, cutscene skipping, and custom sight distance now starts after the core plugin load instead of blocking the first load summary.",
                "- Additional hook-backed restores, including queue position, object hiding, background rendering pause, trade refusal, and map reveal, now use the same post-load activation path.",
                "- Cutscene and camera hooks now arm over several frames, with clearer logs showing what is still arming and when post-load activation is actually complete.",
                "",
                "Game Mod Polish",
                "- `Display Network Latency` now starts safely even when the DTR bar entry has to be reacquired after reload.",
                "- `Display Network Latency` now keeps its DTR updates on the framework tick path and shows when the entry is hidden in /xlsettings.",
                "- `Notify When Friend Is Near` keeps the toast simple: Friend nearby plus the player's name. It does not send in-game chat messages.",
                "- `Better Cast Bar` adds the local cast-bar restyle and slidecast marker controls.",
                "- `Better Duty Finder` now shows its inline Contents Finder and Raid Finder buttons in two compact rows above the normal duty window controls.",
                "- `Special Rendering Modes` now reports UI visibility and world fade support separately, and only disables world fade buttons when that helper is unavailable.",
                "- Debug / Test > XA Abuse now includes `Dalamud Test Notifications`, with three-per-row buttons that create real Dalamud toasts for each notification suppression category.",
                "",
                "Shop Icons And Xagman",
                "- `Enable Item Icon In Shops` now matches the current FreeShop layout and clamps reads and writes to the available AtkValue range.",
                "- Xagman targeting now uses built-in target and focus handling, including direct focus assignment and target recovery while Xagman is running.",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.30 - 2026-05-02",
            Lines =
            [
                "Release Delta",
                "- Update History and release notes were reconciled against the changelog entries after v0.0.0.29",
                "",
                "Dalamud API 15",
                "- Backend debugger and task helpers now use Dalamud's API 15 IFramework.Run path",
                "- Infinite Sprint status checks now use API 15 uint status IDs",
                "",
                "Game Mods",
                "- Added `Disable Title Screen Movie` with `/xa titlemovie on|off`",
                "- `Disable Title Screen Movie` keeps the title-screen lobby idle timer reset so the idle intro movie does not start",
                "- Added `Auto Display IDs` with `/xa displayids on|off` for item, action, target, weather, zone, and map IDs",
                "- `Auto Display IDs` owns item tooltip IDs, action IDs, target/weather IDs, and optional zone/map DTR output; disabling the master toggle disables its tooltip hook and subsettings",
                "- Added `Custom Timestamp Format` with `/xa timestampseconds on|off`",
                "- `Custom Timestamp Format` formats chat timestamps as `[HH:mm:ss]` by default and avoids preview work until chat requests timestamp text",
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
                "- `Teleport Helper` monitors the aetheryte-ticket `SelectYesNo` teleport prompt directly, defaults to No to reject ticket usage, and can be configured to choose Yes instead",
                "- The ticket helper no longer depends on plugin busy-state gating and now uses final Teleport Helper naming",
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
                "- `Low Resolution` now forces the live 3D resolution scale to 1.00 for a render pass before it finishes disabling",
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
                "API 15 Follow-up",
                "- Refuse Trade Request now uses the API 15 InventoryManager trade-request hook path",
                "- Display Queue Position and protected signatures were refreshed for the API 15 ClientStructs queue/camera paths",
            ],
        },
        new VersionEntry
        {
            Header = "v0.0.0.27 - 2026-04-29",
            Lines =
            [
                "Dalamud API 15",
                "- Bumped the source, manifest, SDK, and lockfile to the full Dalamud API 15 structure with DalamudPackager 15.0.0",
                "- API 15 fixes cover callback values, territory and duty signatures, UI flags, GC rank access, buddy kinds, and unsafe sound calls",
                "",
                "XA Mods / Fixes",
                "- `Close Lobby Errors` now catches lobby error `5006` and visiting-character congestion error `3088`",
                "- Added `Fix /target Command` and `/xa targetfix on|off` to recover failed native `/target` lookups through closest matching game objects",
                "- `Display MSQ Progress` now uses the adjusted all-MSQ Lumina quest cache for more accurate percentages",
                "- Eureka Instance Hunter now accepts the `ContentsFinderConfirm` commence prompt after Rodney entry",
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
                "- Startup XA Mods, saved window opens, and some context-specific hooks now arm later when needed, reducing update-time `FrameworkUpdate` hitches",
                "- Xagman owner sendoff now waits for a final two-step give/request check before the owner leaves",
                "- Xagman partial Tony resupply trades now keep the owner in the wait loop with the reduced remaining request",
                "- Xagman peer start, stop, recall, and completion commands now move their local task and log work back to the framework thread",
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
                "- Xagman now performs a two-step whole-flow completion verification before owner sendoff, so a completed trade no longer sends the owner home until both give-side and Tony-supply-side reconciliation checks come back clean",
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
                "- Mass-character task tables now support resizable columns with widths preserved through saved ImGui table settings",
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
                "- XA Peep adds a compact target tracker, a separate history window, cumulative per-player counts, logout-safe cached history, and local persistence through slave.db",
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
                "- Early beta rollout: monitor usage and reset/disable if a local hook acts up",
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
                "- Added slave.db sync tracking for last XA Database save time",
                "- AR Pre/Post and login collection now support 6\u201372hr cadence gates",
                "- Login collection now pauses and safely resumes AR when needed",
                "- Show Live Pulls now defaults off on every plugin load",
                "- Optional open-on-load setting can reopen XA Slave on load/login",
                "- slave.db, cadence gates, and AR Multi detection were verified in-client",
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
                "- Pre + post hooks around AutoRetainer multi-mode",
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
                "- Duty guard: step-based, 3\u00d7 SafeWait, 5 retries (no more deadlocks)",
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

        if (versions.Count == 0)
        {
            ImGui.TextDisabled("No version history available.");
            return;
        }

        for (var i = 0; i < versions.Count; i++)
        {
            var entry = versions[i];
            var isCurrentVersion = i == currentVersionIndex;

            ImGui.PushID(i);

            if (isCurrentVersion)
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.4f, 1.0f, 0.4f, 1.0f));

            if (firstDraw)
                ImGui.SetNextItemOpen(isCurrentVersion, ImGuiCond.Always);
            var open = ImGui.CollapsingHeader(entry.Header);

            if (isCurrentVersion)
                ImGui.PopStyleColor();

            if (open)
            {
                ImGui.Indent(Scale(12f));
                ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X + ImGui.GetCursorPosX() - Scale(12f));

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

                ImGui.PopTextWrapPos();
                ImGui.Unindent(Scale(12f));
                ImGui.Spacing();
            }

            ImGui.PopID();
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
