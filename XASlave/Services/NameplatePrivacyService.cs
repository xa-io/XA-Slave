using System;
using System.Collections.Generic;
using Dalamud.Game.Gui.NamePlate;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

public sealed class NameplatePrivacyService : IDisposable
{
    private readonly INamePlateGui namePlateGui;
    private readonly IPluginLog log;

    private readonly Dictionary<ulong, string> aliasCache = new();

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
            ? "Enabled - visible player nameplates are masked locally."
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
            if (handler.PlayerCharacter == null)
                continue;

            var alias = ResolveAlias(handler.GameObjectId);
            handler.Name = new SeStringBuilder().AddText(alias).Build();
            handler.RemoveTitle();
            handler.RemoveFreeCompanyTag();
        }
    }

    private string ResolveAlias(ulong stableId)
    {
        if (stableId == 0)
            return "Traveler";

        if (aliasCache.TryGetValue(stableId, out var alias))
            return alias;

        alias = $"Traveler {stableId % 10000:0000}";
        aliasCache[stableId] = alias;
        return alias;
    }
}
