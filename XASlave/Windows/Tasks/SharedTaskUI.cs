using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using XASlave.Data;
using XASlave.Services.Tasks;

namespace XASlave.Windows;

/// <summary>
/// Shared UI helpers used by all task panels.
/// Contains: DrawTaskLog, DrawTaskPluginStatus, DrawProcessingList, StartTaskWithConfig, DrawActionCheckboxes.
/// </summary>
public partial class SlaveWindow
{
    /// <summary>Draw plugin status checker for tasks — same pattern as Monthly Relogger.
    /// XA Database becomes required when parseForXaDb is checked.</summary>
    private void DrawTaskPluginStatus(bool parseForXaDb)
    {
        var ipc = plugin.IpcClient;
        var green = new Vector4(0.4f, 1.0f, 0.4f, 1.0f);
        var red = new Vector4(1.0f, 0.4f, 0.4f, 1.0f);
        var dim = new Vector4(0.6f, 0.6f, 0.6f, 1.0f);

        var arOk = ipc.IsAutoRetainerAvailable();
        var lsOk = ipc.IsLifestreamAvailable();
        var taOk = ipc.IsTextAdvanceAvailable();
        var vnavOk = ipc.IsVnavAvailable();
        var xaDbOk = ipc.IsXaDatabaseAvailable();

        var allRequired = arOk && lsOk && taOk && vnavOk && (!parseForXaDb || xaDbOk);

        if (allRequired)
            ImGui.TextColored(green, "All required plugins loaded.");
        else
            ImGui.TextColored(red, "WARNING: Missing required plugins.");

        ImGui.Text("Required: ");
        ImGui.SameLine(); ImGui.TextColored(arOk ? green : red, arOk ? "[AutoRetainer]" : "[AutoRetainer \u2717]");
        ImGui.SameLine(); ImGui.TextColored(lsOk ? green : red, lsOk ? "[Lifestream]" : "[Lifestream \u2717]");
        ImGui.SameLine(); ImGui.TextColored(taOk ? green : red, taOk ? "[TextAdvance]" : "[TextAdvance \u2717]");
        ImGui.SameLine(); ImGui.TextColored(vnavOk ? green : red, vnavOk ? "[vnavmesh]" : "[vnavmesh \u2717]");
        if (parseForXaDb)
        {
            ImGui.SameLine(); ImGui.TextColored(xaDbOk ? green : red, xaDbOk ? "[XA Database]" : "[XA Database \u2717]");
        }

        var optional = new (string Name, bool Available)[]
        {
            ("YesAlready", ipc.IsYesAlreadyAvailable()),
            ("PandorasBox", ipc.IsPandorasBoxAvailable()),
        };
        if (!parseForXaDb)
        {
            ImGui.Text("Optional: ");
            ImGui.SameLine(); ImGui.TextColored(xaDbOk ? green : dim, xaDbOk ? "[XA Database]" : "[XA Database -]");
            foreach (var p in optional)
            {
                ImGui.SameLine(); ImGui.TextColored(p.Available ? green : dim, p.Available ? $"[{p.Name}]" : $"[{p.Name} -]");
            }
        }
        else
        {
            ImGui.Text("Optional: ");
            foreach (var p in optional)
            {
                ImGui.SameLine(); ImGui.TextColored(p.Available ? green : dim, p.Available ? $"[{p.Name}]" : $"[{p.Name} -]");
            }
        }

        ImGui.Spacing();
    }

    /// <summary>Shared log display with Copy/Clear buttons, used by all task panels.</summary>
    private void DrawTaskLog(string id, ref bool showLog, Services.TaskRunner runner)
    {
        ImGui.Spacing();
        if (ImGui.Checkbox($"Show Log##{id}", ref showLog)) { }
        if (showLog)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"Copy Log##{id}"))
            {
                if (runner.LogMessages.Count > 0)
                {
                    ImGui.SetClipboardText(string.Join("\n", runner.LogMessages));
                    arImportStatus = $"Copied {runner.LogMessages.Count} log lines to clipboard";
                    arImportStatusExpiry = DateTime.UtcNow.AddSeconds(5);
                }
            }
            ImGui.SameLine();
            if (ImGui.SmallButton($"Clear Log##{id}"))
                runner.ClearLog();

            if (runner.LogMessages.Count > 0)
            {
                ImGui.Spacing();
                using (var logChild = ImRaii.Child($"TaskLog{id}", new Vector2(0, 150), true))
                {
                    if (logChild.Success)
                    {
                        foreach (var msg in runner.LogMessages)
                            ImGui.TextWrapped(msg);
                        if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 20)
                            ImGui.SetScrollHereY(1.0f);
                    }
                }
            }
        }
    }

    /// <summary>Draw the processing list shown during a running task.</summary>
    private void DrawProcessingList(Services.TaskRunner runner)
    {
        if (reloggerRunList.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled($"Processing Order ({runner.CompletedItems}/{reloggerRunList.Count}):");
            for (int ci = 0; ci < reloggerRunList.Count; ci++)
            {
                var ch = reloggerRunList[ci];
                if (ci < runner.CompletedItems)
                    ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), $"  \u2713 {ci + 1}. {ch}");
                else if (ci == runner.CompletedItems)
                    ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), $"  \u2192 {ci + 1}. {ch}");
                else
                    ImGui.TextDisabled($"     {ci + 1}. {ch}");
            }
        }
    }

    /// <summary>
    /// Start a task using MonthlyReloggerTask with specific Do* flags.
    /// Reused by CheckDuplicatePlots, ReturnAltsToHomeworlds, and any future task.
    /// </summary>
    private void StartTaskWithConfig(string taskName, List<string> characters, HashSet<int> selectedIndices,
        bool doTextAdvance, bool doRemoveSprout, bool doOpenInventory, bool doOpenArmoury,
        bool doOpenSaddlebags, bool doReturnToHome, bool doReturnToFc,
        bool doParseForXaDatabase, bool doEnableArMulti)
    {
        reloggerTask = new MonthlyReloggerTask(plugin)
        {
            DoEnableTextAdvance = doTextAdvance,
            DoRemoveSprout = doRemoveSprout,
            DoOpenInventory = doOpenInventory,
            DoOpenArmouryChest = doOpenArmoury,
            DoOpenSaddlebags = doOpenSaddlebags,
            DoReturnToHome = doReturnToHome,
            DoReturnToFc = doReturnToFc,
            DoParseForXaDatabase = doParseForXaDatabase,
            DoEnableArMultiOnComplete = doEnableArMulti,
        };

        reloggerRunList = new List<string>(characters);

        var steps = reloggerTask.BuildSteps(characters, plugin.TaskRunner, onCharacterCompleted: (charName) =>
        {
            for (int i = 0; i < characters.Count; i++)
            {
                if (characters[i] == charName)
                {
                    selectedIndices.Remove(i);
                    break;
                }
            }
        });

        // Start with onLog callback that prints to Dalamud console as [TaskLogs]
        plugin.TaskRunner.Start(taskName, steps, onLog: (msg) =>
        {
            Plugin.Log.Information($"[TaskLogs] {msg}");
        });
    }

    /// <summary>Helper: extract world from "Name@World" key.</summary>
    private static string GetWorldFromKey(string key)
    {
        var parts = key.Split('@');
        return parts.Length > 1 ? parts[1] : "";
    }
}
