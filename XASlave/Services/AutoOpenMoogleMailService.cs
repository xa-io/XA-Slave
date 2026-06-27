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

    public string StatusText { get; private set; } = "Disabled";
    public string LastActionText { get; private set; } = "No actions yet.";
    public int KnownLetterCount => knownLetterCount < 0 ? 0 : knownLetterCount;
    public int KnownNewLetterCount => knownNewLetterCount < 0 ? 0 : knownNewLetterCount;
    public int KnownAttachmentCount => knownAttachmentCount < 0 ? 0 : knownAttachmentCount;
    public string PendingOperationText => pendingOperation == null
        ? "Idle"
        : $"{pendingOperation.Label} ({pendingOperation.ProcessedCount}/{pendingOperation.TargetIndices.Count})";
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
            StatusText = "Disabled";
            return false;
        }

        enabled = true;
        RefreshKnownCounts();
        Subscribe();
        StatusText = BuildStatusText();
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
            return RejectDisabledManualAction("the Letter List was not opened");

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
                LastActionText = $"Last action: Letter List was already open at {DateTime.Now:HH:mm:ss}.";
                return true;
            }

            var agentModule = AgentModule.Instance();
            if (agentModule == null)
            {
                LastActionText = $"Last action: failed to resolve AgentModule for Letter List at {DateTime.Now:HH:mm:ss}.";
                return false;
            }

            var agent = agentModule->GetAgentByInternalId(LetterListAgentId);
            if (agent == null)
            {
                LastActionText = $"Last action: failed to resolve the Letter List agent at {DateTime.Now:HH:mm:ss}.";
                return false;
            }

            agent->Show();
            LastActionText = $"Last action: requested the Letter List window at {DateTime.Now:HH:mm:ss}.";
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Open Moogle Mail failed while trying to open Letter List.");
            LastActionText = $"Last action: opening Letter List threw an exception at {DateTime.Now:HH:mm:ss}.";
            return false;
        }
    }

    public bool QueueDeleteAllLetters()
    {
        return RunBatchDeleteOperation(
            "Deleting all opened letters",
            letter => letter.Timestamp != 0 && !HasAttachments(letter));
    }

    public bool QueueDeleteNonPlayerLetters()
    {
        return RunBatchDeleteOperation(
            "Deleting opened NPC letters",
            letter => letter.Timestamp != 0 && letter.SenderContentId < NonPlayerSenderThreshold && !HasAttachments(letter));
    }

    public bool QueueClaimAttachments(bool deleteAllWhenFinished = false)
    {
        return QueueLetterOperation(
            PendingOperationKind.ClaimAttachments,
            "Claiming all letter attachments",
            HasAttachments,
            sortDescending: false,
            deleteAllWhenFinished: deleteAllWhenFinished);
    }

    public bool QueueRequestDelivery()
    {
        if (!enabled)
            return RejectDisabledManualAction("the delivery request was not queued");

        if (pendingOperation != null)
        {
            LastActionText = $"Last action: another mail operation is already running ({pendingOperation.Label}) at {DateTime.Now:HH:mm:ss}.";
            return false;
        }

        pendingOperation = new PendingOperation(PendingOperationKind.RequestDelivery, "Requesting delivery", []);
        LastProcessedLetterCount = 0;
        LastActionText = $"Last action: queued a moogle delivery request at {DateTime.Now:HH:mm:ss}.";
        return true;
    }

    public bool StopPendingOperation()
    {
        if (pendingOperation == null)
        {
            LastActionText = $"Last action: no Moogle Mail operation was running at {DateTime.Now:HH:mm:ss}.";
            return false;
        }

        var label = pendingOperation.Label;
        pendingOperation = null;

        if (AddonHelper.IsAddonVisible("SelectYesno"))
            AddonHelper.ClickYesNo(false, closeAfter: true);

        CloseLetterViewer();
        LastActionText = $"Last action: stopped {label.ToLowerInvariant()} at {DateTime.Now:HH:mm:ss}.";
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
            if (DrawOverlayButton("Stop", "Stop", stopWidth))
            {
                StopPendingOperation();
                ImGui.End();
                return;
            }
        }

        if (!CanQueueManualOperation)
            ImGui.BeginDisabled();

        if (DrawOverlayButton("Take all", "TakeAll"))
            QueueClaimAttachments();

        ImGui.SameLine();
        if (DrawOverlayButton("Delete all", "DeleteAll"))
            QueueDeleteAllLetters();

        ImGui.SameLine();
        if (DrawOverlayButton("Delete NPC", "DeleteNpc"))
            QueueDeleteNonPlayerLetters();

        if (!CanQueueManualOperation)
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

    private bool RejectDisabledManualAction(string skippedAction)
    {
        StatusText = "Unavailable - enable Auto Open Moogle Mail before using it.";
        LastActionText = $"Last action: Auto Open Moogle Mail is disabled, so {skippedAction} at {DateTime.Now:HH:mm:ss}.";
        return false;
    }

    private string BuildStatusText()
    {
        return "Enabled.";
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
            AbortPendingOperation("processing threw an exception");
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
            RetryOrAbort("failed to request delivery through AgentId.Letter");
            return;
        }

        CompletedOperationCount++;
        LastProcessedLetterCount = 0;
        LastActionText = $"Last action: requested delivery mail at {DateTime.Now:HH:mm:ss}.";
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
                    RetryOrAbort($"failed to select letter index {targetIndex} for attachment claim");
                    return;
                }

                pendingOperation.Stage = PendingOperationStage.OpeningLetter;
                pendingOperation.ExecuteAtUtc = DateTime.UtcNow.AddMilliseconds(OpenLetterDelayMs);
                pendingOperation.FailureCount = 0;
                LastActionText = $"Last action: selected letter index {targetIndex} to claim attachments at {DateTime.Now:HH:mm:ss}.";
                return;
            }
            case PendingOperationStage.OpeningLetter:
            {
                if (!SendOpenLetterEvent())
                {
                    RetryOrAbort($"failed to open letter index {pendingOperation.CurrentTargetIndex} for attachment claim");
                    return;
                }

                pendingOperation.Stage = PendingOperationStage.WaitingForViewer;
                pendingOperation.ExecuteAtUtc = DateTime.UtcNow.AddMilliseconds(LetterViewerDelayMs);
                pendingOperation.FailureCount = 0;
                LastActionText = $"Last action: opened letter index {pendingOperation.CurrentTargetIndex} to claim attachments at {DateTime.Now:HH:mm:ss}.";
                return;
            }
            case PendingOperationStage.WaitingForViewer:
            {
                if (WaitForSelectYesnoToClear("letter open confirmation"))
                    return;

                if (AddonHelper.IsAddonVisible("LetterEditor"))
                {
                    AddonHelper.CloseAddon("LetterEditor");
                    AbortPendingOperation("opened LetterEditor instead of LetterViewer");
                    return;
                }

                var viewer = AddonHelper.GetAddon("LetterViewer");
                if (viewer == null || !viewer->IsVisible || !viewer->IsReady)
                {
                    RetryOrAbort("LetterViewer did not become ready");
                    return;
                }

                if (!SendLetterViewEvent(0, 1))
                {
                    RetryOrAbort("failed to submit the LetterView attachment claim event");
                    return;
                }

                pendingOperation.ConfirmedCurrentPrompt = false;
                pendingOperation.AcceptedAttachmentPrompt = false;
                pendingOperation.Stage = PendingOperationStage.WaitingForAttachmentTransfer;
                pendingOperation.ExecuteAtUtc = DateTime.UtcNow.AddMilliseconds(AttachmentTransferDelayMs);
                pendingOperation.FailureCount = 0;
                LastActionText = $"Last action: claimed attachments from letter index {pendingOperation.CurrentTargetIndex} at {DateTime.Now:HH:mm:ss}.";
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
                            RetryOrAbort("failed to confirm the attachment claim confirmation");
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
                    RetryOrAbort("LetterViewer did not close after attachment claim");
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

    private bool WaitForSelectYesnoToClear(string promptDescription)
    {
        if (pendingOperation == null || !AddonHelper.IsAddonVisible("SelectYesno"))
            return false;

        if (!pendingOperation.ConfirmedCurrentPrompt)
        {
            if (!AddonHelper.ClickYesNo(true, closeAfter: false))
            {
                RetryOrAbort($"failed to confirm the {promptDescription}");
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

        RetryOrAbort("failed to open Letter List");
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
            return RejectDisabledManualAction($"'{label}' was not queued");

        if (pendingOperation != null)
        {
            LastActionText = $"Last action: another mail operation is already running ({pendingOperation.Label}) at {DateTime.Now:HH:mm:ss}.";
            return false;
        }

        if (!TryFindTargetIndices(predicate, out var indices))
        {
            LastActionText = $"Last action: no letters matched '{label}' at {DateTime.Now:HH:mm:ss}.";
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
        LastActionText = $"Last action: queued '{label}' for {orderedIndices.Count} letters at {DateTime.Now:HH:mm:ss}.";
        return true;
    }

    private bool RunBatchDeleteOperation(string label, Predicate<InfoProxyLetter.Letter> predicate)
    {
        if (!enabled)
            return RejectDisabledManualAction($"'{label}' was not queued");

        if (pendingOperation != null)
        {
            LastActionText = $"Last action: another mail operation is already running ({pendingOperation.Label}) at {DateTime.Now:HH:mm:ss}.";
            return false;
        }

        if (!EnsureLetterListReady())
            return false;

        if (!TryFindTargetIndices(predicate, out var indices))
        {
            LastActionText = $"Last action: no letters matched '{label}' at {DateTime.Now:HH:mm:ss}.";
            return false;
        }

        var processed = 0;
        foreach (var index in indices.OrderByDescending(value => value))
        {
            if (!SendLetterListEvent(0, 0, index, 0, 1) || !SendLetterListEvent(4, 0))
            {
                LastActionText = $"Last action: '{label}' stopped after {processed} letters because letter index {index} could not be deleted at {DateTime.Now:HH:mm:ss}.";
                return false;
            }

            processed++;
        }

        CompletedOperationCount++;
        LastProcessedLetterCount = processed;
        LastActionText = $"Last action: completed {label.ToLowerInvariant()} for {processed} letters at {DateTime.Now:HH:mm:ss}.";
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

    private void RetryOrAbort(string failureReason)
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

    private void AbortPendingOperation(string failureReason)
    {
        var label = pendingOperation?.Label ?? "mail operation";
        pendingOperation = null;
        LastActionText = $"Last action: {label} aborted because it {failureReason} at {DateTime.Now:HH:mm:ss}.";
    }

    private void CompletePendingOperation()
    {
        if (pendingOperation == null)
            return;

        CompletedOperationCount++;
        LastProcessedLetterCount = pendingOperation.ProcessedCount;
        LastActionText = $"Last action: completed {pendingOperation.Label.ToLowerInvariant()} for {pendingOperation.ProcessedCount} letters at {DateTime.Now:HH:mm:ss}.";
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
        return letterData != null && letterData->IntArray[136] != 0;
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
