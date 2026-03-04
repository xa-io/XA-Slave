using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace XASlave.Windows;

/// <summary>
/// IPC Calls Available panel — partial class split from SlaveWindow.
/// </summary>
public partial class SlaveWindow
{
    // Cached IPC connectivity — refreshed every 5 seconds to avoid HITCH warnings
    private DateTime ipcCacheExpiry = DateTime.MinValue;
    private bool cachedArAvail, cachedLsAvail, cachedYaAvail, cachedDelAvail, cachedPbAvail, cachedDbxAvail;
    private bool cachedTaAvail, cachedArtAvail, cachedSplatAvail;

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

            // XA Slave (own channels — direct query, no IPC round-trip)
            liveIpcValues["XASlave.IsBusy"] = plugin.TaskRunner.IsRunning || plugin.AutoCollector.IsRunning;
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
            ImGui.SetNextItemWidth(120);
            var interval = plugin.Configuration.IpcLivePullIntervalSeconds;
            if (ImGui.SliderInt("##IpcInterval", ref interval, 0, 30, interval == 0 ? "Live" : $"{interval}s"))
            {
                plugin.Configuration.IpcLivePullIntervalSeconds = interval;
                plugin.Configuration.Save();
            }
            RefreshLiveIpcValues();
        }
        ImGui.TextDisabled("Shows all IPC integrations available to XA Slave (11 plugins, 52 calls) + XA Slave provider channels.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── XA Slave (provided by this plugin) ──
        DrawIpcPluginStatus("XA Slave (Provider)", true, $"v{PluginVersion}");

        var cols0 = livePulls ? 4 : 3;
        if (ImGui.BeginTable("IpcXaSlave", cols0, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, 260);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            DrawIpcRow("XASlave.IsBusy", "bool", "True when any task is running", livePulls);
            DrawIpcRow("XASlave.RunTask", "Action", "Start a named task (string taskName)", livePulls);

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // ── XA Database ──
        var dbReady = plugin.IpcClient.IsReady();
        var dbVersion = plugin.IpcClient.GetVersion();
        DrawIpcPluginStatus("XA Database", dbReady, dbReady ? $"v{dbVersion}" : null);

        var cols = livePulls ? 4 : 3;

        if (ImGui.BeginTable("IpcXaDb", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, 260);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            DrawIpcRow("XA.Database.Save", "Action", "Refresh + save snapshot to DB", livePulls);
            DrawIpcRow("XA.Database.Refresh", "Action", "Refresh live data only", livePulls);
            DrawIpcRow("XA.Database.IsReady", "bool", "True when player is loaded", livePulls);
            DrawIpcRow("XA.Database.GetVersion", "string", "Plugin version", livePulls);
            DrawIpcRow("XA.Database.GetDbPath", "string", "Path to xa.db", livePulls);
            DrawIpcRow("XA.Database.GetCharacterName", "string", "Current character name", livePulls);
            DrawIpcRow("XA.Database.GetGil", "int", "Character gil", livePulls);
            DrawIpcRow("XA.Database.GetRetainerGil", "int", "Total retainer gil", livePulls);
            DrawIpcRow("XA.Database.GetFcInfo", "string", "FC Name|Tag|Points|Rank", livePulls);
            DrawIpcRow("XA.Database.GetPlotInfo", "string", "FC estate location", livePulls);
            DrawIpcRow("XA.Database.GetPersonalPlotInfo", "string", "Estate|Apartment", livePulls);
            DrawIpcRow("XA.Database.SearchItems", "string", "Item search (takes query)", livePulls);

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // ── vnavmesh ──
        var vnavReady = plugin.IpcClient.VnavIsReady();
        DrawIpcPluginStatus("vnavmesh", vnavReady, null);

        if (ImGui.BeginTable("IpcVnav", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, 340);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 50);
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

        // ── AutoRetainer ──
        DrawIpcPluginStatus("AutoRetainer", cachedArAvail, null);

        if (ImGui.BeginTable("IpcAR", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, 300);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            DrawIpcRow("AutoRetainer.GetSuppressed", "bool", "True when AR is suppressed/paused", livePulls);
            DrawIpcRow("AutoRetainer.SetSuppressed", "Action", "Set suppressed state (bool)", livePulls);
            DrawIpcRow("AutoRetainer.GetMultiModeEnabled", "bool", "Multi-mode enabled state", livePulls);
            DrawIpcRow("AutoRetainer.SetMultiModeEnabled", "Action", "Toggle multi-mode (bool)", livePulls);

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // ── Lifestream ──
        DrawIpcPluginStatus("Lifestream", cachedLsAvail, null);

        if (ImGui.BeginTable("IpcLS", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, 300);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 50);
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

        // ── YesAlready ──
        DrawIpcPluginStatus("YesAlready", cachedYaAvail, null);

        if (ImGui.BeginTable("IpcYA", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, 300);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            DrawIpcRow("YesAlready.IsPluginEnabled", "bool", "Check if YesAlready is enabled", livePulls);
            DrawIpcRow("YesAlready.SetPluginEnabled", "Action", "Enable/disable YesAlready (bool)", livePulls);
            DrawIpcRow("YesAlready.PausePlugin", "Action", "Pause for N milliseconds (int)", livePulls);

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // ── Deliveroo ──
        DrawIpcPluginStatus("Deliveroo", cachedDelAvail, null);

        if (ImGui.BeginTable("IpcDel", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, 260);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            DrawIpcRow("Deliveroo.IsTurnInRunning", "bool", "True during GC turn-in", livePulls);

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // ── PandorasBox ──
        DrawIpcPluginStatus("PandorasBox", cachedPbAvail, null);

        if (ImGui.BeginTable("IpcPB", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, 300);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            DrawIpcRow("PandorasBox.GetFeatureEnabled", "bool?", "Get feature enabled state by name", livePulls);
            DrawIpcRow("PandorasBox.SetFeatureEnabled", "Action", "Enable/disable a feature (name, bool)", livePulls);
            DrawIpcRow("PandorasBox.PauseFeature", "Action", "Pause feature for N ms (name, int)", livePulls);

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // ── Dropbox ──
        DrawIpcPluginStatus("Dropbox", cachedDbxAvail, null);

        if (ImGui.BeginTable("IpcDB", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, 260);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            DrawIpcRow("Dropbox.SetItemQuantity", "Action", "Queue item (id, hq, qty)", livePulls);
            DrawIpcRow("Dropbox.IsBusy", "bool", "True when trading", livePulls);
            DrawIpcRow("Dropbox.BeginTradingQueue", "Action", "Start trading queued items", livePulls);

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // ── TextAdvance ──
        DrawIpcPluginStatus("TextAdvance", cachedTaAvail, null);

        if (ImGui.BeginTable("IpcTA", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, 300);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            DrawIpcRow("TextAdvance.IsEnabled", "bool", "Check if TextAdvance is enabled", livePulls);
            DrawIpcRow("TextAdvance.IsBusy", "bool", "True when task manager is busy", livePulls);
            DrawIpcRow("TextAdvance.IsPaused", "bool", "True when paused/blocked", livePulls);
            DrawIpcRow("TextAdvance.Stop", "Action", "Stop current task + movement", livePulls);

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // ── Artisan ──
        DrawIpcPluginStatus("Artisan", cachedArtAvail, null);

        if (ImGui.BeginTable("IpcArt", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, 300);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 50);
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

        // ── Splatoon ──
        DrawIpcPluginStatus("Splatoon", cachedSplatAvail, null);

        if (ImGui.BeginTable("IpcSplat", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthFixed, 300);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Description");
            if (livePulls) ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            DrawIpcRow("Splatoon.IsLoaded", "bool", "True when Splatoon is loaded", livePulls);

            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextDisabled("All calls are try/catch wrapped — missing plugins will not crash XA Slave.");
        ImGui.TextDisabled("Channel names verified from each plugin's IPC source code on GitHub.");
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
            if (type == "bool" && liveIpcValues.TryGetValue(channel, out var val) && val.HasValue)
            {
                if (val.Value)
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
