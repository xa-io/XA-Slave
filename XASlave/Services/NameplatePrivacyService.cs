using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Gui.NamePlate;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace XASlave.Services;

public sealed class NameplatePrivacyService : IDisposable
{
    private const string RemoteVisitorLabels = "wanderer, traveler, and voyager";

    private readonly INamePlateGui namePlateGui;
    private readonly IPluginLog log;

    private bool anonymousModeEnabled;
    private bool showTravelerWorldNamesEnabled;
    private bool showTravelerWorldNamesDisableInDuties = true;
    private bool showTravelerWorldNamesAddSpacer;
    private bool showTitlesAsPlayernamesEnabled;
    private bool subscribed;

    public NameplatePrivacyService(INamePlateGui namePlateGui, IPluginLog log)
    {
        this.namePlateGui = namePlateGui;
        this.log = log;
    }

    public bool IsAnonymousModeEnabled => anonymousModeEnabled;
    public bool IsShowTravelerWorldNamesEnabled => showTravelerWorldNamesEnabled;
    public bool IsShowTitlesAsPlayernamesEnabled => showTitlesAsPlayernamesEnabled;

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

            return "Enabled - prefix titles move before the player name and suffix titles move after it.";
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

    public void Dispose()
    {
        var wasEnabled = anonymousModeEnabled || showTravelerWorldNamesEnabled || showTitlesAsPlayernamesEnabled;
        anonymousModeEnabled = false;
        showTravelerWorldNamesEnabled = false;
        showTitlesAsPlayernamesEnabled = false;
        if (subscribed)
            namePlateGui.OnDataUpdate -= OnNamePlateUpdate;

        subscribed = false;
        if (wasEnabled)
            RequestRedraw();
    }

    private void UpdateSubscription()
    {
        var shouldSubscribe = anonymousModeEnabled || showTravelerWorldNamesEnabled || showTitlesAsPlayernamesEnabled;
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
        if (!anonymousModeEnabled && !applyTravelerWorldNames && !applyTitlesAsPlayernames)
            return;

        foreach (var handler in handlers)
        {
            var playerCharacter = handler.PlayerCharacter;
            if (playerCharacter == null)
                continue;

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

            if (applyTitlesAsPlayernames && TryApplyTitleToPlayerName(handler, playerName, out displayName))
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

    private static bool TryApplyTitleToPlayerName(INamePlateUpdateHandler handler, string playerName, out string displayName)
    {
        displayName = playerName;

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
