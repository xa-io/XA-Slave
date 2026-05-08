using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud;
using Dalamud.Game.Agent;
using Dalamud.Game.Agent.AgentArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Common.Lua;
using Lumina.Excel.Sheets;
using ClientAgentInterface = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentInterface;
using DalamudAgentId = Dalamud.Game.Agent.AgentId;
using TerritoryIntendedUse = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;

namespace XASlave.Services;

public unsafe sealed class AutoSkipCutsceneService : IDisposable
{
    private const double StartupArmingStepDebugThresholdMilliseconds = 5.0;
    private const double StartupArmingStepWarningThresholdMilliseconds = 200.0;
    private const ushort GoldSaucerTerritoryId = 144;
    private const ushort MahjongTerritoryId = 831;
    private const int PointMenuResultEvent = 12;

    private static readonly ushort[] PraetoriumTerritoryIds = [1044, 1045];
    private static readonly ushort[] CastrumTerritoryIds = [1043];
    private static readonly ushort[] PortaDecumanaTerritoryIds = [1046];

    private const string MsqContentDirectorSig = "48 89 5C 24 ?? 57 48 83 EC 50 48 8B D1 48 8D 4C 24 ?? E8 ?? ?? ?? ?? 48 8B 4C 24 ?? BA ?? ?? ?? ?? B3 01 E8 ?? ?? ?? ?? BA ?? ?? ?? ?? 48 8D 4C 24 ?? 48 8B F8 E8 ?? ?? ?? ?? 48 8B 4C 24 ?? 4C 8B C0 BA ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B 08 84 99 ?? ?? ?? ??";
    private const string MassivePcContentDirectorSig = "48 89 5C 24 ?? 57 48 83 EC 50 48 8B D1 48 8D 4C 24 ?? E8 ?? ?? ?? ?? 48 8B 4C 24 ?? BA ?? ?? ?? ?? B3 01 E8 ?? ?? ?? ?? BA ?? ?? ?? ?? 48 8D 4C 24 ?? 48 8B F8 E8 ?? ?? ?? ?? 48 8B 4C 24 ?? 4C 8B C0 BA ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B 08 48 8B 11";
    private const string GoldSaucerContentDirectorSig = "48 89 5C 24 ?? 57 48 83 EC 50 48 8B D1 48 8D 4C 24 ?? E8 ?? ?? ?? ?? 48 8B 4C 24 ?? BA ?? ?? ?? ?? B3 01 E8 ?? ?? ?? ?? BA ?? ?? ?? ?? 48 8D 4C 24 ?? 48 8B F8 E8 ?? ?? ?? ?? 48 8B 4C 24 ?? 4C 8B C0 BA ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B 08 84 99 ?? ?? ?? ??";
    private const string CustomTalkContentDirectorSig = "48 83 EC 58 48 8B D1 48 8D 4C 24 ?? E8 ?? ?? ?? ?? BA ?? ?? ?? ?? 48 8D 4C 24 ?? E8 ?? ?? ?? ?? 48 8B 4C 24 ?? 4C 8B C0 BA ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B 08 48 85 C9 74 06";
    private const string NormalCutscenesSig = "40 53 55 57 41 56 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B 59 08";
    private const string InnContentDirectorSig = "48 83 EC 58 48 8B D1 48 8D 4C 24 ?? E8 ?? ?? ?? ?? BA ?? ?? ?? ?? 48 8D 4C 24 ?? E8 ?? ?? ?? ?? 48 8B 4C 24 ?? 4C 8B C0 BA ?? ?? ?? ?? E8 ?? ?? ?? ?? BA ?? ?? ?? ?? 48 8D 4C 24 ?? E8 ?? ?? ?? ?? 48 8B 4C 24 ??";

    private readonly ICondition condition;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IDataManager dataManager;
    private readonly IPartyList partyList;
    private readonly ISigScanner sigScanner;
    private readonly IGameInteropProvider interopProvider;
    private readonly IAgentLifecycle agentLifecycle;
    private readonly IPluginLog log;

    private Hook<CutsceneHandleInputDelegate>? cutsceneHandleInputHook;
    private Hook<PlayCutsceneDelegate>? playCutsceneHook;
    private Hook<LuaFunctionDelegate>? playCutsceneLuaHook;
    private Hook<IsCutsceneSeenDelegate>? isCutsceneSeenHook;
    private Hook<LuaFunctionDelegate>? playStaffRollHook;
    private Hook<LuaFunctionDelegate>? playToBeContinuedHook;
    private PushAgentResultToLuaDelegate? pushAgentResultToLua;

    private Hook<ContentDirectorDelegate>? msqContentDirectorHook;
    private Hook<ContentDirectorDelegate>? massivePcContentDirectorHook;
    private Hook<ContentDirectorDelegate>? goldSaucerContentDirectorHook;
    private Hook<ContentDirectorDelegate>? customTalkContentDirectorHook;
    private Hook<NormalCutscenesDelegate>? normalCutscenesHook;
    private Hook<ContentDirectorDelegate>? innContentDirectorHook;

    private nint cutsceneUnskippablePatchAddress;
    private byte[]? cutsceneUnskippableOriginalBytes;
    private bool cutsceneUnskippablePatchApplied;
    private bool initialized;
    private bool enabled;
    private bool frameworkSubscribed;
    private bool clientStateSubscribed;
    private bool pointMenuAgentSubscribed;
    private bool startupArmingPending;
    private bool msqAutoPartyActive;
    private int startupArmingStep;
    private int availableSurfaceCount;
    private int availableOptionalSurfaceCount;
    private DateTime lastPromptAttemptUtc = DateTime.MinValue;
    private DateTime lastFashionReportAttemptUtc = DateTime.MinValue;
    private readonly HashSet<string> unavailableOptionalHooks = new(StringComparer.OrdinalIgnoreCase);

    private bool useZoneWhitelist;
    private HashSet<uint> whitelistTerritories = new();
    private HashSet<uint> blacklistTerritories = new();
    private bool skipNormalCutscenes = true;
    private bool skipMsqRoulette = true;
    private bool autoEnableMsqFourPlayer;
    private bool exemptPraetorium;
    private bool exemptCastrum;
    private bool exemptPortaDecumana;
    private bool skipMassivePc;
    private bool skipGoldSaucer;
    private bool goldSaucerMahjong;
    private bool goldSaucerAirForceOne;
    private bool goldSaucerChocoboRacing;
    private bool goldSaucerLordOfVerminion;
    private bool goldSaucerTripleTriad;
    private bool goldSaucerBlunderville;
    private bool goldSaucerFashionReport;
    private bool skipCustomTalk;
    private bool skipOceanFishing;
    private bool skipCrystallineConflict;
    private bool skipFrontlineRivalWings;
    private bool skipInn;

    public AutoSkipCutsceneService(
        ICondition condition,
        IFramework framework,
        IClientState clientState,
        IDataManager dataManager,
        IPartyList partyList,
        ISigScanner sigScanner,
        IGameInteropProvider interopProvider,
        IAgentLifecycle agentLifecycle,
        IPluginLog log)
    {
        this.condition = condition;
        this.framework = framework;
        this.clientState = clientState;
        this.dataManager = dataManager;
        this.partyList = partyList;
        this.sigScanner = sigScanner;
        this.interopProvider = interopProvider;
        this.agentLifecycle = agentLifecycle;
        this.log = log;
    }

    private enum CutsceneSkipCategory
    {
        Generic,
        Msq,
        MassivePc,
        GoldSaucer,
        CustomTalk,
        NormalCutscenes,
        Inn,
        FashionReport,
    }

    public bool IsEnabled => enabled;

    public bool IsStartupArmingPending => startupArmingPending;

    public uint CurrentTerritoryId => clientState.TerritoryType;

    public string CurrentTerritoryName => GetTerritoryName(CurrentTerritoryId);

    public bool CurrentTerritoryAllowed => IsZoneAllowed(CurrentTerritoryId);

    public string CurrentCategoryLabel => GetCurrentCategoryLabel();

    public string StatusText { get; private set; } = "Disabled";

    public void ApplyConfiguration(Configuration configuration)
    {
        useZoneWhitelist = configuration.AutoSkipCutscenesUseZoneWhitelist;
        whitelistTerritories = NormalizeTerritorySet(configuration.AutoSkipCutscenesWhitelistTerritories);
        blacklistTerritories = NormalizeTerritorySet(configuration.AutoSkipCutscenesBlacklistTerritories);
        skipNormalCutscenes = configuration.AutoSkipCutscenesSkipNormalCutscenes;
        skipMsqRoulette = configuration.AutoSkipCutscenesSkipMsqRoulette;
        autoEnableMsqFourPlayer = configuration.AutoSkipCutscenesAutoEnableMsqFourPlayer;
        exemptPraetorium = configuration.AutoSkipCutscenesExemptPraetorium;
        exemptCastrum = configuration.AutoSkipCutscenesExemptCastrum;
        exemptPortaDecumana = configuration.AutoSkipCutscenesExemptPortaDecumana;
        skipMassivePc = configuration.AutoSkipCutscenesSkipMassivePc;
        skipGoldSaucer = configuration.AutoSkipCutscenesSkipGoldSaucer;
        goldSaucerMahjong = configuration.AutoSkipCutscenesGoldSaucerMahjong;
        goldSaucerAirForceOne = configuration.AutoSkipCutscenesGoldSaucerAirForceOne;
        goldSaucerChocoboRacing = configuration.AutoSkipCutscenesGoldSaucerChocoboRacing;
        goldSaucerLordOfVerminion = configuration.AutoSkipCutscenesGoldSaucerLordOfVerminion;
        goldSaucerTripleTriad = configuration.AutoSkipCutscenesGoldSaucerTripleTriad;
        goldSaucerBlunderville = configuration.AutoSkipCutscenesGoldSaucerBlunderville;
        goldSaucerFashionReport = configuration.AutoSkipCutscenesGoldSaucerFashionReport;
        skipCustomTalk = configuration.AutoSkipCutscenesSkipCustomTalk;
        skipOceanFishing = configuration.AutoSkipCutscenesSkipOceanFishing;
        skipCrystallineConflict = configuration.AutoSkipCutscenesSkipCrystallineConflict;
        skipFrontlineRivalWings = configuration.AutoSkipCutscenesSkipFrontlineRivalWings;
        skipInn = configuration.AutoSkipCutscenesSkipInn;

        RefreshOptionalCategoryHooks();
        SyncCutscenePatchState();
        RefreshStatusText();
    }

    public bool RestoreEnabledOnStartup()
    {
        if (enabled && startupArmingPending)
            return true;

        if (initialized)
            return SetEnabled(true);

        EnsureFrameworkSubscribed();
        UpdateClientStateSubscriptions(true);
        enabled = true;
        startupArmingPending = true;
        startupArmingStep = 0;
        availableSurfaceCount = 0;
        availableOptionalSurfaceCount = 0;
        StatusText = "Arming - cutscene hook surfaces are initializing over upcoming frames.";
        return true;
    }

    public bool SetEnabled(bool value)
    {
        if (value == enabled && !startupArmingPending)
        {
            RefreshOptionalCategoryHooks();
            SyncCutscenePatchState();
            RefreshStatusText();
            return enabled;
        }

        if (!value)
        {
            Disable();
            StatusText = "Disabled";
            return false;
        }

        EnsureFrameworkSubscribed();
        UpdateClientStateSubscriptions(true);
        enabled = true;
        startupArmingPending = false;
        startupArmingStep = 0;
        EnsureInitializedForEnabledState();
        RefreshStatusText();
        return true;
    }

    public void Dispose()
    {
        Disable();
        DisposeHook(ref cutsceneHandleInputHook);
        DisposeHook(ref playCutsceneHook);
        DisposeHook(ref playCutsceneLuaHook);
        DisposeHook(ref isCutsceneSeenHook);
        DisposeHook(ref playStaffRollHook);
        DisposeHook(ref playToBeContinuedHook);
        DisposeHook(ref msqContentDirectorHook);
        DisposeHook(ref massivePcContentDirectorHook);
        DisposeHook(ref goldSaucerContentDirectorHook);
        DisposeHook(ref customTalkContentDirectorHook);
        DisposeHook(ref normalCutscenesHook);
        DisposeHook(ref innContentDirectorHook);
    }

    public string GetTerritoryName(uint territoryId)
    {
        if (territoryId == 0)
            return "Unknown";

        try
        {
            var row = dataManager.GetExcelSheet<TerritoryType>()?.GetRowOrDefault(territoryId);
            return row?.PlaceName.ValueNullable?.Name.ToString() ?? "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    private void Disable()
    {
        enabled = false;
        startupArmingPending = false;
        msqAutoPartyActive = false;
        startupArmingStep = 0;
        availableSurfaceCount = 0;
        availableOptionalSurfaceCount = 0;
        UnsubscribeFramework();
        UpdateClientStateSubscriptions(false);

        ToggleHook(cutsceneHandleInputHook, false, "CutsceneHandleInput");
        ToggleHook(playCutsceneHook, false, "PlayCutscene");
        ToggleHook(playCutsceneLuaHook, false, "PlayCutsceneLua");
        ToggleHook(isCutsceneSeenHook, false, "IsCutsceneSeen");
        ToggleHook(playStaffRollHook, false, "PlayStaffRoll");
        ToggleHook(playToBeContinuedHook, false, "PlayToBeContinued");
        UpdatePointMenuAgentSubscription(false);
        ToggleHook(msqContentDirectorHook, false, "MSQ content director");
        ToggleHook(massivePcContentDirectorHook, false, "Massive PC content director");
        ToggleHook(goldSaucerContentDirectorHook, false, "Gold Saucer content director");
        ToggleHook(customTalkContentDirectorHook, false, "Custom Talk content director");
        ToggleHook(normalCutscenesHook, false, "Normal cutscenes");
        ToggleHook(innContentDirectorHook, false, "Inn content director");
        RestoreCutsceneUnskippablePatch();
    }

    private void EnsureInitialized()
    {
        if (initialized)
            return;

        cutsceneHandleInputHook ??= TryCreateHook<CutsceneHandleInputDelegate>(Sigs.CutsceneHandleInputSig, CutsceneHandleInputDetour, "CutsceneHandleInput");
        playCutsceneHook ??= TryCreateHook<PlayCutsceneDelegate>(Sigs.PlayCutsceneSig, PlayCutsceneDetour, "PlayCutscene");
        playCutsceneLuaHook ??= TryCreateLuaFunctionHook<LuaFunctionDelegate>(Sigs.LuaBaseSig01, "PlayCutScene", PlayCutsceneLuaDetour, "PlayCutsceneLua");
        isCutsceneSeenHook ??= TryCreateHook<IsCutsceneSeenDelegate>(Sigs.IsCutsceneSeenSig, IsCutsceneSeenDetour, "IsCutsceneSeen");
        playStaffRollHook ??= TryCreateLuaFunctionHook<LuaFunctionDelegate>(Sigs.LuaBaseSig02, "PlayStaffRoll", PlayStaffRollDetour, "PlayStaffRoll");
        playToBeContinuedHook ??= TryCreateLuaFunctionHook<LuaFunctionDelegate>(Sigs.LuaBaseSig02, "PlayToBeContinued", PlayToBeContinuedDetour, "PlayToBeContinued");
        pushAgentResultToLua ??= TryCreateDelegate<PushAgentResultToLuaDelegate>(Sigs.PushAgentResultToLuaSig, "PushAgentResultToLua");

        if (cutsceneUnskippablePatchAddress == nint.Zero && !sigScanner.TryScanText(Sigs.CutsceneUnskippablePatchSig, out cutsceneUnskippablePatchAddress))
            log.Warning("[XASlave] Auto Skip Cutscenes could not find the unskippable cutscene patch signature.");

        initialized = true;
    }

    private void EnsureInitializedForEnabledState()
    {
        EnsureInitialized();
        if (!HasAnyCutsceneSurface())
        {
            StatusText = "Unavailable - cutscene signatures were not found.";
            log.Warning("[XASlave] Auto Skip Cutscenes unavailable: no cutscene hook or patch signatures were found.");
            return;
        }

        availableSurfaceCount = 0;
        availableSurfaceCount += ToggleHook(cutsceneHandleInputHook, true, "CutsceneHandleInput");
        availableSurfaceCount += ToggleHook(playCutsceneHook, true, "PlayCutscene");
        availableSurfaceCount += ToggleHook(playCutsceneLuaHook, true, "PlayCutsceneLua");
        availableSurfaceCount += ToggleHook(isCutsceneSeenHook, true, "IsCutsceneSeen");
        availableSurfaceCount += ToggleHook(playStaffRollHook, true, "PlayStaffRoll");
        availableSurfaceCount += ToggleHook(playToBeContinuedHook, true, "PlayToBeContinued");
        availableSurfaceCount += UpdatePointMenuAgentSubscription(true);
        if (cutsceneUnskippablePatchAddress != nint.Zero)
            availableSurfaceCount++;

        RefreshOptionalCategoryHooks();

        if (availableSurfaceCount + availableOptionalSurfaceCount == 0)
        {
            StatusText = "Unavailable - cutscene hooks failed to enable.";
            log.Warning("[XASlave] Auto Skip Cutscenes could not enable any hook or patch surfaces.");
            return;
        }

        SyncCutscenePatchState();
    }

    private void RefreshStatusText()
    {
        if (!enabled)
        {
            StatusText = "Disabled";
            return;
        }

        if (startupArmingPending)
        {
            StatusText = "Arming - cutscene hook surfaces are initializing over upcoming frames.";
            return;
        }

        if (!initialized)
        {
            StatusText = "Ready - cutscene hooks will arm when the next cutscene starts.";
            return;
        }

        var surfaceCount = availableSurfaceCount + availableOptionalSurfaceCount;
        if (surfaceCount == 0)
        {
            StatusText = "Unavailable - cutscene hooks failed to enable.";
            return;
        }

        var inactiveReason = GetInactiveReason();
        if (!string.IsNullOrWhiteSpace(inactiveReason))
        {
            StatusText = $"Enabled but inactive here ({CurrentTerritoryId} {CurrentTerritoryName}, {CurrentCategoryLabel}) - {inactiveReason}. {surfaceCount} cutscene surfaces available.";
            return;
        }

        StatusText = $"Enabled ({surfaceCount} cutscene surfaces available; current: {CurrentTerritoryId} {CurrentTerritoryName}, {CurrentCategoryLabel}).";
    }

    private Hook<T>? TryCreateHook<T>(ProtectedSig signature, T detour, string label)
        where T : Delegate
    {
        try
        {
            if (!sigScanner.TryScanText(signature, out var address) || address == nint.Zero)
                return null;

            return interopProvider.HookFromAddress(address, detour);
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[XASlave] Auto Skip Cutscenes failed to create {label} hook.");
            return null;
        }
    }

    private Hook<T>? TryCreateLuaFunctionHook<T>(ProtectedSig luaBaseSignature, string functionName, T detour, string label)
        where T : Delegate
    {
        try
        {
            if (!sigScanner.TryScanText(luaBaseSignature, out var baseAddress) || baseAddress == nint.Zero)
                return null;

            var functionAddress = GetLuaFunctionByName(baseAddress, functionName);
            if (functionAddress == nint.Zero)
                return null;

            return interopProvider.HookFromAddress(functionAddress, detour);
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[XASlave] Auto Skip Cutscenes failed to create {label} Lua hook.");
            return null;
        }
    }

    private static nint GetLuaFunctionByName(nint luaSetupFunctionStartAddress, string functionName, int scanSize = 8192)
    {
        if (luaSetupFunctionStartAddress == nint.Zero || string.IsNullOrEmpty(functionName))
            return nint.Zero;

        var functionBytes = new byte[scanSize];

        try
        {
            Marshal.Copy(luaSetupFunctionStartAddress, functionBytes, 0, scanSize);
        }
        catch
        {
            return nint.Zero;
        }

        const byte leaPrefix0 = 0x4C;
        const byte leaPrefix1 = 0x8D;
        const byte leaString = 0x05;
        const byte leaFunction = 0x0D;

        var stringLeaIndex = -1;
        for (var i = 0; i <= functionBytes.Length - 7; i++)
        {
            if (functionBytes[i] != leaPrefix0 || functionBytes[i + 1] != leaPrefix1 || functionBytes[i + 2] != leaString)
                continue;

            var displacement = BitConverter.ToInt32(functionBytes, i + 3);
            var nextInstructionAddress = (long)luaSetupFunctionStartAddress + i + 7;
            var stringAddress = nextInstructionAddress + displacement;
            var referencedString = Marshal.PtrToStringAnsi((nint)stringAddress);
            if (!string.Equals(referencedString, functionName, StringComparison.Ordinal))
                continue;

            stringLeaIndex = i;
            break;
        }

        if (stringLeaIndex == -1)
            return nint.Zero;

        var searchLimit = Math.Max(0, stringLeaIndex - 100);
        for (var i = stringLeaIndex - 1; i >= searchLimit; i--)
        {
            if (i + 7 >= functionBytes.Length)
                continue;

            if (functionBytes[i] != leaPrefix0 || functionBytes[i + 1] != leaPrefix1 || functionBytes[i + 2] != leaFunction)
                continue;

            var displacement = BitConverter.ToInt32(functionBytes, i + 3);
            var nextInstructionAddress = (long)luaSetupFunctionStartAddress + i + 7;
            return (nint)(nextInstructionAddress + displacement);
        }

        return nint.Zero;
    }

    private T? TryCreateDelegate<T>(ProtectedSig signature, string label)
        where T : Delegate
    {
        try
        {
            if (!sigScanner.TryScanText(signature, out var address) || address == nint.Zero)
                return null;

            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[XASlave] Auto Skip Cutscenes failed to create {label} delegate.");
            return null;
        }
    }

    private bool HasAnyCutsceneSurface()
    {
        return cutsceneHandleInputHook != null
            || playCutsceneHook != null
            || playCutsceneLuaHook != null
            || isCutsceneSeenHook != null
            || playStaffRollHook != null
            || playToBeContinuedHook != null
            || pushAgentResultToLua != null
            || cutsceneUnskippablePatchAddress != nint.Zero;
    }

    private int UpdatePointMenuAgentSubscription(bool targetEnabled)
    {
        if (targetEnabled && pushAgentResultToLua == null)
            return 0;

        if (pointMenuAgentSubscribed == targetEnabled)
            return targetEnabled ? 1 : 0;

        try
        {
            if (targetEnabled)
                agentLifecycle.RegisterListener(AgentEvent.PostReceiveEvent, DalamudAgentId.PointMenu, OnPointMenuAgent);
            else
                agentLifecycle.UnregisterListener(AgentEvent.PostReceiveEvent, DalamudAgentId.PointMenu, OnPointMenuAgent);

            pointMenuAgentSubscribed = targetEnabled;
            return targetEnabled ? 1 : 0;
        }
        catch (Exception ex)
        {
            pointMenuAgentSubscribed = false;
            log.Warning(ex, $"[XASlave] Auto Skip Cutscenes failed to {(targetEnabled ? "register" : "unregister")} PointMenu agent listener.");
            return 0;
        }
    }

    private void RefreshOptionalCategoryHooks()
    {
        if (!initialized || !enabled || startupArmingPending)
            return;

        availableOptionalSurfaceCount = 0;
        availableOptionalSurfaceCount += RefreshContentDirectorHook(ref msqContentDirectorHook, MsqContentDirectorSig, ShouldArmMsqHook() || ShouldArmGoldSaucerHook(), MsqContentDirectorDetour, "MSQ/Gold Saucer/Ocean/PvP content director");
        availableOptionalSurfaceCount += RefreshContentDirectorHook(ref massivePcContentDirectorHook, MassivePcContentDirectorSig, skipMassivePc, MassivePcContentDirectorDetour, "Massive PC content director");
        ToggleHook(goldSaucerContentDirectorHook, false, "Gold Saucer content director");
        availableOptionalSurfaceCount += RefreshContentDirectorHook(ref customTalkContentDirectorHook, CustomTalkContentDirectorSig, skipCustomTalk, CustomTalkContentDirectorDetour, "Custom Talk content director");
        availableOptionalSurfaceCount += RefreshNormalCutscenesHook();
        availableOptionalSurfaceCount += RefreshContentDirectorHook(ref innContentDirectorHook, InnContentDirectorSig, skipInn, InnContentDirectorDetour, "Inn content director");
    }

    private int RefreshContentDirectorHook(
        ref Hook<ContentDirectorDelegate>? hook,
        string signature,
        bool targetEnabled,
        ContentDirectorDelegate detour,
        string label)
    {
        if (!targetEnabled)
        {
            ToggleHook(hook, false, label);
            return 0;
        }

        if (hook == null && !unavailableOptionalHooks.Contains(label))
        {
            try
            {
                hook = interopProvider.HookFromSignature(signature, detour);
            }
            catch (Exception ex)
            {
                unavailableOptionalHooks.Add(label);
                log.Warning(ex, $"[XASlave] Auto Skip Cutscenes could not create optional {label} hook.");
                return 0;
            }
        }

        return ToggleHook(hook, true, label);
    }

    private int RefreshNormalCutscenesHook()
    {
        if (!skipNormalCutscenes)
        {
            ToggleHook(normalCutscenesHook, false, "Normal cutscenes");
            return 0;
        }

        const string label = "Normal cutscenes";
        if (normalCutscenesHook == null && !unavailableOptionalHooks.Contains(label))
        {
            try
            {
                normalCutscenesHook = interopProvider.HookFromSignature<NormalCutscenesDelegate>(NormalCutscenesSig, NormalCutscenesDetour);
            }
            catch (Exception ex)
            {
                unavailableOptionalHooks.Add(label);
                log.Warning(ex, "[XASlave] Auto Skip Cutscenes could not create optional Normal cutscenes hook.");
                return 0;
            }
        }

        return ToggleHook(normalCutscenesHook, true, label);
    }

    private int ToggleHook<T>(Hook<T>? hook, bool targetEnabled, string label)
        where T : Delegate
    {
        if (hook == null || hook.IsDisposed)
            return 0;

        try
        {
            if (targetEnabled)
            {
                if (!hook.IsEnabled)
                    hook.Enable();
                return hook.IsEnabled ? 1 : 0;
            }

            if (hook.IsEnabled)
                hook.Disable();
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[XASlave] Auto Skip Cutscenes failed to {(targetEnabled ? "enable" : "disable")} {label} hook.");
        }

        return 0;
    }

    private void EnsureFrameworkSubscribed()
    {
        if (frameworkSubscribed)
            return;

        framework.Update += OnFrameworkUpdate;
        frameworkSubscribed = true;
    }

    private void UnsubscribeFramework()
    {
        if (!frameworkSubscribed)
            return;

        framework.Update -= OnFrameworkUpdate;
        frameworkSubscribed = false;
    }

    private void UpdateClientStateSubscriptions(bool targetEnabled)
    {
        if (clientStateSubscribed == targetEnabled)
            return;

        if (targetEnabled)
        {
            clientState.Login += OnLogin;
            clientState.TerritoryChanged += OnTerritoryChanged;
            clientState.CfPop += OnContentFinderPop;
        }
        else
        {
            clientState.Login -= OnLogin;
            clientState.TerritoryChanged -= OnTerritoryChanged;
            clientState.CfPop -= OnContentFinderPop;
        }

        clientStateSubscribed = targetEnabled;
    }

    private static void DisposeHook<T>(ref Hook<T>? hook)
        where T : Delegate
    {
        if (hook is { IsDisposed: false })
            hook.Dispose();

        hook = null;
    }

    private void SyncCutscenePatchState()
    {
        if (IsEffectivelyEnabled(CutsceneSkipCategory.Generic))
            ApplyCutsceneUnskippablePatch();
        else
            RestoreCutsceneUnskippablePatch();
    }

    private bool ApplyCutsceneUnskippablePatch()
    {
        if (cutsceneUnskippablePatchAddress == nint.Zero)
            return false;

        if (cutsceneUnskippablePatchApplied)
            return true;

        if (cutsceneUnskippableOriginalBytes == null
            && !SafeMemory.ReadBytes((IntPtr)cutsceneUnskippablePatchAddress, 1, out cutsceneUnskippableOriginalBytes))
        {
            log.Warning("[XASlave] Auto Skip Cutscenes failed to read the original unskippable branch byte.");
            return false;
        }

        if (!SafeMemory.WriteBytes((IntPtr)cutsceneUnskippablePatchAddress, [0xEB]))
        {
            log.Warning("[XASlave] Auto Skip Cutscenes failed to apply the unskippable cutscene branch patch.");
            return false;
        }

        cutsceneUnskippablePatchApplied = true;
        return true;
    }

    private void RestoreCutsceneUnskippablePatch()
    {
        if (!cutsceneUnskippablePatchApplied || cutsceneUnskippablePatchAddress == nint.Zero || cutsceneUnskippableOriginalBytes == null)
            return;

        if (!SafeMemory.WriteBytes((IntPtr)cutsceneUnskippablePatchAddress, cutsceneUnskippableOriginalBytes))
            log.Warning("[XASlave] Auto Skip Cutscenes failed to restore the unskippable cutscene branch byte.");

        cutsceneUnskippablePatchApplied = false;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (startupArmingPending)
        {
            ProcessStartupArmingStep();
            return;
        }

        SyncCutscenePatchState();
        RefreshStatusText();
        TryHandleFashionReportAddons();

        if (!IsEffectivelyEnabled(CutsceneSkipCategory.Generic) || !condition[ConditionFlag.OccupiedInCutSceneEvent])
            return;

        var now = DateTime.UtcNow;
        if ((now - lastPromptAttemptUtc).TotalMilliseconds < 750)
            return;

        lastPromptAttemptUtc = now;
        if (AddonHelper.IsAddonReady("SelectString"))
            AddonHelper.FireCallbackAndClose("SelectString", 0);
    }

    private void ProcessStartupArmingStep()
    {
        var label = GetStartupArmingStepLabel(startupArmingStep);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            switch (startupArmingStep)
            {
                case 0:
                    cutsceneHandleInputHook ??= TryCreateHook<CutsceneHandleInputDelegate>(Sigs.CutsceneHandleInputSig, CutsceneHandleInputDetour, "CutsceneHandleInput");
                    break;
                case 1:
                    playCutsceneHook ??= TryCreateHook<PlayCutsceneDelegate>(Sigs.PlayCutsceneSig, PlayCutsceneDetour, "PlayCutscene");
                    break;
                case 2:
                    playCutsceneLuaHook ??= TryCreateLuaFunctionHook<LuaFunctionDelegate>(Sigs.LuaBaseSig01, "PlayCutScene", PlayCutsceneLuaDetour, "PlayCutsceneLua");
                    break;
                case 3:
                    isCutsceneSeenHook ??= TryCreateHook<IsCutsceneSeenDelegate>(Sigs.IsCutsceneSeenSig, IsCutsceneSeenDetour, "IsCutsceneSeen");
                    break;
                case 4:
                    playStaffRollHook ??= TryCreateLuaFunctionHook<LuaFunctionDelegate>(Sigs.LuaBaseSig02, "PlayStaffRoll", PlayStaffRollDetour, "PlayStaffRoll");
                    break;
                case 5:
                    playToBeContinuedHook ??= TryCreateLuaFunctionHook<LuaFunctionDelegate>(Sigs.LuaBaseSig02, "PlayToBeContinued", PlayToBeContinuedDetour, "PlayToBeContinued");
                    break;
                case 6:
                    pushAgentResultToLua ??= TryCreateDelegate<PushAgentResultToLuaDelegate>(Sigs.PushAgentResultToLuaSig, "PushAgentResultToLua");
                    break;
                case 7:
                    if (cutsceneUnskippablePatchAddress == nint.Zero && !sigScanner.TryScanText(Sigs.CutsceneUnskippablePatchSig, out cutsceneUnskippablePatchAddress))
                        log.Warning("[XASlave] Auto Skip Cutscenes could not find the unskippable cutscene patch signature.");
                    initialized = true;
                    break;
                case 8:
                    if (!HasAnyCutsceneSurface())
                    {
                        enabled = false;
                        startupArmingPending = false;
                        startupArmingStep = 0;
                        StatusText = "Unavailable - cutscene signatures were not found.";
                        UnsubscribeFramework();
                        UpdateClientStateSubscriptions(false);
                        log.Warning("[XASlave] Auto Skip Cutscenes unavailable: no cutscene hook or patch signatures were found.");
                        return;
                    }

                    availableSurfaceCount = 0;
                    break;
                case 9:
                    availableSurfaceCount += ToggleHook(cutsceneHandleInputHook, true, "CutsceneHandleInput");
                    break;
                case 10:
                    availableSurfaceCount += ToggleHook(playCutsceneHook, true, "PlayCutscene");
                    break;
                case 11:
                    availableSurfaceCount += ToggleHook(playCutsceneLuaHook, true, "PlayCutsceneLua");
                    break;
                case 12:
                    availableSurfaceCount += ToggleHook(isCutsceneSeenHook, true, "IsCutsceneSeen");
                    break;
                case 13:
                    availableSurfaceCount += ToggleHook(playStaffRollHook, true, "PlayStaffRoll");
                    break;
                case 14:
                    availableSurfaceCount += ToggleHook(playToBeContinuedHook, true, "PlayToBeContinued");
                    break;
                default:
                    availableSurfaceCount += UpdatePointMenuAgentSubscription(true);
                    if (cutsceneUnskippablePatchAddress != nint.Zero)
                        availableSurfaceCount++;

                    startupArmingPending = false;
                    RefreshOptionalCategoryHooks();

                    if (availableSurfaceCount + availableOptionalSurfaceCount == 0)
                    {
                        enabled = false;
                        StatusText = "Unavailable - cutscene hooks failed to enable.";
                        UnsubscribeFramework();
                        UpdateClientStateSubscriptions(false);
                        log.Warning("[XASlave] Auto Skip Cutscenes could not enable any hook or patch surfaces.");
                    }
                    else
                    {
                        SyncCutscenePatchState();
                        RefreshStatusText();
                    }

                    startupArmingStep = 0;
                    return;
            }

            startupArmingStep++;
        }
        catch (Exception ex)
        {
            startupArmingPending = false;
            startupArmingStep = 0;
            enabled = false;
            StatusText = $"Unavailable - startup arming failed at {label}.";
            UnsubscribeFramework();
            UpdateClientStateSubscriptions(false);
            log.Warning(ex, $"[XASlave] Auto Skip Cutscenes startup arming failed at {label}.");
        }
        finally
        {
            stopwatch.Stop();
            LogStartupArmingStepDuration(label, stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private static string GetStartupArmingStepLabel(int step)
        => step switch
        {
            0 => "Create CutsceneHandleInput hook",
            1 => "Create PlayCutscene hook",
            2 => "Create PlayCutsceneLua hook",
            3 => "Create IsCutsceneSeen hook",
            4 => "Create PlayStaffRoll hook",
            5 => "Create PlayToBeContinued hook",
            6 => "Create PointMenu result delegate",
            7 => "Scan unskippable patch",
            8 => "Validate surfaces",
            9 => "Enable CutsceneHandleInput hook",
            10 => "Enable PlayCutscene hook",
            11 => "Enable PlayCutsceneLua hook",
            12 => "Enable IsCutsceneSeen hook",
            13 => "Enable PlayStaffRoll hook",
            14 => "Enable PlayToBeContinued hook",
            _ => "Finalize",
        };

    private void LogStartupArmingStepDuration(string label, double elapsedMilliseconds)
    {
        if (elapsedMilliseconds < StartupArmingStepDebugThresholdMilliseconds)
            return;

        var message = $"[XASlave] Auto Skip Cutscenes startup arming step '{label}' took {elapsedMilliseconds:F1}ms.";
        if (elapsedMilliseconds >= StartupArmingStepWarningThresholdMilliseconds)
            log.Warning(message);
        else
            log.Debug(message);
    }

    private void OnLogin()
    {
        if (!enabled)
            return;

        SyncCutscenePatchState();
        RefreshStatusText();
    }

    private void OnTerritoryChanged(uint territory)
    {
        if (msqAutoPartyActive && !IsMsqTerritory((ushort)territory))
        {
            msqAutoPartyActive = false;
            log.Information("[XASlave] Auto Skip Cutscenes MSQ light-party auto-enable cleared after leaving MSQ territory.");
        }

        SyncCutscenePatchState();
        RefreshStatusText();
    }

    private void OnContentFinderPop(ContentFinderCondition conditionRow)
    {
        if (!enabled || !autoEnableMsqFourPlayer)
            return;

        var rowId = conditionRow.RowId;
        var directMsq = rowId is 15 or 16 or 830;
        var msqRoulette = rowId == 0;
        if (!directMsq && !msqRoulette)
            return;

        if (!conditionRow.AllowUndersized && partyList.Length != 4)
            return;

        msqAutoPartyActive = true;
        RefreshStatusText();
        log.Information("[XASlave] Auto Skip Cutscenes MSQ light-party auto-enable armed for content finder row {RowId}.", rowId);
    }

    private void OnPointMenuAgent(AgentEvent _, AgentArgs args)
    {
        try
        {
            if (!IsEffectivelyEnabled(CutsceneSkipCategory.Generic) || pushAgentResultToLua == null)
                return;

            if (args is not AgentReceiveEventArgs receiveEventArgs
                || receiveEventArgs.Agent.Address == nint.Zero
                || receiveEventArgs.AtkValues == nint.Zero
                || receiveEventArgs.ValueCount == 0)
                return;

            var atkValues = (AtkValue*)receiveEventArgs.AtkValues;
            if (atkValues[0].Int != PointMenuResultEvent)
                return;

            var agent = (PointMenuAgent*)receiveEventArgs.Agent.Address;
            if (agent->Context == null)
                return;

            var index = agent->FindFirstUncompletedEntry();
            if (index < 0)
            {
                HidePointMenu(agent);
                return;
            }

            agent->SelectedIndex = index;
            agent->PendingResultFlags |= PointMenuPendingResultFlags.HasPendingResult;
            pushAgentResultToLua(agent);
            HidePointMenu(agent);
            agent->PendingResultFlags &= ~PointMenuPendingResultFlags.HasPendingResult;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Skip Cutscenes PointMenu handling failed.");
        }
    }

    private bool IsEffectivelyEnabled(CutsceneSkipCategory category)
    {
        if (!enabled || startupArmingPending || !clientState.IsLoggedIn)
            return false;

        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
            return false;

        if (!IsZoneAllowed(clientState.TerritoryType))
            return false;

        return category switch
        {
            CutsceneSkipCategory.Msq => IsMsqCategoryAllowed(),
            CutsceneSkipCategory.MassivePc => skipMassivePc,
            CutsceneSkipCategory.GoldSaucer => IsGoldSaucerCategoryAllowed(),
            CutsceneSkipCategory.CustomTalk => skipCustomTalk,
            CutsceneSkipCategory.NormalCutscenes => IsNormalCutsceneCategoryAllowed(),
            CutsceneSkipCategory.Inn => skipInn,
            CutsceneSkipCategory.FashionReport => IsFashionReportCategoryAllowed(),
            _ => IsGenericCutsceneCategoryAllowed(),
        };
    }

    private bool IsZoneAllowed(uint territoryId)
    {
        if (territoryId == 0)
            return true;

        return useZoneWhitelist
            ? whitelistTerritories.Contains(territoryId)
            : !blacklistTerritories.Contains(territoryId);
    }

    private bool IsCurrentCategoryAllowed()
    {
        var territoryId = (ushort)clientState.TerritoryType;
        var intendedUse = GetCurrentTerritoryIntendedUse();

        if (IsMsqTerritory(territoryId))
            return IsMsqTerritoryAllowed(territoryId);

        if (intendedUse == TerritoryIntendedUse.OceanFishing)
            return skipOceanFishing;

        if (IsCrystallineConflict(intendedUse))
            return skipCrystallineConflict;

        if (IsFrontlineOrRivalWings(intendedUse))
            return skipFrontlineRivalWings;

        if (intendedUse == TerritoryIntendedUse.Inn)
            return skipInn;

        if (IsGoldSaucerTerritoryOrUse(territoryId, intendedUse))
            return IsGoldSaucerCategoryAllowed();

        return skipNormalCutscenes || skipCustomTalk || skipMassivePc;
    }

    private bool IsGenericCutsceneCategoryAllowed()
    {
        var territoryId = (ushort)clientState.TerritoryType;
        var intendedUse = GetCurrentTerritoryIntendedUse();

        if (IsMsqTerritory(territoryId))
            return IsMsqTerritoryAllowed(territoryId);

        if (intendedUse == TerritoryIntendedUse.OceanFishing)
            return skipOceanFishing;

        if (IsCrystallineConflict(intendedUse))
            return skipCrystallineConflict;

        if (IsFrontlineOrRivalWings(intendedUse))
            return skipFrontlineRivalWings;

        if (intendedUse == TerritoryIntendedUse.Inn)
            return skipInn;

        if (IsGoldSaucerTerritoryOrUse(territoryId, intendedUse))
            return IsGoldSaucerCategoryAllowed();

        return skipNormalCutscenes;
    }

    private bool IsMsqCategoryAllowed()
    {
        var territoryId = (ushort)clientState.TerritoryType;
        var intendedUse = GetCurrentTerritoryIntendedUse();
        if (intendedUse == TerritoryIntendedUse.OceanFishing)
            return skipOceanFishing;

        if (IsCrystallineConflict(intendedUse))
            return skipCrystallineConflict;

        if (IsFrontlineOrRivalWings(intendedUse))
            return skipFrontlineRivalWings;

        return IsMsqTerritoryAllowed(territoryId);
    }

    private bool IsMsqTerritoryAllowed(ushort territoryId)
    {
        var skipMsq = skipMsqRoulette || msqAutoPartyActive;
        var inPraetorium = PraetoriumTerritoryIds.Contains(territoryId);
        var inCastrum = CastrumTerritoryIds.Contains(territoryId);
        var inPorta = PortaDecumanaTerritoryIds.Contains(territoryId);

        if (!skipMsq)
            return (inPraetorium && exemptPraetorium)
                || (inCastrum && exemptCastrum)
                || (inPorta && exemptPortaDecumana);

        if (inPraetorium && exemptPraetorium)
            return false;

        if (inCastrum && exemptCastrum)
            return false;

        if (inPorta && exemptPortaDecumana)
            return false;

        return inPraetorium || inCastrum || inPorta;
    }

    private bool IsNormalCutsceneCategoryAllowed()
    {
        return skipNormalCutscenes && IsGenericCutsceneCategoryAllowed();
    }

    private bool IsGoldSaucerCategoryAllowed()
    {
        var territoryId = (ushort)clientState.TerritoryType;
        var intendedUse = GetCurrentTerritoryIntendedUse();

        if (!IsGoldSaucerTerritoryOrUse(territoryId, intendedUse))
            return false;

        if (IsMahjong(territoryId, intendedUse))
            return ShouldSkipGoldSaucerMode(goldSaucerMahjong);

        if (IsAirForceOne(territoryId, intendedUse))
            return ShouldSkipGoldSaucerMode(goldSaucerAirForceOne);

        if (intendedUse == TerritoryIntendedUse.ChocoboRacing)
            return ShouldSkipGoldSaucerMode(goldSaucerChocoboRacing);

        if (intendedUse == TerritoryIntendedUse.LordOfVerminion)
            return ShouldSkipGoldSaucerMode(goldSaucerLordOfVerminion);

        if (IsTripleTriad(intendedUse))
            return ShouldSkipGoldSaucerMode(goldSaucerTripleTriad);

        if (intendedUse == TerritoryIntendedUse.Blunderville)
            return ShouldSkipGoldSaucerMode(goldSaucerBlunderville);

        if (IsFashionReportTerritory(territoryId, intendedUse))
            return ShouldSkipGoldSaucerMode(goldSaucerFashionReport);

        return skipGoldSaucer;
    }

    private bool IsFashionReportCategoryAllowed()
    {
        var territoryId = (ushort)clientState.TerritoryType;
        var intendedUse = GetCurrentTerritoryIntendedUse();
        return IsFashionReportTerritory(territoryId, intendedUse)
            && ShouldSkipGoldSaucerMode(goldSaucerFashionReport);
    }

    private bool ShouldSkipGoldSaucerMode(bool modeToggle)
    {
        return skipGoldSaucer ? !modeToggle : modeToggle;
    }

    private bool ShouldArmMsqHook()
    {
        return skipMsqRoulette
            || autoEnableMsqFourPlayer
            || exemptPraetorium
            || exemptCastrum
            || exemptPortaDecumana
            || skipOceanFishing
            || skipCrystallineConflict
            || skipFrontlineRivalWings;
    }

    private bool ShouldArmGoldSaucerHook()
    {
        return skipGoldSaucer
            || goldSaucerMahjong
            || goldSaucerAirForceOne
            || goldSaucerChocoboRacing
            || goldSaucerLordOfVerminion
            || goldSaucerTripleTriad
            || goldSaucerBlunderville
            || goldSaucerFashionReport;
    }

    private string GetInactiveReason()
    {
        if (!clientState.IsLoggedIn)
            return "not logged in";

        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
            return "between areas";

        if (!IsZoneAllowed(clientState.TerritoryType))
            return useZoneWhitelist ? "current territory is not whitelisted" : "current territory is blacklisted";

        if (!IsCurrentCategoryAllowed())
            return "no enabled category matches the current territory";

        return string.Empty;
    }

    private string GetCurrentCategoryLabel()
    {
        var territoryId = (ushort)clientState.TerritoryType;
        var intendedUse = GetCurrentTerritoryIntendedUse();

        if (IsMsqTerritory(territoryId))
            return "MSQ duty";

        if (intendedUse == TerritoryIntendedUse.OceanFishing)
            return "Ocean Fishing";

        if (IsCrystallineConflict(intendedUse))
            return "Crystalline Conflict";

        if (IsFrontlineOrRivalWings(intendedUse))
            return "Frontline/Rival Wings";

        if (intendedUse == TerritoryIntendedUse.Inn)
            return "Inn";

        if (IsMahjong(territoryId, intendedUse))
            return "Gold Saucer - Mahjong";

        if (IsAirForceOne(territoryId, intendedUse))
            return "Gold Saucer - Air Force One";

        if (intendedUse == TerritoryIntendedUse.ChocoboRacing)
            return "Gold Saucer - Chocobo Racing";

        if (intendedUse == TerritoryIntendedUse.LordOfVerminion)
            return "Gold Saucer - Lord of Verminion";

        if (IsTripleTriad(intendedUse))
            return "Gold Saucer - Triple Triad";

        if (intendedUse == TerritoryIntendedUse.Blunderville)
            return "Gold Saucer - Blunderville";

        if (IsFashionReportTerritory(territoryId, intendedUse))
            return "Gold Saucer - Fashion Report / main area";

        if (IsGoldSaucerTerritoryOrUse(territoryId, intendedUse))
            return "Gold Saucer";

        return intendedUse == 0 ? "Normal/unknown" : $"Normal/other ({intendedUse})";
    }

    private TerritoryIntendedUse GetCurrentTerritoryIntendedUse()
    {
        try
        {
            var gameMain = GameMain.Instance();
            if (gameMain != null)
                return gameMain->CurrentTerritoryIntendedUseId;
        }
        catch
        {
            // Fall back to Lumina below.
        }

        try
        {
            var row = dataManager.GetExcelSheet<TerritoryType>()?.GetRowOrDefault(clientState.TerritoryType);
            var intendedUseId = row?.TerritoryIntendedUse.RowId ?? 0;
            return (TerritoryIntendedUse)intendedUseId;
        }
        catch
        {
            return 0;
        }
    }

    private void TryHandleFashionReportAddons()
    {
        if (!IsEffectivelyEnabled(CutsceneSkipCategory.FashionReport))
            return;

        var now = DateTime.UtcNow;
        if ((now - lastFashionReportAttemptUtc).TotalMilliseconds < 750)
            return;

        if (AddonHelper.IsAddonReady("FashionCheck"))
        {
            lastFashionReportAttemptUtc = now;
            AddonHelper.CloseAddon("FashionCheck");
            return;
        }

        if (AddonHelper.IsAddonReady("FashionCheckScoreGauge"))
        {
            lastFashionReportAttemptUtc = now;
            AddonHelper.CloseAddon("FashionCheckScoreGauge");
            return;
        }

        if (AddonHelper.IsAddonReady("SelectYesno") && SelectYesnoLooksLikeFashionReport())
        {
            lastFashionReportAttemptUtc = now;
            AddonHelper.ClickYesNo(true);
            return;
        }

        if (AddonHelper.IsAddonReady("SelectString") && SelectStringLooksLikeFashionReport())
        {
            lastFashionReportAttemptUtc = now;
            AddonHelper.FireCallbackAndClose("SelectString", 1);
        }
    }

    private static bool SelectYesnoLooksLikeFashionReport()
    {
        return AddonHelper.GetAddonTextEntries("SelectYesno").Any(text =>
            text.Contains("fashion", StringComparison.OrdinalIgnoreCase)
            || text.Contains("judg", StringComparison.OrdinalIgnoreCase)
            || text.Contains("present yourself", StringComparison.OrdinalIgnoreCase));
    }

    private static bool SelectStringLooksLikeFashionReport()
    {
        return AddonHelper.GetAddonTextEntries("SelectString").Any(text =>
            text.Contains("Present yourself", StringComparison.OrdinalIgnoreCase)
            || text.Contains("judg", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsMsqTerritory(ushort territoryId)
    {
        return PraetoriumTerritoryIds.Contains(territoryId)
            || CastrumTerritoryIds.Contains(territoryId)
            || PortaDecumanaTerritoryIds.Contains(territoryId);
    }

    private static bool IsCrystallineConflict(TerritoryIntendedUse intendedUse)
    {
        return intendedUse is TerritoryIntendedUse.CrystallineConflict or TerritoryIntendedUse.CrystallineConflictCustomMatch;
    }

    private static bool IsFrontlineOrRivalWings(TerritoryIntendedUse intendedUse)
    {
        return intendedUse is TerritoryIntendedUse.Frontline or TerritoryIntendedUse.RivalWings;
    }

    private bool IsGoldSaucerTerritoryOrUse(ushort territoryId, TerritoryIntendedUse intendedUse)
    {
        return territoryId == GoldSaucerTerritoryId
            || territoryId == MahjongTerritoryId
            || intendedUse is TerritoryIntendedUse.ChocoboSquareOld
                or TerritoryIntendedUse.ChocoboRacing
                or TerritoryIntendedUse.GoldSaucer
                or TerritoryIntendedUse.OriginalStepsOfFaith
                or TerritoryIntendedUse.LordOfVerminion
                or TerritoryIntendedUse.TripleTriadBattlehall
                or TerritoryIntendedUse.LeapOfFaith
                or TerritoryIntendedUse.TripleTriadOpenTournament
                or TerritoryIntendedUse.TripleTriadInvitationalParlor
                or TerritoryIntendedUse.Blunderville
            || IsAirForceOne(territoryId, intendedUse);
    }

    private bool IsFashionReportTerritory(ushort territoryId, TerritoryIntendedUse intendedUse)
    {
        return territoryId == GoldSaucerTerritoryId || intendedUse == TerritoryIntendedUse.GoldSaucer;
    }

    private static bool IsMahjong(ushort territoryId, TerritoryIntendedUse intendedUse)
    {
        return territoryId == MahjongTerritoryId;
    }

    private bool IsAirForceOne(ushort territoryId, TerritoryIntendedUse intendedUse)
    {
        return string.Equals(intendedUse.ToString(), "AirForceOne", StringComparison.OrdinalIgnoreCase)
            || GetTerritoryName(territoryId).Contains("Air Force One", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTripleTriad(TerritoryIntendedUse intendedUse)
    {
        return intendedUse is TerritoryIntendedUse.TripleTriadBattlehall
            or TerritoryIntendedUse.TripleTriadOpenTournament
            or TerritoryIntendedUse.TripleTriadInvitationalParlor;
    }

    private static HashSet<uint> NormalizeTerritorySet(IEnumerable<uint>? territories)
    {
        return territories?
            .Where(territory => territory > 0)
            .Distinct()
            .ToHashSet()
            ?? new HashSet<uint>();
    }

    private byte CutsceneHandleInputDetour(nint a1, float a2)
    {
        try
        {
            if (IsEffectivelyEnabled(CutsceneSkipCategory.Generic) && condition[ConditionFlag.OccupiedInCutSceneEvent] && *(ulong*)(a1 + 56) != 0)
                ApplyCutsceneUnskippablePatch();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Skip Cutscenes input detour failed before original call.");
        }

        return cutsceneHandleInputHook?.Original(a1, a2) ?? 0;
    }

    private nint PlayCutsceneDetour(EventFramework* eventFramework, lua_State* state)
    {
        if (IsEffectivelyEnabled(CutsceneSkipCategory.Generic))
            return 1;

        return playCutsceneHook?.Original(eventFramework, state) ?? 0;
    }

    private ulong PlayCutsceneLuaDetour(lua_State* state)
    {
        if (!IsEffectivelyEnabled(CutsceneSkipCategory.Generic))
            return playCutsceneLuaHook?.Original(state) ?? 0;

        if (state != null)
        {
            state->top->tt = 2;
            state->top->value.n = 1;
            state->top += 1;
        }

        return 1;
    }

    private ulong PlayStaffRollDetour(lua_State* state)
    {
        if (IsEffectivelyEnabled(CutsceneSkipCategory.Generic))
            return 1;

        return playStaffRollHook?.Original(state) ?? 0;
    }

    private ulong PlayToBeContinuedDetour(lua_State* state)
    {
        if (IsEffectivelyEnabled(CutsceneSkipCategory.Generic))
            return 1;

        return playToBeContinuedHook?.Original(state) ?? 0;
    }

    private bool IsCutsceneSeenDetour(UIState* state, uint cutsceneId)
    {
        if (IsEffectivelyEnabled(CutsceneSkipCategory.Generic))
            return true;

        return isCutsceneSeenHook?.Original(state, cutsceneId) ?? true;
    }

    private long MsqContentDirectorDetour(nint luaState)
    {
        if (IsEffectivelyEnabled(CutsceneSkipCategory.Msq)
            || IsEffectivelyEnabled(CutsceneSkipCategory.GoldSaucer))
            return 1;

        return msqContentDirectorHook?.Original(luaState) ?? 0;
    }

    private long MassivePcContentDirectorDetour(nint luaState)
    {
        if (IsEffectivelyEnabled(CutsceneSkipCategory.MassivePc))
            return 1;

        return massivePcContentDirectorHook?.Original(luaState) ?? 0;
    }

    private long GoldSaucerContentDirectorDetour(nint luaState)
    {
        if (IsEffectivelyEnabled(CutsceneSkipCategory.GoldSaucer))
            return 1;

        return goldSaucerContentDirectorHook?.Original(luaState) ?? 0;
    }

    private long CustomTalkContentDirectorDetour(nint luaState)
    {
        if (IsEffectivelyEnabled(CutsceneSkipCategory.CustomTalk))
            return 1;

        return customTalkContentDirectorHook?.Original(luaState) ?? 0;
    }

    private long NormalCutscenesDetour(nint luaState1, nint luaState2)
    {
        if (IsEffectivelyEnabled(CutsceneSkipCategory.NormalCutscenes))
            return 1;

        return normalCutscenesHook?.Original(luaState1, luaState2) ?? 0;
    }

    private long InnContentDirectorDetour(nint luaState)
    {
        if (IsEffectivelyEnabled(CutsceneSkipCategory.Inn))
            return 1;

        return innContentDirectorHook?.Original(luaState) ?? 0;
    }

    private delegate byte CutsceneHandleInputDelegate(nint a1, float a2);

    private delegate nint PlayCutsceneDelegate(EventFramework* eventFramework, lua_State* state);

    private delegate ulong LuaFunctionDelegate(lua_State* state);

    private delegate bool IsCutsceneSeenDelegate(UIState* state, uint cutsceneId);

    private delegate void PushAgentResultToLuaDelegate(void* agent);

    private delegate long ContentDirectorDelegate(nint luaState);

    private delegate long NormalCutscenesDelegate(nint luaState1, nint luaState2);

    private static void HidePointMenu(PointMenuAgent* agent)
    {
        if (agent != null)
            ((ClientAgentInterface*)agent)->Hide();
    }

    [Flags]
    private enum PointMenuPendingResultFlags : byte
    {
        None = 0,
        HasPendingResult = 2,
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private unsafe struct PointMenuAgent
    {
        [FieldOffset(36)]
        public PointMenuPendingResultFlags PendingResultFlags;

        [FieldOffset(40)]
        public PointMenuContext* Context;

        [FieldOffset(48)]
        public nint CompletionTreeRoot;

        [FieldOffset(60)]
        public int SelectedIndex;

        public readonly int EntryCount
        {
            get
            {
                if (Context == null)
                    return 0;

                var byteCount = (long)(nint)Context->EntriesEnd - (long)(nint)Context->Entries;
                return byteCount <= 0 ? 0 : (int)(byteCount / sizeof(PointMenuEntry));
            }
        }

        public readonly int FindFirstUncompletedEntry()
        {
            if (CompletionTreeRoot == nint.Zero)
                return 0;

            if (Context == null)
                return -1;

            var root = *(PointMenuCompletionNode**)CompletionTreeRoot;
            if (root == null)
                return -1;

            var key = Context->CompletionKey;
            var current = root->Right;
            var candidate = root;

            while (current != null && current->IsSentinel == 0)
            {
                if (current->Key >= key)
                {
                    candidate = current;
                    current = current->Left;
                }
                else
                {
                    current = current->Right;
                }
            }

            if (candidate->IsSentinel != 0 || key < candidate->Key || candidate == root)
                return -1;

            var completedBitfield = candidate->CompletedBitfield;
            var entryCount = EntryCount;
            for (var index = 0; index < entryCount; index++)
            {
                if ((completedBitfield & (1 << (index & 31))) == 0)
                    return index;
            }

            return -1;
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 320)]
    private unsafe struct PointMenuContext
    {
        [FieldOffset(288)]
        public PointMenuEntry* Entries;

        [FieldOffset(296)]
        public PointMenuEntry* EntriesEnd;

        [FieldOffset(312)]
        public int CompletionKey;
    }

    [StructLayout(LayoutKind.Explicit, Size = 136)]
    private struct PointMenuEntry
    {
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private unsafe struct PointMenuCompletionNode
    {
        [FieldOffset(0)]
        public PointMenuCompletionNode* Left;

        [FieldOffset(8)]
        public PointMenuCompletionNode* Right;

        [FieldOffset(25)]
        public byte IsSentinel;

        [FieldOffset(28)]
        public int Key;

        [FieldOffset(32)]
        public int CompletedBitfield;
    }
}
