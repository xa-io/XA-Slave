using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XASlave.Services;

public unsafe sealed class AutoOpenMoogleMailService : IDisposable
{
    private const long NonPlayerSenderThreshold = 100000000000L;
    private const int MaxOperationFailures = 30;
    private const int RetryDelayMs = 250;
    private const int LetterListOpenDelayMs = 750;
    private const int OpenLetterDelayMs = 200;
    private const int LetterViewerDelayMs = 500;
    private const int AttachmentTransferDelayMs = 150;
    private const int AttachmentTransferPollDelayMs = 50;
    private const int NextLetterDelayMs = 200;
    private const int ViewerCloseDelayMs = 75;
    private const int DeleteConfirmationDelayMs = 350;
    private const float OverlayButtonWidth = 80f;
    private const float OverlayButtonHeight = 24f;
    private const float OverlayHorizontalPadding = 10f;
    private const float OverlayButtonRounding = 4f;
    private const int OverlayButtonsPerRow = 3;
    private const int OverlayButtonRows = 1;
    private const AgentId LetterListAgentId = AgentId.Letter;

    private readonly IFramework framework;
    private readonly IPluginLog log;
    private bool enabled;
    private bool subscribed;
    private int knownLetterCount = -1;
    private int knownNewLetterCount = -1;
    private int knownAttachmentCount = -1;
    private PendingOperation? pendingOperation;

    public AutoOpenMoogleMailService(
        IFramework framework,
        IPluginLog log)
    {
        this.framework = framework;
        this.log = log;
    }

    public string StatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? uiStatusText = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string LocalizedStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiStatusText, StatusText);
    internal global::XASlave.Localization.UiMessage StatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiStatusText, StatusText);
    public string LastActionText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S03F020CBB71754CC");
    private global::XASlave.Localization.UiMessage? uiLastActionText = global::XASlave.Localization.UiMessage.Key("S03F020CBB71754CC");
    internal string LocalizedLastActionText => global::XASlave.Localization.UiMessage.Resolve(ref uiLastActionText, LastActionText);
    internal global::XASlave.Localization.UiMessage LastActionTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiLastActionText, LastActionText);
    public int KnownLetterCount => knownLetterCount < 0 ? 0 : knownLetterCount;
    public int KnownNewLetterCount => knownNewLetterCount < 0 ? 0 : knownNewLetterCount;
    public int KnownAttachmentCount => knownAttachmentCount < 0 ? 0 : knownAttachmentCount;
    internal global::XASlave.Localization.UiMessage PendingOperationTextMessage => pendingOperation == null ? global::XASlave.Localization.UiMessage.Key("SAB0171CA0494D441") : global::XASlave.Localization.UiMessage.Format("S8B4157F20FD802BB", global::XASlave.Localization.UiMessage.Authored(pendingOperation.Label), pendingOperation.ProcessedCount, pendingOperation.TargetIndices.Count);
    public string PendingOperationText => PendingOperationTextMessage.Original;
    internal string LocalizedPendingOperationText => PendingOperationTextMessage.Display;
    public int CompletedOperationCount { get; private set; }
    public int LastProcessedLetterCount { get; private set; }
    public bool CanQueueManualOperation => enabled && pendingOperation == null;
    public bool CanStopPendingOperation => enabled && pendingOperation != null;
    public bool IsProcessing => pendingOperation != null;

    public bool SetEnabled(bool value)
    {
        if (value == enabled)
            return enabled;

        if (!value)
        {
            enabled = false;
            pendingOperation = null;
            Unsubscribe();
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"));
            return false;
        }

        enabled = true;
        RefreshKnownCounts();
        Subscribe();
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, BuildStatusText());
        return true;
    }

    public void Dispose()
    {
        enabled = false;
        pendingOperation = null;
        Unsubscribe();
    }

    public bool TryOpenLetterList()
    {
        if (!enabled)
            return RejectDisabledManualAction(global::XASlave.Localization.UiMessage.Key("S868C9E54743A15D4"));

        return TryOpenLetterListCore();
    }

    public bool TryOpenLetterListFromCommand()
    {
        return TryOpenLetterListCore();
    }

    private bool TryOpenLetterListCore()
    {
        try
        {
            if (AddonHelper.IsAddonVisible("LetterList"))
            {
                LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S865A73E3885629D0", DateTime.Now));
                return true;
            }

            var agentModule = AgentModule.Instance();
            if (agentModule == null)
            {
                LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("SA7245103392100DF", DateTime.Now));
                return false;
            }

            var agent = agentModule->GetAgentByInternalId(LetterListAgentId);
            if (agent == null)
            {
                LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S40C61DF46B841FD9", DateTime.Now));
                return false;
            }

            agent->Show();
            LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S93039AFE7A1BB94F", DateTime.Now));
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Open Moogle Mail failed while trying to open Letter List.");
            LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S95515292EE829B7A", DateTime.Now));
            return false;
        }
    }

    public bool QueueDeleteAllLetters()
    {
        return RunBatchDeleteOperation(
            global::XASlave.Localization.UiLocale.Native("SB9300B3F10B553E5"),
            letter => letter.Timestamp != 0 && !HasAttachments(letter));
    }

    public bool QueueDeleteNonPlayerLetters()
    {
        return RunBatchDeleteOperation(
            global::XASlave.Localization.UiLocale.Native("SC56925D351163189"),
            letter => letter.Timestamp != 0 && letter.SenderContentId < NonPlayerSenderThreshold && !HasAttachments(letter));
    }

    public bool QueueClaimAttachments(bool deleteAllWhenFinished = false)
    {
        return QueueLetterOperation(
            PendingOperationKind.ClaimAttachments,
            global::XASlave.Localization.UiLocale.Native("S768EF0FD634990FB"),
            HasAttachments,
            sortDescending: false,
            deleteAllWhenFinished: deleteAllWhenFinished);
    }

    public bool QueueRequestDelivery()
    {
        if (!enabled)
            return RejectDisabledManualAction(global::XASlave.Localization.UiMessage.Key("S84E18D53C48AB11E"));

        if (pendingOperation != null)
        {
            LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S398BACA841BEE876", global::XASlave.Localization.UiMessage.Authored(pendingOperation.Label), DateTime.Now));
            return false;
        }

        pendingOperation = new PendingOperation(PendingOperationKind.RequestDelivery, global::XASlave.Localization.UiLocale.Native("S708C4E4FF9E6B46C"), []);
        LastProcessedLetterCount = 0;
        LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S56535DE92D29B132", DateTime.Now));
        return true;
    }

    public bool StopPendingOperation()
    {
        if (pendingOperation == null)
        {
            LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S85A2E768EEE1F4EE", DateTime.Now));
            return false;
        }

        var label = pendingOperation.Label;
        pendingOperation = null;

        if (AddonHelper.IsAddonVisible("SelectYesno"))
            AddonHelper.ClickYesNo(false, closeAfter: true);

        CloseLetterViewer();
        LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S76F3FB3419BDC65B", global::XASlave.Localization.UiMessage.Authored(label.ToLowerInvariant()), DateTime.Now));
        return true;
    }

    public void DrawOverlay()
    {
        if (!enabled || !TryGetLetterListOverlayAnchor(out var position))
            return;

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoNav;

        ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.0f);
        if (!ImGui.Begin("##XASlaveAutoOpenMoogleMailOverlay", flags))
        {
            ImGui.End();
            return;
        }

        var operationActive = pendingOperation != null;
        if (operationActive)
        {
            var stopWidth = (OverlayButtonWidth * OverlayButtonsPerRow) + (ImGui.GetStyle().ItemSpacing.X * (OverlayButtonsPerRow - 1));
            if (DrawOverlayButton(global::XASlave.Localization.UiLocale.Get("SCAE7D57BC067A514"), "Stop", stopWidth))
            {
                StopPendingOperation();
                ImGui.End();
                return;
            }
        }

        var disableActions = !CanQueueManualOperation;
        if (disableActions)
            ImGui.BeginDisabled();

        if (DrawOverlayButton(global::XASlave.Localization.UiLocale.Get("S680FF55706A1AB03"), "TakeAll"))
            QueueClaimAttachments(Plugin.Instance?.Configuration.AutoOpenMoogleMailDeleteAllWhenFinished ?? false);

        ImGui.SameLine();
        if (DrawOverlayButton(global::XASlave.Localization.UiLocale.Get("S978111835DFADD7A"), "DeleteAll"))
            QueueDeleteAllLetters();

        ImGui.SameLine();
        if (DrawOverlayButton(global::XASlave.Localization.UiLocale.Get("S7164BC1579A8D874"), "DeleteNpc"))
            QueueDeleteNonPlayerLetters();

        if (disableActions)
            ImGui.EndDisabled();

        ImGui.End();
    }

    private static bool DrawOverlayButton(string label, string id, float width = OverlayButtonWidth)
    {
        var clicked = ImGui.InvisibleButton($"##AutoOpenMoogleMailOverlay{id}", new Vector2(width, OverlayButtonHeight));
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        var topColor = ImGui.ColorConvertFloat4ToU32(new Vector4(94f / 255f, 93f / 255f, 94f / 255f, 1.0f));
        var bottomColor = ImGui.ColorConvertFloat4ToU32(new Vector4(52f / 255f, 51f / 255f, 52f / 255f, 1.0f));
        var borderColor = ImGui.ColorConvertFloat4ToU32(new Vector4(168f / 255f, 169f / 255f, 168f / 255f, 1.0f));
        var hoverOverlay = ImGui.ColorConvertFloat4ToU32(new Vector4(1.0f, 1.0f, 1.0f, 0.08f));
        var activeOverlay = ImGui.ColorConvertFloat4ToU32(new Vector4(0.0f, 0.0f, 0.0f, 0.12f));
        var textColor = ImGui.GetColorU32(ImGuiCol.Text);

        drawList.AddRectFilledMultiColor(min, max, topColor, topColor, bottomColor, bottomColor);
        drawList.AddRect(min, max, borderColor, OverlayButtonRounding, ImDrawFlags.None, 1.0f);

        if (ImGui.IsItemActive())
            drawList.AddRectFilled(min, max, activeOverlay, OverlayButtonRounding);
        else if (ImGui.IsItemHovered())
            drawList.AddRectFilled(min, max, hoverOverlay, OverlayButtonRounding);

        var textSize = ImGui.CalcTextSize(label);
        var textPos = new Vector2(
            min.X + Math.Max(6f, (width - textSize.X) * 0.5f),
            min.Y + Math.Max(0f, (OverlayButtonHeight - textSize.Y) * 0.5f));
        ImGui.PushClipRect(min, max, true);
        drawList.AddText(textPos, textColor, label);
        ImGui.PopClipRect();
        return clicked;
    }

    private bool RejectDisabledManualAction(global::XASlave.Localization.UiMessage skippedAction)
    {
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("SF2800DC42005260E"));
        LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S50903BAEE59D4C45", skippedAction, DateTime.Now));
        return false;
    }

    private global::XASlave.Localization.UiMessage BuildStatusText()
    {
        return global::XASlave.Localization.UiMessage.Key("SB389D56C1FCEDB91");
    }

    private bool TryGetLetterListOverlayAnchor(out Vector2 position)
    {
        position = Vector2.Zero;
        var addon = AddonHelper.GetAddon("LetterList");
        if (addon == null || !addon->IsVisible || addon->RootNode == null)
            return false;

        var width = Math.Max(1f, addon->RootNode->Width * addon->RootNode->ScaleX);
        var style = ImGui.GetStyle();
        var overlayWidth = (OverlayButtonWidth * OverlayButtonsPerRow) + (style.ItemSpacing.X * (OverlayButtonsPerRow - 1));
        var overlayRows = pendingOperation == null ? OverlayButtonRows : OverlayButtonRows + 1;
        var overlayHeight = (OverlayButtonHeight * overlayRows) + (style.ItemSpacing.Y * (overlayRows - 1));
        position = new Vector2(
            addon->RootNode->ScreenX + Math.Max(OverlayHorizontalPadding, (width - overlayWidth) * 0.5f),
            Math.Max(0f, addon->RootNode->ScreenY - overlayHeight - 6f));
        return true;
    }

    private void Subscribe()
    {
        if (subscribed)
            return;

        framework.Update += OnFrameworkUpdate;
        subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!subscribed)
            return;

        framework.Update -= OnFrameworkUpdate;
        subscribed = false;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!enabled)
            return;

        try
        {
            RefreshKnownCounts();

            if (pendingOperation == null)
                return;

            if (DateTime.UtcNow < pendingOperation.ExecuteAtUtc)
                return;

            switch (pendingOperation.Kind)
            {
                case PendingOperationKind.RequestDelivery:
                    ProcessRequestDelivery();
                    break;
                case PendingOperationKind.ClaimAttachments:
                    ProcessClaimOperation();
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Open Moogle Mail failed while processing the framework update.");
            AbortPendingOperation(global::XASlave.Localization.UiMessage.Key("SE1AABDF928459893"));
        }
    }

    private void RefreshKnownCounts()
    {
        var info = InfoProxyLetter.Instance();
        if (info == null)
            return;

        var currentLetterCount = CountLetters(info);
        var currentNewLetterCount = (int)info->NumNewLetters;
        var currentAttachmentCount = (int)info->NumAttachments;

        knownLetterCount = currentLetterCount;
        knownNewLetterCount = currentNewLetterCount;
        knownAttachmentCount = currentAttachmentCount;
    }

    private void ProcessRequestDelivery()
    {
        if (!EnsureLetterListReady())
            return;

        if (!SendLetterListEvent(9, 0))
        {
            RetryOrAbort(global::XASlave.Localization.UiMessage.Key("S4966D27A2B2749B9"));
            return;
        }

        CompletedOperationCount++;
        LastProcessedLetterCount = 0;
        LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S623F94CB07B3CBA5", DateTime.Now));
        pendingOperation = null;
    }

    private void ProcessClaimOperation()
    {
        if (pendingOperation == null)
            return;

        if (pendingOperation.IsComplete)
        {
            CompletePendingOperation();
            return;
        }

        switch (pendingOperation.Stage)
        {
            case PendingOperationStage.SelectingLetter:
            {
                if (!EnsureLetterListReady())
                    return;

                var targetIndex = pendingOperation.CurrentTargetIndex;
                if (!CurrentTargetMatches(HasAttachments))
                {
                    pendingOperation.ProcessedCount++;
                    pendingOperation.Position++;
                    pendingOperation.ExecuteAtUtc = DateTime.UtcNow.AddMilliseconds(AttachmentTransferPollDelayMs);
                    return;
                }

                pendingOperation.ConfirmedCurrentPrompt = false;
                if (!SendLetterListEvent(0, 0, targetIndex, 0, 1))
                {
                    RetryOrAbort(global::XASlave.Localization.UiMessage.Format("S5D7AE361B26E0B94", targetIndex));
                    return;
                }

                pendingOperation.Stage = PendingOperationStage.OpeningLetter;
                pendingOperation.ExecuteAtUtc = DateTime.UtcNow.AddMilliseconds(OpenLetterDelayMs);
                pendingOperation.FailureCount = 0;
                LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S4436CA83167F8468", targetIndex, DateTime.Now));
                return;
            }
            case PendingOperationStage.OpeningLetter:
            {
                if (!SendOpenLetterEvent())
                {
                    RetryOrAbort(global::XASlave.Localization.UiMessage.Format("S1B196C9BA736B2FD", pendingOperation.CurrentTargetIndex));
                    return;
                }

                pendingOperation.Stage = PendingOperationStage.WaitingForViewer;
                pendingOperation.ExecuteAtUtc = DateTime.UtcNow.AddMilliseconds(LetterViewerDelayMs);
                pendingOperation.FailureCount = 0;
                LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("SE695D06217504F3D", pendingOperation.CurrentTargetIndex, DateTime.Now));
                return;
            }
            case PendingOperationStage.WaitingForViewer:
            {
                if (WaitForSelectYesnoToClear(global::XASlave.Localization.UiMessage.Key("S05560C637E0B1488")))
                    return;

                if (AddonHelper.IsAddonVisible("LetterEditor"))
                {
                    AddonHelper.CloseAddon("LetterEditor");
                    AbortPendingOperation(global::XASlave.Localization.UiMessage.Key("S7361B0118D6B7E1F"));
                    return;
                }

                var viewer = AddonHelper.GetAddon("LetterViewer");
                if (viewer == null || !viewer->IsVisible || !viewer->IsReady)
                {
                    RetryOrAbort(global::XASlave.Localization.UiMessage.Key("S2AB2198451F44F59"));
                    return;
                }

                if (!SendLetterViewEvent(0, 1))
                {
                    RetryOrAbort(global::XASlave.Localization.UiMessage.Key("S6550BB868B6609D0"));
                    return;
                }

                pendingOperation.ConfirmedCurrentPrompt = false;
                pendingOperation.AcceptedAttachmentPrompt = false;
                pendingOperation.Stage = PendingOperationStage.WaitingForAttachmentTransfer;
                pendingOperation.ExecuteAtUtc = DateTime.UtcNow.AddMilliseconds(AttachmentTransferDelayMs);
                pendingOperation.FailureCount = 0;
                LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S9C7EEE196536A77D", pendingOperation.CurrentTargetIndex, DateTime.Now));
                return;
            }
            case PendingOperationStage.WaitingForAttachmentTransfer:
            {
                if (AddonHelper.IsAddonVisible("SelectYesno"))
                {
                    if (!pendingOperation.AcceptedAttachmentPrompt)
                    {
                        if (!AddonHelper.ClickYesNo(true, closeAfter: false))
                        {
                            RetryOrAbort(global::XASlave.Localization.UiMessage.Key("SD74FFE8609488ABC"));
                            return;
                        }

                        pendingOperation.AcceptedAttachmentPrompt = true;
                        pendingOperation.FailureCount = 0;
                        pendingOperation.ExecuteAtUtc = DateTime.UtcNow.AddMilliseconds(AttachmentTransferPollDelayMs);
                        return;
                    }

                    AddonHelper.ClickYesNo(false, closeAfter: true);
                    pendingOperation.ExecuteAtUtc = DateTime.UtcNow.AddMilliseconds(AttachmentTransferPollDelayMs);
                    return;
                }

                pendingOperation.ConfirmedCurrentPrompt = false;
                if (IsLetterTransferBusy())
                {
                    pendingOperation.ExecuteAtUtc = DateTime.UtcNow.AddMilliseconds(AttachmentTransferPollDelayMs);
                    return;
                }

                CloseLetterViewer();
                pendingOperation.Stage = PendingOperationStage.ClosingViewer;
                pendingOperation.ExecuteAtUtc = DateTime.UtcNow.AddMilliseconds(ViewerCloseDelayMs);
                pendingOperation.FailureCount = 0;
                return;
            }
            case PendingOperationStage.ClosingViewer:
            {
                var viewer = AddonHelper.GetAddon("LetterViewer");
                if (viewer != null && viewer->IsVisible)
                {
                    RetryOrAbort(global::XASlave.Localization.UiMessage.Key("SCAFEA3D4CC52B2DD"));
                    return;
                }

                pendingOperation.ProcessedCount++;
                pendingOperation.Position++;
                pendingOperation.Stage = PendingOperationStage.SelectingLetter;
                pendingOperation.ExecuteAtUtc = DateTime.UtcNow.AddMilliseconds(NextLetterDelayMs);
                pendingOperation.FailureCount = 0;

                if (pendingOperation.IsComplete)
                    CompletePendingOperation();

                return;
            }
            case PendingOperationStage.WaitingForConfirmation:
                pendingOperation.Stage = PendingOperationStage.SelectingLetter;
                pendingOperation.ExecuteAtUtc = DateTime.UtcNow.AddMilliseconds(100);
                return;
        }
    }

    private bool WaitForSelectYesnoToClear(global::XASlave.Localization.UiMessage promptDescription)
    {
        if (pendingOperation == null || !AddonHelper.IsAddonVisible("SelectYesno"))
            return false;

        if (!pendingOperation.ConfirmedCurrentPrompt)
        {
            if (!AddonHelper.ClickYesNo(true, closeAfter: false))
            {
                RetryOrAbort(global::XASlave.Localization.UiMessage.Format("SBF2F2E301678DC64", promptDescription));
                return true;
            }

            pendingOperation.ConfirmedCurrentPrompt = true;
            pendingOperation.FailureCount = 0;
        }

        pendingOperation.ExecuteAtUtc = DateTime.UtcNow.AddMilliseconds(DeleteConfirmationDelayMs);
        return true;
    }

    private bool SendOpenLetterEvent()
    {
        return SendLetterListEvent(1, 0, 0, 0U, 0, 0);
    }

    private bool SendLetterListEvent(ulong eventKind, params object[] eventParams)
    {
        return SendMailAgentEvent(LetterListAgentId, eventKind, eventParams);
    }

    private bool SendLetterViewEvent(ulong eventKind, params object[] eventParams)
    {
        return SendMailAgentEvent(AgentId.LetterView, eventKind, eventParams);
    }

    private bool SendMailAgentEvent(AgentId agentId, ulong eventKind, params object[] eventParams)
    {
        try
        {
            var agentModule = AgentModule.Instance();
            if (agentModule == null)
                return false;

            var agent = agentModule->GetAgentByInternalId(agentId);
            if (agent == null)
                return false;

            var result = stackalloc AtkValue[1];
            result[0] = default;

            AtkValue* values = null;
            if (eventParams.Length > 0)
            {
                var allocatedValues = stackalloc AtkValue[eventParams.Length];
                values = allocatedValues;
                for (var index = 0; index < eventParams.Length; index++)
                {
                    values[index] = default;
                    switch (eventParams[index])
                    {
                        case uint uintValue:
                            values[index].Type = AtkValueType.UInt;
                            values[index].UInt = uintValue;
                            break;
                        case int intValue:
                            values[index].Type = AtkValueType.Int;
                            values[index].Int = intValue;
                            break;
                        default:
                            values[index].Type = AtkValueType.Int;
                            values[index].Int = Convert.ToInt32(eventParams[index]);
                            break;
                    }
                }
            }

            agent->ReceiveEvent(result, values, (uint)eventParams.Length, eventKind);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Open Moogle Mail failed while sending agent event {EventKind} to {AgentId}.", eventKind, agentId);
            return false;
        }
    }

    private bool EnsureLetterListReady()
    {
        if (AddonHelper.IsAddonReady("LetterList"))
            return true;

        if (AddonHelper.IsAddonVisible("LetterList"))
        {
            if (pendingOperation != null)
                pendingOperation.ExecuteAtUtc = DateTime.UtcNow.AddMilliseconds(RetryDelayMs);

            return false;
        }

        if (TryOpenLetterList())
        {
            if (pendingOperation != null)
            {
                pendingOperation.ExecuteAtUtc = DateTime.UtcNow.AddMilliseconds(LetterListOpenDelayMs);
                pendingOperation.FailureCount = 0;
            }

            return false;
        }

        RetryOrAbort(global::XASlave.Localization.UiMessage.Key("S2A05D59024B21C99"));
        return false;
    }

    private bool QueueLetterOperation(
        PendingOperationKind kind,
        string label,
        Predicate<InfoProxyLetter.Letter> predicate,
        bool sortDescending,
        bool deleteAllWhenFinished = false)
    {
        if (!enabled)
            return RejectDisabledManualAction(global::XASlave.Localization.UiMessage.Format("S436C930DCD82AA15", global::XASlave.Localization.UiMessage.Authored(label)));

        if (pendingOperation != null)
        {
            LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S398BACA841BEE876", global::XASlave.Localization.UiMessage.Authored(pendingOperation.Label), DateTime.Now));
            return false;
        }

        if (!TryFindTargetIndices(predicate, out var indices))
        {
            LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S2A172FAA485C348E", global::XASlave.Localization.UiMessage.Authored(label), DateTime.Now));
            return false;
        }

        var orderedIndices = sortDescending
            ? indices.OrderByDescending(value => value).ToList()
            : indices.OrderBy(value => value).ToList();

        pendingOperation = new PendingOperation(kind, label, orderedIndices)
        {
            DeleteAllWhenFinished = deleteAllWhenFinished,
        };
        LastProcessedLetterCount = 0;
        LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S744B9A098DA40D9F", global::XASlave.Localization.UiMessage.Authored(label), orderedIndices.Count, DateTime.Now));
        return true;
    }

    private bool RunBatchDeleteOperation(string label, Predicate<InfoProxyLetter.Letter> predicate)
    {
        if (!enabled)
            return RejectDisabledManualAction(global::XASlave.Localization.UiMessage.Format("S436C930DCD82AA15", global::XASlave.Localization.UiMessage.Authored(label)));

        if (pendingOperation != null)
        {
            LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S398BACA841BEE876", global::XASlave.Localization.UiMessage.Authored(pendingOperation.Label), DateTime.Now));
            return false;
        }

        if (!EnsureLetterListReady())
            return false;

        if (!TryFindTargetIndices(predicate, out var indices))
        {
            LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S2A172FAA485C348E", global::XASlave.Localization.UiMessage.Authored(label), DateTime.Now));
            return false;
        }

        var processed = 0;
        foreach (var index in indices.OrderByDescending(value => value))
        {
            if (!SendLetterListEvent(0, 0, index, 0, 1) || !SendLetterListEvent(4, 0))
            {
                LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S6EFF6AFBFF81C339", global::XASlave.Localization.UiMessage.Authored(label), processed, index, DateTime.Now));
                return false;
            }

            processed++;
        }

        CompletedOperationCount++;
        LastProcessedLetterCount = processed;
        LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S4A02E2F66E0ABA35", global::XASlave.Localization.UiMessage.Authored(label.ToLowerInvariant()), processed, DateTime.Now));
        return true;
    }

    private bool TryFindTargetIndices(Predicate<InfoProxyLetter.Letter> predicate, out List<int> indices)
    {
        indices = [];
        var info = InfoProxyLetter.Instance();
        if (info == null)
            return false;

        var letters = info->Letters;
        for (var index = 0; index < letters.Length; index++)
        {
            var letter = letters[index];
            if (letter.Timestamp == 0 || !predicate(letter))
                continue;

            indices.Add(index);
        }

        return indices.Count > 0;
    }

    private bool CurrentTargetMatches(Predicate<InfoProxyLetter.Letter> predicate)
    {
        if (pendingOperation == null || pendingOperation.IsComplete)
            return false;

        var info = InfoProxyLetter.Instance();
        if (info == null)
            return false;

        var index = pendingOperation.CurrentTargetIndex;
        var letters = info->Letters;
        if (index < 0 || index >= letters.Length)
            return false;

        var letter = letters[index];
        return letter.Timestamp != 0 && predicate(letter);
    }

    private void RetryOrAbort(global::XASlave.Localization.UiMessage failureReason)
    {
        if (pendingOperation == null)
            return;

        pendingOperation.FailureCount++;
        if (pendingOperation.FailureCount >= MaxOperationFailures)
        {
            AbortPendingOperation(failureReason);
            return;
        }

        pendingOperation.ExecuteAtUtc = DateTime.UtcNow.AddMilliseconds(RetryDelayMs);
    }

    private void AbortPendingOperation(global::XASlave.Localization.UiMessage failureReason)
    {
        var label = pendingOperation?.Label ?? global::XASlave.Localization.UiLocale.Native("S595C22BBA252CC7A");
        pendingOperation = null;
        LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S8CD99EEA0306673E", global::XASlave.Localization.UiMessage.Authored(label), failureReason, DateTime.Now));
    }

    private void CompletePendingOperation()
    {
        if (pendingOperation == null)
            return;

        CompletedOperationCount++;
        LastProcessedLetterCount = pendingOperation.ProcessedCount;
        LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S4A02E2F66E0ABA35", global::XASlave.Localization.UiMessage.Authored(pendingOperation.Label.ToLowerInvariant()), pendingOperation.ProcessedCount, DateTime.Now));
        var deleteAllWhenFinished = pendingOperation.Kind == PendingOperationKind.ClaimAttachments && pendingOperation.DeleteAllWhenFinished;
        pendingOperation = null;

        if (deleteAllWhenFinished)
            QueueDeleteAllLetters();
    }

    private static bool HasAttachments(InfoProxyLetter.Letter letter)
    {
        foreach (var attachment in letter.Attachments)
        {
            if (attachment.Count > 0)
                return true;
        }

        return false;
    }

    private static bool IsLetterTransferBusy()
    {
        var stage = AtkStage.Instance();
        if (stage == null)
            return false;

        var letterData = stage->GetNumberArrayData(NumberArrayType.Letter);
        var compatible = NativeArrayAccess.TryGetInt(
            letterData,
            NativeOffsets.LetterTransferBusyNumberIndex,
            out var busyValue);
        NativeOffsets.ReportOnce(
            Plugin.Log,
            "NumberArray.Letter.TransferBusy",
            compatible,
            $"length={(letterData == null ? 0 : letterData->Size)}, index={NativeOffsets.LetterTransferBusyNumberIndex}");
        return compatible && busyValue != 0;
    }

    private void CloseLetterViewer()
    {
        var viewer = AddonHelper.GetAddon("LetterViewer");
        if (viewer != null && viewer->IsVisible)
            viewer->Close(true);

        SendLetterViewEvent(0, -1);
    }

    private static int CountLetters(InfoProxyLetter* info)
    {
        if (info == null)
            return 0;

        var count = 0;
        var letters = info->Letters;
        for (var index = 0; index < letters.Length; index++)
        {
            if (letters[index].Timestamp != 0)
                count++;
        }

        return count;
    }

    private enum PendingOperationKind
    {
        ClaimAttachments,
        RequestDelivery,
    }

    private enum PendingOperationStage
    {
        SelectingLetter,
        OpeningLetter,
        WaitingForConfirmation,
        WaitingForViewer,
        WaitingForAttachmentTransfer,
        ClosingViewer,
    }

    private sealed class PendingOperation
    {
        public PendingOperation(PendingOperationKind kind, string label, List<int> targetIndices)
        {
            Kind = kind;
            Label = label;
            TargetIndices = targetIndices;
        }

        public PendingOperationKind Kind { get; }
        public string Label { get; }
        public List<int> TargetIndices { get; }
        public bool DeleteAllWhenFinished { get; set; }
        public PendingOperationStage Stage { get; set; } = PendingOperationStage.SelectingLetter;
        public DateTime ExecuteAtUtc { get; set; }
        public int Position { get; set; }
        public int ProcessedCount { get; set; }
        public int FailureCount { get; set; }
        public bool ConfirmedCurrentPrompt { get; set; }
        public bool AcceptedAttachmentPrompt { get; set; }
        public bool IsComplete => Position >= TargetIndices.Count;
        public int CurrentTargetIndex => Position < TargetIndices.Count ? TargetIndices[Position] : -1;
    }
}
