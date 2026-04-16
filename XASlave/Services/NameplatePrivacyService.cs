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
    private bool subscribed;

    public NameplatePrivacyService(INamePlateGui namePlateGui, IPluginLog log)
    {
        this.namePlateGui = namePlateGui;
        this.log = log;
    }

    public bool IsAnonymousModeEnabled => anonymousModeEnabled;

    public string AnonymousModeStatusText =>
        anonymousModeEnabled
            ? "Enabled - visible player nameplates are masked locally with deterministic Firstname Lastname aliases."
            : "Disabled";

    public bool SetAnonymousModeEnabled(bool value)
    {
        anonymousModeEnabled = value;
        UpdateSubscription();
        RequestRedraw();
        return anonymousModeEnabled;
    }

    public void Dispose()
    {
        anonymousModeEnabled = false;
        if (subscribed)
            namePlateGui.OnDataUpdate -= OnNamePlateUpdate;

        subscribed = false;
    }

    private void UpdateSubscription()
    {
        var shouldSubscribe = anonymousModeEnabled;
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
        if (!anonymousModeEnabled)
            return;

        foreach (var handler in handlers)
        {
            var playerCharacter = handler.PlayerCharacter;
            if (playerCharacter == null)
                continue;

            var originalName = NormalizeIdentityPart(playerCharacter.Name.ToString());
            var originalWorld = ResolveOriginalWorld(playerCharacter);
            var alias = ResolveAlias(originalName, originalWorld, handler.GameObjectId);
            handler.Name = new SeStringBuilder().AddText(alias).Build();
            handler.RemoveTitle();
            handler.RemoveFreeCompanyTag();
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

    private static string NormalizeIdentityPart(string value)
    {
        return CharacterAliasHelper.NormalizeIdentityPart(value);
    }
}
