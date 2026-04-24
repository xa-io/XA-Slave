using System.Collections.Generic;
using System.Numerics;
using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace XASlave.Windows;

/// <summary>
/// Standalone version-history window — displays changelog entries as collapsible tree nodes.
/// Opened via <c>/xa updates</c> or the ⬆ Show Updates toggle in Plugin Operations.
/// </summary>
public sealed class UpdatesWindow : Window
{
    private const string UpdatesWindowTitle = "XA Slave - Updates";
    private static float UiScale => ImGuiHelpers.GlobalScale;
    private static float UiScaleSafe => ImGuiHelpers.GlobalScaleSafe;

    private bool firstDraw = true;

    private readonly List<VersionEntry> versions = new()
    {
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
                "- AR post-processing can now capture FC chest gil — Check FC Chest For Gil targets, paths, interacts, saves, and closes the chest automatically",
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
