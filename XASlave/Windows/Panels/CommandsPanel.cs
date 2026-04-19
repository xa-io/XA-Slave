using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace XASlave.Windows;

/// <summary>
/// Commands reference panel for the current XA Slave slash-command surface.
/// </summary>
public partial class SlaveWindow
{
    private readonly record struct CommandReferenceEntry(string Command, string Purpose, string Notes);
    private string commandsSearchText = string.Empty;

    private static readonly CommandReferenceEntry[] GeneralCommandEntries =
    {
        new("/xa", "Toggle the XA Slave window.", "This is the root command when entered without a subcommand."),
        new("/xa allrestore", "Disable every top-level XA Mod toggle.", "Chat equivalent of the `Disable All Mods` button."),
        new("/xa commands", "Open the window directly to `References > Commands`.", "Useful when you want the command inventory instead of just toggling the main window."),
        new("/xa updates", "Open the version history window.", "Shows the plugin changelog as collapsible version entries. Also accessible from Plugin Operations > Show Updates."),
        new("/xa db <itemId:qty ...>", "Queue Dropbox trade items from local inventory and start trading.", "Accepts the same `itemId:qty` syntax as the older external queue flow, but XA now owns the command surface directly."),
        new("/xa db inv", "Queue all eligible items from `Inventory1` through `Inventory4` and start trading.", "Scans the four main inventory bags only, preserves NQ/HQ quantities, skips bound/collectable/untradable entries, and adds the results to the current Dropbox queue."),
        new("/xa db clear", "Clear the current Dropbox item queue.", "Used by Xagman cleanup and for manual queue resets without any extra wrapper command."),
        new("/xa db request <itemId:qty ...>", "Print the missing quantities still needed locally.", "Returns a ready-to-run `/xa db ...` command built from the requested amounts minus what this character already has."),
        new("/xa db <shortcut>", "Build missing crystal-fill commands.", "Shortcuts: `shards`, `crystals`, `clusters`, `shards+crystals`, `crystals+clusters`, and `shards+crystals+clusters`. Prints the equivalent `/xa db ...` request output."),
        new("/xa preset list", "List the currently saved XA Mods presets.", "Prints each saved preset name to chat."),
        new("/xa preset load <name>", "Load a saved XA Mods preset by name.", "Applies the saved mod-key set after clearing the current top-level toggles."),
        new("/xa preset save <name>", "Save the current XA Mods selection as a preset.", "Creates or overwrites a saved list without opening the panel."),
        new("/xa xamods | /xa mods", "Open the window directly to `Utility > XA Mods`.", "Both commands resolve to the same XA Mods panel open action."),
    };

    private static readonly CommandReferenceEntry[] GameModsCommandEntries =
    {
        new("/xa anonymous on|off", "Toggle `Live Anonymous Mode`.", "Masks visible player nameplates locally with deterministic `Firstname Lastname` aliases."),
        new("/xa chocobocutscene on|off", "Toggle `Skip Cutscenes Feeding Chocobo`.", "Only affects the companion-feeding cutscene surface."),
        new("/xa closeerrors on|off", "Toggle `Close Lobby Errors`.", "Auto-confirms supported disconnect and stuck-logout lobby Dialogue popups, including `3102`."),
        new("/xa copyitemname on|off", "Toggle `Copy Item Name For All`.", "Adds XA item-name copy actions to supported context menus."),
        new("/xa gamerestore", "Disable the current top-level Game Mods toggles.", "Turns off the current Game Mods section in one command."),
        new("/xa hidepopups on|off", "Toggle `Hide Unnecessary Popups`.", "Closes supported tutorial and recommendation popups."),
        new("/xa logincooldown on|off", "Toggle `Cancel Login Cooldown`.", "Clears the local temporary character-select cooldown."),
        new("/xa msqprogress on|off", "Toggle `Display MSQ Progress`.", "Expands Scenario Tree with remaining-count and completion details."),
        new("/xa multiinstance on|off", "Toggle `Allow Multiple Game Instances`.", "Also accepts `/xa multibox on|off` as an alias."),
        new("/xa playersearch on|off", "Toggle `Expanded Player Right-Click Menu Search`.", "Adds XA search-provider shortcuts to supported player context menus."),
        new("/xa preventlobbyexit on|off", "Toggle `Prevent Game Exiting From Lobby Errors`.", "Overrides the local forced-shutdown countdown for the supported lobby dialog."),
        new("/xa queueposition on|off", "Toggle `Display Actual Queue Position`.", "Shows queue position and ETA details when available."),
        new("/xa skipcutscenes on|off", "Toggle `Skip Cutscenes`.", "Controls XA's main cutscene-skip surface."),
        new("/xa skipdialogue on|off", "Toggle `Skip Dialogue`.", "Auto-advances the `Talk` addon and the broader native talk surfaces when available."),
    };

    private static readonly CommandReferenceEntry[] GraphicModsCommandEntries =
    {
        new("/xa bgpause on|off", "Toggle `Disable Background Rendering`.", "Arms or disarms the DX11 and nameplate background-render pause hooks."),
        new("/xa customres on|off", "Toggle `Custom Resolutions`.", "Enables or disables XA's custom-resolution control surface."),
        new("/xa hideobjects on|off", "Toggle `Hide Game Objects`.", "Uses the XA object-hide seam with the current category filters plus the duty, island, and Occult Crescent options."),
        new("/xa lowres <scale>", "Set and enable `Low Resolution` from chat.", "Accepts values from `0.01` to `1.00`, including inputs such as `1` and `0.01`, and temporarily switches DLSS to AMD FSR while the feature is active."),
        new("/xa lowres off", "Disable `Low Resolution`.", "Turns off the forced low-resolution scale and restores the previous runtime upscale mode."),
        new("/xa minwindow on|off", "Toggle `Ignore Minimum Window Size`.", "Lowers or restores XA's guarded local minimum-size floor and corrects undersized restore or maximize results after the window changes."),
        new("/xa res <width>x<height>", "Apply a custom client resolution such as `/xa res 500x345`.", "Requires `Custom Resolutions` to be enabled in XA Mods."),
        new("/xa res add <width>x<height>", "Add a saved custom-resolution button.", "Stores a panel preset without needing to use the UI add-button flow."),
        new("/xa res remove <width>x<height>", "Remove a saved custom-resolution button.", "Deletes the matching saved preset when present."),
        new("/xa resrestore", "Disable the current top-level Graphic Mods toggles.", "Also runs the normal Special Rendering Modes world/UI restore behavior."),
        new("/xa specialrender on|off", "Toggle `Special Rendering Modes`.", "Shows or hides the Special Render tools, reapplies the stored UI-visibility toggles when turned on, and off restores XA-touched world/UI state. `Hide Chat` cannot be enabled while AutoRetainer Multi Mode is active."),
    };

    private static readonly CommandReferenceEntry[] PlayerModsCommandEntries =
    {
        new("/xa antiafk on|off", "Toggle `Anti-AFK`.", "Resets the local AFK timer roughly every 9 minutes while the mod stays enabled so the client stays ahead of the game's 10-minute AFK kick."),
        new("/xa equip <itemId>", "Equip an item by ID.", "XA scans the main inventory and armory chest, then queues the matching equip move to the correct slot."),
        new("/xa doze", "Trigger Doze Anywhere.", "Requires `Doze & Sit Anywhere` to be enabled."),
        new("/xa expertdelivery on|off", "Toggle `Automate Expert Delivery`.", "Controls the hand-in automation feature, not the unlock bypass."),
        new("/xa instantlogout on|off", "Toggle `Instant Logout`.", "Arms or disarms XA's hard logout seam and the `/xa logout` command."),
        new("/xa itemcommands on|off", "Toggle `Item Commands`.", "Adds XA's `/xa equip <itemId>` command."),
        new("/xa killgame", "Hard logout, then close the client.", "Requires `Instant Logout` to be enabled. XA waits for logout to complete before sending `/xlkill`, and the action is blocked while Special Rendering Modes is hiding chat."),
        new("/xa leaveduty on|off", "Toggle `Auto Leave Duty`.", "Also accepts `/xa autoleaveduty on|off`. XA watches for duty completion, waits the configured delay, then opens the duty menu and confirms Leave Duty once combat and blocking duty UI states are clear."),
        new("/xa automerge on|off", "Toggle `Auto Merge`.", "Watches for the inventory window to open, then merges incomplete main-bag stacks together locally."),
        new("/xa instancereturn on|off", "Toggle `Instance Return`.", "XA hooks the native Return action so it can skip the cast/cooldown path, leave the in-game confirmation prompt manual, and stay off in PvP. XA also leaves or disbands party first when the fast-return path needs party-state cleanup."),
        new("/xa sit", "Trigger Sit Anywhere.", "Requires `Doze & Sit Anywhere` to be enabled."),
        new("/xa logout", "Trigger XA's hard logout seam.", "Requires `Instant Logout` to be enabled, and the action is blocked while Special Rendering Modes is hiding chat."),
        new("/xa peep [on|off|clear]", "Open XA Peep or control its XA target tracker.", "Without arguments it toggles the XA Peep window. `on/off` enables or disables tracking, and `clear` wipes the stored XA Peep history."),
        new("/xa playerrestore", "Disable the current top-level Player Mods toggles.", "Useful for dropping movement, sprint, sit/doze, sight-distance, and logout-related XA Mods back to off."),
        new("/xa refusetrade on|off", "Toggle `Refuse Trade Request`.", "Uses the trade-window and status-update refusal surfaces plus the current local feedback and extra-command options."),
        new("/xa revealmap on|off", "Toggle `Reveal Undiscovered Areas`.", "Clears local map-discovery flags when the map agent refreshes."),
        new("/xa sightdistance on|off", "Toggle `Custom Sight Distance`.", "Turns the current camera override profile on or off."),
        new("/xa sitdoze on|off", "Toggle `Doze & Sit Anywhere`.", "Controls the master emote hook used by `/xa sit`, `/xa doze`, and the panel/titlebar quick actions."),
        new("/xa sprint on|off", "Toggle `Infinite Sprint`.", "Controls XA's movement-gated Sprint recast surface."),
        new("/xa sprintdelay <seconds>", "Set the `Infinite Sprint` movement-start delay.", "Accepts values from `0.0` to `30.0` seconds."),
        new("/xa teleportlock on|off", "Toggle `Clear Teleportation Lock`.", "Uses XA's teleport-stuck recovery seam."),
    };

    private static readonly CommandReferenceEntry[] PluginModsCommandEntries =
    {
        new("/xa anonchars on|off", "Toggle `Anonymize Character Lists`.", "Forces screenshot-safe aliases in XA Slave character-list tables and duplicate summaries. Every task-list `Anonymize` checkbox writes to this same shared global setting, and it can also be exposed through titlebar favourites because it is a normal XA Mod."),
        new("/xa peepingtom on|off", "Toggle `Force PeepingTom`.", "Controls XA's runtime PvP forcing path for PeepingTom."),
        new("/xa pluginrestore", "Disable the current top-level Plugin Mods toggles.", "Covers XA's plugin-scoped toggles such as `Anonymize Character Lists` and `Force PeepingTom`."),
    };

    private static readonly CommandReferenceEntry[] IllegalModsCommandEntries =
    {
        new("/xa imlegit", "Disable the current `Illegal Shit You Shouldn't Use` toggles.", "Quickly turns off the risky local unlock/spoof surfaces in that section."),
        new("/xa moveafterdeath on|off", "Toggle `Moveable After Death`.", "Controls XA's local movement-permission hook after death."),
        new("/xa unlockexpert on|off", "Toggle `Unlock Expert Delivery`.", "Controls the local Grand Company rank spoof used to expose Expert Delivery."),
    };

    private void DrawCommandsReference()
    {
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Commands");
        ImGui.TextDisabled("Current XA Slave command inventory in a compact grouped table layout.");

        ImGui.Spacing();
        ImGui.SetNextItemWidth(Math.Max(Scale(220f), ImGui.GetContentRegionAvail().X));
        ImGui.InputTextWithHint(
            "##CommandsSearch",
            "Search commands, settings, descriptions, and notes",
            ref commandsSearchText,
            256);

        var visibleSectionCount = 0;
        DrawFilteredCommandSection("General", GeneralCommandEntries, ref visibleSectionCount);
        DrawFilteredCommandSection("Game Mods", GameModsCommandEntries, ref visibleSectionCount);
        DrawFilteredCommandSection("Graphic Mods", GraphicModsCommandEntries, ref visibleSectionCount);
        DrawFilteredCommandSection("Player Mods", PlayerModsCommandEntries, ref visibleSectionCount);
        DrawFilteredCommandSection("Plugin Mods", PluginModsCommandEntries, ref visibleSectionCount);
        DrawFilteredCommandSection("Illegal Shit You Shouldn't Use", IllegalModsCommandEntries, ref visibleSectionCount);

        if (visibleSectionCount == 0)
        {
            ImGui.TextDisabled("No commands matched the current search.");
            ImGui.TextDisabled("Search matches command text, setting names, descriptions, and notes.");
        }
    }

    private void DrawFilteredCommandSection(string title, CommandReferenceEntry[] entries, ref int visibleSectionCount)
    {
        var filteredEntries = FilterCommandEntries(entries);
        if (filteredEntries.Length == 0)
            return;

        visibleSectionCount++;
        DrawCommandSection(title, filteredEntries);
    }

    private CommandReferenceEntry[] FilterCommandEntries(CommandReferenceEntry[] entries)
    {
        if (string.IsNullOrWhiteSpace(commandsSearchText))
            return entries;

        var queryTerms = commandsSearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (queryTerms.Length == 0)
            return entries;

        var matches = new List<CommandReferenceEntry>();
        foreach (var entry in entries)
        {
            if (MatchesCommandSearch(entry, queryTerms))
                matches.Add(entry);
        }

        return matches.ToArray();
    }

    private static bool MatchesCommandSearch(CommandReferenceEntry entry, string[] queryTerms)
    {
        var haystack = $"{entry.Command}\n{entry.Purpose}\n{entry.Notes}";
        foreach (var term in queryTerms)
        {
            if (!haystack.Contains(term, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static void DrawCommandSection(string title, CommandReferenceEntry[] entries)
    {
        DrawWrappedDisabledText(title);

        if (ImGui.BeginTable(
                $"CommandsReferenceTable##{title}",
                2,
                ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.PadOuterX))
        {
            ImGui.TableSetupColumn("Command", ImGuiTableColumnFlags.WidthFixed, Scale(210f));
            ImGui.TableSetupColumn("Description", ImGuiTableColumnFlags.WidthStretch);

            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                ImGui.TableNextRow();
                ImGui.TableSetBgColor(
                    ImGuiTableBgTarget.RowBg0,
                    ImGui.GetColorU32(i % 2 == 0
                        ? new Vector4(0.28f, 0.28f, 0.28f, 0.24f)
                        : new Vector4(0.03f, 0.03f, 0.03f, 0.24f)));

                ImGui.TableNextColumn();
                ImGui.PushTextWrapPos(0f);
                ImGui.TextColored(new Vector4(0.88f, 0.92f, 0.98f, 1.0f), entry.Command);
                ImGui.PopTextWrapPos();

                ImGui.TableNextColumn();
                ImGui.PushTextWrapPos(0f);
                ImGui.TextUnformatted($"{entry.Purpose} {entry.Notes}");
                ImGui.PopTextWrapPos();
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
    }
}
