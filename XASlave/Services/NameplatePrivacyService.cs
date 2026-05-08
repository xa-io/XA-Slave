using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Gui.NamePlate;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

public sealed class NameplatePrivacyService : IDisposable
{
    private readonly INamePlateGui namePlateGui;
    private readonly IPluginLog log;

    private bool anonymousModeEnabled;
    private bool showTravelerWorldNamesEnabled;
    private bool subscribed;

    public NameplatePrivacyService(INamePlateGui namePlateGui, IPluginLog log)
    {
        this.namePlateGui = namePlateGui;
        this.log = log;
    }

    public bool IsAnonymousModeEnabled => anonymousModeEnabled;
    public bool IsShowTravelerWorldNamesEnabled => showTravelerWorldNamesEnabled;

    public string AnonymousModeStatusText =>
        anonymousModeEnabled
            ? "Enabled - visible player nameplates are masked locally with deterministic Firstname Lastname aliases."
            : "Disabled";

    public string ShowTravelerWorldNamesStatusText =>
        showTravelerWorldNamesEnabled && anonymousModeEnabled
            ? "Enabled - hidden while Live Anonymous Mode is masking names and removing FC tags."
            : showTravelerWorldNamesEnabled
            ? "Enabled - visible traveler and wanderer nameplates show Name@HomeWorld and hide the FC/travel tag."
            : "Disabled";

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

    public void Dispose()
    {
        var wasEnabled = anonymousModeEnabled || showTravelerWorldNamesEnabled;
        anonymousModeEnabled = false;
        showTravelerWorldNamesEnabled = false;
        if (subscribed)
            namePlateGui.OnDataUpdate -= OnNamePlateUpdate;

        subscribed = false;
        if (wasEnabled)
            RequestRedraw();
    }

    private void UpdateSubscription()
    {
        var shouldSubscribe = anonymousModeEnabled || showTravelerWorldNamesEnabled;
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
        if (!anonymousModeEnabled && !showTravelerWorldNamesEnabled)
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

            if (showTravelerWorldNamesEnabled)
                ApplyTravelerWorldName(handler, playerCharacter);
        }
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

    private static void ApplyTravelerWorldName(INamePlateUpdateHandler handler, IPlayerCharacter playerCharacter)
    {
        if (playerCharacter.HomeWorld.RowId == 0
            || playerCharacter.CurrentWorld.RowId == 0
            || playerCharacter.HomeWorld.RowId == playerCharacter.CurrentWorld.RowId)
            return;

        var homeWorld = playerCharacter.HomeWorld.ValueNullable?.Name.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(homeWorld))
            return;

        var playerName = playerCharacter.Name.ToString().Trim();
        if (string.IsNullOrWhiteSpace(playerName))
            return;

        handler.Name = new SeStringBuilder().AddText($"{playerName}@{homeWorld}").Build();
        handler.RemoveFreeCompanyTag();
    }

    private static string NormalizeIdentityPart(string value)
    {
        return CharacterAliasHelper.NormalizeIdentityPart(value);
    }
}
