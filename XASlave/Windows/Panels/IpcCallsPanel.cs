using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using XASlave.Localization;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

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
    private bool cachedXaDbReady, cachedVnavReady;
    private string cachedXaDbVersion = string.Empty;

    // Live IPC bool polling
    private DateTime liveIpcExpiry = DateTime.MinValue;
    private readonly Dictionary<string, bool?> liveIpcValues = new();
    private readonly Dictionary<string, string> liveClientStateValues = new();
    private global::XASlave.Localization.UiMessage? liveClientLogoutMessage;

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
        cachedXaDbReady = plugin.IpcClient.IsReady();
        cachedXaDbVersion = cachedXaDbReady ? plugin.IpcClient.GetVersion() : string.Empty;
        cachedVnavReady = plugin.IpcClient.VnavIsReady();
    }

    private void RefreshLiveIpcValues()
    {
        if (!plugin.Configuration.IpcLivePullsEnabled) return;
        if (DateTime.UtcNow < liveIpcExpiry) return;
        var intervalSeconds = Math.Clamp(plugin.Configuration.IpcLivePullIntervalSeconds, 1, 30);
        liveIpcExpiry = DateTime.UtcNow.AddSeconds(intervalSeconds);

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

            // Dalamud/client state (local reads, not IPC round-trips)
            liveClientStateValues["IClientState.IsLoggedIn"] = Plugin.ClientState.IsLoggedIn ? "true" : "false";
            liveClientStateValues["IClientState.Logout"] =
                global::XASlave.Localization.UiMessage.Store(ref liveClientLogoutMessage, (plugin.LastClientStateLogoutType.HasValue && plugin.LastClientStateLogoutCode.HasValue ? global::XASlave.Localization.UiMessage.Format("S3188978DA0B0DA09", plugin.LastClientStateLogoutType.Value, plugin.LastClientStateLogoutCode.Value, plugin.LastClientStateLogoutAtUtc) : global::XASlave.Localization.UiMessage.Key("S7138541BDDEE8C2E")));

            unsafe
            {
                var lobby = AgentLobby.Instance();
                if (lobby == null)
                {
                    liveClientStateValues["AgentLobby.IsLoggedIn"] = "unavailable";
                    liveClientStateValues["AgentLobby.IsLoggedIntoZone"] = "unavailable";
                    liveClientStateValues["AgentLobby.LogoutParams.Type"] = "unavailable";
                    liveClientStateValues["AgentLobby.LogoutParams.Code"] = "unavailable";
                }
                else
                {
                    liveClientStateValues["AgentLobby.IsLoggedIn"] = lobby->IsLoggedIn ? "true" : "false";
                    liveClientStateValues["AgentLobby.IsLoggedIntoZone"] = lobby->IsLoggedIntoZone ? "true" : "false";
                    liveClientStateValues["AgentLobby.LogoutParams.Type"] = lobby->LogoutParams.Type.ToString();
                    liveClientStateValues["AgentLobby.LogoutParams.Code"] = lobby->LogoutParams.Code.ToString();
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[XASlave] Refreshing live IPC values failed.");
        }
    }

    private void DrawIpcCallsAvailable()
    {
        RefreshIpcCache();

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("S03BD054B237A0FA2"));
        ImGui.SameLine();
        var livePulls = plugin.Configuration.IpcLivePullsEnabled;
        if (global::XASlave.Localization.UiNative.Checkbox(global::XASlave.Localization.UiLocale.Native("SCE0B5D7CAB01D17D"), ref livePulls))
        {
            plugin.Configuration.IpcLivePullsEnabled = livePulls;
            plugin.Configuration.Save();
            if (!livePulls)
            {
                liveIpcValues.Clear();
                liveClientStateValues.Clear();
            }
            else liveIpcExpiry = DateTime.MinValue;
        }
        if (livePulls)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(Scale(120f));
            var interval = Math.Clamp(plugin.Configuration.IpcLivePullIntervalSeconds, 1, 30);
            if (interval != plugin.Configuration.IpcLivePullIntervalSeconds)
            {
                plugin.Configuration.IpcLivePullIntervalSeconds = interval;
                plugin.Configuration.SaveDeferred();
            }
            if (ImGui.SliderInt("##IpcInterval", ref interval, 1, 30, $"{interval}s", ImGuiSliderFlags.AlwaysClamp))
            {
                plugin.Configuration.IpcLivePullIntervalSeconds = interval;
                plugin.Configuration.SaveDeferred();
            }
            RefreshLiveIpcValues();
        }
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S7D3583BAE3FA27EF"));
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // -- XA Slave (provided by this plugin) --
        DrawIpcPluginStatus("XA Slave (Provider)", true, $"v{PluginVersion}");

        var cols0 = livePulls ? 4 : 3;
        using (var tableScope = ImRaii.Table("IpcXaSlave", cols0, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        if (tableScope)
        {
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SCE4683E7013A18CD"), ImGuiTableColumnFlags.WidthFixed, Scale(260f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SBAADDF70FB5D432B"), ImGuiTableColumnFlags.WidthFixed, Scale(80f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S526E0087CC3F254D"));
            if (livePulls) global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S8E37953D23DACA5F"), ImGuiTableColumnFlags.WidthFixed, Scale(50f));
            global::XASlave.Localization.UiNative.TableHeadersRow();

            DrawIpcRow("XASlave.IsBusy", "bool", UiLocale.Get("SD72F7B4353D839FB"), livePulls);
            DrawIpcRow("XASlave.ExecuteCommand", "string", UiLocale.Get("S7AC8EB8ADC8A72D6"), livePulls);
            DrawIpcRow("XASlave.RunTask", "Action", UiLocale.Get("SA7EBDA03F0809224"), livePulls);

        }

        ImGui.Spacing();

        // -- XA Database --
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SAAD41E757C9A404D"));
        ImGui.BulletText("XASlave.IsBusy()");
        ImGui.BulletText("XASlave.ExecuteCommand(\"xamods\")");
        ImGui.BulletText(global::XASlave.Localization.UiLocale.FormatKey("S5092FF8509826DD7", "XASlave.ExecuteCommand(\"clearlog\")", "OK:", "ERROR:"));
        ImGui.BulletText("XASlave.ExecuteCommand(\"db subgil 5000000\")");
        ImGui.BulletText("XASlave.ExecuteCommand(\"sprint on\")");
        ImGui.BulletText("XASlave.ExecuteCommand(\"killgame\")");
        ImGui.BulletText("XASlave.ExecuteCommand(\"/xa logout\")");
        ImGui.BulletText("XASlave.RunTask(\"save\")");
        ImGui.Spacing();

        DrawIpcPluginStatus("XA Database", cachedXaDbReady, cachedXaDbReady ? $"v{cachedXaDbVersion}" : null);

        var cols = livePulls ? 4 : 3;

        using (var tableScope = ImRaii.Table("IpcXaDb", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        if (tableScope)
        {
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SCE4683E7013A18CD"), ImGuiTableColumnFlags.WidthFixed, Scale(260f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SBAADDF70FB5D432B"), ImGuiTableColumnFlags.WidthFixed, Scale(80f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S526E0087CC3F254D"));
            if (livePulls) global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S8E37953D23DACA5F"), ImGuiTableColumnFlags.WidthFixed, Scale(50f));
            global::XASlave.Localization.UiNative.TableHeadersRow();

            DrawIpcRow("XA.Database.Save", "Action", UiLocale.Get("S4D939D34515DDC6F"), livePulls);
            DrawIpcRow("XA.Database.Refresh", "Action", UiLocale.Get("S0AF7D66B586C9402"), livePulls);
            DrawIpcRow("XA.Database.IsReady", "bool", UiLocale.Get("SE437ADAA5C9E502E"), livePulls);
            DrawIpcRow("XA.Database.GetVersion", "string", UiLocale.Get("S504925BA9822DC6A"), livePulls);
            DrawIpcRow("XA.Database.GetDbPath", "string", UiLocale.Get("SA754A11756811C79"), livePulls);
            DrawIpcRow("XA.Database.GetCharacterName", "string", UiLocale.Get("S45940AC335FC57E7"), livePulls);
            DrawIpcRow("XA.Database.GetGil", "int", UiLocale.Get("S5C178BB533667B63"), livePulls);
            DrawIpcRow("XA.Database.GetRetainerGil", "long", UiLocale.Get("S55963A4D5F9F9815"), livePulls);
            DrawIpcRow("XA.Database.GetFcInfo", "string", UiLocale.Get("SAF0DA59E3584FFF0"), livePulls);
            DrawIpcRow("XA.Database.GetFcName", "string", UiLocale.Get("S5036E3EA1A84A32C"), livePulls);
            DrawIpcRow("XA.Database.GetFcTag", "string", UiLocale.Get("S2D31E651DDA27F2C"), livePulls);
            DrawIpcRow("XA.Database.GetFcPoints", "int", UiLocale.Get("SE98565D39F75023D"), livePulls);
            DrawIpcRow("XA.Database.GetPlotInfo", "string", UiLocale.Get("SB12E2712E9E99A3B"), livePulls);
            DrawIpcRow("XA.Database.GetPersonalPlotInfo", "string", UiLocale.Get("S5326F38B2EA832C9"), livePulls);
            DrawIpcRow("XA.Database.GetApartment", "string", UiLocale.Get("S2E826E93FF6E0772"), livePulls);
            DrawIpcRow("XA.Database.GetCharacterSummaryJson", "string", UiLocale.Get("S4A9B25E294A0C1C6"), livePulls);
            DrawIpcRow("XA.Database.GetLastSnapshotResultJson", "string", UiLocale.Get("S3F5A5889E5DCBDA0"), livePulls);
            DrawIpcRow("XA.Database.SearchItems", "string", UiLocale.Get("SDB965709B678A704"), livePulls);
            DrawIpcRow("XA.Database.GetMatchingCharactersForItems", "string", UiLocale.Get("S09F4FFB4529D9053"), livePulls);

        }

        ImGui.Spacing();

        // -- vnavmesh --
        DrawIpcPluginStatus("vnavmesh", cachedVnavReady, null);

        using (var tableScope = ImRaii.Table("IpcVnav", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        if (tableScope)
        {
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SCE4683E7013A18CD"), ImGuiTableColumnFlags.WidthFixed, Scale(340f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SBAADDF70FB5D432B"), ImGuiTableColumnFlags.WidthFixed, Scale(80f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S526E0087CC3F254D"));
            if (livePulls) global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S8E37953D23DACA5F"), ImGuiTableColumnFlags.WidthFixed, Scale(50f));
            global::XASlave.Localization.UiNative.TableHeadersRow();

            DrawIpcRow("vnavmesh.Nav.IsReady", "bool", UiLocale.Get("SBC8C2DE85FAC3BD0"), livePulls);
            DrawIpcRow("vnavmesh.Nav.Rebuild", "Action", UiLocale.Get("S52E3ADF7E5B95198"), livePulls);
            DrawIpcRow("vnavmesh.SimpleMove.PathfindAndMoveTo", "bool", UiLocale.Get("S71E02567D68A4DA6"), livePulls);
            DrawIpcRow("vnavmesh.Path.IsRunning", "bool", UiLocale.Get("S98BBB407E08D0498"), livePulls);
            DrawIpcRow("vnavmesh.Nav.PathfindInProgress", "bool", UiLocale.Get("SF4674AF76D800F2D"), livePulls);
            DrawIpcRow("vnavmesh.SimpleMove.PathfindInProgress", "bool", UiLocale.Get("SA838F87B41FFA713"), livePulls);
            DrawIpcRow("vnavmesh.Path.Stop", "Action", UiLocale.Get("S09C8AE58B6D922C5"), livePulls);

        }

        ImGui.Spacing();

        // -- AutoRetainer --
        DrawIpcPluginStatus("AutoRetainer", cachedArAvail, null);

        using (var tableScope = ImRaii.Table("IpcAR", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        if (tableScope)
        {
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SCE4683E7013A18CD"), ImGuiTableColumnFlags.WidthFixed, Scale(380f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SBAADDF70FB5D432B"), ImGuiTableColumnFlags.WidthFixed, Scale(80f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S526E0087CC3F254D"));
            if (livePulls) global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S8E37953D23DACA5F"), ImGuiTableColumnFlags.WidthFixed, Scale(50f));
            global::XASlave.Localization.UiNative.TableHeadersRow();

            DrawIpcRow("AutoRetainer.GetSuppressed", "bool", UiLocale.Get("SE6CA6461CE70C38F"), livePulls);
            DrawIpcRow("AutoRetainer.GetMultiModeEnabled", "bool", UiLocale.Get("S79979B63CF892E7D"), livePulls);
            DrawIpcRow("AutoRetainer.PluginState.IsBusy", "bool", UiLocale.Get("SD1AB583D1D5B20C1"), livePulls);
            DrawIpcRow("AutoRetainer.PluginState.AreAnyRetainersAvailableForCurrentChara", "bool", UiLocale.Get("S39C605523505F78B"), livePulls);
            DrawIpcRow("AutoRetainer.PluginState.GetMultiModeStatus", "bool", UiLocale.Get("S9AC710AD105827AF"), livePulls);
            DrawIpcRow("AutoRetainer.PluginState.CanAutoLogin", "bool", UiLocale.Get("S6C967D60AD341444"), livePulls);
            DrawIpcRow("AutoRetainer.PluginState.GetOptionRetainerSense", "bool", UiLocale.Get("SB96C60E02580E23C"), livePulls);
            DrawIpcRow("AutoRetainer.PluginState.IsItemProtected", "bool", UiLocale.Get("S80C52CA4C424297C"), livePulls);
            DrawIpcRow("AutoRetainer.PluginState.AreAnyEnabledVesselsNotDeployed", "bool?", UiLocale.Get("S8A726B06D7C4D857"), livePulls);
            DrawIpcRow("AutoRetainer.PluginState.AreAnyEnabledVesselsReady", "bool?", UiLocale.Get("SCBB5CF3112768B92"), livePulls);
            DrawIpcRow("AutoRetainer.SetSuppressed", "Action", UiLocale.Get("SBA570E95D8328A25"), livePulls);
            DrawIpcRow("AutoRetainer.SetMultiModeEnabled", "Action", UiLocale.Get("S68BB59EDC7234CB7"), livePulls);

        }

        ImGui.Spacing();

        // -- Lifestream --
        DrawIpcPluginStatus("Lifestream", cachedLsAvail, null);

        using (var tableScope = ImRaii.Table("IpcLS", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        if (tableScope)
        {
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SCE4683E7013A18CD"), ImGuiTableColumnFlags.WidthFixed, Scale(300f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SBAADDF70FB5D432B"), ImGuiTableColumnFlags.WidthFixed, Scale(80f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S526E0087CC3F254D"));
            if (livePulls) global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S8E37953D23DACA5F"), ImGuiTableColumnFlags.WidthFixed, Scale(50f));
            global::XASlave.Localization.UiNative.TableHeadersRow();

            DrawIpcRow("Lifestream.IsBusy", "bool", UiLocale.Get("S1F1592005C8FCFDC"), livePulls);
            DrawIpcRow("Lifestream.Abort", "Action", UiLocale.Get("S33AC16FF7B8FAB98"), livePulls);
            DrawIpcRow("Lifestream.ExecuteCommand", "Action", UiLocale.Get("SFC8EB799EE4CAB29"), livePulls);
            DrawIpcRow("Lifestream.ChangeWorld", "bool", UiLocale.Get("S863866585ADAB380"), livePulls);
            DrawIpcRow("Lifestream.TeleportToFC", "bool", UiLocale.Get("SC47E0CDE59E9C71B"), livePulls);
            DrawIpcRow("Lifestream.TeleportToHome", "bool", UiLocale.Get("SF921EBEA403D29AB"), livePulls);
            DrawIpcRow("Lifestream.TeleportToApartment", "bool", UiLocale.Get("SDAA40242B4E04B3A"), livePulls);
            DrawIpcRow("Lifestream.AethernetTeleport", "bool", UiLocale.Get("S8BAD348882F0F48C"), livePulls);

        }

        ImGui.Spacing();

        // -- YesAlready --
        DrawIpcPluginStatus("YesAlready", cachedYaAvail, null);

        using (var tableScope = ImRaii.Table("IpcYA", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        if (tableScope)
        {
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SCE4683E7013A18CD"), ImGuiTableColumnFlags.WidthFixed, Scale(300f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SBAADDF70FB5D432B"), ImGuiTableColumnFlags.WidthFixed, Scale(80f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S526E0087CC3F254D"));
            if (livePulls) global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S8E37953D23DACA5F"), ImGuiTableColumnFlags.WidthFixed, Scale(50f));
            global::XASlave.Localization.UiNative.TableHeadersRow();

            DrawIpcRow("YesAlready.IsPluginEnabled", "bool", UiLocale.Get("S8932F4F253196CDC"), livePulls);
            DrawIpcRow("YesAlready.SetPluginEnabled", "Action", UiLocale.Get("SB14B34045951C111"), livePulls);
            DrawIpcRow("YesAlready.PausePlugin", "Action", UiLocale.Get("S92BFAF9CFB641394"), livePulls);

        }

        ImGui.Spacing();

        // -- Deliveroo --
        DrawIpcPluginStatus("Deliveroo", cachedDelAvail, null);

        using (var tableScope = ImRaii.Table("IpcDel", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        if (tableScope)
        {
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SCE4683E7013A18CD"), ImGuiTableColumnFlags.WidthFixed, Scale(260f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SBAADDF70FB5D432B"), ImGuiTableColumnFlags.WidthFixed, Scale(80f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S526E0087CC3F254D"));
            if (livePulls) global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S8E37953D23DACA5F"), ImGuiTableColumnFlags.WidthFixed, Scale(50f));
            global::XASlave.Localization.UiNative.TableHeadersRow();

            DrawIpcRow("Deliveroo.IsTurnInRunning", "bool", UiLocale.Get("S3B616EE8C3E707FC"), livePulls);

        }

        ImGui.Spacing();

        // -- PandorasBox --
        DrawIpcPluginStatus("PandorasBox", cachedPbAvail, null);

        using (var tableScope = ImRaii.Table("IpcPB", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        if (tableScope)
        {
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SCE4683E7013A18CD"), ImGuiTableColumnFlags.WidthFixed, Scale(300f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SBAADDF70FB5D432B"), ImGuiTableColumnFlags.WidthFixed, Scale(80f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S526E0087CC3F254D"));
            if (livePulls) global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S8E37953D23DACA5F"), ImGuiTableColumnFlags.WidthFixed, Scale(50f));
            global::XASlave.Localization.UiNative.TableHeadersRow();

            DrawIpcRow("PandorasBox.GetFeatureEnabled", "bool?", UiLocale.Get("S164942CCF6E562D4"), livePulls);
            DrawIpcRow("PandorasBox.SetFeatureEnabled", "Action", UiLocale.Get("SE941BE4691D34DAB"), livePulls);
            DrawIpcRow("PandorasBox.PauseFeature", "Action", UiLocale.Get("S7A64697637E2EE51"), livePulls);

        }

        ImGui.Spacing();

        // -- Dropbox --
        DrawIpcPluginStatus("Dropbox", cachedDbxAvail, null);

        using (var tableScope = ImRaii.Table("IpcDB", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        if (tableScope)
        {
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SCE4683E7013A18CD"), ImGuiTableColumnFlags.WidthFixed, Scale(260f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SBAADDF70FB5D432B"), ImGuiTableColumnFlags.WidthFixed, Scale(80f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S526E0087CC3F254D"));
            if (livePulls) global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S8E37953D23DACA5F"), ImGuiTableColumnFlags.WidthFixed, Scale(50f));
            global::XASlave.Localization.UiNative.TableHeadersRow();

            DrawIpcRow("Dropbox.GetItemQuantity", "int", UiLocale.Get("SBC0A8C3FF6CC92A8"), livePulls);
            DrawIpcRow("Dropbox.SetItemQuantity", "Action", UiLocale.Get("SC33CF50DA24E7DD8"), livePulls);
            DrawIpcRow("Dropbox.IsBusy", "bool", UiLocale.Get("SF6384E2A4D3C06F9"), livePulls);
            DrawIpcRow("Dropbox.BeginTradingQueue", "Action", UiLocale.Get("S73E2A26197E1966B"), livePulls);

        }

        ImGui.Spacing();

        // -- TextAdvance --
        DrawIpcPluginStatus("TextAdvance", cachedTaAvail, null);

        using (var tableScope = ImRaii.Table("IpcTA", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        if (tableScope)
        {
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SCE4683E7013A18CD"), ImGuiTableColumnFlags.WidthFixed, Scale(300f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SBAADDF70FB5D432B"), ImGuiTableColumnFlags.WidthFixed, Scale(80f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S526E0087CC3F254D"));
            if (livePulls) global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S8E37953D23DACA5F"), ImGuiTableColumnFlags.WidthFixed, Scale(50f));
            global::XASlave.Localization.UiNative.TableHeadersRow();

            DrawIpcRow("TextAdvance.IsEnabled", "bool", UiLocale.Get("SCDB7EFB2FE164731"), livePulls);
            DrawIpcRow("TextAdvance.IsBusy", "bool", UiLocale.Get("S3231F200A9C4A27F"), livePulls);
            DrawIpcRow("TextAdvance.IsPaused", "bool", UiLocale.Get("S4FDA6116A7A02F7A"), livePulls);
            DrawIpcRow("TextAdvance.Stop", "Action", UiLocale.Get("S100B0EDAADF7F6A7"), livePulls);

        }

        ImGui.Spacing();

        // -- Artisan --
        DrawIpcPluginStatus("Artisan", cachedArtAvail, null);

        using (var tableScope = ImRaii.Table("IpcArt", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        if (tableScope)
        {
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SCE4683E7013A18CD"), ImGuiTableColumnFlags.WidthFixed, Scale(300f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SBAADDF70FB5D432B"), ImGuiTableColumnFlags.WidthFixed, Scale(80f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S526E0087CC3F254D"));
            if (livePulls) global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S8E37953D23DACA5F"), ImGuiTableColumnFlags.WidthFixed, Scale(50f));
            global::XASlave.Localization.UiNative.TableHeadersRow();

            DrawIpcRow("Artisan.IsBusy", "bool", UiLocale.Get("S8797A9E9D4EAABC0"), livePulls);
            DrawIpcRow("Artisan.GetEnduranceStatus", "bool", UiLocale.Get("S8F8C12FBAE1E7E74"), livePulls);
            DrawIpcRow("Artisan.SetEnduranceStatus", "Action", UiLocale.Get("S8C60993942CE48F9"), livePulls);
            DrawIpcRow("Artisan.IsListRunning", "bool", UiLocale.Get("S0B0D1BC0DE922844"), livePulls);
            DrawIpcRow("Artisan.IsListPaused", "bool", UiLocale.Get("S21F4C5EFC050344A"), livePulls);
            DrawIpcRow("Artisan.SetListPause", "Action", UiLocale.Get("SF46F2B538717DE0C"), livePulls);
            DrawIpcRow("Artisan.GetStopRequest", "bool", UiLocale.Get("S9A243A803C3D662A"), livePulls);
            DrawIpcRow("Artisan.SetStopRequest", "Action", UiLocale.Get("SBD3089734A0731FE"), livePulls);
            DrawIpcRow("Artisan.CraftItem", "Action", UiLocale.Get("SE520EC9F7D9E3ADE"), livePulls);

        }

        ImGui.Spacing();

        // -- Splatoon --
        DrawIpcPluginStatus("Splatoon", cachedSplatAvail, null);

        using (var tableScope = ImRaii.Table("IpcSplat", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        if (tableScope)
        {
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SCE4683E7013A18CD"), ImGuiTableColumnFlags.WidthFixed, Scale(300f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SBAADDF70FB5D432B"), ImGuiTableColumnFlags.WidthFixed, Scale(80f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S526E0087CC3F254D"));
            if (livePulls) global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S8E37953D23DACA5F"), ImGuiTableColumnFlags.WidthFixed, Scale(50f));
            global::XASlave.Localization.UiNative.TableHeadersRow();

            DrawIpcRow("Splatoon.IsLoaded", "bool", UiLocale.Get("SDADCF30D9C0B1F5C"), livePulls);

        }

        ImGui.Spacing();

        // -- Honorific --
        DrawIpcPluginStatus("Honorific", cachedHonorificAvail, cachedHonorificAvail ? "API 3.2+" : null);

        using (var tableScope = ImRaii.Table("IpcHonorific", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        if (tableScope)
        {
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SCE4683E7013A18CD"), ImGuiTableColumnFlags.WidthFixed, Scale(300f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SBAADDF70FB5D432B"), ImGuiTableColumnFlags.WidthFixed, Scale(110f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S526E0087CC3F254D"));
            if (livePulls) global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S8E37953D23DACA5F"), ImGuiTableColumnFlags.WidthFixed, Scale(50f));
            global::XASlave.Localization.UiNative.TableHeadersRow();

            DrawIpcRow("Honorific.ApiVersion", "(uint,uint)", UiLocale.Get("S7C596F83A88B3429"), livePulls);
            DrawIpcRow("Honorific.GetCharacterTitle", "string", UiLocale.Get("S239F1EDDE4B60B5E"), livePulls);
            DrawIpcRow("Honorific.GetCharacterTitleList", "TitleData[]", UiLocale.Get("SD1BE3B0AB4141AD9"), livePulls);

        }

        ImGui.Spacing();

        // -- Dalamud Client State --
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("SD6F09CC71CC52905"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S717213721EC46B59"));
        ImGui.Spacing();

        using (var tableScope = ImRaii.Table("DalamudClientState", cols, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        if (tableScope)
        {
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S7C968FB71F50E335"), ImGuiTableColumnFlags.WidthFixed, Scale(260f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("SBAADDF70FB5D432B"), ImGuiTableColumnFlags.WidthFixed, Scale(110f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S526E0087CC3F254D"));
            if (livePulls) global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S8E37953D23DACA5F"), ImGuiTableColumnFlags.WidthFixed, Scale(230f));
            global::XASlave.Localization.UiNative.TableHeadersRow();

            DrawIpcRow("IClientState.IsLoggedIn", "bool", UiLocale.Get("S1559950D308F2182"), livePulls, liveClientStateValues.GetValueOrDefault("IClientState.IsLoggedIn"));
            DrawIpcRow("IClientState.Logout", "event(int,int)", UiLocale.Get("S6E3AC8B7D524244C"), livePulls, global::XASlave.Localization.UiMessage.Resolve(ref liveClientLogoutMessage, liveClientStateValues.GetValueOrDefault("IClientState.Logout") ?? string.Empty));
            DrawIpcRow("AgentLobby.IsLoggedIn", "bool", UiLocale.Get("S1C332D538CEC1861"), livePulls, liveClientStateValues.GetValueOrDefault("AgentLobby.IsLoggedIn"));
            DrawIpcRow("AgentLobby.IsLoggedIntoZone", "bool", UiLocale.Get("SC58CEF8547B63859"), livePulls, liveClientStateValues.GetValueOrDefault("AgentLobby.IsLoggedIntoZone"));
            DrawIpcRow("AgentLobby.LogoutParams.Type", "int", UiLocale.Get("S8197C74FA7B01846"), livePulls, liveClientStateValues.GetValueOrDefault("AgentLobby.LogoutParams.Type"));
            DrawIpcRow("AgentLobby.LogoutParams.Code", "int", UiLocale.Get("S113C60D866E5DDF5"), livePulls, liveClientStateValues.GetValueOrDefault("AgentLobby.LogoutParams.Code"));
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SA98A3CF85D9AAF71"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("SFFA46F914FBC6F5A"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S7E1ADFA7E82EAB1A"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S102D53928A127550"));
    }

    private static void DrawIpcPluginStatus(string name, bool? connected, string? extra)
    {
        if (connected == true)
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), extra != null ? global::XASlave.Localization.UiLocale.FormatKey("SD4B552A89F4A09C4", name, extra) : global::XASlave.Localization.UiLocale.FormatKey("S3A7A0C99672BFE98", name));
        else if (connected == false)
            ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), global::XASlave.Localization.UiLocale.FormatKey("SF6A10240AF4F7113", name));
        else
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1.0f), name);
        ImGui.Spacing();
    }

    private void DrawIpcRow(string channel, string type, string description, bool showValue = false, string? displayValue = null)
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
            if (displayValue != null)
            {
                if (type == "bool" && bool.TryParse(displayValue, out var boolValue))
                {
                    ImGui.TextColored(
                        boolValue
                            ? new Vector4(0.4f, 1.0f, 0.4f, 1.0f)
                            : new Vector4(1.0f, 0.4f, 0.4f, 1.0f),
                        displayValue);
                }
                else
                {
                    ImGui.TextUnformatted(displayValue);
                }
            }
            else if ((type == "bool" || type == "bool?") && liveIpcValues.TryGetValue(channel, out var val))
            {
                if (!val.HasValue)
                    ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S74234E98AFE7498F"));
                else if (val.Value)
                    ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), global::XASlave.Localization.UiLocale.Get("SB5BEA41B6C623F7C"));
                else
                    ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), global::XASlave.Localization.UiLocale.Get("SFCBCF165908DD18A"));
            }
            else
            {
                ImGui.TextDisabled("-");
            }
        }
    }
}
