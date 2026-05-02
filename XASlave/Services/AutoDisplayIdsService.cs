using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace XASlave.Services;

public unsafe sealed class AutoDisplayIdsService : IDisposable
{
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IDataManager dataManager;
    private readonly ITargetManager targetManager;
    private readonly IDtrBar dtrBar;
    private readonly IPluginLog log;
    private IDtrBarEntry? dtrEntry;
    private bool enabled;
    private bool subscribed;
    private bool showItemId = true;
    private bool showActionId = true;
    private bool showTargetDataId = true;
    private bool showWeatherId = true;
    private bool showZoneInfoInDtr = true;
    private string lastTargetNameWithoutMarker = string.Empty;
    private string lastDtrText = string.Empty;
    private bool lastDtrShown;
    private DateTime lastUiUpdateUtc = DateTime.MinValue;

    public AutoDisplayIdsService(
        IAddonLifecycle addonLifecycle,
        IFramework framework,
        IClientState clientState,
        IDataManager dataManager,
        ITargetManager targetManager,
        IDtrBar dtrBar,
        IPluginLog log)
    {
        this.addonLifecycle = addonLifecycle;
        this.framework = framework;
        this.clientState = clientState;
        this.dataManager = dataManager;
        this.targetManager = targetManager;
        this.dtrBar = dtrBar;
        this.log = log;
    }

    public string StatusText { get; private set; } = "Disabled";
    public string LastActionText { get; private set; } = "No actions yet.";
    public uint CurrentZoneId { get; private set; }
    public uint CurrentMapId { get; private set; }
    public uint CurrentWeatherId { get; private set; }
    public uint CurrentTargetDataId { get; private set; }
    public uint LastActionOriginalId { get; private set; }
    public uint LastActionResolvedId { get; private set; }

    public void ApplyConfiguration(
        bool showItemId,
        bool showActionId,
        bool showTargetDataId,
        bool showWeatherId,
        bool showZoneInfoInDtr)
    {
        this.showItemId = showItemId;
        this.showActionId = showActionId;
        this.showTargetDataId = showTargetDataId;
        this.showWeatherId = showWeatherId;
        this.showZoneInfoInDtr = showZoneInfoInDtr;
        UpdateDtrEntry();

        if (enabled)
            StatusText = BuildStatusText();
    }

    public bool SetEnabled(bool value)
    {
        if (value == enabled)
            return enabled;

        if (!value)
        {
            enabled = false;
            Unsubscribe();
            RestoreTargetName();
            SetDtrState(string.Empty, false);
            StatusText = "Disabled";
            return false;
        }

        Subscribe();
        enabled = true;
        StatusText = BuildStatusText();
        return true;
    }

    public void Dispose()
    {
        enabled = false;
        Unsubscribe();
        RestoreTargetName();
        RemoveDtrEntry();
    }

    private string BuildStatusText()
    {
        var enabledSurfaces = new List<string>();
        if (showItemId)
            enabledSurfaces.Add("item");
        if (showActionId)
            enabledSurfaces.Add("action");
        if (showTargetDataId)
            enabledSurfaces.Add("target");
        if (showWeatherId)
            enabledSurfaces.Add("weather");
        if (showZoneInfoInDtr)
            enabledSurfaces.Add("zone/map DTR");

        return enabledSurfaces.Count == 0
            ? "Enabled - all ID subsettings are currently off."
            : $"Enabled - {string.Join(", ", enabledSurfaces)} ID surfaces are active.";
    }

    private void Subscribe()
    {
        if (subscribed)
            return;

        addonLifecycle.RegisterListener(AddonEvent.PostDraw, "ActionDetail", OnActionDetail);
        addonLifecycle.RegisterListener(AddonEvent.PreDraw, "_TargetInfo", OnTargetInfo);
        addonLifecycle.RegisterListener(AddonEvent.PreDraw, "_TargetInfoMainTarget", OnTargetInfo);
        addonLifecycle.RegisterListener(AddonEvent.PostSetup, "Tooltip", OnTooltip);
        addonLifecycle.RegisterListener(AddonEvent.PostSetup, "TextTooltip", OnTooltip);
        addonLifecycle.RegisterListener(AddonEvent.PostRefresh, "Tooltip", OnTooltip);
        addonLifecycle.RegisterListener(AddonEvent.PostRefresh, "TextTooltip", OnTooltip);
        addonLifecycle.RegisterListener(AddonEvent.PostDraw, "Tooltip", OnTooltip);
        addonLifecycle.RegisterListener(AddonEvent.PostDraw, "TextTooltip", OnTooltip);
        framework.Update += OnFrameworkUpdate;
        clientState.MapIdChanged += OnMapChanged;
        clientState.TerritoryChanged += OnTerritoryChanged;
        subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!subscribed)
            return;

        addonLifecycle.UnregisterListener(OnActionDetail);
        addonLifecycle.UnregisterListener(OnTargetInfo);
        addonLifecycle.UnregisterListener(OnTooltip);
        framework.Update -= OnFrameworkUpdate;
        clientState.MapIdChanged -= OnMapChanged;
        clientState.TerritoryChanged -= OnTerritoryChanged;
        subscribed = false;
    }

    private void OnMapChanged(uint _)
    {
        RefreshState();
        UpdateDtrEntry();
    }

    private void OnTerritoryChanged(uint _)
    {
        RefreshState();
        UpdateDtrEntry();
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!enabled || !clientState.IsLoggedIn)
        {
            RestoreTargetName();
            return;
        }

        if ((DateTime.UtcNow - lastUiUpdateUtc).TotalMilliseconds < 250)
            return;

        lastUiUpdateUtc = DateTime.UtcNow;
        RefreshState();
    }

    private void RefreshState()
    {
        if (!enabled || !clientState.IsLoggedIn)
        {
            RestoreTargetName();
            return;
        }

        try
        {
            CurrentZoneId = clientState.TerritoryType;
            CurrentMapId = clientState.MapId;
            CurrentWeatherId = GetCurrentWeatherId();
            CurrentTargetDataId = showTargetDataId && targetManager.Target != null ? targetManager.Target.BaseId : 0u;
            UpdateDtrEntry();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Display IDs failed while refreshing live state.");
        }
    }

    private void OnActionDetail(AddonEvent _, AddonArgs args)
    {
        if (!enabled || !showActionId || args.Addon.IsNull)
            return;

        try
        {
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null || !addon->IsVisible)
                return;

            var textNode = addon->GetTextNodeById(6);
            if (textNode == null)
                return;

            textNode->TextFlags |= TextFlags.MultiLine;
            var agent = AgentActionDetail.Instance();
            if (agent == null)
                return;

            LastActionResolvedId = agent->ActionId;
            LastActionOriginalId = agent->OriginalId;
            var marker = BuildActionMarker();
            if (string.IsNullOrWhiteSpace(marker))
                return;

            AppendMarker(textNode, marker);
            LastActionText = $"Last action: updated action detail tooltip at {DateTime.Now:HH:mm:ss} with {marker}.";
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Display IDs failed while updating ActionDetail.");
        }
    }

    private void OnTargetInfo(AddonEvent _, AddonArgs args)
    {
        if (!enabled || args.Addon.IsNull)
            return;

        try
        {
            var target = targetManager.Target;
            if (!showTargetDataId || target == null || target.BaseId == 0)
            {
                RestoreTargetName();
                return;
            }

            CurrentTargetDataId = target.BaseId;
            var stage = AtkStage.Instance();
            if (stage == null)
                return;

            var hud2 = stage->GetStringArrayData(StringArrayType.Hud2);
            if (hud2 == null)
                return;

            var currentName = GetStringArrayValue(hud2, 0);
            if (string.IsNullOrWhiteSpace(currentName))
                return;

            var cleanName = StripInlineIdMarker(currentName);
            lastTargetNameWithoutMarker = cleanName;
            if (!currentName.Contains($"[{CurrentTargetDataId}]", StringComparison.Ordinal))
                SetStringArrayValue(hud2, 0, $"{cleanName}  [{CurrentTargetDataId}]");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Display IDs failed while updating target info.");
        }
    }

    private void OnTooltip(AddonEvent _, AddonArgs args)
    {
        if (!enabled || !showWeatherId || args.Addon.IsNull)
            return;

        try
        {
            CurrentWeatherId = GetCurrentWeatherId();
            if (CurrentWeatherId == 0 ||
                !dataManager.GetExcelSheet<Weather>().TryGetRow(CurrentWeatherId, out var weather))
            {
                return;
            }

            var weatherName = weather.Name.ToString().Trim();
            if (string.IsNullOrWhiteSpace(weatherName))
                return;

            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null || !addon->IsVisible)
                return;

            for (var index = 0; index < addon->UldManager.NodeListCount; index++)
            {
                var node = addon->UldManager.NodeList[index];
                if (TryAppendWeatherMarker(node, weatherName, CurrentWeatherId))
                {
                    LastActionText = $"Last action: updated weather tooltip with weather ID {CurrentWeatherId} at {DateTime.Now:HH:mm:ss}.";
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Display IDs failed while updating a weather tooltip.");
        }
    }

    private string BuildActionMarker()
    {
        if (LastActionOriginalId != 0 && LastActionResolvedId != 0)
        {
            return LastActionOriginalId == LastActionResolvedId
                ? $"[{LastActionOriginalId}]"
                : $"[{LastActionOriginalId} -> {LastActionResolvedId}]";
        }

        if (LastActionOriginalId != 0)
            return $"[{LastActionOriginalId}]";

        return LastActionResolvedId == 0 ? string.Empty : $"[{LastActionResolvedId}]";
    }

    private static bool TryAppendWeatherMarker(AtkResNode* node, string weatherName, uint weatherId, int depth = 0)
    {
        if (node == null || depth > 6)
            return false;

        if (node->Type == NodeType.Text)
        {
            var textNode = (AtkTextNode*)node;
            var text = textNode->NodeText.ToString();
            if (!string.IsNullOrWhiteSpace(text) &&
                !text.Contains($"[{weatherId}]", StringComparison.Ordinal) &&
                text.Contains(weatherName, StringComparison.OrdinalIgnoreCase))
            {
                AppendInlineMarker(textNode, $"[{weatherId}]");
                return true;
            }
        }

        if ((int)node->Type < 1000)
            return false;

        var componentNode = (AtkComponentNode*)node;
        if (componentNode->Component == null)
            return false;

        for (var index = 0; index < componentNode->Component->UldManager.NodeListCount; index++)
        {
            var child = componentNode->Component->UldManager.NodeList[index];
            if (TryAppendWeatherMarker(child, weatherName, weatherId, depth + 1))
                return true;
        }

        return false;
    }

    private static void AppendMarker(AtkTextNode* textNode, string marker)
    {
        var currentText = textNode->NodeText.ToString();
        currentText = StripMarker(currentText);
        if (string.IsNullOrWhiteSpace(currentText))
            return;

        var newText = $"{currentText}{Environment.NewLine}{marker}";
        var bytes = Encoding.UTF8.GetBytes(newText + '\0');
        fixed (byte* ptr = bytes)
        {
            textNode->SetText(ptr);
        }
    }

    private static void AppendInlineMarker(AtkTextNode* textNode, string marker)
    {
        var currentText = textNode->NodeText.ToString();
        currentText = StripMarker(currentText);
        if (string.IsNullOrWhiteSpace(currentText))
            return;

        var newText = $"{currentText}  {marker}";
        var bytes = Encoding.UTF8.GetBytes(newText + '\0');
        fixed (byte* ptr = bytes)
        {
            textNode->SetText(ptr);
        }
    }

    private static string StripMarker(string text)
    {
        var markerIndex = text.LastIndexOf($"{Environment.NewLine}[ID ", StringComparison.Ordinal);
        if (markerIndex >= 0)
            return text[..markerIndex];

        markerIndex = text.LastIndexOf($"{Environment.NewLine}[", StringComparison.Ordinal);
        return markerIndex >= 0 && text.EndsWith(']') ? text[..markerIndex] : text;
    }

    private void RestoreTargetName()
    {
        if (string.IsNullOrWhiteSpace(lastTargetNameWithoutMarker))
            return;

        try
        {
            var stage = AtkStage.Instance();
            var hud2 = stage == null ? null : stage->GetStringArrayData(StringArrayType.Hud2);
            if (hud2 == null)
                return;

            var currentName = GetStringArrayValue(hud2, 0);
            if (string.IsNullOrWhiteSpace(currentName) || !currentName.Contains("[", StringComparison.Ordinal))
                return;

            SetStringArrayValue(hud2, StripInlineIdMarker(currentName));
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static string GetStringArrayValue(StringArrayData* stringArrayData, int field)
    {
        if (stringArrayData == null || stringArrayData->AtkArrayData.Size <= field)
            return string.Empty;

        var stringAddress = new IntPtr(stringArrayData->StringArray[field]);
        return stringAddress == IntPtr.Zero
            ? string.Empty
            : MemoryHelper.ReadSeStringNullTerminated(stringAddress).TextValue;
    }

    private static void SetStringArrayValue(StringArrayData* stringArrayData, string value)
    {
        SetStringArrayValue(stringArrayData, 0, value);
    }

    private static void SetStringArrayValue(StringArrayData* stringArrayData, int field, string value)
    {
        if (stringArrayData == null || stringArrayData->AtkArrayData.Size <= field)
            return;

        stringArrayData->SetValue(field, Encoding.UTF8.GetBytes(value + '\0'), false);
    }

    private static string StripInlineIdMarker(string text)
    {
        var markerIndex = text.LastIndexOf("  [", StringComparison.Ordinal);
        return markerIndex >= 0 && text.EndsWith(']') ? text[..markerIndex] : text;
    }

    private static uint GetCurrentWeatherId()
    {
        var weatherManager = WeatherManager.Instance();
        return weatherManager == null ? 0u : weatherManager->WeatherId;
    }

    private void UpdateDtrEntry()
    {
        if (!enabled || !showZoneInfoInDtr || !clientState.IsLoggedIn)
        {
            SetDtrState(string.Empty, false);
            return;
        }

        if (CurrentZoneId == 0 && CurrentMapId == 0)
        {
            SetDtrState(string.Empty, false);
            return;
        }

        SetDtrState($"Region: {CurrentZoneId} / Map: {CurrentMapId}", true);
    }

    private void SetDtrState(string text, bool shown)
    {
        try
        {
            if (!shown)
            {
                if (lastDtrShown && dtrEntry != null)
                    dtrEntry.Shown = false;

                lastDtrText = string.Empty;
                lastDtrShown = false;
                return;
            }

            dtrEntry ??= dtrBar.Get("XA Region IDs");
            if (!lastDtrShown || !string.Equals(lastDtrText, text, StringComparison.Ordinal))
                dtrEntry.Text = text;

            if (!lastDtrShown)
                dtrEntry.Shown = true;

            lastDtrText = text;
            lastDtrShown = true;
        }
        catch
        {
            // DTR is optional; this feature should keep running if the bar is unavailable.
        }
    }

    private void RemoveDtrEntry()
    {
        try
        {
            if (dtrEntry != null)
            {
                dtrEntry.Shown = false;
                dtrEntry.Remove();
                dtrEntry = null;
            }
        }
        catch
        {
            // Ignore DTR cleanup failures.
        }
        finally
        {
            lastDtrText = string.Empty;
            lastDtrShown = false;
        }
    }

}
