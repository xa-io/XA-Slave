using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace XASlave.Services;

public unsafe sealed class FieldEntryCommandService : IDisposable
{
    private const float EntryNpcStopDistance = 1.5f;
    private const float EntryRouteStopDistance = 3.0f;
    private const float EntryNpcAethernetDistance = 100.0f;
    private const int MaxTargetSearchAttempts = 120;
    private const int PostTravelCharacterSafeWaitPasses = 3;
    private const int DestinationWaitTimeoutSeconds = 180;

    private static readonly Vector3 KuganeFlagPosition = new(-114.3f, -5f, 150f);

    private readonly IFramework framework;
    private readonly IDataManager dataManager;
    private readonly IClientState clientState;
    private readonly IpcClient ipcClient;
    private readonly IPluginLog log;
    private readonly List<FieldEntryDefinition> entries;
    private bool enabled;
    private bool subscribed;
    private PendingFieldEntry? pendingEntry;

    public FieldEntryCommandService(
        IFramework framework,
        IDataManager dataManager,
        IClientState clientState,
        IpcClient ipcClient,
        IPluginLog log)
    {
        this.framework = framework;
        this.dataManager = dataManager;
        this.clientState = clientState;
        this.ipcClient = ipcClient;
        this.log = log;
        entries = BuildEntryDefinitions();
    }

    public string StatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? uiStatusText = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string LocalizedStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiStatusText, StatusText);
    internal global::XASlave.Localization.UiMessage StatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiStatusText, StatusText);
    public string LastActionText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S03F020CBB71754CC");
    private global::XASlave.Localization.UiMessage? uiLastActionText = global::XASlave.Localization.UiMessage.Key("S03F020CBB71754CC");
    internal string LocalizedLastActionText => global::XASlave.Localization.UiMessage.Resolve(ref uiLastActionText, LastActionText);
    internal global::XASlave.Localization.UiMessage LastActionTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiLastActionText, LastActionText);
    internal global::XASlave.Localization.UiMessage PendingEntryTextMessage => pendingEntry == null ? global::XASlave.Localization.UiMessage.Key("SAB0171CA0494D441") : global::XASlave.Localization.UiMessage.Raw(pendingEntry.Definition.DisplayName);
    public string PendingEntryText => PendingEntryTextMessage.Original;
    internal string LocalizedPendingEntryText => PendingEntryTextMessage.Display;
    public string LastTeleportCommand { get; private set; } = global::XASlave.Localization.UiMessage.Key("SDC937B59892604F5").Original;
    private global::XASlave.Localization.UiMessage? uiLastTeleportCommand = global::XASlave.Localization.UiMessage.Key("SDC937B59892604F5");
    internal global::XASlave.Localization.UiMessage LastTeleportCommandMessage => global::XASlave.Localization.UiMessage.Capture(ref uiLastTeleportCommand, LastTeleportCommand);
    internal string LocalizedLastTeleportCommand => global::XASlave.Localization.UiMessage.Resolve(ref uiLastTeleportCommand, LastTeleportCommand);
    public string LastSuggestedZone { get; private set; } = global::XASlave.Localization.UiLocale.Native("SDC937B59892604F5");
    private global::XASlave.Localization.UiMessage? uiLastSuggestedZone = global::XASlave.Localization.UiMessage.Key("SDC937B59892604F5");
    internal global::XASlave.Localization.UiMessage LastSuggestedZoneMessage => global::XASlave.Localization.UiMessage.Capture(ref uiLastSuggestedZone, LastSuggestedZone);
    internal string LocalizedLastSuggestedZone => global::XASlave.Localization.UiMessage.Resolve(ref uiLastSuggestedZone, LastSuggestedZone);
    public int SupportedEntryCount => entries.Count;
    public IReadOnlyList<string> SupportedKeys => entries.Select(entry => entry.Key).ToList();

    public bool SetEnabled(bool value)
    {
        if (value == enabled)
            return enabled;

        if (!value)
        {
            enabled = false;
            pendingEntry = null;
            Unsubscribe();
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"));
            return false;
        }

        enabled = true;
        Subscribe();
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S26C2A50D627B39B4"));
        return true;
    }

    public void Dispose()
    {
        enabled = false;
        pendingEntry = null;
        Unsubscribe();
    }

    internal global::XASlave.Localization.UiMessage BuildUsageMessage()
    {
        var keys = string.Join(", ", entries.Select(entry => entry.Key));
        return global::XASlave.Localization.UiMessage.Format("SDF9AB30DAA1AC705", keys);
    }
    public string BuildUsageText() => BuildUsageMessage().Original;
    internal string BuildLocalizedUsageText() => BuildUsageMessage().Display;

    public bool TryStart(string query)
    {
        if (!enabled)
        {
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("SEA596324727471BC"));
            return false;
        }

        var trimmed = query.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return false;

        var entry = ResolveEntry(trimmed);
        if (entry == null)
        {
            LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S9429F60E083E4C2F", trimmed, DateTime.Now));
            return false;
        }

        pendingEntry = new PendingFieldEntry(entry);
        LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("SD81B2229F25FDACA", entry.DisplayName, DateTime.Now));
        TryAdvancePendingEntry(forceImmediate: true);
        return true;
    }

    public bool TryStartByKey(string key)
    {
        return TryStart(key);
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
        if (!enabled || pendingEntry == null)
            return;

        TryAdvancePendingEntry(forceImmediate: false);
    }

    private void TryAdvancePendingEntry(bool forceImmediate)
    {
        if (pendingEntry == null)
            return;

        if (!forceImmediate && DateTime.UtcNow < pendingEntry.NextActionAtUtc)
            return;

        try
        {
            var definition = pendingEntry.Definition;
            if (TryCompleteIfInDestination(pendingEntry))
                return;

            if (clientState.TerritoryType != definition.StagingTerritoryId)
            {
                if (!pendingEntry.TravelRequested)
                {
                    if (TryRequestTravel(definition, out var travelCommand))
                    {
                        pendingEntry.TravelRequested = true;
                        pendingEntry.StagingAethernetRequested = !string.IsNullOrWhiteSpace(definition.TravelCommand);
                        LastTeleportCommand = global::XASlave.Localization.UiMessage.Store(ref uiLastTeleportCommand, global::XASlave.Localization.UiMessage.Raw(travelCommand));
                        LastSuggestedZone = global::XASlave.Localization.UiMessage.Store(ref uiLastSuggestedZone, global::XASlave.Localization.UiMessage.Raw(definition.StagingZoneName));
                        LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("SCF063750D79657B4", definition.StagingZoneName, definition.DisplayName, DateTime.Now));
                    }
                    else
                    {
                        LastSuggestedZone = global::XASlave.Localization.UiMessage.Store(ref uiLastSuggestedZone, global::XASlave.Localization.UiMessage.Raw(definition.StagingZoneName));
                        LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S6293BC88A25A9781", definition.StagingZoneName, definition.DisplayName));
                    }
                }

                pendingEntry.NextActionAtUtc = DateTime.UtcNow.AddSeconds(1);
                return;
            }

            if (pendingEntry.TravelRequested && ipcClient.LifestreamIsBusy())
            {
                pendingEntry.NextActionAtUtc = DateTime.UtcNow.AddMilliseconds(500);
                return;
            }

            if (pendingEntry.TravelRequested && !pendingEntry.PostTravelSafeWaitComplete)
            {
                if (!RunPostTravelCharacterSafeWait(pendingEntry))
                    return;
            }

            if (!EnsureEntryTargetIsLocallyReachable(pendingEntry))
                return;

            if (!pendingEntry.RoutePointPrepared)
            {
                PrepareRoutePoint(definition);
                pendingEntry.RoutePointPrepared = true;
                pendingEntry.NextActionAtUtc = DateTime.UtcNow.AddMilliseconds(400);
                return;
            }

            if (pendingEntry.StepIndex >= definition.Steps.Count)
            {
                WaitForDestinationOrComplete(pendingEntry);
                return;
            }

            if (TryApplyCurrentStep(pendingEntry))
            {
                if (pendingEntry.StepIndex >= definition.Steps.Count)
                    WaitForDestinationOrComplete(pendingEntry);

                return;
            }

            if (pendingEntry == null)
                return;

            if (!pendingEntry.EntryInteractionRequested)
            {
                TryReachEntryPoint(pendingEntry);
                return;
            }

            pendingEntry.NextActionAtUtc = DateTime.UtcNow.AddMilliseconds(150);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Field Operations Entry Command failed while advancing a pending entry flow.");
        }
    }

    private bool TryRequestTravel(FieldEntryDefinition definition, out string travelCommand)
    {
        travelCommand = string.Empty;

        if (!string.IsNullOrWhiteSpace(definition.TravelCommand) &&
            ipcClient.IsLifestreamAvailable() &&
            ipcClient.LifestreamExecuteCommand(definition.TravelCommand))
        {
            travelCommand = $"/li {definition.TravelCommand}";
            return true;
        }

        if (!string.IsNullOrWhiteSpace(definition.TravelCommand) &&
            ChatHelper.TrySend($"/li {definition.TravelCommand}"))
        {
            travelCommand = $"/li {definition.TravelCommand}";
            return true;
        }

        if (string.IsNullOrWhiteSpace(definition.StagingZoneName))
            return false;

        travelCommand = $"/tp \"{definition.StagingZoneName}\"";
        return ChatHelper.TrySend(travelCommand);
    }

    private bool EnsureEntryTargetIsLocallyReachable(PendingFieldEntry entry)
    {
        var definition = entry.Definition;
        if (string.IsNullOrWhiteSpace(definition.TargetName))
            return true;

        if (entry.StagingAethernetRequested)
            return true;

        if (TryFindTargetableActorDistance(definition.TargetName, out var distance) &&
            distance <= EntryNpcAethernetDistance)
        {
            return true;
        }

        if (!TryRequestTravel(definition, out var travelCommand))
        {
            LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S13A75E4B8572BEA1", definition.TargetName, EntryNpcAethernetDistance, definition.StagingZoneName, definition.DisplayName));
            entry.NextActionAtUtc = DateTime.UtcNow.AddSeconds(1);
            return false;
        }

        entry.TravelRequested = true;
        entry.StagingAethernetRequested = true;
        entry.PostTravelSafeWaitComplete = false;
        entry.PostTravelSafeWaitPassCount = 0;
        entry.NextSafeWaitCheckUtc = DateTime.UtcNow.AddMilliseconds(500);
        entry.RoutePointPrepared = false;
        entry.RouteRequested = false;
        entry.EntryInteractionRequested = false;
        entry.TargetSearchAttempts = 0;
        entry.NextActionAtUtc = DateTime.UtcNow.AddSeconds(1);
        LastTeleportCommand = global::XASlave.Localization.UiMessage.Store(ref uiLastTeleportCommand, global::XASlave.Localization.UiMessage.Raw(travelCommand));
        LastSuggestedZone = global::XASlave.Localization.UiMessage.Store(ref uiLastSuggestedZone, global::XASlave.Localization.UiMessage.Raw(definition.StagingZoneName));
        LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S5817AB3B23308E32", definition.TargetName, travelCommand, definition.DisplayName, DateTime.Now));
        return false;
    }

    private bool TryApplyCurrentStep(PendingFieldEntry entry)
    {
        var definition = entry.Definition;
        if (entry.StepIndex >= definition.Steps.Count)
            return true;

        var step = definition.Steps[entry.StepIndex];
        var stepApplied = step.Kind switch
        {
            FieldEntryStepKind.SelectStringIndex when AddonHelper.IsAddonReady("SelectString")
                => AddonHelper.FireCallback("SelectString", step.Value),
            FieldEntryStepKind.SelectStringText when AddonHelper.IsAddonReady("SelectString")
                => TrySelectStringStep(entry, step),
            FieldEntryStepKind.SelectYes when AddonHelper.IsAddonVisible("SelectYesno")
                => AddonHelper.ClickYesNo(true, closeAfter: false),
            _ => false,
        };

        if (!stepApplied)
            return false;

        entry.StepIndex++;
        entry.NextActionAtUtc = DateTime.UtcNow.AddMilliseconds(500);
        LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("SC798CA85E920F6A8", definition.DisplayName, entry.StepIndex, definition.Steps.Count, DateTime.Now));
        return true;
    }

    private bool TrySelectStringStep(PendingFieldEntry entry, FieldEntryStep step)
    {
        var callbackIndex = AddonHelper.GetAddonListTextCallbackIndex("SelectString", step.Text, step.Contains);
        if (callbackIndex < 0)
        {
            AbortEntryUnavailable(entry);
            return false;
        }

        return AddonHelper.FireCallback("SelectString", callbackIndex);
    }

    private void AbortEntryUnavailable(PendingFieldEntry entry)
    {
        var definition = entry.Definition;
        var message = global::XASlave.Localization.UiMessage.Format("S9525984A91439092", definition.DisplayName);
        ChatHelper.TrySend($"/echo {message}");
        LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S0B42A0AB4428C05D", message, DateTime.Now));
        pendingEntry = null;
    }

    private bool RunPostTravelCharacterSafeWait(PendingFieldEntry entry)
    {
        if (DateTime.UtcNow < entry.NextSafeWaitCheckUtc)
            return false;

        if (!CharacterSafetyHelper.IsCharacterSafeWaitReady())
        {
            entry.PostTravelSafeWaitPassCount = 0;
            entry.NextSafeWaitCheckUtc = DateTime.UtcNow.AddMilliseconds(500);
            LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S79368658565E6AE8", entry.Definition.TargetName ?? entry.Definition.DisplayName, DateTime.Now));
            return false;
        }

        entry.PostTravelSafeWaitPassCount++;
        if (entry.PostTravelSafeWaitPassCount >= PostTravelCharacterSafeWaitPasses)
        {
            entry.PostTravelSafeWaitComplete = true;
            LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S6F5235A177EC4D5F", entry.Definition.DisplayName, DateTime.Now));
            return true;
        }

        entry.NextSafeWaitCheckUtc = DateTime.UtcNow.AddMilliseconds(500);
        return false;
    }

    private bool TryCompleteIfInDestination(PendingFieldEntry entry)
    {
        var definition = entry.Definition;
        if (definition.DestinationTerritoryId == 0 ||
            definition.DestinationTerritoryId == definition.StagingTerritoryId ||
            clientState.TerritoryType != definition.DestinationTerritoryId)
        {
            return false;
        }

        LastSuggestedZone = global::XASlave.Localization.UiMessage.Store(ref uiLastSuggestedZone, global::XASlave.Localization.UiMessage.Raw(definition.DisplayName));
        LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S761AA452B9523C69", definition.DisplayName, DateTime.Now));
        pendingEntry = null;
        return true;
    }

    private void WaitForDestinationOrComplete(PendingFieldEntry entry)
    {
        var definition = entry.Definition;
        if (definition.DestinationTerritoryId == 0)
        {
            LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S23C30CED6F85F3D3", definition.DisplayName, DateTime.Now));
            pendingEntry = null;
            return;
        }

        entry.EntryFlowSubmittedAtUtc ??= DateTime.UtcNow;
        if (DateTime.UtcNow - entry.EntryFlowSubmittedAtUtc.Value > TimeSpan.FromSeconds(DestinationWaitTimeoutSeconds))
        {
            LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("SFA7908C296FE3314", definition.DisplayName, DateTime.Now));
            pendingEntry = null;
            return;
        }

        LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S47E5AC41BAA64DB9", definition.DisplayName, DateTime.Now));
        entry.NextActionAtUtc = DateTime.UtcNow.AddSeconds(1);
    }

    private void TryReachEntryPoint(PendingFieldEntry entry)
    {
        var definition = entry.Definition;

        if (string.IsNullOrWhiteSpace(definition.TargetName))
        {
            if (!entry.RouteRequested)
            {
                if (TryStartRouteToEntry(definition))
                {
                    entry.RouteRequested = true;
                    entry.NextActionAtUtc = DateTime.UtcNow.AddMilliseconds(500);
                    return;
                }

                entry.EntryInteractionRequested = true;
                entry.NextActionAtUtc = DateTime.UtcNow.AddMilliseconds(300);
                LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("SBD52A4D602EB6E70", definition.DisplayName, DateTime.Now));
                return;
            }

            if (AddonHelper.IsVnavMovementActive())
            {
                entry.NextActionAtUtc = DateTime.UtcNow.AddMilliseconds(500);
                return;
            }

            entry.EntryInteractionRequested = true;
            entry.NextActionAtUtc = DateTime.UtcNow.AddMilliseconds(300);
            LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S13CD0C99748BE35C", definition.DisplayName, DateTime.Now));
            return;
        }

        if (!AddonHelper.CurrentTargetMatches(definition.TargetName))
        {
            AddonHelper.TargetByName(definition.TargetName);
            entry.TargetSearchAttempts++;

            if (!entry.RouteRequested && TryStartRouteToEntry(definition))
                entry.RouteRequested = true;

            if (entry.TargetSearchAttempts >= MaxTargetSearchAttempts)
            {
                LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("SC4022B646FEE6719", definition.TargetName, definition.DisplayName, definition.StagingZoneName, DateTime.Now));
                pendingEntry = null;
                return;
            }

            entry.NextActionAtUtc = DateTime.UtcNow.AddMilliseconds(500);
            return;
        }

        entry.TargetSearchAttempts = 0;
        if (!AddonHelper.IsCurrentTargetWithinStopDistanceAndStopped(definition.TargetName, EntryNpcStopDistance))
        {
            entry.NextActionAtUtc = DateTime.UtcNow.AddMilliseconds(500);
            LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S78B012721A821E23", definition.TargetName, definition.DisplayName, DateTime.Now));
            return;
        }

        if (AddonHelper.InteractWithTarget())
        {
            entry.EntryInteractionRequested = true;
            entry.NextActionAtUtc = DateTime.UtcNow.AddMilliseconds(700);
            LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S3BBB7A9731BD0DFE", definition.TargetName, definition.DisplayName, DateTime.Now));
            return;
        }

        entry.NextActionAtUtc = DateTime.UtcNow.AddMilliseconds(500);
    }

    private bool TryStartRouteToEntry(FieldEntryDefinition definition)
    {
        if (!ipcClient.VnavIsReady())
            return false;

        if (!ipcClient.VnavPathfindAndMoveCloseTo(definition.FlagPosition, false, EntryRouteStopDistance))
            return false;

        LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("SD44CDE311CB1E39C", definition.DisplayName, definition.StagingZoneName, DateTime.Now));
        return true;
    }

    private static bool TryFindTargetableActorDistance(string targetName, out float distance)
    {
        distance = float.MaxValue;
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null || string.IsNullOrWhiteSpace(targetName))
            return false;

        IGameObject? closestMatch = null;
        foreach (var actor in Plugin.ObjectTable)
        {
            var actorName = actor.Name.TextValue;
            if (string.IsNullOrWhiteSpace(actorName) ||
                !actorName.Equals(targetName, StringComparison.OrdinalIgnoreCase) ||
                !IsTargetable(actor))
            {
                continue;
            }

            var actorDistance = Vector3.Distance(localPlayer.Position, actor.Position);
            if (closestMatch != null && actorDistance >= distance)
                continue;

            closestMatch = actor;
            distance = actorDistance;
        }

        return closestMatch != null;
    }

    private static bool IsTargetable(IGameObject actor)
    {
        if (actor.Address == nint.Zero)
            return false;

        try
        {
            return ((CSGameObject*)actor.Address)->GetIsTargetable();
        }
        catch
        {
            return false;
        }
    }

    private void PrepareRoutePoint(FieldEntryDefinition definition)
    {
        try
        {
            var agentMap = AgentMap.Instance();
            if (agentMap == null)
                return;

            if (!dataManager.GetExcelSheet<TerritoryType>().TryGetRow(definition.StagingTerritoryId, out var territoryRow))
                return;

            var mapId = territoryRow.Map.RowId;
            if (mapId == 0)
                return;

            agentMap->SetFlagMapMarker(definition.StagingTerritoryId, mapId, definition.FlagPosition);
            LastSuggestedZone = global::XASlave.Localization.UiMessage.Store(ref uiLastSuggestedZone, global::XASlave.Localization.UiMessage.Raw(definition.StagingZoneName));
            LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S18878D2CC9689C57", definition.DisplayName, definition.StagingZoneName, DateTime.Now));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Field Operations Entry Command failed while preparing the entry route point.");
        }
    }

    private FieldEntryDefinition? ResolveEntry(string query)
    {
        foreach (var entry in entries)
        {
            if (entry.Key.Equals(query, StringComparison.OrdinalIgnoreCase) ||
                entry.Aliases.Any(alias => alias.Equals(query, StringComparison.OrdinalIgnoreCase)))
            {
                return entry;
            }
        }

        return entries.FirstOrDefault(entry =>
            entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            entry.Aliases.Any(alias => alias.Contains(query, StringComparison.OrdinalIgnoreCase)));
    }

    private List<FieldEntryDefinition> BuildEntryDefinitions()
    {
        return
        [
            new("anemos", ResolveContentName(283, "Eureka Anemos"), 628, ResolveContentTerritoryId(283, 732), "Kugane", "Pier #1", KuganeFlagPosition, "Rodney", EurekaSteps("Anemos")),
            new("pagos", ResolveContentName(581, "Eureka Pagos"), 628, ResolveContentTerritoryId(581, 763), "Kugane", "Pier #1", KuganeFlagPosition, "Rodney", EurekaSteps("Pagos")),
            new("pyros", ResolveContentName(598, "Eureka Pyros"), 628, ResolveContentTerritoryId(598, 795), "Kugane", "Pier #1", KuganeFlagPosition, "Rodney", EurekaSteps("Pyros")),
            new("hydatos", ResolveContentName(639, "Eureka Hydatos"), 628, ResolveContentTerritoryId(639, 827), "Kugane", "Pier #1", KuganeFlagPosition, "Rodney", EurekaSteps("Hydatos")),
        ];
    }

    private static IReadOnlyList<FieldEntryStep> EurekaSteps(string zoneName)
    {
        return [FieldEntryStep.SelectStringText(zoneName, true), FieldEntryStep.SelectYes()];
    }

    private string ResolveContentName(uint contentId, string fallback)
    {
        return dataManager.GetExcelSheet<ContentFinderCondition>().TryGetRow(contentId, out var content)
            ? content.Name.ToString().Trim()
            : fallback;
    }

    private uint ResolveContentTerritoryId(uint contentId, uint fallback)
    {
        return dataManager.GetExcelSheet<ContentFinderCondition>().TryGetRow(contentId, out var content) &&
               content.TerritoryType.RowId != 0
            ? content.TerritoryType.RowId
            : fallback;
    }

    private sealed class PendingFieldEntry(FieldEntryDefinition definition)
    {
        public FieldEntryDefinition Definition { get; } = definition;
        public DateTime NextActionAtUtc { get; set; }
        public int StepIndex { get; set; }
        public bool TravelRequested { get; set; }
        public bool StagingAethernetRequested { get; set; }
        public bool PostTravelSafeWaitComplete { get; set; }
        public int PostTravelSafeWaitPassCount { get; set; }
        public DateTime NextSafeWaitCheckUtc { get; set; }
        public bool RoutePointPrepared { get; set; }
        public bool RouteRequested { get; set; }
        public bool EntryInteractionRequested { get; set; }
        public int TargetSearchAttempts { get; set; }
        public DateTime? EntryFlowSubmittedAtUtc { get; set; }
    }

    private sealed class FieldEntryDefinition
    {
        public FieldEntryDefinition(
            string key,
            string displayName,
            uint stagingTerritoryId,
            uint destinationTerritoryId,
            string stagingZoneName,
            string? travelCommand,
            Vector3 flagPosition,
            string? targetName,
            IReadOnlyList<FieldEntryStep> steps,
            IReadOnlyList<string>? aliases = null)
        {
            Key = key;
            DisplayName = displayName;
            StagingTerritoryId = stagingTerritoryId;
            DestinationTerritoryId = destinationTerritoryId;
            StagingZoneName = stagingZoneName;
            TravelCommand = travelCommand;
            FlagPosition = flagPosition;
            TargetName = targetName;
            Aliases = aliases?.ToList() ?? [];
            Steps = steps.ToList();
        }

        public string Key { get; }
        public string DisplayName { get; }
        public uint StagingTerritoryId { get; }
        public uint DestinationTerritoryId { get; }
        public string StagingZoneName { get; }
        public string? TravelCommand { get; }
        public Vector3 FlagPosition { get; }
        public string? TargetName { get; }
        public List<string> Aliases { get; }
        public List<FieldEntryStep> Steps { get; }
    }

    private readonly record struct FieldEntryStep(FieldEntryStepKind Kind, int Value, string Text, bool Contains)
    {
        public static FieldEntryStep SelectStringIndex(int value)
            => new(FieldEntryStepKind.SelectStringIndex, value, string.Empty, false);

        public static FieldEntryStep SelectStringText(string text, bool contains)
            => new(FieldEntryStepKind.SelectStringText, 0, text, contains);

        public static FieldEntryStep SelectYes()
            => new(FieldEntryStepKind.SelectYes, 0, string.Empty, false);
    }

    private enum FieldEntryStepKind
    {
        SelectStringIndex,
        SelectStringText,
        SelectYes,
    }
}
