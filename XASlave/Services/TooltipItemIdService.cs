using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XASlave.Services;

public unsafe sealed class TooltipItemIdService : IDisposable
{
    private const string ItemDetailAddonName = "ItemDetail";
    private const int ItemUiCategoryStringIndex = 2;
    private const uint ItemCategoryTextNodeId = 35;
    private const ulong DecoratedItemIdFloor = 2_000_000;
    private const ulong ItemIdDecorationModulo = 500_000;

    private readonly IAddonLifecycle addonLifecycle;
    private readonly IGameGui gameGui;
    private readonly ISigScanner sigScanner;
    private readonly IGameInteropProvider interopProvider;
    private readonly IPluginLog log;

    private Hook<GenerateItemTooltipDelegate>? generateItemTooltipHook;
    private bool enabled;
    private bool subscribed;
    private bool hookInitialized;

    private delegate void GenerateItemTooltipDelegate(AddonItemDetail* addonItemDetail, NumberArrayData* numberArrayData, StringArrayData* stringArrayData);

    public TooltipItemIdService(
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        ISigScanner sigScanner,
        IGameInteropProvider interopProvider,
        IPluginLog log)
    {
        this.addonLifecycle = addonLifecycle;
        this.gameGui = gameGui;
        this.sigScanner = sigScanner;
        this.interopProvider = interopProvider;
        this.log = log;
    }

    public string StatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? uiStatusText = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string LocalizedStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiStatusText, StatusText);
    internal global::XASlave.Localization.UiMessage StatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiStatusText, StatusText);

    public bool SetEnabled(bool value)
    {
        if (!value)
        {
            enabled = false;
            UpdateHookState(false);
            UpdateSubscription();
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"));
            return false;
        }

        EnsureHookInitialized();
        if (generateItemTooltipHook == null)
        {
            enabled = false;
            UpdateSubscription();
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("SD115A1D6D60730D4"));
            return false;
        }

        enabled = true;
        if (!UpdateHookState(true))
        {
            enabled = false;
            UpdateSubscription();
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("SD13D339B14F92086"));
            return false;
        }

        UpdateSubscription();
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S4FAA7ADB8E3DD81A"));
        TryEnableMultilineOnVisibleItemDetail();
        return true;
    }

    public void Dispose()
    {
        enabled = false;
        UpdateSubscription();
        UpdateHookState(false);

        if (generateItemTooltipHook is { IsDisposed: false })
            generateItemTooltipHook.Dispose();

        generateItemTooltipHook = null;
    }

    private void EnsureHookInitialized()
    {
        if (hookInitialized)
            return;

        hookInitialized = true;

        try
        {
            if (!sigScanner.TryScanText(Sigs.GenerateItemTooltipSig, out var address) || address == nint.Zero)
                return;

            generateItemTooltipHook = interopProvider.HookFromAddress<GenerateItemTooltipDelegate>(address, GenerateItemTooltipDetour);
        }
        catch (Exception ex)
        {
            generateItemTooltipHook = null;
            log.Warning(ex, "[XASlave] Failed to create Show Item ID tooltip generator hook.");
        }
    }

    private bool UpdateHookState(bool shouldEnable)
    {
        if (generateItemTooltipHook == null || generateItemTooltipHook.IsDisposed)
            return !shouldEnable;

        try
        {
            if (shouldEnable)
            {
                if (!generateItemTooltipHook.IsEnabled)
                    generateItemTooltipHook.Enable();
            }
            else if (generateItemTooltipHook.IsEnabled)
            {
                generateItemTooltipHook.Disable();
            }

            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[XASlave] Failed to {(shouldEnable ? "enable" : "disable")} Show Item ID tooltip generator hook.");
            return false;
        }
    }

    private void UpdateSubscription()
    {
        if (subscribed == enabled)
            return;

        if (enabled)
            addonLifecycle.RegisterListener(AddonEvent.PostRefresh, ItemDetailAddonName, OnItemDetailPostRefresh);
        else
            addonLifecycle.UnregisterListener(OnItemDetailPostRefresh);

        subscribed = enabled;
    }

    private void OnItemDetailPostRefresh(AddonEvent _, AddonArgs args)
    {
        if (!enabled || args.Addon.IsNull)
            return;

        try
        {
            EnableMultilineOnItemDetail((AtkUnitBase*)args.Addon.Address);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Show Item ID failed while updating ItemDetail text flags.");
        }
    }

    private void GenerateItemTooltipDetour(AddonItemDetail* addonItemDetail, NumberArrayData* numberArrayData, StringArrayData* stringArrayData)
    {
        try
        {
            if (enabled)
                AppendItemIdToTooltipString(stringArrayData);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Show Item ID failed while generating an item tooltip.");
        }

        generateItemTooltipHook!.OriginalDisposeSafe(addonItemDetail, numberArrayData, stringArrayData);
    }

    private void TryEnableMultilineOnVisibleItemDetail()
    {
        var addonAddress = gameGui.GetAddonByName(ItemDetailAddonName, 1);
        if (addonAddress.IsNull)
            return;

        EnableMultilineOnItemDetail((AtkUnitBase*)addonAddress.Address);
    }

    private static void EnableMultilineOnItemDetail(AtkUnitBase* addon)
    {
        if (addon == null || !addon->IsReady)
            return;

        var categoryNode = addon->GetTextNodeById(ItemCategoryTextNodeId);
        if (categoryNode != null)
            categoryNode->TextFlags |= TextFlags.MultiLine;
    }

    private void AppendItemIdToTooltipString(StringArrayData* stringArrayData)
    {
        if (stringArrayData == null || stringArrayData->AtkArrayData.Size <= ItemUiCategoryStringIndex)
            return;

        var seString = GetTooltipString(stringArrayData, ItemUiCategoryStringIndex);
        if (seString == null || seString.TextValue.EndsWith(']') || !TryGetCurrentItemId(out var itemId))
            return;

        seString.Payloads.Add(new UIForegroundPayload(3));
        seString.Payloads.Add(new TextPayload($"   [{itemId}]"));
        seString.Payloads.Add(new UIForegroundPayload(0));

        stringArrayData->SetValue(ItemUiCategoryStringIndex, seString.EncodeWithNullTerminator(), false);
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Format("S567A9798F13BA4E2", itemId));
    }

    private static SeString? GetTooltipString(StringArrayData* stringArrayData, int field)
    {
        if (stringArrayData == null || stringArrayData->AtkArrayData.Size <= field)
            return null;

        var stringAddress = new IntPtr(stringArrayData->StringArray[field]);
        return stringAddress == IntPtr.Zero
            ? null
            : MemoryHelper.ReadSeStringNullTerminated(stringAddress);
    }

    private bool TryGetCurrentItemId(out ulong itemId)
    {
        itemId = 0;

        var agentItemDetail = AgentItemDetail.Instance();
        if (agentItemDetail != null)
            itemId = agentItemDetail->ItemId;

        if (itemId == 0)
            itemId = gameGui.HoveredItem;

        if (itemId == 0)
            return false;

        if (itemId < DecoratedItemIdFloor)
            itemId %= ItemIdDecorationModulo;

        return itemId != 0;
    }
}
