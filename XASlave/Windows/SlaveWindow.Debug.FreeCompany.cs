using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Component.GUI;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private string debugFreeCompanyResult = global::XASlave.Localization.UiMessage.Raw(string.Empty).Original;
    private global::XASlave.Localization.UiMessage? debugFreeCompanyResultMessage = global::XASlave.Localization.UiMessage.Raw(string.Empty);

    private void DrawXaAbuseFreeCompany()
    {
        var visible = AddonHelper.IsAddonVisible("FreeCompany");
        using (ImRaii.Disabled(!Plugin.ClientState.IsLoggedIn || visible))
        {
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S5D8AB7B40FB7FA4C") + "##xaAbuseFreeCompany")))
                SetDebugFreeCompanyWindowOpen(true);
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(!AddonHelper.IsAddonReady("FreeCompany")))
        {
            if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SD73DF262C0A20ECC") + "##xaAbuseFreeCompany")))
                SetDebugFreeCompanyWindowOpen(false);
        }
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S6851F2035736B577"));

        // HUD Navigator capture 2026-09-25: stable node IDs and native event parameters.
        // Component resource IDs (1005/1006 in the capture) are not ComponentType values.
        DrawDebugFreeCompanyButton("Topics", 22, 1);
        ImGui.SameLine();
        DrawDebugFreeCompanyButton("Members", 23, 2);
        ImGui.SameLine();
        DrawDebugFreeCompanyButton("Rank", 24, 3);
        DrawDebugFreeCompanyButton("Actions", 26, 5);
        ImGui.SameLine();
        DrawDebugFreeCompanyButton("Activity", 25, 4);
        ImGui.SameLine();
        DrawDebugFreeCompanyButton("Info", 27, 6);

        ImGui.Spacing();
        DrawDebugFreeCompanyButton("Leave Company", 64, 6, leaveCompany: true);
        ImGui.TextWrapped(global::XASlave.Localization.UiLocale.Get("S48679E21A4EDD66E"));
        if (!string.IsNullOrEmpty(debugFreeCompanyResult))
            ImGui.TextWrapped(global::XASlave.Localization.UiMessage.Resolve(ref debugFreeCompanyResultMessage, debugFreeCompanyResult));
    }

    private void SetDebugFreeCompanyWindowOpen(bool open)
    {
        // Recheck on click: the slash command can toggle an already-open window closed.
        var visible = AddonHelper.IsAddonVisible("FreeCompany");
        if (open)
        {
            if (!Plugin.ClientState.IsLoggedIn)
                debugFreeCompanyResult = global::XASlave.Localization.UiMessage.Store(ref debugFreeCompanyResultMessage, global::XASlave.Localization.UiMessage.Key("S6B26AD3AD85F3B34"));
            else if (visible)
                debugFreeCompanyResult = global::XASlave.Localization.UiMessage.Store(ref debugFreeCompanyResultMessage, global::XASlave.Localization.UiMessage.Key("SCBAC419174093F70"));
            else
                debugFreeCompanyResult = global::XASlave.Localization.UiMessage.Store(ref debugFreeCompanyResultMessage, (ChatHelper.TrySend("/freecompanycmd") ? global::XASlave.Localization.UiMessage.Key("S094E6CB5EDC3D000") : global::XASlave.Localization.UiMessage.Key("S586928D41454EAB8")));
        }
        else if (!visible)
        {
            debugFreeCompanyResult = global::XASlave.Localization.UiMessage.Store(ref debugFreeCompanyResultMessage, global::XASlave.Localization.UiMessage.Key("S225F6F6FCDBCA65A"));
        }
        else if (!AddonHelper.IsAddonReady("FreeCompany"))
        {
            debugFreeCompanyResult = global::XASlave.Localization.UiMessage.Store(ref debugFreeCompanyResultMessage, global::XASlave.Localization.UiMessage.Key("S514E43961E18245A"));
        }
        else
        {
            // Close only the main addon; let the game manage its child panels.
            AddonHelper.CloseAddon("FreeCompany");
            debugFreeCompanyResult = global::XASlave.Localization.UiMessage.Store(ref debugFreeCompanyResultMessage, global::XASlave.Localization.UiMessage.Key("S54D979DE44B0DE1C"));
        }

        SetDebugResult(global::XASlave.Localization.UiMessage.Format("S8A2D0B3D412EB3E0", global::XASlave.Localization.UiMessage.Capture(ref debugFreeCompanyResultMessage, debugFreeCompanyResult)));
    }

    private unsafe void DrawDebugFreeCompanyButton(string label, uint nodeId, uint eventParam,
        bool leaveCompany = false)
    {
        var available = TryGetDebugFreeCompanyButton(label, nodeId, eventParam, leaveCompany,
            out _, out _, out var reason);
        var buttonLabel = leaveCompany ? label : $"Test {label}";
        using (ImRaii.Disabled(!available))
        {
            if (global::XASlave.Localization.UiNative.Button($"{buttonLabel}##xaAbuseFreeCompany", new Vector2(Scale(125f), 0f), display: leaveCompany ? global::XASlave.Localization.UiLocale.T(label) : global::XASlave.Localization.UiLocale.FormatKey("S41F627DAF51F01E1", global::XASlave.Localization.UiLocale.T(label))))
                ClickDebugFreeCompanyButton(label, nodeId, eventParam, leaveCompany);
        }

        if (!available && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            using var tooltip = ImRaii.Tooltip();
            ImGui.TextUnformatted(reason.Display);
        }

        if (leaveCompany && !available)
            ImGui.TextWrapped(reason.Display);
    }

    private static unsafe bool TryGetDebugFreeCompanyButton(string label, uint nodeId, uint eventParam,
        bool leaveCompany, out AtkUnitBase* addon, out AtkResNode* node, out global::XASlave.Localization.UiMessage reason)
    {
        addon = null;
        node = null;
        reason = global::XASlave.Localization.UiMessage.Key("S14C58EB1CEB0DCF1");
        try
        {
            var company = AddonHelper.GetAddon("FreeCompany");
            if (company == null || !company->IsVisible || !company->IsReady)
                return false;

            addon = leaveCompany ? AddonHelper.GetAddon("FreeCompanyStatus") : company;
            reason = global::XASlave.Localization.UiMessage.Key("SA07023F84D70FBDF");
            if (addon == null || !addon->IsVisible || !addon->IsReady)
                return false;

            reason = global::XASlave.Localization.UiMessage.Format("S9282DCD298273BB6", label);
            node = addon->GetNodeById(nodeId);
            if (node == null || (int)node->Type < 1000 || !IsDebugInputNodeVisible(node))
                return false;

            var component = ((AtkComponentNode*)node)->Component;
            var expectedType = leaveCompany ? ComponentType.Button : ComponentType.RadioButton;
            if (component == null || component->GetComponentType() != expectedType)
            {
                reason = global::XASlave.Localization.UiMessage.Format("S525E1992784A4395", label);
                return false;
            }

            // RadioButton inherits AtkComponentButton; validate the actual type before casting.
            var button = leaveCompany
                ? (AtkComponentButton*)component
                : &((AtkComponentRadioButton*)component)->AtkComponentButton;
            if (button->ButtonTextNode == null
                || !string.Equals(button->ButtonTextNode->NodeText.ToString(), label, StringComparison.Ordinal))
            {
                reason = global::XASlave.Localization.UiMessage.Format("SD58C92AF8A50C3E0", label);
                return false;
            }

            var evt = node->AtkEventManager.Event;
            if (evt == null || evt->Param != eventParam)
            {
                reason = global::XASlave.Localization.UiMessage.Format("S230B5068C3634FB5", label);
                return false;
            }

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
            reason = global::XASlave.Localization.UiMessage.Format("S4DEC9B54566A2B94", label, ex.GetType().Name);
            return false;
        }
    }

    private unsafe void ClickDebugFreeCompanyButton(string label, uint nodeId, uint eventParam,
        bool leaveCompany)
    {
        // Resolve and validate again on click; never retain native pointers between frames.
        if (!TryGetDebugFreeCompanyButton(label, nodeId, eventParam, leaveCompany,
                out var addon, out var node, out var reason))
        {
            debugFreeCompanyResult = global::XASlave.Localization.UiMessage.Store(ref debugFreeCompanyResultMessage, reason);
        }
        else
        {
            var dispatched = AddonHelper.ClickResNode(addon, node);
            // The action may destroy an addon: do not dereference native pointers after dispatch.
            debugFreeCompanyResult = global::XASlave.Localization.UiMessage.Store(ref debugFreeCompanyResultMessage, (dispatched ? global::XASlave.Localization.UiMessage.Format("S4D934587CF04248E", label) : global::XASlave.Localization.UiMessage.Format("S2B5FF4CBAD964624", label)));
        }

        SetDebugResult(global::XASlave.Localization.UiMessage.Format("S8A2D0B3D412EB3E0", global::XASlave.Localization.UiMessage.Capture(ref debugFreeCompanyResultMessage, debugFreeCompanyResult)));
    }
}
