using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Component.GUI;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private string debugInputAddonName = "InputString";
    private int debugInputNode1 = 17;
    private int debugInputNode2 = 19;
    private string debugInputText1 = string.Empty;
    private string debugInputText2 = string.Empty;
    private bool debugInputLimit1 = true;
    private bool debugInputLimit2 = true;
    private int debugInputMaxCharacters1 = 20;
    private int debugInputMaxCharacters2 = 5;
    private bool debugInputUseSecondField = true;
    private string debugInputWindowResult = "Open a naming window, then inspect its input fields.";

    private void DrawXaAbuseInputWindows()
    {
        ImGui.SetNextItemWidth(Scale(220f));
        ImGui.InputText("Addon##xaInputWindows", ref debugInputAddonName, 96);
        if (ImGui.Button("Free Company preset##xaInputWindows"))
        {
            // User's 2026-09-20 InputString capture: name = node 17, tag = node 19.
            debugInputAddonName = "InputString";
            debugInputNode1 = 17;
            debugInputNode2 = 19;
            debugInputUseSecondField = true;
            debugInputLimit1 = debugInputLimit2 = true;
            debugInputMaxCharacters1 = 20;
            debugInputMaxCharacters2 = 5;
        }
        ImGui.SameLine();
        if (ImGui.Button("Inspect fields##xaInputWindows"))
            InspectDebugInputWindow();

        ImGui.Checkbox("Use second field##xaInputWindows", ref debugInputUseSecondField);
        DrawDebugInputField(1, ref debugInputNode1, ref debugInputText1,
            ref debugInputLimit1, ref debugInputMaxCharacters1);
        if (debugInputUseSecondField)
            DrawDebugInputField(2, ref debugInputNode2, ref debugInputText2,
                ref debugInputLimit2, ref debugInputMaxCharacters2);

        ImGui.TextWrapped("Inject replaces the field text. Empty text clears it. The game's input limits apply.");
        DrawDebugFreeCompanyInputButtons();
        ImGui.TextWrapped(debugInputWindowResult);
    }

    private const string DebugFreeCompanyInputPrompt = "Enter free company name and tag.";

    private unsafe void DrawDebugFreeCompanyInputButtons()
    {
        var canOk = TryGetDebugFreeCompanyInputButton(true, out _, out _, out var okReason);
        var canCancel = TryGetDebugFreeCompanyInputButton(false, out _, out _, out var cancelReason);
        ImGui.BeginDisabled(!canOk);
        if (ImGui.Button("Test OK##xaInputWindowsFc"))
            ClickDebugFreeCompanyInputButton(true);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!canCancel);
        if (ImGui.Button("Test Cancel##xaInputWindowsFc"))
            ClickDebugFreeCompanyInputButton(false);
        ImGui.EndDisabled();
        ImGui.TextWrapped("FC tests: OK submits the current name/tag; Cancel closes the dialog.");
        if (!canOk)
            ImGui.TextWrapped($"OK: {okReason}");
        if (!canCancel && cancelReason != okReason)
            ImGui.TextWrapped($"Cancel: {cancelReason}");
    }

    private unsafe bool TryGetDebugFreeCompanyInputButton(bool accept, out AtkUnitBase* addon,
        out AtkResNode* node, out string reason)
    {
        addon = null;
        node = null;
        reason = "Requires InputString with the exact FC name/tag prompt.";
        try
        {
            if (!string.Equals(debugInputAddonName.Trim(), "InputString", StringComparison.Ordinal))
                return false;

            addon = AddonHelper.GetAddon("InputString");
            if (addon == null || !addon->IsVisible || !addon->IsReady)
                return false;

            // Snapshot 2026-09-20: prompt node 3, OK node 21/event 0, Cancel node 22/event 1.
            var prompt = addon->GetNodeById(3);
            if (prompt == null || prompt->Type != NodeType.Text || !IsDebugInputNodeVisible(prompt)
                || !string.Equals(((AtkTextNode*)prompt)->NodeText.ToString(),
                    DebugFreeCompanyInputPrompt, StringComparison.Ordinal))
                return false;

            var label = accept ? "OK" : "Cancel";
            reason = $"{label} button is missing, hidden, or does not match the expected node/event.";
            node = addon->GetNodeById(accept ? 21u : 22u);
            if (node == null || (int)node->Type < 1000 || !IsDebugInputNodeVisible(node))
                return false;

            var component = ((AtkComponentNode*)node)->Component;
            if (component == null || component->GetComponentType() != ComponentType.Button)
                return false;

            var button = (AtkComponentButton*)component;
            if (button->ButtonTextNode == null
                || !string.Equals(button->ButtonTextNode->NodeText.ToString(), label, StringComparison.Ordinal))
                return false;

            var evt = node->AtkEventManager.Event;
            if (evt == null || evt->Param != (accept ? 0u : 1u))
                return false;

            if (!button->IsEnabled)
            {
                reason = $"The game's {label} button is disabled.";
                return false;
            }

            reason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            reason = $"FC dialog check failed ({ex.GetType().Name}).";
            return false;
        }
    }

    private unsafe void ClickDebugFreeCompanyInputButton(bool accept)
    {
        // Revalidate the exact dialog at click time, independently of the draw-time enabled state.
        if (!TryGetDebugFreeCompanyInputButton(accept, out var addon, out var node, out var reason))
        {
            SetDebugInputWindowResult($"Input Windows: {reason}");
            return;
        }

        var label = accept ? "OK" : "Cancel";
        var dispatched = AddonHelper.ClickResNode(addon, node);
        // Dispatch may close/destroy the addon: never dereference either pointer afterwards.
        SetDebugInputWindowResult(dispatched
            ? $"Input Windows: FC {label} click dispatched; check the game result."
            : $"Input Windows: FC {label} click could not be dispatched.");
    }

    private unsafe void DrawDebugInputField(int field, ref int nodeId, ref string text,
        ref bool limitEnabled, ref int maxCharacters)
    {
        ImGui.SetNextItemWidth(Scale(100f));
        ImGui.InputInt($"Field {field} node ID##xaInputWindows{field}", ref nodeId, 0, 0);
        ImGui.Checkbox($"Max Characters##xaInputLimit{field}", ref limitEnabled);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Scale(100f));
        ImGui.BeginDisabled(!limitEnabled);
        ImGui.InputInt($"##xaInputMaxCharacters{field}", ref maxCharacters, 0, 0);
        ImGui.EndDisabled();
        maxCharacters = Math.Clamp(maxCharacters, 1, 1024);

        // Keep a bounded editor even when its optional, smaller per-field limit is off.
        var limit = limitEnabled ? maxCharacters : 1024;
        var starts = StringInfo.ParseCombiningCharacters(text);
        if (starts.Length > limit)
            text = text[..starts[limit]];

        ImGui.TextUnformatted($"Text {field}");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Scale(300f));
        ImGui.InputText($"##xaInputWindows{field}", ref text, 16384,
            ImGuiInputTextFlags.CallbackAlways, (scoped ref ImGuiInputTextCallbackData data) =>
            {
                // Edit ImGui's active UTF-8 buffer too: trimming only the managed string can
                // leave over-limit text visible while the editor retains keyboard focus.
                var value = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(data.Buf, data.BufTextLen));
                var positions = StringInfo.ParseCombiningCharacters(value);
                if (positions.Length > limit)
                {
                    var byteOffset = Encoding.UTF8.GetByteCount(value.AsSpan(0, positions[limit]));
                    data.DeleteChars(byteOffset, data.BufTextLen - byteOffset);
                }
                return 0;
            });
        ImGui.SameLine();
        var count = new StringInfo(text).LengthInTextElements;
        ImGui.TextUnformatted(limitEnabled ? $"{count}/{maxCharacters}" : $"{count} chars");
        ImGui.SameLine();
        if (ImGui.Button($"Inject {field}##xaInputWindows{field}"))
            InjectDebugInputWindow(nodeId, text);
        if (field == 1)
            ImGui.TextDisabled("Spaces count. Lowering a limit trims excess text. Editor cap: 1024 characters.");
    }

    private void SetDebugInputWindowResult(string result)
    {
        debugInputWindowResult = result;
        SetDebugResult(result);
    }

    private unsafe AtkUnitBase* GetDebugInputWindow()
    {
        var name = debugInputAddonName.Trim();
        var addon = string.IsNullOrEmpty(name) ? null : AddonHelper.GetAddon(name);
        if (addon == null || !addon->IsVisible || !addon->IsReady)
        {
            SetDebugInputWindowResult("Input Windows: addon is missing, hidden, or not ready.");
            return null;
        }

        return addon;
    }

    private static unsafe bool IsDebugInputNodeVisible(AtkResNode* node)
    {
        // Child visibility alone is insufficient: InputString keeps unused layouts under hidden parents.
        for (var depth = 0; node != null && depth < 64; depth++, node = node->ParentNode)
        {
            if (!node->IsVisible())
                return false;
        }

        return node == null;
    }

    private static unsafe AtkComponentTextInput* GetDebugTextInput(AtkResNode* node)
    {
        // NodeType values above 1000 describe component nodes, not their ComponentType.
        // The capture labels these fields "Window"; query the actual component before casting.
        if (node == null || (int)node->Type < 1000 || !IsDebugInputNodeVisible(node))
            return null;

        var component = ((AtkComponentNode*)node)->Component;
        return component != null && component->GetComponentType() == ComponentType.TextInput
            ? (AtkComponentTextInput*)component
            : null;
    }

    private unsafe void InspectDebugInputWindow()
    {
        try
        {
            var addon = GetDebugInputWindow();
            if (addon == null)
                return;

            var nodes = addon->UldManager.NodeList;
            var count = addon->UldManager.NodeListCount;
            if (nodes == null || count > 4096)
            {
                SetDebugInputWindowResult("Input Windows: node list is unavailable or outside the inspection limit.");
                return;
            }

            var ids = new List<uint>();
            for (var i = 0; i < count; i++)
            {
                if (GetDebugTextInput(nodes[i]) != null && !ids.Contains(nodes[i]->NodeId))
                    ids.Add(nodes[i]->NodeId);
            }

            ids.Sort();
            // A single visible input needs no manual mapping. Preserve two-field mapping for name/tag.
            if (ids.Count == 1)
            {
                debugInputNode1 = checked((int)ids[0]);
                debugInputUseSecondField = false;
            }

            SetDebugInputWindowResult(ids.Count == 0
                ? "Input Windows: no visible top-level text inputs found."
                : $"Input Windows: visible text input node IDs: {string.Join(", ", ids)}."
                    + (ids.Count == 1 ? " Field 1 selected." : " Set the field node IDs above."));
        }
        catch (Exception ex)
        {
            SetDebugInputWindowResult($"Input Windows: inspection failed ({ex.GetType().Name}).");
        }
    }

    private unsafe void InjectDebugInputWindow(int nodeId, string text)
    {
        if (nodeId <= 0 || text.Contains('\0'))
        {
            SetDebugInputWindowResult("Input Windows: use a positive node ID and text without embedded NUL characters.");
            return;
        }

        try
        {
            // Resolve fresh on each manual click; never retain addon/component pointers between frames.
            var addon = GetDebugInputWindow();
            if (addon == null)
                return;

            var input = GetDebugTextInput(addon->GetNodeById((uint)nodeId));
            if (input == null)
            {
                SetDebugInputWindowResult($"Input Windows: node {nodeId} is not a visible text input.");
                return;
            }

            input->SetText(text);
            SetDebugInputWindowResult($"Input Windows: SetText called for node {nodeId}; check the game field before confirming.");
        }
        catch (Exception ex)
        {
            SetDebugInputWindowResult($"Input Windows: injection failed ({ex.GetType().Name}).");
        }
    }
}
