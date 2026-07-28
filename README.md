# XA Slave

A Dalamud plugin for FINAL FANTASY XIV that automates repetitive multi-character workflows - relogging, world travel, chat announcements, housing checks, and more. Works alongside **XA Database** to collect and push character data hands-free.

- View all our utilities & plugins here: https://aethertek.io/

## Key Features

- **AutoRetainer Tasks** - Run pre-processing, post-processing, bailout recovery, startup recovery, sync cadence gates, and optional collection from one panel. Includes a workshop-only FC Chest gil sync to XA Database before AutoRetainer continues.
- **Save to XA Database** - Push character data into XA Database on demand or through cadence-gated login collection, with task logs for save/debug visibility.
- **Auto-Glam Weather** - Pick valid glamour plates from per-weather lists and apply them automatically when the active weather changes.
- **City Chat Flooder** - Send announcements across selected worlds and cities with loop and delay controls.
- **Xagman** - Coordinate FC item trading between Tony and Franchise Owner clients on the same PC or LAN. Supports Dropbox queueing, `Give` / `Take` / `Balance` / `TopUp`, supplier matching, trade recovery, item-list import/export, standby queues, queue-first owner handoffs, self-healing peer hub reconnects, live-coordinate Tony approach, optional Tony item selling when inventory fills in supported ARR city/hamlet zones with gil-cap fallback, FC-return cleanup, and direct target/focus recovery while running. **Server Matching** (optional, choose it in the meet-world dropdown) lets you pick one meet world per server (data center) plus a shared meet location: the Tony then sweeps server-by-server within a region (Aether → Crystal → Dynamis → Primal) and region by region (NA → EU → JP → OCE), so Franchise Owners only world-travel inside their own server and never DC-travel. Its waiting and in-flight handoffs share the active data-center scope for Collection, Resupply, and mixed Give-then-supply owners, and Tony locks one exact Franchise Owner plugin instance per trade; fixed-world runs retain their global queue. When Resupply exhausts one Tony mid-owner, the pending owner adopts the replacement Tony's exact call, retargets and reapproaches it, and can recover from a missed one-shot start signal; Tony remains stationary and waits for range before beginning the supply task. Multiple Tonys per region rotate when one fills or depletes (the run advances to the next region only when that region's Tonys are exhausted), and the order list persists after completion with failed owners in red and skipped owners in purple until the next start. Server Matching is per-client and snapshots are per-client; non-Server-Matching runs are unchanged. **Outside Network Helper** (optional checkbox under the role row) is for two *different* players on *different* machines with no shared peer network: it disconnects and hides the peer hub (Address/Port/Connect) and Peers table, lets each side pick its own meet world + location, and shares character rosters by clipboard export/import (Tony imports the partner's Franchise Owner list in place of the Tony Search Item List; Franchise Owner imports the partner's Tony list under the Shared Item List). Coordination is fully in game - Franchise Owner gives items, Tony only receives. **Start now works** (Phase 2): it runs a self-hosted state machine that relogs + travels each of your own selected characters to your meet spot, then for a Franchise Owner gives to whichever roster Tony is physically present (target + path + Dropbox transfer), and for a Tony enables auto-accept and receives, rotating to your next Tony when inventory fills and finishing after a 5-minute idle with no trades. There is no peer network in this mode, so "ready/done" is signalled by in-game proximity + the Dropbox transfer rather than a literal 1-gil trade. ONH needs only your own meet world + location and at least one selected character to start; Stop is the normal Xagman task Stop button. (Tony selling-when-full is not yet wired in ONH - it rotates instead.) Both character tables (Tonys and Franchise Owners) show sortable Region/DC, Inventory, Gil, Treasure, Kits, Tanks, Retainers, and Submarines columns - Treasure is the vendor gil value of held treasure items (IDs 22500-22507, including retainer stock and market listings) and Kits/Tanks are the character-held Magitek Repair Materials and Ceruleum Tank counts, all refreshed by `Pull XA Database Info`.
- **Xagman quick relog** - Ctrl+click a character name in either the Tony or Franchise Owner table to send `/ays relog FirstLast@World` for that saved row. The real roster key is used even when names are anonymized, while ordinary clicks, selection checkboxes, sorting, and filtering remain unchanged. The shortcut is unavailable until Xagman trade safety and the shared task runner are idle.
- **Xagman late-client travel recovery** - Server Matching Tony preserves its committed sweep destination through startup/relog publication. Owners require a Tony heartbeat no more than five seconds old, an active sweep, both meet fields, and a meet world consistent with Tony's active data center. Server Matching stays latched: unavailable, incomplete, malformed, stale, or not-yet-reached sweep data pauses without falling through to cached/fixed-world travel, while an unknown owner data center or a sweep already past that owner marks it purple and advances without IPC. Fixed-world owners also require a freshly resolved complete Tony destination. After relog, an owner waits at most 600 seconds for Lifestream idle and a fresh complete destination without sending IPC or progressing beyond the first command step, then pins it for the full attempt. A real command gets up to three attempts; each verifies the same owner, observes Lifestream busy and stable idle when travel starts, runs the three-pass `CharacterSafeWait`, and verifies the pinned world/aetheryte zone even if Tony's later advertisement changes. For compound `World, Aetheryte` commands, intermediate Lifestream idle is provisional: `CharacterSafeWait` cannot pass while casting, and final verification waits through casting, zoning, unloaded player state, missing `LocalPlayer`, or unreadable world/territory/identity. Blank identity is unavailable rather than a character swap. Only three one-second stable observations of a non-empty different character can stop the owner; only three stable same-owner/wrong-destination observations consume an attempt. Three genuine same-character failures mark the owner red and advance to the next owner. Every meetup command is now checked from the planned character's home world before Lifestream IPC: same-region travel and NA/EU/JP-to-OCE travel are allowed, while OCE-to-NA/EU/JP and unknown mappings fail closed. Tony runtime keeps an accepted compound command in a separate bounded in-flight phase—up to 600 seconds to reach the target world, followed by a fresh 60-second local-teleport window—so an idle IPC gap, casting, loading, or zoning cannot consume overlapping retries.
- **Xagman travel-route freeze and peer guard** - Tony snapshots the fixed meetup or complete Server Matching world map plus aetheryte at start and keeps that route through the coordinated collection/restock run. Fixed and Server Matching peer selection rejects Error, malformed, mismatched-region/data-center, and home-world-unreachable advertisements before owners bind, queue, or accept a call. During a collection-first Server Matching sweep, an unacknowledged expected FO that becomes stale/Error blocks server and region advance immediately; a blank, unknown, or unconfigured pending FO server also blocks and then enters visible Error after its bounded grace. Neither case can be dropped from the live frontier and mistaken for a finished sweep. This correction passed source/static checks only; build, reload, packaging, publication, client reset, and live multi-client acceptance were not run.
- **Xagman idle trade refusal** - The default-off `Refuse Trades When Idle` option below `Ignore Gil in Select Matching Items` reuses XA Mods > Player Mods > Refuse Trade Request during live Xagman sessions. Every Xagman run coordinates the saved manual preference: refusal is suppressed before confirmed Dropbox auto-accept writes, restored to manual-only behavior after confirmed `Active = false` when the option is off, or enabled as an idle guard when the option is on. Unknown writes remain suppressed so the two handlers never knowingly compete. Normal, Server Matching, and Outside Network Helper paths share the coordinator, confirmed reflection readback, and safe terminal cleanup without rewriting the global checkbox. Explicitly opted-in cross-data-center-capable Xagman Lifestream actions cover normal Tony and fixed-world owner meetup, both ONH roles, Tony runtime meetup reassertion, pre-relog FC return, normal Tony/owner FC return, failure recall, and completion FC return. They attempt to arm one 60-second action marker immediately before each nonblank dispatch, bound to the actual outgoing character name and Content ID plus the planned Xagman character, role, status, exact command, operation context, and TaskRunner-active state. A matching Content ID consumes the marker only after Lifestream busy was observed or remains current, and the handled logout bypasses the plugin's generic TaskRunner logout cancellation. A successful IPC invocation still dispatches when a safe marker cannot be armed, but success does not prove Lifestream accepted or began travel; any resulting logout remains unexpected and fails closed. Every successful opted-in IPC invocation or periodic reissue refreshes the marker deadline to 60 seconds from that call. An invocation that returns false after newly arming rolls back that marker; a reissue that returns false neither clears nor extends a still-valid marker from an earlier successful invocation. Duplicate, delayed beyond the latest successful dispatch, expired, mismatched, manual, or unrelated logouts still stop Xagman. Existing `/ays relog` and completion logout handling remains separate and is checked first.
- **Xagman mass stop and result clear** - Tony's connected-client controls now include `Stop All Clients and Results` immediately after `Stop All Peers`. It stops Xagman on the initiating client and every currently connected client, then clears each client's retained Tony Order / Franchise Owner Order snapshot. The existing `Stop All Peers` remains stop-only so its results stay available for review. Character selections, item lists, saved lists, and settings are unchanged.
- **Xagman item lookup** - `Add Item` in both the Shared Item List and Tony Search Item List searches the tradeable named rows in the current Lumina `Item` sheet loaded by Dalamud, so valid trade items remain searchable even when no XA Database ownership snapshot contains them. Each item appears once as normal quality; use the item row's existing `HQ` checkbox when the list should target HQ instead. XA Database remains the source for character ownership, stock matching, and forecasts after an item is selected, but Xagman treats only exact `Inventory 1`-`Inventory 4` rows as held/tradable stock.
- **Xagman conditional Shared Item policies** - The Franchise Owner Mode dropdown now offers 12 policies: ordinary `Give`, `Take`, `Balance`, and `TopUp`, plus `if Subs` and `if Retainers` variants of each. An ordinary row remains the fallback for every owner; a matching conditional row overrides it in the deterministic order Subs, then Retainers, then fallback. The same Item ID/HQ can therefore appear once for each applicability so, for example, `Ceruleum Tank - Give 0` offloads every tank from owners without a matching conditional policy while `Ceruleum Tank - Balance if Subs 22,650` keeps submarine owners at 22,650. A conditional item group is skipped when that owner's AutoRetainer registration is unknown; a successful AutoRetainer snapshot that omits the owner is known zero. Registration changes which policy applies but never makes retainer or submarine storage tradable: only `Inventory 1`-`Inventory 4` counts, and all existing amount-zero meanings remain unchanged. Saved lists and XA Slave JSON schema 3 preserve applicability; schema 1/2 imports retain their existing exact rows, and older JSON with no applicability loads as ordinary fallback rows. Teamcraft and Artisan imports remain ordinary `Balance`, while Teamcraft export refuses a conditional or typed-target list and directs you to the lossless Xagman export.
- **Xagman green-gear value targets** - The Franchise Owner Shared Item List `Add Item` picker also offers typed `Green Item GC Seals` and `Green Item FC Credits / Rank Progress` targets. These rows stay in the `TopUp` mode with ordinary, `if Subs`, or `if Retainers` applicability, cannot be added to the Tony Search Item List, require a same-protocol connected-peer run, and are refused by Outside Network Helper. Each owner counts safe eligible green gear already held in `Inventory 1`-`Inventory 4` and every active Armoury container, including Soul Crystal; Tony's transferable supply uses the main bags and Dropbox-supported Armoury containers, excluding Soul Crystal. Eligible gear must be green rarity, equippable, vendor-sellable, have an expert-delivery reward, and be confirmed tradable, unbound/non-collectable, unglamoured, unmateriaed, outside every gearset, and not protected by AutoRetainer. Unknown container, protection, or peer-capability state fails closed. `GC Seals` uses Lumina's expert-delivery reward and is identical for NQ and HQ. `FC Credits / Rank Progress` uses exact half-credit arithmetic: NQ is item level × 1.5 and HQ is item level × 3, so HQ doubles the FC value and odd-level NQ `.5` values are preserved. Both selectors consume one physical Tony supply pool: each gear instance is queued once, while that one transfer contributes its intrinsic seal and FC-credit values to both active targets. Exact-item supply reserves the same physical stock first and reduces both value shortages before aggregate gear is selected. The aggregate forecast shows current eligible value, target, and remaining shortage. Lossless XA Slave JSON is schema 3; schema 1/2 and older exact-item saved lists migrate as exact-item rows with their behavior unchanged. The existing collection-first exact-item Inventory 1-4 contract is unchanged. Focused source checks passed on 2026-07-25; build, reload, packaging, publication, client reset, and live multi-client acceptance were not run.
- **Xagman collection-first two-pass runs** - When conditional Shared Item policies exist, the default-off `Prioritize Characters Giving Items First` checkbox appears on each Franchise Owner's Shared Item List. The preference is FO-owned: every idle FO advertises its own setting and conditional-policy capability, while Tony ignores Tony's locally saved checkbox value and Shared Item list. All participating FOs off starts the legacy combined flow; all FOs on with conditional policies and coordination protocol 2 starts collection-first; mixed, invalid-policy, or incompatible clients are named and refused before Tony creates a run ID, freezes the cohort/forecast, or broadcasts Start Task. Tony's Peers table shows each FO as `On`, `Off`, `Invalid`, or `Old build`. In a valid run Tony freezes the connected FO cohort and forecast baseline, completes one global collection pass containing only effective `Give` and `Balance` surplus work, and waits at a visible barrier until every expected FO acknowledges the same run and phase; clients with no collection candidates acknowledge immediately and remain visibly paused. Tony then resets the full originally selected Tony roster and world/data-center sweep, while every FO restarts its saved restock plan from the beginning for effective `Take`, `Balance` deficit, and `TopUp` work. Final completion waits for every expected FO restock acknowledgement, then rebroadcasts one run-scoped cleanup directive until every frozen FO acknowledges receipt or the bounded delivery window fails closed with Tony still connected in Error. The forecast distinguishes `Stock Now` from `Stock After Collect` and `Short Now` from `Short After` using the frozen baseline. Only `Inventory 1`-`Inventory 4` counts. Missing/stale/wrong-protocol peers fail closed, Stop/cancel never advances a barrier, Outside Network Helper is excluded, and every participant must be rebuilt/reloaded from the same source before use.
- **Xagman live inventory pull** - When a character is logged in, `Pull XA Database Info` first asks XA Database to run its live `Refresh + Save` operation, then rereads the committed snapshot and refreshes Xagman's Tony/Franchise Owner table values, matching data, and forecasts. When logged out, it only reads the last saved XA Database snapshot. A failed live save is reported and is never presented as fresh data.
- **Xagman final Tony disconnect** - When an ordinary overall Tony run reaches natural terminal completion, Tony finishes any optional FC return, gives a pending peer-completion notification a bounded send window, then explicitly disconnects its local Xagman peer service before optional Logout, Kill Game, or Enable AR Multi Mode actions. Collection-first runs use the stricter frozen-cohort contract above: every FO must acknowledge the run-scoped cleanup directive, and a timeout keeps Tony connected in Error instead of logging out or closing the client while an FO may still be paused. Intermediate Tony rotation, Server Matching handoffs, standby, and selling remain connected.
- **Xagman Server Matching rotation safeguards** - Requested-supply replacement arrival follows Tony's published live coordinates before final target/range closure. Tony checks usable requested stock before any range wait: an empty replacement rotates immediately, while a stocked Tony stays stationary with its exact call open for up to 600 seconds. Out-of-range polling writes `Tony resumed...` only when the actual trade task starts. A replacement Tony must belong to the active sweep region, and its meet world must belong to the active server. The old first-global-run-list fallback is blocked: no same-region replacement advances only through the explicit sweep path or stops visibly, so an OCE Tony cannot inherit a JP destination.
- **Xagman connected trade-capacity forecast** - While the Tony role is connected to Franchise Owner peers, a compact forecast below the Tony table combines the peers' selected-owner snapshots with the currently selected Tonys. Server Matching keeps capacity and stock totals separate by physical region; fixed-world runs use one combined pool. Give items and Balance surpluses estimate incoming collection slots with Lumina stack sizes and known partial-stack headroom, while Take, Balance deficits, and TopUp deficits report item-by-item need from the Tony pool. A second table shows the same pooled need beside each selected Tony's current available stock without pretending to reserve or split it; runtime order and rotation remain authoritative. Only main `Inventory 1`-`Inventory 4` stock is treated as tradable, NQ and HQ remain separate, and remote inventory must come from a real XA Database snapshot no more than 45 days old. Unknown, stale, failed-search, duplicate-owner, and Take-quantity-0 all-available cases stay visibly indeterminate instead of becoming false zeroes; a successful zero-match search remains a real zero through a read-only XA Database fallback. Forecast work refreshes outside ImGui drawing, remains advisory, and never replaces the live inventory and trade reconciliation used during a run.
- **Xagman selected-Tony collection capacity** - For one incoming item such as Ceruleum Tanks, the connected forecast converts every selected Tony's known empty `Inventory 1`-`Inventory 4` slot into one stack at the Lumina stack size, adds matching NQ/HQ partial-stack room separately, and shows both `Can Collect now` and the quantity remaining. A Tony with 140 empty slots can receive 140 stacks × 999 = 139,860 tanks; four such Tonys show 560 empty stack slots and 559,440 tanks of empty-slot capacity. Checking or unchecking Tonys refreshes the cached total. Fixed-world mode keeps one authoritative combined result and, when collection work is present, lists the cached selected-Tony raw capacity beneath it by NA / EU / JP / OCE. A regional capacity is labeled exact `Can Collect` only for one incoming item when every selected Tony snapshot is known, combined incoming is at least the combined known capacity, and no other forecast-input warning is active; low incoming, uncertain inputs, and multi-item workloads remain capacity-only with no regional Remaining allocation. Server Matching keeps its independently capped per-region math and informational all-selected-regions total unchanged. When several incoming item types share the bags, the forecast shows the shared slot count without falsely multiplying those slots once per item.
- **Xagman Franchise Owner forecasts and finite Take reconciliation** - A cached setup forecast below the Franchise Owners table covers finite `Give`, positive `Balance`, and finite `Take` rows. For `Give N`, N is one pooled planning target while runtime still caps each owner at N: Known to Give sums each selected owner's capped contribution and Still Needed is `max(N - known contribution, 0)`, so the screenshot case `963,096 - 967,261` correctly reports `0`. Balance remains per owner and totals every owner's independent `max(0, target - inventory)` deficit, so another owner's surplus cannot hide it. Take receive-capacity evaluates one configured batch per selected owner: it credits matching partial stacks, totals all finite Take rows per owner, and compares that owner's required slots with that owner's free main-inventory slots before reporting bag-ready, space-short, and unknown owners. At runtime, finite `Take N` is exactly one N-item inventory increase for that owner: Xagman records the Inventory 1-4 starting quantity, requests only the remaining amount needed to reach `start + N`, carries that target through partial trades and Tony rotation, and clears the request after the owner inventory proves the target was reached. For example, 6 held with `Take 4` requests 4 and completes at 10 instead of requesting another 4. `Take 0` keeps its all-available behavior. Owner held counts and Tony Dropbox-eligible counts are logged separately; giver safety remains authoritative. `Give 0` has no finite goal and `Take 0` has unknown receive volume, so both remain visibly indeterminate. Only Inventory 1-4 and matching NQ/HQ data count. The view works before peer connection and in Outside Network Helper mode, refreshes outside Draw, changes no trade queues, and is hidden during an active owner run in favor of live progress.
- **Xagman Tony supplier matching** - Tony `Select Matching Items` selects visible Tonys that hold a Tony Search Item List item in main `Inventory 1`-`Inventory 4`. Saddlebags, retainers, market listings, armoury/equipped slots, crystals, and every other saved container are excluded. Its right-click menu adds `Retainers Only`, `Subs Only`, `Without Retainers`, and `Without Subs`; these scopes describe AutoRetainer registration only and do not make retainer-held items tradable. Each scoped choice refreshes AutoRetainer and intersects that registration scope with the existing Region, Search, and `Selected Only` visibility filters plus the main-inventory holder match. A failed or empty AutoRetainer read preserves the current Tony selection.
- **Xagman AutoRetainer roster columns** - Both Tony and Franchise Owner tables include sortable `Retainers` and `Submarines` columns that show positive registered AutoRetainer counts; zero or unavailable registrations stay blank. Owner-side `Retainers Only` and `Subs Only` buttons refresh that data and replace the current selection under the active Region and Search filters. Normal `Select Matching Items` selects every owner under those filters that needs the configured item change based only on `Inventory 1`-`Inventory 4` stock; right-click it for positive or inverse AutoRetainer registration scopes. These owner mass selectors recalculate independently of the old `Selected Only` subset, while Tony matching retains its established visible-row behavior.
- **Xagman optional table columns** - Both character tables let you right-click the headers or rows to hide and restore informational columns. The native chooser uses the full names `Inventory`, `Retainers`, `Submarines`, and `Delete` instead of abbreviations or an unknown action label. Each table saves its own column choices; all informational columns are visible by default, while Character/selection and Delete remain available. Column visibility is presentation-only and does not change filtering, selection, matching, forecasts, or trade execution.
- **Monthly Relogger** - Cycle through characters with AutoRetainer support, XA Database rank and plot visibility, optional per-character actions, and shared completion actions. A 300-second per-character login timeout failsafe handles deleted/unavailable characters: if a `/ays relog` never loads within 300s the run re-runs the pre-flight checklist to recover a safe state (back to the main menu) and moves on instead of getting stuck. The processing order highlights each character - red for failed to log in, purple for logged in but the process could not finish - shows each character's processing time plus a rolling `Avg/char, remaining, ETA` estimate, and persists as a reviewable "Last relogger run" snapshot after completion until you press `Clear results`. Importing or refreshing from AutoRetainer flags any character AutoRetainer has no data for (manually added or unknown) with a red `(not found in AR)` marker - these cannot be relogged and are a common cause of login failures.
- **Shared Task Completion Options** - Major task panels share one completion footer for `Logout`, `Kill Game`, and `Enable AR Multi Mode`. `Kill Game` uses XA's hard logout and close-client flow even when `Instant Logout` is disabled.
- **Prep Logistics** - Relog selected characters, check main-inventory space, move them to a target world or location, and finish with shared completion actions.
- **Auto-Accept FC Invites** - Accept FC invitations automatically, wait for a configured period, and optionally leave again through the floater-assist flow.
- **FC Permissions Updater** - Review FC rosters with member-rank and FC-rank visibility before applying shared permission updates.
- **Check Duplicate Plots** - Scan characters for duplicate housing plots and optionally rerun follow-up actions with the shared completion flow.
- **Return Alts To Homeworlds** - Send characters back to their home worlds with the shared task action flow.
- **Refresh Sub/Bell/Chest** - Refresh workshop and bell interactions with prep actions, region filters, bell-only mode, safer menu recovery, optional Company Chest gil sync, and shared completion actions.
- **Field Operations** - Eureka tools for instance tracking and Logos Manipulator automation. `Instance Hunter` handles per-zone InstanceID, Rodney controls, duty-ready commence, alerts, and rollover until new instances are found; `Logogram Creator` adds favorites, recipe locks, queue automation, overlays, and a floating cancel control.
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

  `Replace Unowned Mount Hotbars` is default-off. For native Mount-type slots whose assigned mount is
  not unlocked on the current character, it displays and executes the game's current Mount Roulette
  action. Owned mounts and non-Mount slots remain native, and XA does not rewrite or save hotbar data.

  </details>

- **Plugin Operations** - Manage startup behavior (including Open Plugin on Load and Custom Resolution on Plugin Load, which force-resizes the game window to a saved width/height with an optional Ignore Minimum Window Size sub-option), verbose logging, titlebar favourites, version display, update history, and quick actions such as presets, rendering presets, Sit/Doze, All XA Mods Off, task stop, Xagman disconnect, and Kill Game.
- **Export Data** - Export AutoRetainer, Lifestream, and XA Database tables to timestamped TSV/CSV files or overwrite a fixed path for automation.
- **Repo List** - Review all plugins from the referenced repositories in one sortable table with group, author, plugin status, installer/settings shortcuts, and copy-to-clipboard repo actions.
- **IPC Calls Available** - Check supported IPC integrations, live/cached plugin availability, XA Slave provider channels, and direct examples such as `XASlave.ExecuteCommand("xamods")`.
- **Commands** - Browse the current `/xa` command surface in searchable grouped tables for general commands, XA Mods categories, Dropbox queueing, movement helpers, and item commands.
- **Support Diagnostics** - `/xa debug` reveals the hidden Debug / Test panel for support-guided checks and keeps it visible across reloads until toggled off.
- **Priority Tasks** - Long-running automation tasks share one active-task lock, cross-panel stop controls, pulsing menu status, and clearer DTR visibility.
- **XA Mods Native Hooks** - 50+ local QoL hooks cover multi-instance handling, login/queue cleanup, menu and duty recovery, inventory actions, return/logout shortcuts, rendering, camera controls, teleport-lock recovery, and other client utilities. Startup prioritizes safety hooks, defers heavier work, and restores live rendering, UI visibility, and nameplate privacy on unload.

## Patch 7.55 Beta Compatibility

- XA Slave v0.0.0.41 keeps its existing API 15 package and version. The current Patch 7.55 executable resolves every reviewed XA Slave protected signature exactly once, so this pass does not introduce speculative signature replacements.
- Eureka Logogram Creator now validates `Framework`, `UIModule`, `RaptureAtkModule`, and the requested number array before reading Logogram or Logos Action stock. Invalid shard counts and unavailable native state fail closed inside that feature.
- Debug and Release warning-as-error builds pass against the Patch 7.55 dependency set. In-client Logogram Creator, AgentLobby, Xagman, task, and reload checks remain required before runtime acceptance.
- The in-plugin v0.0.0.41 Update History groups the large July change set by feature while retaining every shipped behavior and safety mention; the full release detail remains in `docs/release-notes-v0.0.0.41.md`.

## Commands

The in-plugin `References > Commands` page is the full index for command descriptions and notes. The same XA command surface can also be used over IPC through `XASlave.ExecuteCommand`.

<details>
<summary>General</summary>

| Command | Purpose |
| --- | --- |
| `/xa` | Toggle the XA Slave window. |
| `/xa allrestore` | Disable every top-level XA Mod toggle. |
| `/xa commands` | Open `References > Commands`. |
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

- **Optional:** [XA Database](https://github.com/xa-io/XA-Database) - For Save to XA Database, Xagman character ownership/stock matching and forecasts, and IPC data collection. Xagman `Add Item` lookup itself uses Lumina.

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
