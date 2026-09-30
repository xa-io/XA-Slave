using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Gui.NamePlate;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace XASlave.Services;

public sealed record NameplateStatusOption(uint StatusId, string Label);

public sealed class NameplatePrivacyService : IDisposable
{
    private sealed record StatusIconMapping(uint TextureIcon, BitmapFontIcon[] BitmapIcons, bool BitmapMappingUnavailable);

    private const string RemoteVisitorLabels = "wanderer, traveler, and voyager";

    private readonly INamePlateGui namePlateGui;
    private readonly IpcClient ipcClient;
    private readonly IPluginLog log;
    private readonly IDataManager dataManager;

    private Dictionary<uint, StatusIconMapping> statusIconMappings = new();
    private HashSet<uint> hiddenStatusIds = new();
    private HashSet<uint> hiddenTextureIcons = new();
    private HashSet<BitmapFontIcon> hiddenBitmapIcons = new();
    private bool statusOptionsAvailable;
    private bool selectedStatusMappingUnavailable;
    private bool hideStatusIconsEnabled;
    private bool removeFcTagEnabled;

    private bool anonymousModeEnabled;
    private bool showTravelerWorldNamesEnabled;
    private bool showTravelerWorldNamesDisableInDuties = true;
    private bool showTravelerWorldNamesAddSpacer;
    private bool showTitlesAsPlayernamesEnabled;
    private bool showTitlesAsPlayernamesHonorificSupportEnabled = true;
    private bool subscribed;

    public NameplatePrivacyService(INamePlateGui namePlateGui, IpcClient ipcClient, IPluginLog log, IDataManager dataManager)
    {
        this.namePlateGui = namePlateGui;
        this.ipcClient = ipcClient;
        this.log = log;
        this.dataManager = dataManager;
        RefreshStatusOptions();
    }

    public bool IsAnonymousModeEnabled => anonymousModeEnabled;
    public bool IsShowTravelerWorldNamesEnabled => showTravelerWorldNamesEnabled;
    public bool IsShowTitlesAsPlayernamesEnabled => showTitlesAsPlayernamesEnabled;
    public IReadOnlyList<NameplateStatusOption> StatusOptions { get; private set; } = Array.Empty<NameplateStatusOption>();

    public string HideStatusIconsStatusText => !hideStatusIconsEnabled
        ? statusOptionsAvailable ? "Disabled" : "Disabled - status options unavailable; refresh to retry."
        : !statusOptionsAvailable
            ? "Enabled - status mappings unavailable; icons remain unchanged. Refresh to retry."
            : hiddenStatusIds.Count == 0
                ? "Enabled - no status icons selected."
                : selectedStatusMappingUnavailable
                    ? "Enabled - some selected icon mappings are unavailable; unrecognized icons remain visible."
                    : "Enabled - selected status icons are hidden on player nameplates.";

    public string RemoveFcTagStatusText => removeFcTagEnabled
        ? "Enabled - FC tags and visitor labels in the same nameplate field are hidden locally."
        : "Disabled";

    public bool SetHideStatusIconsEnabled(bool value)
    {
        if (value && !hideStatusIconsEnabled)
            RefreshStatusOptions();
        hideStatusIconsEnabled = value;
        UpdateSubscription();
        RequestRedraw();
        return hideStatusIconsEnabled;
    }

    public bool SetRemoveFcTagEnabled(bool value)
    {
        removeFcTagEnabled = value;
        UpdateSubscription();
        RequestRedraw();
        return removeFcTagEnabled;
    }

    public void ApplyStatusIconConfiguration(IEnumerable<uint>? selectedStatusIds)
    {
        var selected = selectedStatusIds == null ? new HashSet<uint>() : new HashSet<uint>(selectedStatusIds);
        selected.Remove(0);
        if (hiddenStatusIds.SetEquals(selected))
            return;

        hiddenStatusIds = selected;
        RebuildHiddenStatusIcons();
        if (hideStatusIconsEnabled)
            RequestRedraw();
    }

    // Only startup, explicit refresh, or enabling calls this; rendering and callbacks use cached mappings.
    public bool RefreshStatusOptions()
    {
        try
        {
            var statuses = dataManager.GetExcelSheet<OnlineStatus>();
            var addon = dataManager.GetExcelSheet<Addon>();
            var options = new List<NameplateStatusOption>();
            var mappings = new Dictionary<uint, StatusIconMapping>();
            foreach (var status in statuses)
            {
                var label = status.Name.ExtractText().Trim();
                if (status.RowId == 0 || status.Icon == 0 || string.IsNullOrWhiteSpace(label))
                    continue;

                var bitmapIcons = new HashSet<BitmapFontIcon>();
                var bitmapMappingUnavailable = false;
                // Installed Lumina calls this Unknown0; EXDSchema identifies it as TextIcon -> Addon.
                // Do not use OnlineStatus.List: that would omit statuses such as Disconnected.
                if (status.Unknown0 > 0)
                {
                    try
                    {
                        if (addon.TryGetRow((uint)status.Unknown0, out var textIcon))
                        {
                            foreach (var payload in SeString.Parse(textIcon.Text.Data.Span).Payloads)
                            {
                                if (payload is IconPayload icon && icon.Icon != BitmapFontIcon.None)
                                    bitmapIcons.Add(icon.Icon);
                            }
                        }
                        bitmapMappingUnavailable = bitmapIcons.Count == 0;
                    }
                    catch (Exception)
                    {
                        // Keep the texture mapping usable without guessing at an unreadable text icon.
                        bitmapMappingUnavailable = true;
                    }
                }
                else if (status.Unknown0 < 0)
                {
                    bitmapMappingUnavailable = true;
                }

                var icons = new BitmapFontIcon[bitmapIcons.Count];
                bitmapIcons.CopyTo(icons);
                mappings[status.RowId] = new StatusIconMapping(status.Icon, icons, bitmapMappingUnavailable);
                options.Add(new NameplateStatusOption(status.RowId, label));
            }

            options.Sort((left, right) =>
            {
                var order = StringComparer.OrdinalIgnoreCase.Compare(left.Label, right.Label);
                return order != 0 ? order : left.StatusId.CompareTo(right.StatusId);
            });
            statusIconMappings = mappings;
            StatusOptions = options.AsReadOnly();
            statusOptionsAvailable = options.Count > 0;
        }
        catch (Exception ex)
        {
            statusIconMappings = new Dictionary<uint, StatusIconMapping>();
            StatusOptions = Array.Empty<NameplateStatusOption>();
            statusOptionsAvailable = false;
            log.Warning(ex, "[XASlave] Nameplate status options are unavailable; use Refresh to retry.");
        }

        RebuildHiddenStatusIcons();
        if (hideStatusIconsEnabled)
            RequestRedraw();
        return statusOptionsAvailable;
    }

    private void RebuildHiddenStatusIcons()
    {
        var textures = new HashSet<uint>();
        var bitmaps = new HashSet<BitmapFontIcon>();
        var unavailable = false;
        foreach (var statusId in hiddenStatusIds)
        {
            if (!statusIconMappings.TryGetValue(statusId, out var mapping))
            {
                unavailable = true;
                continue;
            }
            textures.Add(mapping.TextureIcon);
            bitmaps.UnionWith(mapping.BitmapIcons);
            unavailable |= mapping.BitmapMappingUnavailable;
        }
        hiddenTextureIcons = textures;
        hiddenBitmapIcons = bitmaps;
        selectedStatusMappingUnavailable = unavailable;
    }

    private string TravelerWorldNamesFormatLabel => showTravelerWorldNamesAddSpacer ? "Name @ HomeWorld" : "Name@HomeWorld";

    public string AnonymousModeStatusText =>
        anonymousModeEnabled
            ? "Enabled - visible player nameplates are masked locally with deterministic Firstname Lastname aliases."
            : "Disabled";

    public string ShowTravelerWorldNamesStatusText
    {
        get
        {
            if (!showTravelerWorldNamesEnabled)
                return "Disabled";

            if (anonymousModeEnabled)
                return "Enabled - hidden while Anonymous Mode is masking names and removing FC tags.";

            if (IsTravelerWorldNamesDisabledInDuty())
                return "Enabled - disabled while in duty content.";

            return showTravelerWorldNamesDisableInDuties
                ? $"Enabled - visible {RemoteVisitorLabels} nameplates show {TravelerWorldNamesFormatLabel} and hide the FC/travel tag; disabled in duties."
                : $"Enabled - visible {RemoteVisitorLabels} nameplates show {TravelerWorldNamesFormatLabel} and hide the FC/travel tag.";
        }
    }

    public string ShowTitlesAsPlayernamesStatusText
    {
        get
        {
            if (!showTitlesAsPlayernamesEnabled)
                return "Disabled";

            if (anonymousModeEnabled)
                return "Enabled - hidden while Anonymous Mode is masking names and removing titles.";

            return showTitlesAsPlayernamesHonorificSupportEnabled
                ? "Enabled - Honorific custom titles are used when available, with native title fallback."
                : "Enabled - prefix titles move before the player name and suffix titles move after it.";
        }
    }

    public bool SetAnonymousModeEnabled(bool value)
    {
        anonymousModeEnabled = value;
        UpdateSubscription();
        RequestRedraw();
        return anonymousModeEnabled;
    }

    public bool SetShowTravelerWorldNamesEnabled(bool value)
    {
        showTravelerWorldNamesEnabled = value;
        UpdateSubscription();
        RequestRedraw();
        return showTravelerWorldNamesEnabled;
    }

    public bool SetShowTitlesAsPlayernamesEnabled(bool value)
    {
        showTitlesAsPlayernamesEnabled = value;
        UpdateSubscription();
        RequestRedraw();
        return showTitlesAsPlayernamesEnabled;
    }

    public void ApplyShowTravelerWorldNamesConfiguration(bool disableInDuties, bool addSpacer)
    {
        if (showTravelerWorldNamesDisableInDuties == disableInDuties
            && showTravelerWorldNamesAddSpacer == addSpacer)
            return;

        showTravelerWorldNamesDisableInDuties = disableInDuties;
        showTravelerWorldNamesAddSpacer = addSpacer;
        if (showTravelerWorldNamesEnabled)
            RequestRedraw();
    }

    public void ApplyShowTitlesAsPlayernamesConfiguration(bool honorificSupportEnabled)
    {
        if (showTitlesAsPlayernamesHonorificSupportEnabled == honorificSupportEnabled)
            return;

        showTitlesAsPlayernamesHonorificSupportEnabled = honorificSupportEnabled;
        if (showTitlesAsPlayernamesEnabled)
            RequestRedraw();
    }

    public void Dispose()
    {
        var wasEnabled = anonymousModeEnabled || showTravelerWorldNamesEnabled || showTitlesAsPlayernamesEnabled
            || hideStatusIconsEnabled || removeFcTagEnabled;
        anonymousModeEnabled = false;
        showTravelerWorldNamesEnabled = false;
        showTitlesAsPlayernamesEnabled = false;
        hideStatusIconsEnabled = false;
        removeFcTagEnabled = false;
        if (subscribed)
            namePlateGui.OnDataUpdate -= OnNamePlateUpdate;

        subscribed = false;
        if (wasEnabled)
            RequestRedraw();
    }

    private void UpdateSubscription()
    {
        var shouldSubscribe = anonymousModeEnabled || showTravelerWorldNamesEnabled || showTitlesAsPlayernamesEnabled
            || hideStatusIconsEnabled || removeFcTagEnabled;
        if (shouldSubscribe == subscribed)
            return;

        if (shouldSubscribe)
            namePlateGui.OnDataUpdate += OnNamePlateUpdate;
        else
            namePlateGui.OnDataUpdate -= OnNamePlateUpdate;

        subscribed = shouldSubscribe;
    }

    private void RequestRedraw()
    {
        try
        {
            namePlateGui.RequestRedraw();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Failed to request a nameplate redraw.");
        }
    }

    private void OnNamePlateUpdate(INamePlateUpdateContext _, IReadOnlyList<INamePlateUpdateHandler> handlers)
    {
        var applyTravelerWorldNames = showTravelerWorldNamesEnabled && !IsTravelerWorldNamesDisabledInDuty();
        var applyTitlesAsPlayernames = showTitlesAsPlayernamesEnabled;
        if (!anonymousModeEnabled && !applyTravelerWorldNames && !applyTitlesAsPlayernames
            && !hideStatusIconsEnabled && !removeFcTagEnabled)
            return;

        foreach (var handler in handlers)
        {
            var playerCharacter = handler.PlayerCharacter;
            if (playerCharacter == null)
                continue;

            if (removeFcTagEnabled)
                handler.RemoveFreeCompanyTag();
            if (hideStatusIconsEnabled)
                HideSelectedStatusIcons(handler);

            if (anonymousModeEnabled)
            {
                var originalName = NormalizeIdentityPart(playerCharacter.Name.ToString());
                var originalWorld = ResolveOriginalWorld(playerCharacter);
                var alias = ResolveAlias(originalName, originalWorld, handler.GameObjectId);
                handler.Name = new SeStringBuilder().AddText(alias).Build();
                handler.RemoveTitle();
                handler.RemoveFreeCompanyTag();
                continue;
            }

            var playerName = playerCharacter.Name.ToString().Trim();
            if (string.IsNullOrWhiteSpace(playerName))
                continue;

            var displayName = playerName;
            var changedName = false;

            if (applyTitlesAsPlayernames && TryApplyTitleToPlayerName(handler, playerCharacter, playerName, out displayName))
            {
                handler.RemoveTitle();
                changedName = true;
            }

            if (applyTravelerWorldNames && TryAppendTravelerWorldName(playerCharacter, displayName, showTravelerWorldNamesAddSpacer, out displayName))
            {
                handler.RemoveFreeCompanyTag();
                changedName = true;
            }

            if (changedName)
                handler.Name = new SeStringBuilder().AddText(displayName).Build();
        }
    }

    private void HideSelectedStatusIcons(INamePlateUpdateHandler handler)
    {
        var textureIcon = handler.NameIconId;
        if (textureIcon > 0 && hiddenTextureIcons.Contains((uint)textureIcon))
            handler.NameIconId = -1;

        if (hiddenBitmapIcons.Count == 0)
            return;

        try
        {
            var prefix = handler.StatusPrefix;
            List<Payload>? retained = null;
            for (var i = 0; i < prefix.Payloads.Count; i++)
            {
                var payload = prefix.Payloads[i];
                if (payload is IconPayload icon && hiddenBitmapIcons.Contains(icon.Icon))
                    retained ??= prefix.Payloads.GetRange(0, i);
                else
                    retained?.Add(payload);
            }

            if (retained != null)
                handler.StatusPrefix = new SeString(retained);
        }
        catch (Exception)
        {
            // Preserve malformed/unrecognized text prefixes; never log repeatedly from the update callback.
        }
    }

    private unsafe bool IsTravelerWorldNamesDisabledInDuty()
    {
        if (!showTravelerWorldNamesDisableInDuties)
            return false;

        var gameMain = GameMain.Instance();
        return gameMain != null && gameMain->CurrentContentFinderConditionId != 0;
    }

    private string ResolveAlias(string originalName, string originalWorld, ulong stableId)
    {
        return CharacterAliasHelper.Resolve(originalName, originalWorld, stableId).Name;
    }

    private static string ResolveOriginalWorld(IPlayerCharacter playerCharacter)
    {
        var homeWorld = playerCharacter.HomeWorld.ValueNullable?.Name.ToString();
        if (!string.IsNullOrWhiteSpace(homeWorld))
            return NormalizeIdentityPart(homeWorld);

        var currentWorld = playerCharacter.CurrentWorld.ValueNullable?.Name.ToString();
        if (!string.IsNullOrWhiteSpace(currentWorld))
            return NormalizeIdentityPart(currentWorld);

        return string.Empty;
    }

    private bool TryApplyTitleToPlayerName(INamePlateUpdateHandler handler, IPlayerCharacter playerCharacter, string playerName, out string displayName)
    {
        displayName = playerName;

        if (showTitlesAsPlayernamesHonorificSupportEnabled
            && TryApplyHonorificTitleToPlayerName(playerCharacter, playerName, out displayName))
        {
            return true;
        }

        var title = StripTitleWrapper(handler.InfoView.Title.ToString());
        if (string.IsNullOrWhiteSpace(title))
            title = StripTitleWrapper(handler.Title.ToString());

        if (string.IsNullOrWhiteSpace(title)
            || title.Equals(playerName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        displayName = handler.IsPrefixTitle
            ? $"{title} {playerName}"
            : $"{playerName} {title}";
        return true;
    }

    private bool TryApplyHonorificTitleToPlayerName(IPlayerCharacter playerCharacter, string playerName, out string displayName)
    {
        displayName = playerName;

        if (!ipcClient.TryGetHonorificCharacterTitle((int)playerCharacter.ObjectIndex, out var titleInfo))
            return false;

        var title = StripTitleWrapper(titleInfo.Title);
        if (string.IsNullOrWhiteSpace(title))
            return true;

        if (title.Equals(playerName, StringComparison.OrdinalIgnoreCase))
            return false;

        displayName = titleInfo.IsPrefix
            ? $"{title} {playerName}"
            : $"{playerName} {title}";
        return true;
    }

    private static bool TryAppendTravelerWorldName(IPlayerCharacter playerCharacter, string displayName, bool addSpacer, out string displayNameWithWorld)
    {
        displayNameWithWorld = displayName;

        if (playerCharacter.HomeWorld.RowId == 0
            || playerCharacter.CurrentWorld.RowId == 0
            || playerCharacter.HomeWorld.RowId == playerCharacter.CurrentWorld.RowId)
            return false;

        var homeWorld = playerCharacter.HomeWorld.ValueNullable?.Name.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(homeWorld))
            return false;

        displayNameWithWorld = addSpacer
            ? $"{displayName} @ {homeWorld}"
            : $"{displayName}@{homeWorld}";
        return true;
    }

    private static string StripTitleWrapper(string value)
    {
        var title = NormalizeIdentityPart(value);
        while (title.Length >= 2 && IsTitleWrapperPair(title[0], title[^1]))
            title = NormalizeIdentityPart(title[1..^1]);

        return title;
    }

    private static bool IsTitleWrapperPair(char left, char right)
    {
        return (left == '<' && right == '>')
            || (left == '\u300A' && right == '\u300B')
            || (left == '\uFF1C' && right == '\uFF1E')
            || (left == '\u2039' && right == '\u203A')
            || (left == '\u00AB' && right == '\u00BB');
    }

    private static string NormalizeIdentityPart(string value)
    {
        return CharacterAliasHelper.NormalizeIdentityPart(value);
    }
}
