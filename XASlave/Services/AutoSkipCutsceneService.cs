using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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
using ClientAgentPointMenu = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentPointMenu;
using DalamudAgentId = Dalamud.Game.Agent.AgentId;
using TerritoryIntendedUse = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;

namespace XASlave.Services;

public unsafe sealed class AutoSkipCutsceneService : IDisposable
{
    private const ushort GoldSaucerTerritoryId = 144;
    private const ushort MahjongTerritoryId = 831;
    private const int PointMenuResultEvent = 12;
    private const bool PointMenuApiAvailable = true;
    private const string MsqContentDirectorLabel = "MSQ/Gold Saucer/Ocean/PvP content director";
    private const string MassivePcContentDirectorLabel = "Massive PC content director";
    private const string CustomTalkContentDirectorLabel = "Custom Talk content director";
    private const string NormalCutscenesLabel = "Normal cutscenes";
    private const string InnContentDirectorLabel = "Inn content director";

    private static readonly ushort[] PraetoriumTerritoryIds = [1044, 1045];
    private static readonly ushort[] CastrumTerritoryIds = [1043];
    private static readonly ushort[] PortaDecumanaTerritoryIds = [1046];

    // const strings leak through metadata; runtime initializers allow Obfuscar string hiding.
    private static readonly string MsqContentDirectorSig = "48 89 5C 24 ?? 57 48 83 EC 50 48 8B D1 48 8D 4C 24 ?? E8 ?? ?? ?? ?? 48 8B 4C 24 ?? BA ?? ?? ?? ?? B3 01 E8 ?? ?? ?? ?? BA ?? ?? ?? ?? 48 8D 4C 24 ?? 48 8B F8 E8 ?? ?? ?? ?? 48 8B 4C 24 ?? 4C 8B C0 BA ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B 08 84 99 ?? ?? ?? ??";
    private static readonly string MassivePcContentDirectorSig = "48 89 5C 24 ?? 57 48 83 EC 50 48 8B D1 48 8D 4C 24 ?? E8 ?? ?? ?? ?? 48 8B 4C 24 ?? BA ?? ?? ?? ?? B3 01 E8 ?? ?? ?? ?? BA ?? ?? ?? ?? 48 8D 4C 24 ?? 48 8B F8 E8 ?? ?? ?? ?? 48 8B 4C 24 ?? 4C 8B C0 BA ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B 08 48 8B 11";
    private static readonly string CustomTalkContentDirectorSig = "48 83 EC 58 48 8B D1 48 8D 4C 24 ?? E8 ?? ?? ?? ?? BA ?? ?? ?? ?? 48 8D 4C 24 ?? E8 ?? ?? ?? ?? 48 8B 4C 24 ?? 4C 8B C0 BA ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B 08 48 85 C9 74 06";
    private static readonly string NormalCutscenesSig = "40 53 55 57 41 56 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B 59 08";
    private static readonly string InnContentDirectorSig = "48 83 EC 58 48 8B D1 48 8D 4C 24 ?? E8 ?? ?? ?? ?? BA ?? ?? ?? ?? 48 8D 4C 24 ?? E8 ?? ?? ?? ?? 48 8B 4C 24 ?? 4C 8B C0 BA ?? ?? ?? ?? E8 ?? ?? ?? ?? BA ?? ?? ?? ?? 48 8D 4C 24 ?? E8 ?? ?? ?? ?? 48 8B 4C 24 ??";

    private readonly ICondition condition;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IDataManager dataManager;
    private readonly IPartyList partyList;
    private readonly ISigScanner sigScanner;
    private readonly IGameInteropProvider interopProvider;
    private readonly IAgentLifecycle agentLifecycle;
    private readonly IPluginLog log;
    private readonly object startupArmingLock = new();

    private Hook<CutsceneHandleInputDelegate>? cutsceneHandleInputHook;
    private Hook<PlayCutsceneDelegate>? playCutsceneHook;
    private Hook<LuaFunctionDelegate>? playCutsceneLuaHook;
    private Hook<IsCutsceneSeenDelegate>? isCutsceneSeenHook;
    private Hook<LuaFunctionDelegate>? playStaffRollHook;
    private Hook<LuaFunctionDelegate>? playToBeContinuedHook;
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
    private bool disposed;
    private bool msqAutoPartyActive;
    private int availableSurfaceCount;
    private int availableOptionalSurfaceCount;
    private DateTime lastPromptAttemptUtc = DateTime.MinValue;
    private DateTime lastPromptResolverWarningUtc = DateTime.MinValue;
    private DateTime lastFashionReportAttemptUtc = DateTime.MinValue;
    private readonly HashSet<string> unavailableOptionalHooks = new(StringComparer.OrdinalIgnoreCase);
    private System.Threading.Tasks.Task<StartupHookResult>? startupHookTask;
    private System.Threading.CancellationTokenSource? startupHookCancellation;

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
        availableSurfaceCount = 0;
        availableOptionalSurfaceCount = 0;
        StatusText = "Arming - cutscene hook surfaces are initializing on the framework thread.";
        StartStartupHookCreation();
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

        if (value && startupArmingPending)
            return true;

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
        EnsureInitializedForEnabledState(retryMissing: initialized);
        RefreshStatusText();
        return enabled;
    }

    public void Dispose()
    {
        disposed = true;
        CancelStartupArming(disposeCompletedResult: true);
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
        CancelStartupArming(disposeCompletedResult: true);
        enabled = false;
        startupArmingPending = false;
        msqAutoPartyActive = false;
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

    private void EnsureInitialized(bool retryMissing = false)
    {
        if (initialized && !retryMissing)
            return;

        if (retryMissing)
            unavailableOptionalHooks.Clear();

        cutsceneHandleInputHook ??= TryCreateHook<CutsceneHandleInputDelegate>(Sigs.CutsceneHandleInputSig, CutsceneHandleInputDetour, "CutsceneHandleInput");
        playCutsceneHook ??= TryCreateHook<PlayCutsceneDelegate>(Sigs.PlayCutsceneSig, PlayCutsceneDetour, "PlayCutscene");
        playCutsceneLuaHook ??= TryCreateLuaFunctionHook<LuaFunctionDelegate>(Sigs.LuaBaseSig01, "PlayCutScene", PlayCutsceneLuaDetour, "PlayCutsceneLua");
        isCutsceneSeenHook ??= TryCreateHook<IsCutsceneSeenDelegate>(Sigs.IsCutsceneSeenSig, IsCutsceneSeenDetour, "IsCutsceneSeen");
        playStaffRollHook ??= TryCreateLuaFunctionHook<LuaFunctionDelegate>(Sigs.LuaBaseSig02, "PlayStaffRoll", PlayStaffRollDetour, "PlayStaffRoll");
        playToBeContinuedHook ??= TryCreateLuaFunctionHook<LuaFunctionDelegate>(Sigs.LuaBaseSig02, "PlayToBeContinued", PlayToBeContinuedDetour, "PlayToBeContinued");
        if (cutsceneUnskippablePatchAddress == nint.Zero
            && (!sigScanner.TryScanText(Sigs.CutsceneUnskippablePatchSig, out cutsceneUnskippablePatchAddress)
                || cutsceneUnskippablePatchAddress == nint.Zero))
        {
            log.Warning("[XASlave] Auto Skip Cutscenes could not find the unskippable cutscene patch signature; retry by disabling and re-enabling the feature.");
        }

        initialized = true;
    }

    private StartupHookResult CreateStartupHookResult()
    {
        var patchAddress = nint.Zero;
        if (!sigScanner.TryScanText(Sigs.CutsceneUnskippablePatchSig, out patchAddress) || patchAddress == nint.Zero)
            log.Warning("[XASlave] Auto Skip Cutscenes could not find the unskippable cutscene patch signature; retry by disabling and re-enabling the feature.");

        var armMsqContentDirector = ShouldArmMsqHook() || ShouldArmGoldSaucerHook();
        var armMassivePcContentDirector = skipMassivePc;
        var armCustomTalkContentDirector = skipCustomTalk;
        var armNormalCutscenes = skipNormalCutscenes;
        var armInnContentDirector = skipInn;

        return new StartupHookResult(
            TryCreateHook<CutsceneHandleInputDelegate>(Sigs.CutsceneHandleInputSig, CutsceneHandleInputDetour, "CutsceneHandleInput"),
            TryCreateHook<PlayCutsceneDelegate>(Sigs.PlayCutsceneSig, PlayCutsceneDetour, "PlayCutscene"),
            TryCreateLuaFunctionHook<LuaFunctionDelegate>(Sigs.LuaBaseSig01, "PlayCutScene", PlayCutsceneLuaDetour, "PlayCutsceneLua"),
            TryCreateHook<IsCutsceneSeenDelegate>(Sigs.IsCutsceneSeenSig, IsCutsceneSeenDetour, "IsCutsceneSeen"),
            TryCreateLuaFunctionHook<LuaFunctionDelegate>(Sigs.LuaBaseSig02, "PlayStaffRoll", PlayStaffRollDetour, "PlayStaffRoll"),
            TryCreateLuaFunctionHook<LuaFunctionDelegate>(Sigs.LuaBaseSig02, "PlayToBeContinued", PlayToBeContinuedDetour, "PlayToBeContinued"),
            patchAddress,
            armMsqContentDirector,
            armMsqContentDirector ? TryCreateContentDirectorHook(MsqContentDirectorSig, MsqContentDirectorDetour, MsqContentDirectorLabel) : null,
            armMassivePcContentDirector,
            armMassivePcContentDirector ? TryCreateContentDirectorHook(MassivePcContentDirectorSig, MassivePcContentDirectorDetour, MassivePcContentDirectorLabel) : null,
            armCustomTalkContentDirector,
            armCustomTalkContentDirector ? TryCreateContentDirectorHook(CustomTalkContentDirectorSig, CustomTalkContentDirectorDetour, CustomTalkContentDirectorLabel) : null,
            armNormalCutscenes,
            armNormalCutscenes ? TryCreateNormalCutscenesHook() : null,
            armInnContentDirector,
            armInnContentDirector ? TryCreateContentDirectorHook(InnContentDirectorSig, InnContentDirectorDetour, InnContentDirectorLabel) : null);
    }

    private void EnsureInitializedForEnabledState(bool retryMissing = false)
    {
        EnsureInitialized(retryMissing);
        if (!HasAnyCutsceneSurface())
        {
            enabled = false;
            UnsubscribeFramework();
            UpdateClientStateSubscriptions(false);
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
            enabled = false;
            UnsubscribeFramework();
            UpdateClientStateSubscriptions(false);
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
            StatusText = "Arming - cutscene hook surfaces are initializing on the framework thread.";
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

        var availabilityLabel = HasMissingConfiguredSurface() ? "Partially enabled" : "Enabled";
        var inactiveReason = GetInactiveReason();
        if (!string.IsNullOrWhiteSpace(inactiveReason))
        {
            StatusText = $"{availabilityLabel} but inactive here ({CurrentTerritoryId} {CurrentTerritoryName}, {CurrentCategoryLabel}) - {inactiveReason}. {surfaceCount} cutscene surfaces available.";
            return;
        }

        StatusText = $"{availabilityLabel} ({surfaceCount} cutscene surfaces available; current: {CurrentTerritoryId} {CurrentTerritoryName}, {CurrentCategoryLabel}).";
    }

    private bool HasMissingConfiguredSurface()
        => cutsceneHandleInputHook == null
            || playCutsceneHook == null
            || playCutsceneLuaHook == null
            || isCutsceneSeenHook == null
            || playStaffRollHook == null
            || playToBeContinuedHook == null
            || cutsceneUnskippablePatchAddress == nint.Zero
            || unavailableOptionalHooks.Count > 0;

    private Hook<T>? TryCreateHook<T>(ProtectedSig signature, T detour, string label)
        where T : Delegate
    {
        try
        {
            if (!sigScanner.TryScanText(signature, out var address) || address == nint.Zero)
            {
                log.Warning($"[XASlave] Auto Skip Cutscenes could not resolve {label}; retry by disabling and re-enabling the feature.");
                return null;
            }

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
            {
                log.Warning($"[XASlave] Auto Skip Cutscenes could not resolve the {label} Lua base; retry by disabling and re-enabling the feature.");
                return null;
            }

            var functionAddress = GetLuaFunctionByName(baseAddress, functionName);
            if (functionAddress == nint.Zero)
            {
                log.Warning($"[XASlave] Auto Skip Cutscenes could not validate {label}'s computed Lua function address; retry by disabling and re-enabling the feature.");
                return null;
            }

            return interopProvider.HookFromAddress(functionAddress, detour);
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[XASlave] Auto Skip Cutscenes failed to create {label} Lua hook.");
            return null;
        }
    }

    private nint GetLuaFunctionByName(nint luaSetupFunctionStartAddress, string functionName, int scanSize = 8192)
    {
        if (luaSetupFunctionStartAddress == nint.Zero || string.IsNullOrEmpty(functionName) || scanSize <= 0)
            return nint.Zero;

        var textBase = sigScanner.Module.BaseAddress + (sigScanner.TextSectionBase - sigScanner.SearchBase);
        if (!NativeAddressPolicy.TryGetBoundedLength(luaSetupFunctionStartAddress, textBase, sigScanner.TextSectionSize, scanSize, out var boundedScanSize)
            || boundedScanSize < 7)
            return nint.Zero;

        var functionBytes = new byte[boundedScanSize];

        try
        {
            Marshal.Copy(luaSetupFunctionStartAddress, functionBytes, 0, boundedScanSize);
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
            if (!IsExactAsciiStringAt((nint)stringAddress, functionName))
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
            var functionAddress = (nint)(nextInstructionAddress + displacement);
            return NativeAddressPolicy.IsRangeValid(functionAddress, textBase, sigScanner.TextSectionSize, 1)
                ? functionAddress
                : nint.Zero;
        }

        return nint.Zero;
    }

    private bool IsExactAsciiStringAt(nint address, string expected)
    {
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        if (!NativeAddressPolicy.IsRangeValid(address, sigScanner.Module.BaseAddress, sigScanner.Module.ModuleMemorySize, expectedBytes.Length + 1))
            return false;

        var actual = new byte[expectedBytes.Length + 1];
        try
        {
            Marshal.Copy(address, actual, 0, actual.Length);
        }
        catch
        {
            return false;
        }

        return actual[^1] == 0 && actual.AsSpan(0, expectedBytes.Length).SequenceEqual(expectedBytes);
    }

    private Hook<ContentDirectorDelegate>? TryCreateContentDirectorHook(string signature, ContentDirectorDelegate detour, string label)
    {
        try
        {
            return interopProvider.HookFromSignature(signature, detour);
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[XASlave] Auto Skip Cutscenes could not create optional {label} hook.");
            return null;
        }
    }

    private Hook<NormalCutscenesDelegate>? TryCreateNormalCutscenesHook()
    {
        try
        {
            return interopProvider.HookFromSignature<NormalCutscenesDelegate>(NormalCutscenesSig, NormalCutscenesDetour);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Skip Cutscenes could not create optional Normal cutscenes hook.");
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
            || PointMenuApiAvailable
            || cutsceneUnskippablePatchAddress != nint.Zero;
    }

    private int UpdatePointMenuAgentSubscription(bool targetEnabled)
    {
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
        availableOptionalSurfaceCount += RefreshContentDirectorHook(ref msqContentDirectorHook, MsqContentDirectorSig, ShouldArmMsqHook() || ShouldArmGoldSaucerHook(), MsqContentDirectorDetour, MsqContentDirectorLabel);
        availableOptionalSurfaceCount += RefreshContentDirectorHook(ref massivePcContentDirectorHook, MassivePcContentDirectorSig, skipMassivePc, MassivePcContentDirectorDetour, MassivePcContentDirectorLabel);
        ToggleHook(goldSaucerContentDirectorHook, false, "Gold Saucer content director");
        availableOptionalSurfaceCount += RefreshContentDirectorHook(ref customTalkContentDirectorHook, CustomTalkContentDirectorSig, skipCustomTalk, CustomTalkContentDirectorDetour, CustomTalkContentDirectorLabel);
        availableOptionalSurfaceCount += RefreshNormalCutscenesHook();
        availableOptionalSurfaceCount += RefreshContentDirectorHook(ref innContentDirectorHook, InnContentDirectorSig, skipInn, InnContentDirectorDetour, InnContentDirectorLabel);
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

        const string label = NormalCutscenesLabel;
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

    private static void DisposeHook<T>(Hook<T>? hook)
        where T : Delegate
    {
        if (hook is { IsDisposed: false })
            hook.Dispose();
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
        {
            log.Warning("[XASlave] Auto Skip Cutscenes failed to restore the unskippable cutscene branch byte.");
            return;
        }

        cutsceneUnskippablePatchApplied = false;
    }

    private void StartStartupHookCreation()
    {
        lock (startupArmingLock)
        {
            if (startupHookTask != null)
                return;

            startupHookCancellation?.Dispose();
            startupHookCancellation = new System.Threading.CancellationTokenSource();
            startupHookTask = Plugin.RunOnGameThread(
                CreateStartupHookResult,
                "Auto Skip Cutscenes startup hook creation",
                startupHookCancellation.Token);
        }
    }

    private void CancelStartupArming(bool disposeCompletedResult)
    {
        System.Threading.Tasks.Task<StartupHookResult>? task;
        System.Threading.CancellationTokenSource? cancellation;
        lock (startupArmingLock)
        {
            startupArmingPending = false;
            task = startupHookTask;
            startupHookTask = null;
            cancellation = startupHookCancellation;
            startupHookCancellation = null;
        }

        cancellation?.Cancel();
        cancellation?.Dispose();

        if (!disposeCompletedResult || task == null)
            return;

        DisposeStartupHookTaskResult(task);
    }

    private static void DisposeStartupHookTaskResult(System.Threading.Tasks.Task<StartupHookResult> task)
    {
        if (task.IsCompleted)
        {
            if (task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                _ = Plugin.RunOnGameThread(task.Result.DisposeHooks, "Dispose cancelled Auto Skip Cutscenes startup hooks");
            return;
        }

        task.ContinueWith(
            completedTask =>
            {
                if (completedTask.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                    _ = Plugin.RunOnGameThread(completedTask.Result.DisposeHooks, "Dispose cancelled Auto Skip Cutscenes startup hooks");
            },
            System.Threading.Tasks.TaskScheduler.Default);
    }

    private void ProcessStartupArmingTask()
    {
        System.Threading.Tasks.Task<StartupHookResult>? task;
        lock (startupArmingLock)
            task = startupHookTask;

        if (task == null)
        {
            CancelStartupArming(disposeCompletedResult: false);
            return;
        }

        if (!task.IsCompleted)
            return;

        StartupHookResult? result = null;
        try
        {
            if (task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                result = task.Result;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Skip Cutscenes startup hook initialization failed.");
        }

        System.Threading.CancellationTokenSource? completedCancellation;
        lock (startupArmingLock)
        {
            startupHookTask = null;
            startupArmingPending = false;
            completedCancellation = startupHookCancellation;
            startupHookCancellation = null;
        }
        completedCancellation?.Dispose();

        initialized = true;

        if (result == null)
        {
            enabled = false;
            availableSurfaceCount = 0;
            availableOptionalSurfaceCount = 0;
            StatusText = "Unavailable - cutscene hook surfaces could not be initialized.";
            UnsubscribeFramework();
            UpdateClientStateSubscriptions(false);
            return;
        }

        if (disposed || !enabled)
        {
            result.DisposeHooks();
            return;
        }

        if (!result.HasAnyCutsceneSurface)
        {
            result.DisposeHooks();
            enabled = false;
            availableSurfaceCount = 0;
            availableOptionalSurfaceCount = 0;
            StatusText = "Unavailable - cutscene signatures were not found.";
            UnsubscribeFramework();
            UpdateClientStateSubscriptions(false);
            log.Warning("[XASlave] Auto Skip Cutscenes unavailable: no cutscene hook or patch signatures were found.");
            return;
        }

        ApplyStartupHookResult(result);
        EnableStartupCutsceneSurfaces();
    }

    private void ApplyStartupHookResult(StartupHookResult result)
    {
        cutsceneHandleInputHook = result.CutsceneHandleInputHook;
        playCutsceneHook = result.PlayCutsceneHook;
        playCutsceneLuaHook = result.PlayCutsceneLuaHook;
        isCutsceneSeenHook = result.IsCutsceneSeenHook;
        playStaffRollHook = result.PlayStaffRollHook;
        playToBeContinuedHook = result.PlayToBeContinuedHook;
        cutsceneUnskippablePatchAddress = result.CutsceneUnskippablePatchAddress;

        if (result.MsqContentDirectorAttempted)
        {
            msqContentDirectorHook = result.MsqContentDirectorHook;
            if (msqContentDirectorHook == null)
                unavailableOptionalHooks.Add(MsqContentDirectorLabel);
        }

        if (result.MassivePcContentDirectorAttempted)
        {
            massivePcContentDirectorHook = result.MassivePcContentDirectorHook;
            if (massivePcContentDirectorHook == null)
                unavailableOptionalHooks.Add(MassivePcContentDirectorLabel);
        }

        if (result.CustomTalkContentDirectorAttempted)
        {
            customTalkContentDirectorHook = result.CustomTalkContentDirectorHook;
            if (customTalkContentDirectorHook == null)
                unavailableOptionalHooks.Add(CustomTalkContentDirectorLabel);
        }

        if (result.NormalCutscenesAttempted)
        {
            normalCutscenesHook = result.NormalCutscenesHook;
            if (normalCutscenesHook == null)
                unavailableOptionalHooks.Add(NormalCutscenesLabel);
        }

        if (result.InnContentDirectorAttempted)
        {
            innContentDirectorHook = result.InnContentDirectorHook;
            if (innContentDirectorHook == null)
                unavailableOptionalHooks.Add(InnContentDirectorLabel);
        }
    }

    private void EnableStartupCutsceneSurfaces()
    {
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
            enabled = false;
            StatusText = "Unavailable - cutscene hooks failed to enable.";
            UnsubscribeFramework();
            UpdateClientStateSubscriptions(false);
            log.Warning("[XASlave] Auto Skip Cutscenes could not enable any hook or patch surfaces.");
            return;
        }

        SyncCutscenePatchState();
        RefreshStatusText();
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (startupArmingPending)
        {
            ProcessStartupArmingTask();
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
            TrySelectValidatedCutsceneSkipPrompt(now);
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
            if (!IsEffectivelyEnabled(CutsceneSkipCategory.Generic))
                return;

            if (args is not AgentReceiveEventArgs receiveEventArgs
                || receiveEventArgs.Agent.Address == nint.Zero
                || receiveEventArgs.AtkValues == nint.Zero
                || receiveEventArgs.ValueCount == 0)
                return;

            var atkValues = (AtkValue*)receiveEventArgs.AtkValues;
            if (atkValues[0].Int != PointMenuResultEvent)
                return;

            var agent = (ClientAgentPointMenu*)receiveEventArgs.Agent.Address;
            if (agent != ClientAgentPointMenu.Instance()
                || agent->Context == null
                || !agent->Context->IsLoaded)
                return;

            var entryCount = agent->Context->Entries.Count;
            var completionKey = agent->Context->PointMenuId;
            var completedBitfield = 0;
            if (agent->CompletionData != null)
                agent->CompletionData->TryGetValue(in completionKey, out completedBitfield, false);

            var index = PointMenuSelectionPolicy.FindFirstUncompleted(entryCount, completedBitfield);
            if (index < 0 || index >= entryCount)
            {
                agent->Hide();
                return;
            }

            ref var entry = ref agent->Context->Entries[index];
            if (entry.PointMenuStringId == 0 && entry.Text.Length == 0)
            {
                log.Warning("[XASlave] Auto Skip Cutscenes refused a PointMenu entry with neither a string id nor text.");
                return;
            }

            agent->SelectedIndex = index;
            agent->SendEntryToAddon((uint)index);
            agent->Hide();
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

        if (AddonHelper.IsAddonReady("SelectString") && TryResolveFashionReportSelectString(out var callbackIndex))
        {
            lastFashionReportAttemptUtc = now;
            AddonHelper.SelectAddonListText("SelectString", callbackIndex);
        }
    }

    private static bool SelectYesnoLooksLikeFashionReport()
    {
        return AddonHelper.GetAddonTextEntries("SelectYesno").Any(text =>
            text.Contains("fashion", StringComparison.OrdinalIgnoreCase)
            || text.Contains("judg", StringComparison.OrdinalIgnoreCase)
            || text.Contains("present yourself", StringComparison.OrdinalIgnoreCase));
    }

    private void TrySelectValidatedCutsceneSkipPrompt(DateTime now)
    {
        var optionText = AddonHelper.GetAddonTextEntries("SelectString")
            .FirstOrDefault(text =>
                text.Contains("skip", StringComparison.OrdinalIgnoreCase)
                && text.Contains("cutscene", StringComparison.OrdinalIgnoreCase));
        var callbackIndex = string.IsNullOrWhiteSpace(optionText)
            ? -1
            : AddonHelper.GetAddonListTextCallbackIndex("SelectString", optionText);
        if (callbackIndex >= 0)
        {
            AddonHelper.SelectAddonListText("SelectString", callbackIndex);
            return;
        }

        if ((now - lastPromptResolverWarningUtc).TotalSeconds < 10)
            return;

        lastPromptResolverWarningUtc = now;
        log.Warning("[XASlave] Auto Skip Cutscenes left SelectString untouched because no validated cutscene-skip option was resolved.");
    }

    private static bool TryResolveFashionReportSelectString(out int callbackIndex)
    {
        var optionText = AddonHelper.GetAddonTextEntries("SelectString").FirstOrDefault(text =>
            text.Contains("Present yourself", StringComparison.OrdinalIgnoreCase)
            || text.Contains("judg", StringComparison.OrdinalIgnoreCase));
        callbackIndex = string.IsNullOrWhiteSpace(optionText)
            ? -1
            : AddonHelper.GetAddonListTextCallbackIndex("SelectString", optionText);
        return callbackIndex >= 0;
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

        return cutsceneHandleInputHook?.OriginalDisposeSafe(a1, a2) ?? 0;
    }

    private nint PlayCutsceneDetour(EventFramework* eventFramework, lua_State* state)
    {
        try
        {
            if (IsEffectivelyEnabled(CutsceneSkipCategory.Generic))
                return 1;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Skip Cutscenes PlayCutscene detour failed; calling the original.");
        }

        return playCutsceneHook?.OriginalDisposeSafe(eventFramework, state) ?? 0;
    }

    private ulong PlayCutsceneLuaDetour(lua_State* state)
    {
        try
        {
            if (!IsEffectivelyEnabled(CutsceneSkipCategory.Generic))
                return playCutsceneLuaHook?.OriginalDisposeSafe(state) ?? 0;

            if (state == null
                || state->top == null
                || state->stack_last == null
                || state->top >= state->stack_last)
            {
                return playCutsceneLuaHook?.OriginalDisposeSafe(state) ?? 0;
            }

            state->top->tt = 2;
            state->top->value.n = 1;
            state->top += 1;

            return 1;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Skip Cutscenes PlayCutsceneLua detour failed; calling the original.");
        }

        return playCutsceneLuaHook?.OriginalDisposeSafe(state) ?? 0;
    }

    private ulong PlayStaffRollDetour(lua_State* state)
    {
        try
        {
            if (IsEffectivelyEnabled(CutsceneSkipCategory.Generic))
                return 1;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Skip Cutscenes staff-roll detour failed; calling the original.");
        }

        return playStaffRollHook?.OriginalDisposeSafe(state) ?? 0;
    }

    private ulong PlayToBeContinuedDetour(lua_State* state)
    {
        try
        {
            if (IsEffectivelyEnabled(CutsceneSkipCategory.Generic))
                return 1;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Skip Cutscenes ToBeContinued detour failed; calling the original.");
        }

        return playToBeContinuedHook?.OriginalDisposeSafe(state) ?? 0;
    }

    private bool IsCutsceneSeenDetour(UIState* state, uint cutsceneId)
    {
        try
        {
            if (IsEffectivelyEnabled(CutsceneSkipCategory.Generic))
                return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Skip Cutscenes seen-state detour failed; calling the original.");
        }

        return isCutsceneSeenHook?.OriginalDisposeSafe(state, cutsceneId) ?? true;
    }

    private long MsqContentDirectorDetour(nint luaState)
    {
        try
        {
            if (IsEffectivelyEnabled(CutsceneSkipCategory.Msq)
                || IsEffectivelyEnabled(CutsceneSkipCategory.GoldSaucer))
                return 1;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Skip Cutscenes MSQ content-director detour failed; calling the original.");
        }

        return msqContentDirectorHook?.OriginalDisposeSafe(luaState) ?? 0;
    }

    private long MassivePcContentDirectorDetour(nint luaState)
    {
        try
        {
            if (IsEffectivelyEnabled(CutsceneSkipCategory.MassivePc))
                return 1;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Skip Cutscenes massive-PC content-director detour failed; calling the original.");
        }

        return massivePcContentDirectorHook?.OriginalDisposeSafe(luaState) ?? 0;
    }

    private long GoldSaucerContentDirectorDetour(nint luaState)
    {
        try
        {
            if (IsEffectivelyEnabled(CutsceneSkipCategory.GoldSaucer))
                return 1;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Skip Cutscenes Gold Saucer content-director detour failed; calling the original.");
        }

        return goldSaucerContentDirectorHook?.OriginalDisposeSafe(luaState) ?? 0;
    }

    private long CustomTalkContentDirectorDetour(nint luaState)
    {
        try
        {
            if (IsEffectivelyEnabled(CutsceneSkipCategory.CustomTalk))
                return 1;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Skip Cutscenes custom-talk content-director detour failed; calling the original.");
        }

        return customTalkContentDirectorHook?.OriginalDisposeSafe(luaState) ?? 0;
    }

    private long NormalCutscenesDetour(nint luaState1, nint luaState2)
    {
        try
        {
            if (IsEffectivelyEnabled(CutsceneSkipCategory.NormalCutscenes))
                return 1;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Skip Cutscenes normal-cutscene detour failed; calling the original.");
        }

        return normalCutscenesHook?.OriginalDisposeSafe(luaState1, luaState2) ?? 0;
    }

    private long InnContentDirectorDetour(nint luaState)
    {
        try
        {
            if (IsEffectivelyEnabled(CutsceneSkipCategory.Inn))
                return 1;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Skip Cutscenes inn content-director detour failed; calling the original.");
        }

        return innContentDirectorHook?.OriginalDisposeSafe(luaState) ?? 0;
    }

    private delegate byte CutsceneHandleInputDelegate(nint a1, float a2);

    private delegate nint PlayCutsceneDelegate(EventFramework* eventFramework, lua_State* state);

    private delegate ulong LuaFunctionDelegate(lua_State* state);

    private delegate bool IsCutsceneSeenDelegate(UIState* state, uint cutsceneId);

    private delegate long ContentDirectorDelegate(nint luaState);

    private delegate long NormalCutscenesDelegate(nint luaState1, nint luaState2);

    private sealed record StartupHookResult(
        Hook<CutsceneHandleInputDelegate>? CutsceneHandleInputHook,
        Hook<PlayCutsceneDelegate>? PlayCutsceneHook,
        Hook<LuaFunctionDelegate>? PlayCutsceneLuaHook,
        Hook<IsCutsceneSeenDelegate>? IsCutsceneSeenHook,
        Hook<LuaFunctionDelegate>? PlayStaffRollHook,
        Hook<LuaFunctionDelegate>? PlayToBeContinuedHook,
        nint CutsceneUnskippablePatchAddress,
        bool MsqContentDirectorAttempted,
        Hook<ContentDirectorDelegate>? MsqContentDirectorHook,
        bool MassivePcContentDirectorAttempted,
        Hook<ContentDirectorDelegate>? MassivePcContentDirectorHook,
        bool CustomTalkContentDirectorAttempted,
        Hook<ContentDirectorDelegate>? CustomTalkContentDirectorHook,
        bool NormalCutscenesAttempted,
        Hook<NormalCutscenesDelegate>? NormalCutscenesHook,
        bool InnContentDirectorAttempted,
        Hook<ContentDirectorDelegate>? InnContentDirectorHook)
    {
        public bool HasAnyCutsceneSurface =>
            CutsceneHandleInputHook != null
            || PlayCutsceneHook != null
            || PlayCutsceneLuaHook != null
            || IsCutsceneSeenHook != null
            || PlayStaffRollHook != null
            || PlayToBeContinuedHook != null
            || PointMenuApiAvailable
            || CutsceneUnskippablePatchAddress != nint.Zero;

        public void DisposeHooks()
        {
            DisposeHook(CutsceneHandleInputHook);
            DisposeHook(PlayCutsceneHook);
            DisposeHook(PlayCutsceneLuaHook);
            DisposeHook(IsCutsceneSeenHook);
            DisposeHook(PlayStaffRollHook);
            DisposeHook(PlayToBeContinuedHook);
            DisposeHook(MsqContentDirectorHook);
            DisposeHook(MassivePcContentDirectorHook);
            DisposeHook(CustomTalkContentDirectorHook);
            DisposeHook(NormalCutscenesHook);
            DisposeHook(InnContentDirectorHook);
        }
    }

}
