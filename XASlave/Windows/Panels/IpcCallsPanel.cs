using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace XASlave.Windows;

/// <summary>
/// IPC Calls Available panel - partial class split from SlaveWindow.
/// </summary>
public partial class SlaveWindow
{
    // Cached IPC connectivity - refreshed every 5 seconds to avoid HITCH warnings
    private DateTime ipcCacheExpiry = DateTime.MinValue;
    private bool cachedArAvail, cachedLsAvail, cachedYaAvail, cachedDelAvail, cachedPbAvail, cachedDbxAvail;
    private bool cachedTaAvail, cachedArtAvail, cachedSplatAvail, cachedHonorificAvail;

    // Live IPC bool polling
    private DateTime liveIpcExpiry = DateTime.MinValue;
    private readonly Dictionary<string, bool?> liveIpcValues = new();

    private void RefreshIpcCache()
    {
        if (DateTime.UtcNow < ipcCacheExpiry) return;
        ipcCacheExpiry = DateTime.UtcNow.AddSeconds(5);
        cachedArAvail = plugin.IpcClient.IsAutoRetainerAvailable();
        cachedLsAvail = plugin.IpcClient.IsLifestreamAvailable();
        cachedYaAvail = plugin.IpcClient.IsYesAlreadyAvailable();
        cachedDelAvail = plugin.IpcClient.IsDeliverooAvailable();
        cachedPbAvail = plugin.IpcClient.IsPandorasBoxAvailable();
        cachedDbxAvail = plugin.IpcClient.IsDropboxAvailable();
        cachedTaAvail = plugin.IpcClient.IsTextAdvanceAvailable();
        cachedArtAvail = plugin.IpcClient.IsArtisanAvailable();
        cachedSplatAvail = plugin.IpcClient.IsSplatoonAvailable();
        cachedHonorificAvail = plugin.IpcClient.IsHonorificAvailable();
    }

    private void RefreshLiveIpcValues()
    {
        if (!plugin.Configuration.IpcLivePullsEnabled) return;
        if (DateTime.UtcNow < liveIpcExpiry) return;
        liveIpcExpiry = DateTime.UtcNow.AddSeconds(plugin.Configuration.IpcLivePullIntervalSeconds);

        try
        {
            // XA Database
            liveIpcValues["XA.Database.IsReady"] = plugin.IpcClient.IsReady();

            // vnavmesh
            liveIpcValues["vnavmesh.Nav.IsReady"] = plugin.IpcClient.VnavIsReady();
            liveIpcValues["vnavmesh.Path.IsRunning"] = plugin.IpcClient.VnavPathIsRunning();
            liveIpcValues["vnavmesh.Nav.PathfindInProgress"] = plugin.IpcClient.VnavNavPathfindInProgress();
            liveIpcValues["vnavmesh.SimpleMove.PathfindInProgress"] = plugin.IpcClient.VnavSimpleMovePathfindInProgress();

            // AutoRetainer
            if (cachedArAvail)
            {
                liveIpcValues["AutoRetainer.GetSuppressed"] = plugin.IpcClient.AutoRetainerGetSuppressed();
                liveIpcValues["AutoRetainer.GetMultiModeEnabled"] = plugin.IpcClient.AutoRetainerGetMultiModeEnabled();
                liveIpcValues["AutoRetainer.PluginState.IsBusy"] = plugin.IpcClient.AutoRetainerPluginStateIsBusy();
                liveIpcValues["AutoRetainer.PluginState.AreAnyRetainersAvailableForCurrentChara"] = plugin.IpcClient.AutoRetainerPluginStateAreAnyRetainersAvailableForCurrentChara();
                liveIpcValues["AutoRetainer.PluginState.GetMultiModeStatus"] = plugin.IpcClient.AutoRetainerPluginStateGetMultiModeStatus();
                liveIpcValues["AutoRetainer.PluginState.CanAutoLogin"] = plugin.IpcClient.AutoRetainerPluginStateCanAutoLogin();
                liveIpcValues["AutoRetainer.PluginState.GetOptionRetainerSense"] = plugin.IpcClient.AutoRetainerPluginStateGetOptionRetainerSense();
                liveIpcValues["AutoRetainer.PluginState.AreAnyEnabledVesselsNotDeployed"] = plugin.IpcClient.AutoRetainerPluginStateAreAnyEnabledVesselsNotDeployed(Plugin.PlayerState.ContentId);
                liveIpcValues["AutoRetainer.PluginState.AreAnyEnabledVesselsReady"] = plugin.IpcClient.AutoRetainerPluginStateAreAnyEnabledVesselsReady(Plugin.PlayerState.ContentId);
            }

            // Lifestream
            if (cachedLsAvail)
                liveIpcValues["Lifestream.IsBusy"] = plugin.IpcClient.LifestreamIsBusy();

            // YesAlready
            if (cachedYaAvail)
                liveIpcValues["YesAlready.IsPluginEnabled"] = plugin.IpcClient.YesAlreadyIsEnabled();

            // Deliveroo
            if (cachedDelAvail)
                liveIpcValues["Deliveroo.IsTurnInRunning"] = plugin.IpcClient.DeliverooIsTurnInRunning();

            // Dropbox
            if (cachedDbxAvail)
                liveIpcValues["Dropbox.IsBusy"] = plugin.IpcClient.DropboxIsBusy();

            // TextAdvance
            if (cachedTaAvail)
            {
                liveIpcValues["TextAdvance.IsEnabled"] = plugin.IpcClient.TextAdvanceIsEnabled();
                liveIpcValues["TextAdvance.IsBusy"] = plugin.IpcClient.TextAdvanceIsBusy();
                liveIpcValues["TextAdvance.IsPaused"] = plugin.IpcClient.TextAdvanceIsPaused();
            }

            // Artisan
            if (cachedArtAvail)
            {
                liveIpcValues["Artisan.IsBusy"] = plugin.IpcClient.ArtisanIsBusy();
                liveIpcValues["Artisan.GetEnduranceStatus"] = plugin.IpcClient.ArtisanGetEnduranceStatus();
                liveIpcValues["Artisan.IsListRunning"] = plugin.IpcClient.ArtisanIsListRunning();
                liveIpcValues["Artisan.IsListPaused"] = plugin.IpcClient.ArtisanIsListPaused();
                liveIpcValues["Artisan.GetStopRequest"] = plugin.IpcClient.ArtisanGetStopRequest();
            }

            // Splatoon
            if (cachedSplatAvail)
                liveIpcValues["Splatoon.IsLoaded"] = plugin.IpcClient.SplatoonIsLoaded();

            // XA Slave (own channels - direct query, no IPC round-trip)
            liveIpcValues["XASlave.IsBusy"] =
                plugin.TaskRunner.IsRunning
                || plugin.AutoCollector.IsRunning
                || plugin.AutoOpenMoogleMail.IsProcessing;
        }
        catch { /* individual failures already handled in IpcClient try/catch */ }
    }

    private void DrawIpcCallsAvailable()
    {
        RefreshIpcCache();

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "IPC Calls Available");
        ImGui.SameLine();
        var livePulls = plugin.Configuration.IpcLivePullsEnabled;
        if (ImGui.Checkbox("Show Live Pulls", ref livePulls))
        {
            plugin.Configuration.IpcLivePullsEnabled = livePulls;
            plugin.Configuration.Save();
            if (!livePulls) liveIpcValues.Clear();
            else liveIpcExpiry = DateTime.MinValue;
        }
        if (livePulls)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(Scale(120f));
            var interval = plugin.Configuration.IpcLivePullIntervalSeconds;
            if (ImGui.SliderInt("##IpcInterval", ref interval, 0, 30, interval == 0 ? "Live" : $"{interval}s"))
            {
                plugin.Configuration.IpcLivePullIntervalSeconds = interval;
                plugin.Configuration.Save();
            }
            RefreshLiveIpcValues();
        }
        ImGui.TextDisabled("Shows XA Slave's outbound IPC integrations plus the XA Slave provider channels exposed to other plugins.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // -- XA Slave (provided by this plugin) --
        DrawIpcPluginStatus("XA Slave (Provider)", true, $"v{PluginVersion}");

        var cols0 = livePulls ? 4 : 3;
        if (ImGui.BeginTable("IpcXaSlave", cols0, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, Scale(260f));
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, Scale(80f));
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, Scale(50f));
            ImGui.TableHeadersRow();

            DrawIpcRow("XASlave.IsBusy", "bool", "True while a task, auto-collection, or Moogle Mail operation is running", livePulls);
            DrawIpcRow("XASlave.ExecuteCommand", "string", "Run the same subcommands accepted by `/xa` and return an `OK:`/`ERROR:` status string. Accepts either `logout`, `killgame`, or `/xa logout` style input.", livePulls);
            DrawIpcRow("XASlave.RunTask", "Action", "Start a named task (string taskName)", livePulls);

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // -- XA Database --
        ImGui.TextDisabled("Example IPC usage:");
        ImGui.BulletText("XASlave.IsBusy()");
        ImGui.BulletText("XASlave.ExecuteCommand(\"xamods\")");
        ImGui.BulletText("XASlave.ExecuteCommand(\"sprint on\")");
        ImGui.BulletText("XASlave.ExecuteCommand(\"killgame\")");
        ImGui.BulletText("XASlave.ExecuteCommand(\"/xa logout\")");
        ImGui.BulletText("XASlave.RunTask(\"save\")");
        ImGui.Spacing();

        var dbReady = plugin.IpcClient.IsReady();
        var dbVersion = plugin.IpcClient.GetVersion();
        DrawIpcPluginStatus("XA Database", dbReady, dbReady ? $"v{dbVersion}" : null);

        var cols = livePulls ? 4 : 3;

        if (ImGui.BeginTable("IpcXaDb", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, Scale(260f));
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, Scale(80f));
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, Scale(50f));
            ImGui.TableHeadersRow();

            DrawIpcRow("XA.Database.Save", "Action", "Refresh + save snapshot to DB", livePulls);
            DrawIpcRow("XA.Database.Refresh", "Action", "Refresh live data only", livePulls);
            DrawIpcRow("XA.Database.IsReady", "bool", "True when player is loaded", livePulls);
            DrawIpcRow("XA.Database.GetVersion", "string", "Plugin version", livePulls);
            DrawIpcRow("XA.Database.GetDbPath", "string", "Path to xa.db", livePulls);
            DrawIpcRow("XA.Database.GetCharacterName", "string", "Current character name", livePulls);
            DrawIpcRow("XA.Database.GetGil", "int", "Character gil", livePulls);
            DrawIpcRow("XA.Database.GetRetainerGil", "long", "Total retainer gil", livePulls);
            DrawIpcRow("XA.Database.GetFcInfo", "string", "FC Name|Tag|Points|Rank", livePulls);
            DrawIpcRow("XA.Database.GetFcName", "string", "FC name only", livePulls);
            DrawIpcRow("XA.Database.GetFcTag", "string", "FC tag only", livePulls);
            DrawIpcRow("XA.Database.GetFcPoints", "int", "FC company credits", livePulls);
            DrawIpcRow("XA.Database.GetPlotInfo", "string", "FC estate location", livePulls);
            DrawIpcRow("XA.Database.GetPersonalPlotInfo", "string", "Estate|Apartment", livePulls);
            DrawIpcRow("XA.Database.GetApartment", "string", "Apartment location only", livePulls);
            DrawIpcRow("XA.Database.GetCharacterSummaryJson", "string", "Structured current-character summary JSON", livePulls);
            DrawIpcRow("XA.Database.GetLastSnapshotResultJson", "string", "Structured last save result JSON", livePulls);
            DrawIpcRow("XA.Database.SearchItems", "string", "Item search (takes query)", livePulls);
            DrawIpcRow("XA.Database.GetMatchingCharactersForItems", "string", "Exact item-key match (takes itemId:isHq payload)", livePulls);

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // -- vnavmesh --
        var vnavReady = plugin.IpcClient.VnavIsReady();
        DrawIpcPluginStatus("vnavmesh", vnavReady, null);

        if (ImGui.BeginTable("IpcVnav", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, Scale(340f));
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, Scale(80f));
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, Scale(50f));
            ImGui.TableHeadersRow();

            DrawIpcRow("vnavmesh.Nav.IsReady", "bool", "Nav mesh built and ready", livePulls);
            DrawIpcRow("vnavmesh.Nav.Rebuild", "Action", "Rebuild nav mesh", livePulls);
            DrawIpcRow("vnavmesh.SimpleMove.PathfindAndMoveTo", "bool", "Pathfind + move to Vector3 (pos, fly)", livePulls);
            DrawIpcRow("vnavmesh.Path.IsRunning", "bool", "Path movement active", livePulls);
            DrawIpcRow("vnavmesh.Nav.PathfindInProgress", "bool", "Nav pathfinding calculation active", livePulls);
            DrawIpcRow("vnavmesh.SimpleMove.PathfindInProgress", "bool", "SimpleMove pathfinding active", livePulls);
            DrawIpcRow("vnavmesh.Path.Stop", "Action", "Stop current path", livePulls);

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // -- AutoRetainer --
        DrawIpcPluginStatus("AutoRetainer", cachedArAvail, null);

        if (ImGui.BeginTable("IpcAR", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, Scale(380f));
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, Scale(80f));
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, Scale(50f));
            ImGui.TableHeadersRow();

            DrawIpcRow("AutoRetainer.GetSuppressed", "bool", "True when AR is suppressed/paused", livePulls);
            DrawIpcRow("AutoRetainer.GetMultiModeEnabled", "bool", "Multi-mode enabled state", livePulls);
            DrawIpcRow("AutoRetainer.PluginState.IsBusy", "bool", "True while AutoRetainer is busy through its task manager, AutoGC hand-in, or Lifestream travel", livePulls);
            DrawIpcRow("AutoRetainer.PluginState.AreAnyRetainersAvailableForCurrentChara", "bool", "True when the current character has at least one enabled retainer with a venture ready inside AR's unsync window", livePulls);
            DrawIpcRow("AutoRetainer.PluginState.GetMultiModeStatus", "bool", "Multi-mode enabled state from the PluginState namespace", livePulls);
            DrawIpcRow("AutoRetainer.PluginState.CanAutoLogin", "bool", "True when AutoRetainer considers auto-login/relog possible now", livePulls);
            DrawIpcRow("AutoRetainer.PluginState.GetOptionRetainerSense", "bool", "Current RetainerSense option state", livePulls);
            DrawIpcRow("AutoRetainer.PluginState.IsItemProtected", "bool", "Takes item id; true when the current character's inventory-management protection list contains that item", livePulls);
            DrawIpcRow("AutoRetainer.PluginState.AreAnyEnabledVesselsNotDeployed", "bool?", "For local CID: true if enabled deployables include an available vessel with no active deployment, null when AR has no data", livePulls);
            DrawIpcRow("AutoRetainer.PluginState.AreAnyEnabledVesselsReady", "bool?", "For local CID: true if enabled deployables are ready inside AR's configured workshop advance timer, null when AR has no data", livePulls);
            DrawIpcRow("AutoRetainer.SetSuppressed", "Action", "Set suppressed state (bool)", livePulls);
            DrawIpcRow("AutoRetainer.SetMultiModeEnabled", "Action", "Toggle multi-mode (bool)", livePulls);

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // -- Lifestream --
        DrawIpcPluginStatus("Lifestream", cachedLsAvail, null);

        if (ImGui.BeginTable("IpcLS", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, Scale(300f));
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, Scale(80f));
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, Scale(50f));
            ImGui.TableHeadersRow();

            DrawIpcRow("Lifestream.IsBusy", "bool", "True during world/DC travel", livePulls);
            DrawIpcRow("Lifestream.Abort", "Action", "Cancel current travel operation", livePulls);
            DrawIpcRow("Lifestream.ExecuteCommand", "Action", "Execute command string", livePulls);
            DrawIpcRow("Lifestream.ChangeWorld", "bool", "Change to world by name", livePulls);
            DrawIpcRow("Lifestream.TeleportToFC", "bool", "Teleport to FC estate", livePulls);
            DrawIpcRow("Lifestream.TeleportToHome", "bool", "Teleport to personal house", livePulls);
            DrawIpcRow("Lifestream.TeleportToApartment", "bool", "Teleport to apartment", livePulls);
            DrawIpcRow("Lifestream.AethernetTeleport", "bool", "Aethernet teleport by name", livePulls);

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // -- YesAlready --
        DrawIpcPluginStatus("YesAlready", cachedYaAvail, null);

        if (ImGui.BeginTable("IpcYA", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, Scale(300f));
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, Scale(80f));
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, Scale(50f));
            ImGui.TableHeadersRow();

            DrawIpcRow("YesAlready.IsPluginEnabled", "bool", "Check if YesAlready is enabled", livePulls);
            DrawIpcRow("YesAlready.SetPluginEnabled", "Action", "Enable/disable YesAlready (bool)", livePulls);
            DrawIpcRow("YesAlready.PausePlugin", "Action", "Pause for N milliseconds (int)", livePulls);

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // -- Deliveroo --
        DrawIpcPluginStatus("Deliveroo", cachedDelAvail, null);

        if (ImGui.BeginTable("IpcDel", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, Scale(260f));
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, Scale(80f));
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, Scale(50f));
            ImGui.TableHeadersRow();

            DrawIpcRow("Deliveroo.IsTurnInRunning", "bool", "True during GC turn-in", livePulls);

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // -- PandorasBox --
        DrawIpcPluginStatus("PandorasBox", cachedPbAvail, null);

        if (ImGui.BeginTable("IpcPB", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, Scale(300f));
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, Scale(80f));
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, Scale(50f));
            ImGui.TableHeadersRow();

            DrawIpcRow("PandorasBox.GetFeatureEnabled", "bool?", "Get feature enabled state by name", livePulls);
            DrawIpcRow("PandorasBox.SetFeatureEnabled", "Action", "Enable/disable a feature (name, bool)", livePulls);
            DrawIpcRow("PandorasBox.PauseFeature", "Action", "Pause feature for N ms (name, int)", livePulls);

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // -- Dropbox --
        DrawIpcPluginStatus("Dropbox", cachedDbxAvail, null);

        if (ImGui.BeginTable("IpcDB", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, Scale(260f));
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, Scale(80f));
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, Scale(50f));
            ImGui.TableHeadersRow();

            DrawIpcRow("Dropbox.GetItemQuantity", "int", "Read queued quantity for an item (id, hq)", livePulls);
            DrawIpcRow("Dropbox.SetItemQuantity", "Action", "Queue item (id, hq, qty)", livePulls);
            DrawIpcRow("Dropbox.IsBusy", "bool", "True when trading", livePulls);
            DrawIpcRow("Dropbox.BeginTradingQueue", "Action", "Start trading queued items", livePulls);

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // -- TextAdvance --
        DrawIpcPluginStatus("TextAdvance", cachedTaAvail, null);

        if (ImGui.BeginTable("IpcTA", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, Scale(300f));
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, Scale(80f));
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, Scale(50f));
            ImGui.TableHeadersRow();

            DrawIpcRow("TextAdvance.IsEnabled", "bool", "Check if TextAdvance is enabled", livePulls);
            DrawIpcRow("TextAdvance.IsBusy", "bool", "True when task manager is busy", livePulls);
            DrawIpcRow("TextAdvance.IsPaused", "bool", "True when paused/blocked", livePulls);
            DrawIpcRow("TextAdvance.Stop", "Action", "Stop current task + movement", livePulls);

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // -- Artisan --
        DrawIpcPluginStatus("Artisan", cachedArtAvail, null);

        if (ImGui.BeginTable("IpcArt", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, Scale(300f));
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, Scale(80f));
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, Scale(50f));
            ImGui.TableHeadersRow();

            DrawIpcRow("Artisan.IsBusy", "bool", "True when crafting/processing", livePulls);
            DrawIpcRow("Artisan.GetEnduranceStatus", "bool", "Endurance mode enabled", livePulls);
            DrawIpcRow("Artisan.SetEnduranceStatus", "Action", "Toggle endurance (bool)", livePulls);
            DrawIpcRow("Artisan.IsListRunning", "bool", "Crafting list active", livePulls);
            DrawIpcRow("Artisan.IsListPaused", "bool", "Crafting list paused", livePulls);
            DrawIpcRow("Artisan.SetListPause", "Action", "Pause/resume list (bool)", livePulls);
            DrawIpcRow("Artisan.GetStopRequest", "bool", "Stop requested by external", livePulls);
            DrawIpcRow("Artisan.SetStopRequest", "Action", "Request stop/restart (bool)", livePulls);
            DrawIpcRow("Artisan.CraftItem", "Action", "Craft recipe x times (id, amount)", livePulls);

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // -- Splatoon --
        DrawIpcPluginStatus("Splatoon", cachedSplatAvail, null);

        if (ImGui.BeginTable("IpcSplat", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, Scale(300f));
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, Scale(80f));
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, Scale(50f));
            ImGui.TableHeadersRow();

            DrawIpcRow("Splatoon.IsLoaded", "bool", "True when Splatoon is loaded", livePulls);

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // -- Honorific --
        DrawIpcPluginStatus("Honorific", cachedHonorificAvail, cachedHonorificAvail ? "API 3.2+" : null);

        if (ImGui.BeginTable("IpcHonorific", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, Scale(300f));
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, Scale(110f));
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, Scale(50f));
            ImGui.TableHeadersRow();

            DrawIpcRow("Honorific.ApiVersion", "(uint,uint)", "Compatibility check; XA uses API 3.2 or newer.", livePulls);
            DrawIpcRow("Honorific.GetCharacterTitle", "string", "Resolved active title JSON for a visible object index.", livePulls);
            DrawIpcRow("Honorific.GetCharacterTitleList", "TitleData[]", "Configured default/custom title list for a character name and world id.", livePulls);

            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextDisabled("All calls are try/catch wrapped - missing plugins will not crash XA Slave.");
        ImGui.TextDisabled("Channel names verified from each plugin's IPC source code.");
        ImGui.TextDisabled("Connectivity refreshed every 5 seconds.");
    }

    private static void DrawIpcPluginStatus(string name, bool? connected, string? extra)
    {
        if (connected == true)
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), extra != null ? $"{name}: Connected ({extra})" : $"{name}: Connected");
        else if (connected == false)
            ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), $"{name}: Not available");
        else
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1.0f), name);
        ImGui.Spacing();
    }

    private void DrawIpcRow(string channel, string type, string description, bool showValue = false)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextColored(new Vector4(0.6f, 0.9f, 0.6f, 1.0f), channel);
        ImGui.TableNextColumn();
        ImGui.Text(type);
        ImGui.TableNextColumn();
        ImGui.TextWrapped(description);
        if (showValue)
        {
            ImGui.TableNextColumn();
            if ((type == "bool" || type == "bool?") && liveIpcValues.TryGetValue(channel, out var val))
            {
                if (!val.HasValue)
                    ImGui.TextDisabled("null");
                else if (val.Value)
                    ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "true");
                else
                    ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), "false");
            }
            else
            {
                ImGui.TextDisabled("-");
            }
        }
    }
}
