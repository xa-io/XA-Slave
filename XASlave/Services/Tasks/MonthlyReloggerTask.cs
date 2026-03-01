using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using XASlave.Data;

namespace XASlave.Services.Tasks;

/// <summary>
/// Monthly Relogger — rotates through a list of characters, logging into each
/// and executing a configurable sequence of actions per character.
///
/// Converted from: 7.35 XA Monthly Relogger.lua
/// Uses IPC: AutoRetainer (relog, multi-mode), Lifestream (home/fc), TextAdvance, XA Database
/// Uses commands: /ays relog, /at y, /nastatus off, /inventory, /armourychest, /saddlebag, /freecompanycmd
///
/// Lifestream wait pattern (mirrors xafunc.lua):
///   1. Issue command → wait 1s
///   2. Poll Lifestream.IsBusy every ~1s until it becomes busy (short timeout for missing house)
///   3. Poll until Lifestream.IsBusy becomes false
///   4. Confirm not-busy 3 consecutive times with 1s intervals
///   5. CharacterSafeWait
/// </summary>
public sealed class MonthlyReloggerTask
{
    private readonly Plugin plugin;

    // ── Configurable actions per character ──
    public bool DoEnableTextAdvance { get; set; } = true;
    public bool DoRemoveSprout { get; set; } = true;
    public bool DoOpenInventory { get; set; } = true;
    public bool DoOpenArmouryChest { get; set; } = true;
    public bool DoOpenSaddlebags { get; set; } = true;
    public bool DoReturnToHome { get; set; } = true;
    public bool DoReturnToFc { get; set; } = true;
    public bool DoParseForXaDatabase { get; set; } = true;

    public MonthlyReloggerTask(Plugin plugin)
    {
        this.plugin = plugin;
    }

    /// <summary>
    /// Builds the complete step list for the relogger task.
    /// 1. Disable AR Multi Mode
    /// 2. For each character: relog → wait → per-char actions
    /// 3. Re-enable AR Multi Mode
    /// </summary>
    public List<TaskStep> BuildSteps(List<string> characters, TaskRunner runner)
    {
        var steps = new List<TaskStep>();

        runner.TotalItems = characters.Count;
        runner.CompletedItems = 0;

        // ── Step 1: Disable AR Multi Mode ──
        steps.Add(new TaskStep
        {
            Name = "Disable AR Multi Mode",
            OnEnter = () =>
            {
                runner.AddLog("Disabling AutoRetainer Multi Mode...");
                plugin.IpcClient.AutoRetainerSetMultiModeEnabled(false);
            },
            IsComplete = () => true,
            TimeoutSec = 3f,
        });

        steps.Add(MakeDelay("AR Disable Cooldown", 1.0f));

        // ── Steps for each character ──
        for (int i = 0; i < characters.Count; i++)
        {
            var charName = characters[i];
            var charIndex = i + 1;
            var charTotal = characters.Count;

            // Label update
            steps.Add(new TaskStep
            {
                Name = $"[{charIndex}/{charTotal}] {charName}",
                OnEnter = () =>
                {
                    runner.CurrentItemLabel = $"[{charIndex}/{charTotal}] {charName}";
                    runner.AddLog($"── Processing {charName} ({charIndex}/{charTotal}) ──");
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });

            // Relog if needed
            steps.AddRange(BuildRelogSteps(charName, runner));

            // CharacterSafeWait 3-Pass — 3 consecutive checks with 1s intervals
            // Hard rule: always use 3-pass before performing actions on a character
            steps.AddRange(BuildCharacterSafeWait3Pass("Post-Relog SafeWait", 30f));

            // Per-character actions
            steps.AddRange(BuildPerCharacterActions(charName, charIndex, charTotal, runner));

            // Mark character complete + record last-logged-in
            steps.Add(new TaskStep
            {
                Name = $"Complete: {charName}",
                OnEnter = () =>
                {
                    runner.CompletedItems = charIndex;
                    runner.AddLog($"Finished {charName} ({charIndex}/{charTotal})");

                    // Record last-logged-in timestamp in persistent ReloggerCharacterInfo
                    try
                    {
                        var cfg = plugin.Configuration;
                        if (!cfg.ReloggerCharacterInfo.TryGetValue(charName, out var data))
                            data = new ReloggerCharacterData();
                        data.LastLoggedIn = DateTime.UtcNow;
                        cfg.ReloggerCharacterInfo[charName] = data;
                        cfg.Save();
                    }
                    catch { /* non-critical */ }
                },
                IsComplete = () => true,
                TimeoutSec = 1f,
            });
        }

        // ── Final: Re-enable AR Multi Mode ──
        steps.Add(new TaskStep
        {
            Name = "Enable AR Multi Mode",
            OnEnter = () =>
            {
                runner.AddLog("Re-enabling AutoRetainer Multi Mode...");
                plugin.IpcClient.AutoRetainerSetMultiModeEnabled(true);
            },
            IsComplete = () => true,
            TimeoutSec = 3f,
        });

        steps.Add(MakeDelay("Final Cooldown", 1.0f));

        return steps;
    }

    /// <summary>
    /// Builds relog steps for a character.
    /// Equivalent to ARRelogXA(name) in xafunc.lua.
    /// Uses /ays relog Name@World command via CommandManager.
    /// </summary>
    private List<TaskStep> BuildRelogSteps(string charName, TaskRunner runner)
    {
        var steps = new List<TaskStep>();

        // Check if already logged in as this character
        steps.Add(new TaskStep
        {
            Name = $"Check Login: {charName}",
            OnEnter = () =>
            {
                var current = GetCurrentCharacterNameWorld();
                if (current == charName)
                {
                    runner.AddLog($"Already logged in as {charName}");
                }
                else
                {
                    runner.AddLog($"Relogging to {charName}...");
                    ChatHelper.SendMessage($"/ays relog {charName}");
                }
            },
            IsComplete = () => true,
            TimeoutSec = 3f,
        });

        // Wait for AR to start the relog process
        steps.Add(MakeDelay("Relog Init", 2.0f));

        // Wait for the character to be fully logged in
        // During relog, the player will be unavailable, then become available
        steps.Add(new TaskStep
        {
            Name = $"Wait Relog: {charName}",
            IsComplete = () =>
            {
                try
                {
                    if (!Plugin.PlayerState.IsLoaded) return false;
                    var local = Plugin.ObjectTable.LocalPlayer;
                    if (local == null) return false;

                    // Check NamePlate addon readiness (equivalent to CharacterSafeWaitXA)
                    return IsNamePlateReady() && IsPlayerAvailable();
                }
                catch { return false; }
            },
            TimeoutSec = 120f, // Relog can take a while
            MaxRetries = 0,
        });

        return steps;
    }

    /// <summary>
    /// Builds the per-character action steps.
    /// Equivalent to the sequence in ProcessToonXA() from the Lua script.
    /// </summary>
    private List<TaskStep> BuildPerCharacterActions(string charName, int idx, int total, TaskRunner runner)
    {
        var steps = new List<TaskStep>();

        // EnableTextAdvanceXA() → /at y
        if (DoEnableTextAdvance)
        {
            steps.Add(new TaskStep
            {
                Name = "Enable TextAdvance",
                OnEnter = () =>
                {
                    runner.AddLog("Enabling TextAdvance...");
                    ChatHelper.SendMessage("/at y");
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(MakeDelay("TextAdvance Delay", 0.5f));
        }

        // RemoveSproutXA() → /nastatus off
        if (DoRemoveSprout)
        {
            steps.Add(new TaskStep
            {
                Name = "Remove Sprout",
                OnEnter = () =>
                {
                    runner.AddLog("Removing New Adventurer Status...");
                    ChatHelper.SendMessage("/nastatus off");
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(MakeDelay("Sprout Delay", 0.5f));
        }

        // OpenInventoryXA() → /inventory
        if (DoOpenInventory)
        {
            steps.Add(new TaskStep
            {
                Name = "Open Inventory",
                OnEnter = () =>
                {
                    runner.AddLog("Opening Inventory...");
                    ChatHelper.SendMessage("/inventory");
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(MakeDelay("Inventory Delay", 0.5f));
        }

        // OpenArmouryChestXA() → /armourychest
        if (DoOpenArmouryChest)
        {
            steps.Add(new TaskStep
            {
                Name = "Open Armoury Chest",
                OnEnter = () =>
                {
                    runner.AddLog("Opening Armoury Chest...");
                    ChatHelper.SendMessage("/armourychest");
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(MakeDelay("Armoury Delay", 0.5f));
        }

        // OpenSaddlebagsXA() → /saddlebag
        if (DoOpenSaddlebags)
        {
            steps.Add(new TaskStep
            {
                Name = "Open Saddlebags",
                OnEnter = () =>
                {
                    runner.AddLog("Opening Saddlebags...");
                    ChatHelper.SendMessage("/saddlebag");
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(MakeDelay("Saddlebag Delay", 0.5f));
        }

        // return_to_homeXA() → Lifestream: /li home
        // Short timeout (5s) for "wait busy" — if char has no house, Lifestream never becomes busy
        if (DoReturnToHome)
        {
            steps.AddRange(BuildLifestreamTeleportSteps(
                "Home", "home", runner, waitBusyTimeoutSec: 5f));
        }

        // return_to_fcXA() → Lifestream: /li fc
        if (DoReturnToFc)
        {
            steps.AddRange(BuildLifestreamTeleportSteps(
                "FC", "fc", runner, waitBusyTimeoutSec: 8f));
        }

        // Parse for XA Database — opens FC window, collects all data, saves
        // Combines FreeCompanyCmdXA() + XA Database Save into one workflow
        if (DoParseForXaDatabase)
        {
            // Open FC window to trigger data collection (fc name, members, points, plot)
            steps.Add(new TaskStep
            {
                Name = "Parse XA: Open FC Window",
                OnEnter = () =>
                {
                    runner.AddLog("Opening FC window for data collection...");
                    ChatHelper.SendMessage("/freecompanycmd");
                },
                IsComplete = () => true,
                TimeoutSec = 2f,
            });
            steps.Add(MakeDelay("Parse XA: FC Window Delay", 1.5f));

            // Save to XA Database — pushes all collected data (inventory, armoury, saddlebag, FC)
            steps.Add(new TaskStep
            {
                Name = "Parse XA: Save to Database",
                OnEnter = () =>
                {
                    runner.AddLog("Saving to XA Database (all collected data)...");
                    plugin.IpcClient.Save();
                },
                IsComplete = () => true,
                TimeoutSec = 3f,
            });
            steps.Add(MakeDelay("Parse XA: Save Delay", 0.5f));
        }

        return steps;
    }

    // ═══════════════════════════════════════════════════════
    //  Lifestream Teleport Steps
    //  Pattern: command → 1s → poll busy → poll not-busy → confirm 3x → safe wait
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Builds the full Lifestream teleport step sequence.
    /// Mirrors the xafunc.lua pattern: issue command, wait 1s, poll Lifestream busy,
    /// wait for completion, confirm 3 times not-busy, then CharacterSafeWait.
    ///
    /// waitBusyTimeoutSec controls how long we wait for Lifestream to START being busy.
    /// Short timeout (3-5s) means "skip if this char doesn't have a house".
    /// </summary>
    private List<TaskStep> BuildLifestreamTeleportSteps(
        string label, string command, TaskRunner runner, float waitBusyTimeoutSec = 5f)
    {
        var steps = new List<TaskStep>();

        // 1. Issue the command
        steps.Add(new TaskStep
        {
            Name = $"Teleport {label}: Command",
            OnEnter = () =>
            {
                runner.AddLog($"Teleporting to {label} (/li {command})...");
                plugin.IpcClient.LifestreamExecuteCommand(command);
            },
            IsComplete = () => true,
            TimeoutSec = 2f,
        });

        // 2. Wait 1 second for Lifestream to register the command
        steps.Add(MakeDelay($"Teleport {label}: Init Wait", 1.0f));

        // 3. Wait for Lifestream to become busy (start the teleport)
        //    Short timeout — if it never becomes busy, the teleport destination doesn't exist
        steps.Add(new TaskStep
        {
            Name = $"Teleport {label}: Wait Start",
            IsComplete = () =>
            {
                try { return plugin.IpcClient.LifestreamIsBusy(); }
                catch { return true; } // Lifestream not available → skip
            },
            TimeoutSec = waitBusyTimeoutSec,
        });

        // 4. Wait for Lifestream to finish (busy → not busy)
        {
            steps.Add(new TaskStep
            {
                Name = $"Teleport {label}: Wait Complete",
                IsComplete = () =>
                {
                    try { return !plugin.IpcClient.LifestreamIsBusy(); }
                    catch { return true; }
                },
                TimeoutSec = 60f,
            });
        }

        // 5. Confirm Lifestream is not busy — 3 consecutive 1-second checks
        //    This prevents false positives from brief not-busy flickers
        {
            int confirmCount = 0;
            DateTime lastCheck = DateTime.MinValue;
            steps.Add(new TaskStep
            {
                Name = $"Teleport {label}: Confirm (3x)",
                OnEnter = () => { confirmCount = 0; lastCheck = DateTime.UtcNow; },
                IsComplete = () =>
                {
                    try
                    {
                        // Only check once per second
                        if ((DateTime.UtcNow - lastCheck).TotalSeconds < 1.0)
                            return false;

                        lastCheck = DateTime.UtcNow;

                        if (!plugin.IpcClient.LifestreamIsBusy())
                        {
                            confirmCount++;
                            if (confirmCount >= 3) return true;
                        }
                        else
                        {
                            confirmCount = 0; // Reset if it became busy again
                        }

                        return false;
                    }
                    catch { return true; }
                },
                TimeoutSec = 15f,
            });
        }

        // 6. CharacterSafeWait after teleport
        steps.Add(BuildCharacterSafeWait($"Teleport {label}: SafeWait", 15f));

        // 7. Final settle
        steps.Add(MakeDelay($"Teleport {label}: Settle", 1.0f));

        return steps;
    }

    // ═══════════════════════════════════════════════════════
    //  Helper step builders
    // ═══════════════════════════════════════════════════════

    /// <summary>Creates a simple delay step.</summary>
    public static TaskStep MakeDelay(string name, float seconds)
    {
        DateTime? start = null;
        return new TaskStep
        {
            Name = name,
            OnEnter = () => start = DateTime.UtcNow,
            IsComplete = () => start.HasValue && (DateTime.UtcNow - start.Value).TotalSeconds >= seconds,
            TimeoutSec = seconds + 2f,
        };
    }

    /// <summary>
    /// CharacterSafeWait equivalent — waits for NamePlate addon + player available + not zoning.
    /// Mirrors CharacterSafeWaitXA() from xafunc.lua.
    /// </summary>
    public TaskStep BuildCharacterSafeWait(string name, float timeoutSec)
    {
        return new TaskStep
        {
            Name = name,
            IsComplete = () => IsNamePlateReady() && IsPlayerAvailable(),
            TimeoutSec = timeoutSec,
        };
    }

    /// <summary>
    /// CharacterSafeWait 3-Pass — confirms the player is truly safe by requiring
    /// 3 consecutive successful checks with 1-second intervals between each.
    /// This prevents false positives during zone transitions or animation states.
    ///
    /// Hard rule: Any time the plugin needs to confirm the player is safe before
    /// performing actions, use this 3-pass variant instead of the single-check version.
    ///
    /// Mirrors the xafunc.lua Lifestream wait pattern:
    ///   "Confirm not-busy 3 consecutive times with 1s intervals"
    /// </summary>
    public List<TaskStep> BuildCharacterSafeWait3Pass(string label, float perPassTimeoutSec = 15f)
    {
        return new List<TaskStep>
        {
            BuildCharacterSafeWait($"{label} [pass 1/3]", perPassTimeoutSec),
            MakeDelay($"{label} [wait 1s]", 1.0f),
            BuildCharacterSafeWait($"{label} [pass 2/3]", perPassTimeoutSec),
            MakeDelay($"{label} [wait 1s]", 1.0f),
            BuildCharacterSafeWait($"{label} [pass 3/3]", perPassTimeoutSec),
        };
    }

    /// <summary>
    /// Mount up step — equivalent to MountUpXA() → /gaction "Mount Roulette".
    /// Usable as a template for future tasks that involve movement.
    /// </summary>
    public static TaskStep BuildMountUp(string name = "Mount Up")
    {
        return new TaskStep
        {
            Name = name,
            OnEnter = () => ChatHelper.SendMessage("/gaction \"Mount Roulette\""),
            IsComplete = () => true,
            TimeoutSec = 3f,
        };
    }

    // ═══════════════════════════════════════════════════════
    //  Game state checks — reusable across tasks
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Gets current character name in "FirstName LastName@World" format.
    /// Equivalent to GetCharacterName(true) from dfunc/xafunc.
    /// </summary>
    public static string GetCurrentCharacterNameWorld()
    {
        try
        {
            var local = Plugin.ObjectTable.LocalPlayer;
            if (local == null) return string.Empty;
            var name = local.Name.ToString();
            var worldId = local.HomeWorld.RowId;
            var worldInfo = WorldData.GetById(worldId);
            var worldName = worldInfo?.Name ?? "Unknown";
            return $"{name}@{worldName}";
        }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Checks if NamePlate addon is ready and visible.
    /// Part of CharacterSafeWaitXA() checks.
    /// </summary>
    public static unsafe bool IsNamePlateReady()
    {
        try
        {
            var addon = AtkStage.Instance()->RaptureAtkUnitManager->GetAddonByName("NamePlate");
            return addon != null && addon->IsVisible;
        }
        catch { return false; }
    }

    /// <summary>
    /// Checks if the player is available (loaded and not in a blocking state).
    /// Part of CharacterSafeWaitXA() checks.
    /// </summary>
    public static bool IsPlayerAvailable()
    {
        try
        {
            if (!Plugin.PlayerState.IsLoaded) return false;
            var local = Plugin.ObjectTable.LocalPlayer;
            if (local == null) return false;
            // Not zoning
            if (Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51]) return false;
            return true;
        }
        catch { return false; }
    }
}
