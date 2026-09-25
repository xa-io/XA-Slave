using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Component.GUI;
using XASlave.Services;

namespace XASlave.Windows;

public partial class SlaveWindow
{
    private string debugFreeCompanyResult = string.Empty;

    private void DrawXaAbuseFreeCompany()
    {
        var visible = AddonHelper.IsAddonVisible("FreeCompany");
        using (ImRaii.Disabled(!Plugin.ClientState.IsLoggedIn || visible))
        {
            if (ImGui.Button("Open Free Company##xaAbuseFreeCompany"))
                SetDebugFreeCompanyWindowOpen(true);
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(!AddonHelper.IsAddonReady("FreeCompany")))
        {
            if (ImGui.Button("Close Free Company##xaAbuseFreeCompany"))
                SetDebugFreeCompanyWindowOpen(false);
        }
        ImGui.TextWrapped("Open Free Company, then test a tab. Hover a disabled test for details.");

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
        ImGui.TextWrapped("Leave Company clicks the game's button on the Info tab. Complete any confirmation in the game.");
        if (!string.IsNullOrEmpty(debugFreeCompanyResult))
            ImGui.TextWrapped(debugFreeCompanyResult);
    }

    private void SetDebugFreeCompanyWindowOpen(bool open)
    {
        // Recheck on click: the slash command can toggle an already-open window closed.
        var visible = AddonHelper.IsAddonVisible("FreeCompany");
        if (open)
        {
            if (!Plugin.ClientState.IsLoggedIn)
                debugFreeCompanyResult = "Log in before opening Free Company.";
            else if (visible)
                debugFreeCompanyResult = "Free Company is already open.";
            else
                debugFreeCompanyResult = ChatHelper.TrySend("/freecompanycmd")
                    ? "Open command sent; check the game window."
                    : "Could not send /freecompanycmd.";
        }
        else if (!visible)
        {
            debugFreeCompanyResult = "Free Company is already closed.";
        }
        else if (!AddonHelper.IsAddonReady("FreeCompany"))
        {
            debugFreeCompanyResult = "Free Company is not ready to close yet.";
        }
        else
        {
            // Close only the main addon; let the game manage its child panels.
            AddonHelper.CloseAddon("FreeCompany");
            debugFreeCompanyResult = "Close requested; check the game window.";
        }

        SetDebugResult($"Free Company: {debugFreeCompanyResult}");
    }

    private unsafe void DrawDebugFreeCompanyButton(string label, uint nodeId, uint eventParam,
        bool leaveCompany = false)
    {
        var available = TryGetDebugFreeCompanyButton(label, nodeId, eventParam, leaveCompany,
            out _, out _, out var reason);
        var buttonLabel = leaveCompany ? label : $"Test {label}";
        using (ImRaii.Disabled(!available))
        {
            if (ImGui.Button($"{buttonLabel}##xaAbuseFreeCompany", new Vector2(Scale(125f), 0f)))
                ClickDebugFreeCompanyButton(label, nodeId, eventParam, leaveCompany);
        }

        if (!available && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            using var tooltip = ImRaii.Tooltip();
            ImGui.TextUnformatted(reason);
        }

        if (leaveCompany && !available)
            ImGui.TextWrapped(reason);
    }

    private static unsafe bool TryGetDebugFreeCompanyButton(string label, uint nodeId, uint eventParam,
        bool leaveCompany, out AtkUnitBase* addon, out AtkResNode* node, out string reason)
    {
        addon = null;
        node = null;
        reason = "Open the Free Company window in the game.";
        try
        {
            var company = AddonHelper.GetAddon("FreeCompany");
            if (company == null || !company->IsVisible || !company->IsReady)
                return false;

            addon = leaveCompany ? AddonHelper.GetAddon("FreeCompanyStatus") : company;
            reason = "Open the Info tab in Free Company.";
            if (addon == null || !addon->IsVisible || !addon->IsReady)
                return false;

            reason = $"{label}: expected game control is missing or hidden.";
            node = addon->GetNodeById(nodeId);
            if (node == null || (int)node->Type < 1000 || !IsDebugInputNodeVisible(node))
                return false;

            var component = ((AtkComponentNode*)node)->Component;
            var expectedType = leaveCompany ? ComponentType.Button : ComponentType.RadioButton;
            if (component == null || component->GetComponentType() != expectedType)
            {
                reason = $"{label}: game control type does not match the captured layout.";
                return false;
            }

            // RadioButton inherits AtkComponentButton; validate the actual type before casting.
            var button = leaveCompany
                ? (AtkComponentButton*)component
                : &((AtkComponentRadioButton*)component)->AtkComponentButton;
            if (button->ButtonTextNode == null
                || !string.Equals(button->ButtonTextNode->NodeText.ToString(), label, StringComparison.Ordinal))
            {
                reason = $"{label}: this test requires the matching English game button.";
                return false;
            }

            var evt = node->AtkEventManager.Event;
            if (evt == null || evt->Param != eventParam)
            {
                reason = $"{label}: game button event does not match the captured layout.";
                return false;
            }

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
            reason = $"{label}: game control check failed ({ex.GetType().Name}).";
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
            debugFreeCompanyResult = reason;
        }
        else
        {
            var dispatched = AddonHelper.ClickResNode(addon, node);
            // The action may destroy an addon: do not dereference native pointers after dispatch.
            debugFreeCompanyResult = dispatched
                ? $"{label}: click sent; check the game result."
                : $"{label}: click could not be sent.";
        }

        SetDebugResult($"Free Company: {debugFreeCompanyResult}");
    }
}
