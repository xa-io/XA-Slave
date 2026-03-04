using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace XASlave.Windows;

/// <summary>
/// Save to XA Database panel — partial class split from SlaveWindow.
/// </summary>
public partial class SlaveWindow
{
    // Auto-collection state
    private DateTime? autoCollectScheduledAt;
    private string lastIpcResult = string.Empty;
    private DateTime lastIpcResultExpiry = DateTime.MinValue;

    // ───────────────────────────────────────────────
    //  Task: Save to XA Database
    // ───────────────────────────────────────────────
    private void DrawSaveToXaDatabaseTask()
    {
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Save to XA Database");
        ImGui.TextDisabled("Collects data from game windows and saves to XA Database via IPC.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Connection status ──
        var dbReady = plugin.IpcClient.IsReady();
        var dbVersion = plugin.IpcClient.GetVersion();
        if (dbReady)
        {
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), $"XA Database: Connected (v{dbVersion})");
        }
        else
        {
            ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), "XA Database: Not available");
            ImGui.TextDisabled("Make sure XA Database plugin is loaded.");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Manual actions ──
        ImGui.Text("Manual Actions");
        ImGui.Spacing();

        if (plugin.AutoCollector.IsRunning)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), $"Collecting: {plugin.AutoCollector.StatusText}");
            if (ImGui.Button("Cancel"))
                plugin.AutoCollector.Cancel();
        }
        else
        {
            if (ImGui.Button("Collect Now"))
                RunAutoCollection();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Opens saddlebag/FC windows to collect data, then saves to XA Database via IPC.");

            ImGui.SameLine();
            if (ImGui.Button("IPC Save"))
            {
                if (plugin.IpcClient.Save())
                    SetIpcResult("Save sent to XA Database");
                else
                    SetIpcResult("Save failed — XA Database not available");
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Send a save command to XA Database without opening any game windows.");
        }

        // IPC result feedback
        if (!string.IsNullOrEmpty(lastIpcResult) && DateTime.UtcNow < lastIpcResultExpiry)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), lastIpcResult);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Auto-Collection on Login settings ──
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Auto-Collection on Login");
        ImGui.Spacing();

        var autoCollect = plugin.Configuration.AutoCollectOnLogin;
        if (ImGui.Checkbox("Enable Auto-Collection on Login", ref autoCollect))
        {
            plugin.Configuration.AutoCollectOnLogin = autoCollect;
            plugin.Configuration.Save();
        }
        ImGui.TextDisabled("Automatically opens saddlebag and FC windows after login to collect data.");

        if (autoCollect)
        {
            ImGui.Spacing();
            var acSaddlebag = plugin.Configuration.AutoCollectSaddlebag;
            if (ImGui.Checkbox("  Collect Saddlebag", ref acSaddlebag))
            {
                plugin.Configuration.AutoCollectSaddlebag = acSaddlebag;
                plugin.Configuration.Save();
            }

            var acFc = plugin.Configuration.AutoCollectFc;
            if (ImGui.Checkbox("  Collect FC Data (Members, Info, Housing)", ref acFc))
            {
                plugin.Configuration.AutoCollectFc = acFc;
                plugin.Configuration.Save();
            }

            ImGui.Spacing();
            var delay = plugin.Configuration.AutoCollectDelaySeconds;
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputFloat("Login Delay (seconds)", ref delay, 1f, 5f, "%.0f"))
            {
                if (delay < 3f) delay = 3f;
                if (delay > 30f) delay = 30f;
                plugin.Configuration.AutoCollectDelaySeconds = delay;
                plugin.Configuration.Save();
            }
            ImGui.TextDisabled("Wait time after login before starting collection.");
        }

        // Scheduled status
        if (autoCollectScheduledAt.HasValue)
        {
            var remaining = plugin.Configuration.AutoCollectDelaySeconds - (float)(DateTime.UtcNow - autoCollectScheduledAt.Value).TotalSeconds;
            if (remaining > 0)
            {
                ImGui.Spacing();
                ImGui.TextDisabled($"Auto-collection starting in ~{remaining:F0}s...");
            }
        }
    }

    // ───────────────────────────────────────────────
    //  Helpers
    // ───────────────────────────────────────────────
    private void RunAutoCollection()
    {
        if (plugin.AutoCollector.IsRunning) return;
        plugin.AutoCollector.StartCollection(
            plugin.Configuration.AutoCollectSaddlebag,
            plugin.Configuration.AutoCollectFc,
            () =>
            {
                // On completion, send IPC save to XA Database
                if (plugin.IpcClient.Save())
                    SetIpcResult("Collection complete — saved to XA Database");
                else
                    SetIpcResult("Collection complete — XA Database save failed");
            });
    }

    private void SetIpcResult(string message)
    {
        lastIpcResult = message;
        lastIpcResultExpiry = DateTime.UtcNow.AddSeconds(8);
    }
}
