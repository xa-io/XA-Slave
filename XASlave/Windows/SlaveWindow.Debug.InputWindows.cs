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
    private string debugInputWindowResult = global::XASlave.Localization.UiMessage.Key("SFBF2F08BB6193F5C").Original;
    private global::XASlave.Localization.UiMessage? debugInputWindowResultMessage = global::XASlave.Localization.UiMessage.Key("SFBF2F08BB6193F5C");

    private void DrawXaAbuseInputWindows()
    {
        ImGui.SetNextItemWidth(Scale(220f));
        global::XASlave.Localization.UiNative.InputText((global::XASlave.Localization.UiLocale.Native("S25BD3377DF88F39F") + "##xaInputWindows"), ref debugInputAddonName, 96);
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SB21DCE4D15B6648A") + "##xaInputWindows")))
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
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S749BB6B5A233C7B4") + "##xaInputWindows")))
            InspectDebugInputWindow();

        global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S6B83C375D594DE15") + "##xaInputWindows"), ref debugInputUseSecondField);
        DrawDebugInputField(1, ref debugInputNode1, ref debugInputText1,
            ref debugInputLimit1, ref debugInputMaxCharacters1);
        if (debugInputUseSecondField)
            DrawDebugInputField(2, ref debugInputNode2, ref debugInputText2,
                ref debugInputLimit2, ref debugInputMaxCharacters2);

        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S69095BC8FBFC988C"));
        DrawDebugFreeCompanyInputButtons();
        ImGui.TextWrapped(global::XASlave.Localization.UiMessage.Resolve(ref debugInputWindowResultMessage, debugInputWindowResult));
    }

    private const string DebugFreeCompanyInputPrompt = "Enter free company name and tag.";

    private unsafe void DrawDebugFreeCompanyInputButtons()
    {
        var canOk = TryGetDebugFreeCompanyInputButton(true, out _, out _, out var okReason);
        var canCancel = TryGetDebugFreeCompanyInputButton(false, out _, out _, out var cancelReason);
        ImGui.BeginDisabled(!canOk);
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S0E764B1738ED46CB") + "##xaInputWindowsFc")))
            ClickDebugFreeCompanyInputButton(true);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!canCancel);
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SE12A42DFC91E7223") + "##xaInputWindowsFc")))
            ClickDebugFreeCompanyInputButton(false);
        ImGui.EndDisabled();
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("SCE0A340DB37BD608"));
        if (!canOk)
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.FormatKey("SEC552145B73D9D29", okReason.Display));
        if (!canCancel && cancelReason.Original != okReason.Original)
            ImGui.TextWrapped(global::XASlave.Localization.UiLocale.FormatKey("S1FDA212C73740869", cancelReason.Display));
    }

    private unsafe bool TryGetDebugFreeCompanyInputButton(bool accept, out AtkUnitBase* addon,
        out AtkResNode* node, out global::XASlave.Localization.UiMessage reason)
    {
        addon = null;
        node = null;
        reason = global::XASlave.Localization.UiMessage.Key("S25FF6ED807AC9DB7");
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
            reason = global::XASlave.Localization.UiMessage.Format("S315A421B718352CC", label);
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
                reason = global::XASlave.Localization.UiMessage.Format("SFF6E14EEA54A99BA", label);
                return false;
            }

            reason = global::XASlave.Localization.UiMessage.Raw(string.Empty);
            return true;
        }
        catch (Exception ex)
        {
            reason = global::XASlave.Localization.UiMessage.Format("SFE45136F6955C110", ex.GetType().Name);
            return false;
        }
    }

    private unsafe void ClickDebugFreeCompanyInputButton(bool accept)
    {
        // Revalidate the exact dialog at click time, independently of the draw-time enabled state.
        if (!TryGetDebugFreeCompanyInputButton(accept, out var addon, out var node, out var reason))
        {
            SetDebugInputWindowResult(global::XASlave.Localization.UiMessage.Format("S8D13FB548842D371", reason));
            return;
        }

        var label = accept ? "OK" : "Cancel";
        var dispatched = AddonHelper.ClickResNode(addon, node);
        // Dispatch may close/destroy the addon: never dereference either pointer afterwards.
        SetDebugInputWindowResult((dispatched ? global::XASlave.Localization.UiMessage.Format("S5C042CB8592BCFBB", label) : global::XASlave.Localization.UiMessage.Format("S7C370CE72F9FE0D6", label)));
    }

    private unsafe void DrawDebugInputField(int field, ref int nodeId, ref string text,
        ref bool limitEnabled, ref int maxCharacters)
    {
        ImGui.SetNextItemWidth(Scale(100f));
        global::XASlave.Localization.UiNative.InputInt((string.Format(global::XASlave.Localization.UiLocale.Native("S1F2EF503B2C84D40"), field) + $"##xaInputWindows{field}"), ref nodeId, 0, 0, display: global::XASlave.Localization.UiLocale.FormatKey("S1F2EF503B2C84D40", field));
        global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("SFC589D6A97A319A0") + $"##xaInputLimit{field}"), ref limitEnabled);
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

        ImGui.TextUnformatted(global::XASlave.Localization.UiLocale.FormatKey("S51D7C08DD27F93E8", field));
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
        ImGui.TextUnformatted(limitEnabled ? $"{count}/{maxCharacters}" : global::XASlave.Localization.UiLocale.FormatKey("SCEFE0CEA2B839FE0", count));
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button((string.Format(global::XASlave.Localization.UiLocale.Native("S43DC3C2C45865C76"), field) + $"##xaInputWindows{field}"), display: global::XASlave.Localization.UiLocale.FormatKey("S43DC3C2C45865C76", field)))
            InjectDebugInputWindow(nodeId, text);
        if (field == 1)
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S615375270AB8A4FB"));
    }

    private void SetDebugInputWindowResult(global::XASlave.Localization.UiMessage result)
    {
        debugInputWindowResult = global::XASlave.Localization.UiMessage.Store(ref debugInputWindowResultMessage, result);
        SetDebugResult(result);
    }

    private unsafe AtkUnitBase* GetDebugInputWindow()
    {
        var name = debugInputAddonName.Trim();
        var addon = string.IsNullOrEmpty(name) ? null : AddonHelper.GetAddon(name);
        if (addon == null || !addon->IsVisible || !addon->IsReady)
        {
            SetDebugInputWindowResult(global::XASlave.Localization.UiMessage.Key("SDD3DBD2C3E68F14B"));
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
                SetDebugInputWindowResult(global::XASlave.Localization.UiMessage.Key("S32D0AC4A8D6874A4"));
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

            SetDebugInputWindowResult((ids.Count == 0 ? global::XASlave.Localization.UiMessage.Key("SA8FDD5B136D4D954") : global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Format("SF88A4381E354EDA5", string.Join(", ", ids)), ((ids.Count == 1 ? global::XASlave.Localization.UiMessage.Key("S9121F347CD0F869C") : global::XASlave.Localization.UiMessage.Key("S73084073622A771F"))))));
        }
        catch (Exception ex)
        {
            SetDebugInputWindowResult(global::XASlave.Localization.UiMessage.Format("S551A94CE383BC5D9", ex.GetType().Name));
        }
    }

    private unsafe void InjectDebugInputWindow(int nodeId, string text)
    {
        if (nodeId <= 0 || text.Contains('\0'))
        {
            SetDebugInputWindowResult(global::XASlave.Localization.UiMessage.Key("S51C20AC9F94171B0"));
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
                SetDebugInputWindowResult(global::XASlave.Localization.UiMessage.Format("SACF08E5D67F68475", nodeId));
                return;
            }

            input->SetText(text);
            SetDebugInputWindowResult(global::XASlave.Localization.UiMessage.Format("S63211E0DE0DF9C4C", nodeId));
        }
        catch (Exception ex)
        {
            SetDebugInputWindowResult(global::XASlave.Localization.UiMessage.Format("S1FFB1DB7EF054469", ex.GetType().Name));
        }
    }
}
