using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Text.ReadOnly;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using XASlave;
using DalamudFramework = Dalamud.Plugin.Services.IFramework;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace XASlave.Services
{
    public sealed unsafe class EurekaLogogramCreatorService : IDisposable
    {
        private enum QueueStep
        {
            Idle,
            PrepareManipulator,
            SelectArray,
            SelectCategory,
            PlaceLogogram,
            VerifyPlacement,
            ExtractMneme,
            WaitForSuccess,
            WaitForRetryReady,
            AcknowledgeSuccess,
        }

        private enum QueueBlockReason
        {
            None,
            MagiaBoardFull,
            PendingManualMnemePrompt,
        }

        private enum MnemeExtractionResult
        {
            None,
            Success,
            SuccessMagiaBoardFull,
            Failed,
        }

        private const int DefaultQueueStepFrameDelay = 20;
        private const int MinimumQueueStepFrameDelay = 1;
        private const int MaximumQueueStepFrameDelay = 120;
        private const int LogosActionRefreshDelayFrames = 2;
        private const int MaxDeferredLogosActionRefreshAttempts = 4;
        private const float AutomationOverlayButtonWidth = 160f;
        private const float AutomationOverlayVerticalOffset = 1f;
        private const string PlateNoActionsSelectedError = "Select at least one action before starting a plate";
        private const string PlateRequiresAstralError = "Umbral-only plates cannot be extracted; set an Astral action or use Solo.";
        private const string PlateInsufficientLogogramsError = "You do not have enough logograms for this plate";
        internal const string AutomaticRecipeModeLabel = "Cheapest By Gil";
        private static readonly TimeSpan SuccessTimeout = TimeSpan.FromSeconds(5);
        private static readonly int[] StockCategoryButtons = [13, 12, 11, 10, 9, 8];
        private static readonly IReadOnlyList<LogogramSourceDefinition> DefaultLogogramSourceDefinitions =
        [
            new(24007, "Conceptual", 1300),
            new(24008, "Fundamental", 500),
            new(24010, "Offensive", 9000),
            new(24011, "Protective", 120000),
            new(24009, "Curative", 30000),
            new(24012, "Tactical", 2500),
            new(24014, "Inimical", 2500),
            new(24013, "Mitigative", 5000),
            new(24809, "Obscure", 13000),
        ];
        private static readonly IReadOnlyDictionary<ulong, LogogramSourceDefinition> LogogramSourceDefinitionsById =
            DefaultLogogramSourceDefinitions.ToDictionary(source => source.ItemId);
        private const int MaxShardSelectionAttempts = 4;
        private const int MaxManipulatorFocusAttempts = 5;
        private const int ManipulatorAstralFocusValueIndex = 0;
        private const int ManipulatorUmbralFocusValueIndex = 2;

        internal Configuration Configuration { get; }
        internal IGameGui GameGui => Plugin.GameGui;
        internal IPluginLog Log => Plugin.Log;
        internal IDataManager DataManager => Plugin.DataManager;
        internal ITextureProvider TextureProvider => Plugin.TextureProvider;
        private IGameInteropProvider GameInteropProvider => Plugin.GameInterop;
        private IAddonLifecycle AddonLifecycle => Plugin.AddonLifecycle;
        private DalamudFramework FrameworkService => Plugin.Framework;
        private IClientState ClientState => Plugin.ClientState;

        internal List<LogosAction> LogosActions = [];
        internal Dictionary<int, Logogram> Logograms = [];
        internal Dictionary<ulong, LogogramItem> LogogramItems = [];
        internal Dictionary<int, int> LogogramStock = [];
        internal Dictionary<uint, int> LogosActionStock = [];
        internal Queue<PlateQueueRequest> SynthesisQueue = new();
        internal bool IsProcessingQueue = false;
        internal string LastStatus { get; private set; } = "Idle";
        internal int LogosActionSlotCapacity { get; private set; } = 3;
        internal int LogosActionSlotsUsed { get; private set; }
        internal bool IsMagiaBoardFull => LogosActionSlotCapacity > 0 && LogosActionSlotsUsed >= LogosActionSlotCapacity;
        internal bool HasLogogramStockCache { get; private set; }
        internal bool HasActiveOrQueuedAutoLogoAction => IsProcessingQueue || SynthesisQueue.Count > 0;
        internal uint? PendingAstralActionId { get; private set; }
        internal uint? PendingUmbralActionId { get; private set; }
        internal int QueueStepFrameDelayFrames => GetConfiguredQueueStepFrameDelay();
        private readonly Dictionary<string, int> categoryNodeByLogogramName = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Wisdom of the Aetherweaver"] = 13,
            ["Wisdom of the Martialist"] = 13,
            ["Wisdom of the Skirmisher"] = 13,
            ["Bloodbath L"] = 13,
            ["Magic Burst L"] = 13,
            ["Double Edge L"] = 13,
            ["Eagle Eye Shot L"] = 13,
            ["Wisdom of the Platebearer"] = 12,
            ["Incense L"] = 12,
            ["Wisdom of the Guardian"] = 12,
            ["Spirit of the Remembered"] = 12,
            ["Cure L"] = 11,
            ["Esuna L"] = 11,
            ["Raise L"] = 11,
            ["Wisdom of the Ordained"] = 11,
            ["Cure L II"] = 11,
            ["Feint L"] = 10,
            ["Backstep L"] = 10,
            ["Featherfoot L"] = 10,
            ["Stealth L"] = 10,
            ["Wisdom of the Breathtaker"] = 10,
            ["Paralyze L"] = 9,
            ["Tranquilizer L"] = 9,
            ["Spirit Dart L"] = 9,
            ["Dispel L"] = 9,
            ["Protect L"] = 8,
            ["Shell L"] = 8,
            ["Stoneskin L"] = 8,
        };

        private QueueStep queueStep = QueueStep.Idle;
        private ulong currentFrameworkFrame;
        private ulong nextQueueActionFrame;
        private DateTime queueStepStartedAt = DateTime.MinValue;
        private PlateQueueRequest? currentPlateRequest;
        private List<PlateActionSelection> currentQueuedSelections = [];
        private int currentSelectionIndex;
        private int currentRecipeIndex;
        private int currentQuantityIndex;
        private int currentSlotIndex;
        private int currentPlacementAttempt;
        private int currentExpectedFilledSlotCount;
        private int currentExpectedOtherArrayFilledSlotCount;
        private int currentCategoryNodeIndex = 14;
        private QueueBlockReason queueBlockReason = QueueBlockReason.None;
        private bool pendingDestroyConfirmation;
        private int currentWrongArrayRecoveryCount;
        private int currentArrayFocusAttempt;
        private MnemeExtractionResult pendingMnemeExtractionResult = MnemeExtractionResult.None;
        private bool currentExtractionAttemptStockConsumed;
        private bool retryUsesLoadedPlate;
        private string currentExtractionResultBaselineSignature = string.Empty;
        private bool currentExtractionResultClearedSinceStart;
        private bool waitingForPreviousExtractionPromptToClear;

        private bool pendingFullStockScan;
        private int stockScanCategoryIndex = -1;
        private ulong nextStockScanFrame;
        private bool pendingLogosActionRefresh;
        private ulong nextLogosActionRefreshFrame;
        private int pendingLogosActionRefreshAttempts;
        private ushort cachedLogogramTerritoryId;
        private readonly Dictionary<int, long> logogramGilCostById = [];
        private readonly Dictionary<int, ulong> logogramSourceItemIdByLogogramId = [];

        public EurekaLogogramCreatorService(Configuration configuration)
        {
            Configuration = configuration;
            EnsureLogogramSourceCostsConfigured();
            LoadData();
            FrameworkService.Update += OnFrameworkUpdate;
            ClientState.TerritoryChanged += OnTerritoryChanged;

            AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "EurekaMagiciteItemShardList", OnShardListSetup);
            AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "EurekaMagiciteItemAtherList", OnAtherListSetup);
            AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "EurekaMagiciteItemSynthesis", OnSynthesisSetup);

            SetStatus("Initialized");
            Log.Information($"[XASlave] Eureka Logogram Creator initialized with {Logograms.Count} logograms and {LogosActions.Count} logos actions.");
        }

        public void Dispose()
        {
            AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "EurekaMagiciteItemShardList", OnShardListSetup);
            AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "EurekaMagiciteItemAtherList", OnAtherListSetup);
            AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "EurekaMagiciteItemSynthesis", OnSynthesisSetup);
            ClientState.TerritoryChanged -= OnTerritoryChanged;
            FrameworkService.Update -= OnFrameworkUpdate;
        }

        private void OnFrameworkUpdate(DalamudFramework framework)
        {
            currentFrameworkFrame++;

            if (pendingFullStockScan && !IsProcessingQueue)
            {
                TickFullStockScan();
            }

            if (pendingLogosActionRefresh && currentFrameworkFrame >= nextLogosActionRefreshFrame)
            {
                RefreshVisibleLogosActionStock();
            }

            if (IsProcessingQueue)
            {
                TickSynthesisQueue();
            }
            else if (SynthesisQueue.Count > 0 && IsManipulatorVisible() && CanStartNextQueuedSynthesis())
            {
                StartNextQueuedSynthesis();
            }
        }

        private void OnShardListSetup(AddonEvent type, AddonArgs args)
        {
            SetStatus("Shard list opened");
            if (Configuration.AutoRefreshAllPagesOnOpen || !HasLogogramStockCache)
            {
                ScheduleFullStockScan();
            }
        }

        private void OnAtherListSetup(AddonEvent type, AddonArgs args)
        {
            SetStatus("Logos actions opened");
            ScheduleLogosActionRefresh(LogosActionRefreshDelayFrames, resetAttempts: true);
        }

        private void OnSynthesisSetup(AddonEvent type, AddonArgs args)
        {
            SetStatus("Synthesis opened");
            if (SynthesisQueue.Count > 0 && !IsProcessingQueue)
            {
                StartNextQueuedSynthesis();
            }
        }

        private void OnTerritoryChanged(ushort _)
        {
            if (IsInEurekaTerritory())
            {
                return;
            }

            ClearCachedEurekaData();
        }

        internal void RefreshKnownDataNow()
        {
            if (GetAddon("EurekaMagiciteItemShardList") != null)
            {
                ScheduleFullStockScan();
            }

            if (GetAddon("EurekaMagiciteItemAtherList") != null)
            {
                ScheduleLogosActionRefresh(LogosActionRefreshDelayFrames, resetAttempts: true);
            }
        }

        internal void SetAutoRefreshAllPagesOnOpen(bool enabled)
        {
            if (Configuration.AutoRefreshAllPagesOnOpen == enabled)
            {
                return;
            }

            Configuration.AutoRefreshAllPagesOnOpen = enabled;
            Configuration.Save();
        }

        internal void SetAutoDestroyWhenMagiaBoardFull(bool enabled)
        {
            if (Configuration.AutoDestroyWhenMagiaBoardFull == enabled)
            {
                return;
            }

            Configuration.AutoDestroyWhenMagiaBoardFull = enabled;
            Configuration.Save();
        }

        internal void SetAutoRetryFailedExtraction(bool enabled)
        {
            if (Configuration.AutoRetryFailedExtraction == enabled)
            {
                return;
            }

            Configuration.AutoRetryFailedExtraction = enabled;
            Configuration.Save();
        }

        internal void SetShowFavoritesOverlay(bool enabled)
        {
            if (Configuration.ShowFavoritesOverlay == enabled)
            {
                return;
            }

            Configuration.ShowFavoritesOverlay = enabled;
            Configuration.Save();
        }

        internal void SetQueueStepFrameDelay(int frames)
        {
            var clampedFrames = ClampQueueStepFrameDelay(frames);
            if (Configuration.QueueStepFrameDelay == clampedFrames)
            {
                return;
            }

            Configuration.QueueStepFrameDelay = clampedFrames;
            Configuration.Save();
            SetStatus($"Set automation delay to {clampedFrames} frame(s)");
        }

        internal int GetPreferredRecipeIndex(uint actionId)
        {
            return Configuration.PreferredRecipeIndexes.TryGetValue(actionId, out var index) ? index : -1;
        }

        internal void SetPreferredRecipeIndex(uint actionId, int recipeIndex)
        {
            if (recipeIndex < 0)
            {
                Configuration.PreferredRecipeIndexes.Remove(actionId);
            }
            else
            {
                Configuration.PreferredRecipeIndexes[actionId] = recipeIndex;
            }

            Configuration.Save();
        }

        private void ScheduleFullStockScan()
        {
            pendingFullStockScan = true;
            stockScanCategoryIndex = -1;
            nextStockScanFrame = 0;
            SetStatus("Scheduled full logogram stock scan");
        }

        private void ClearCachedEurekaData()
        {
            LogogramStock.Clear();
            LogosActionStock.Clear();
            LogosActionSlotsUsed = 0;
            LogosActionSlotCapacity = 3;
            HasLogogramStockCache = false;
            cachedLogogramTerritoryId = 0;
            pendingFullStockScan = false;
            stockScanCategoryIndex = -1;
            pendingLogosActionRefresh = false;
            nextLogosActionRefreshFrame = 0;
            pendingLogosActionRefreshAttempts = 0;
            nextStockScanFrame = 0;
        }

        private bool IsInEurekaTerritory()
        {
            var territoryId = ClientState.TerritoryType;
            if (territoryId == 0)
            {
                return false;
            }

            var sheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
            if (!sheet.TryGetRow(territoryId, out var territory))
            {
                return false;
            }

            var placeName = territory.PlaceName.ValueNullable?.Name.ToString() ?? string.Empty;
            return placeName.Contains("Eureka", StringComparison.OrdinalIgnoreCase);
        }

        private unsafe void TickFullStockScan()
        {
            if (currentFrameworkFrame < nextStockScanFrame)
            {
                return;
            }

            var shardAddon = GetAddon("EurekaMagiciteItemShardList");
            if (shardAddon == null || !shardAddon->IsVisible)
            {
                return;
            }

            if (stockScanCategoryIndex == -1)
            {
                LogogramStock.Clear();
                HasLogogramStockCache = false;
                stockScanCategoryIndex = 0;
                if (!ClickAddonButton("EurekaMagiciteItemShardList", StockCategoryButtons[stockScanCategoryIndex]))
                {
                    pendingFullStockScan = false;
                    SetStatus("Could not start logogram stock scan");
                    return;
                }

                currentCategoryNodeIndex = StockCategoryButtons[stockScanCategoryIndex];
                ScheduleStockScanAfterFrames();
                return;
            }

            ReadLogogramStock();
            stockScanCategoryIndex++;

            if (stockScanCategoryIndex >= StockCategoryButtons.Length)
            {
                ClickAddonButton("EurekaMagiciteItemShardList", 14);
                currentCategoryNodeIndex = 14;
                pendingFullStockScan = false;
                stockScanCategoryIndex = -1;
                HasLogogramStockCache = true;
                cachedLogogramTerritoryId = ClientState.TerritoryType;
                SetStatus($"Logogram stock refreshed ({LogogramStock.Count} entries)");
                return;
            }

            if (ClickAddonButton("EurekaMagiciteItemShardList", StockCategoryButtons[stockScanCategoryIndex]))
            {
                currentCategoryNodeIndex = StockCategoryButtons[stockScanCategoryIndex];
            }
            ScheduleStockScanAfterFrames();
        }

        private void RefreshVisibleLogosActionStock()
        {
            pendingLogosActionRefresh = false;
            if (GetAddon("EurekaMagiciteItemAtherList") == null)
            {
                return;
            }

            var usedVisibleRows = ReadLogosActionStock();
            if (!usedVisibleRows && pendingLogosActionRefreshAttempts < MaxDeferredLogosActionRefreshAttempts)
            {
                pendingLogosActionRefreshAttempts++;
                ScheduleLogosActionRefresh(LogosActionRefreshDelayFrames, resetAttempts: false);
                Log.Debug($"Visible Logos Action rows were not ready yet; scheduled retry {pendingLogosActionRefreshAttempts}/{MaxDeferredLogosActionRefreshAttempts}");
            }
            else if (usedVisibleRows)
            {
                pendingLogosActionRefreshAttempts = 0;
            }

            SetStatus($"Logos Actions refreshed ({LogosActionSlotsUsed}/{LogosActionSlotCapacity} slots used)");
        }

        private void LoadData()
        {
            try
            {
                var dataDirectory = Path.Combine(
                    Plugin.PluginInterface.AssemblyLocation.Directory?.FullName ?? string.Empty,
                    "Data",
                    "EurekaLogogramCreator");
                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                };

                var logogramJson = File.ReadAllText(Path.Combine(dataDirectory, "logograms.json"));
                var logos = JsonSerializer.Deserialize<List<Logogram>>(logogramJson, jsonOptions);
                Logograms = logos?.ToDictionary(l => l.Id, l => l) ?? [];

                var itemJson = File.ReadAllText(Path.Combine(dataDirectory, "itemContents.json"));
                var items = JsonSerializer.Deserialize<List<LogogramItem>>(itemJson, jsonOptions);
                LogogramItems = items?.ToDictionary(i => i.Id, i => i) ?? [];
                RebuildLogogramGilCosts();

                var logosJson = File.ReadAllText(Path.Combine(dataDirectory, "logosActions.json"));
                LogosActions = JsonSerializer.Deserialize<List<LogosAction>>(logosJson, jsonOptions) ?? [];

                Log.Information($"Loaded {Logograms.Count} logograms, {LogosActions.Count} logos actions, and {logogramGilCostById.Count} gil-cost mappings");
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to load data: {ex.Message}");
            }
        }

        private void EnsureLogogramSourceCostsConfigured()
        {
            var changed = false;
            foreach (var source in DefaultLogogramSourceDefinitions)
            {
                if (Configuration.LogogramSourceGilCosts.TryGetValue(source.ItemId, out var configuredCost) && configuredCost >= 0)
                {
                    continue;
                }

                Configuration.LogogramSourceGilCosts[source.ItemId] = source.DefaultGilCost;
                changed = true;
            }

            if (changed)
            {
                Configuration.Save();
            }
        }

        private void RebuildLogogramGilCosts()
        {
            logogramGilCostById.Clear();
            logogramSourceItemIdByLogogramId.Clear();

            foreach (var item in LogogramItems.Values)
            {
                if (!LogogramSourceDefinitionsById.TryGetValue(item.Id, out _) || item.Contents.Count == 0)
                {
                    continue;
                }

                var logogramCost = GetConfiguredLogogramSourceGilCost(item.Id);
                var specificMnemeCost = checked((long)logogramCost * item.Contents.Count);
                foreach (var logogramId in item.Contents)
                {
                    logogramGilCostById[logogramId] = specificMnemeCost;
                    logogramSourceItemIdByLogogramId[logogramId] = item.Id;
                }
            }
        }

        public unsafe void ReadLogogramStock()
        {
            var framework = Framework.Instance();
            if (framework == null)
            {
                return;
            }

            var arrayData = framework->GetUIModule()->GetRaptureAtkModule()->AtkModule.AtkArrayDataHolder;
            var numberArray = arrayData.NumberArrays[137];
            if (numberArray == null || numberArray->IntArray == null)
            {
                return;
            }

            var count = numberArray->IntArray[0];
            Log.Debug($"Reading {count} logograms from shard list");

            for (var i = 1; i <= count; i++)
            {
                var id = numberArray->IntArray[(4 * i) + 1];
                var stock = numberArray->IntArray[4 * i];
                LogogramStock[id] = stock;
            }
        }

        public unsafe bool ReadLogosActionStock()
        {
            var framework = Framework.Instance();
            if (framework == null)
            {
                return false;
            }

            var arrayData = framework->GetUIModule()->GetRaptureAtkModule()->AtkModule.AtkArrayDataHolder;
            var numberArray = arrayData.NumberArrays[138];
            if (numberArray == null || numberArray->IntArray == null)
            {
                return false;
            }

            UpdateLogosActionSlotCapacity();

            var rawSlotEntryCount = Math.Clamp(numberArray->IntArray[0], 0, 32);
            var scanSlotCount = Math.Clamp(Math.Max(rawSlotEntryCount, LogosActionSlotCapacity), 0, 32);
            Log.Debug($"Reading {scanSlotCount} logos action slot entries from ather list (raw header: {rawSlotEntryCount}, capacity: {LogosActionSlotCapacity})");

            LogosActionStock.Clear();
            var occupiedSlotCount = 0;

            // NumberArray[138] exposes one entry per slot; empty slots carry an id of 0.
            for (var i = 1; i <= scanSlotCount; i++)
            {
                var id = (uint)numberArray->IntArray[(4 * i) + 1];
                if (id == 0)
                {
                    continue;
                }

                occupiedSlotCount++;
                LogosActionStock.TryGetValue(id, out var existingCount);
                LogosActionStock[id] = existingCount + 1;
            }

            if (TryCountVisibleLogosActionSlots(out var visibleOccupiedSlotCount, out var visibleSlotCount))
            {
                LogosActionSlotsUsed = visibleOccupiedSlotCount;
                Log.Debug($"Detected {LogosActionSlotsUsed}/{visibleSlotCount} occupied Logos Action slots from visible Ather-list rows");
                return true;
            }

            LogosActionSlotsUsed = occupiedSlotCount;
            Log.Debug($"Detected {LogosActionSlotsUsed}/{LogosActionSlotCapacity} occupied Logos Action slots from non-zero Ather-list entries");
            return false;
        }

        private unsafe void UpdateLogosActionSlotCapacity()
        {
            var addon = GetAddon("EurekaMagiciteItemAtherList");
            if (addon == null || addon->AtkValues == null || addon->AtkValuesCount <= 3)
            {
                return;
            }

            try
            {
                var value = &addon->AtkValues[3];
                var capacity = value->Type switch
                {
                    ValueType.UInt => (int)value->UInt,
                    ValueType.Int => value->Int,
                    _ => 0,
                };

                if (capacity <= 0 || capacity == LogosActionSlotCapacity)
                {
                    return;
                }

                LogosActionSlotCapacity = capacity;
                Log.Debug($"Detected Logos Action slot capacity: {LogosActionSlotCapacity}");
            }
            catch (Exception ex)
            {
                Log.Debug($"Could not read Logos Action slot capacity: {ex.Message}");
            }
        }

        internal LogosAction? GetLogosAction(uint actionId)
        {
            return LogosActions.FirstOrDefault(a => a.Id == actionId);
        }

        internal IReadOnlyList<LogogramSourceDefinition> GetLogogramSourceDefinitions()
        {
            return DefaultLogogramSourceDefinitions;
        }

        internal int GetLogogramSourcePoolSize(ulong sourceItemId)
        {
            return LogogramItems.TryGetValue(sourceItemId, out var item) ? item.Contents.Count : 0;
        }

        internal int GetConfiguredLogogramSourceGilCost(ulong sourceItemId)
        {
            if (Configuration.LogogramSourceGilCosts.TryGetValue(sourceItemId, out var configuredCost) && configuredCost >= 0)
            {
                return configuredCost;
            }

            return LogogramSourceDefinitionsById.TryGetValue(sourceItemId, out var sourceDefinition)
                ? sourceDefinition.DefaultGilCost
                : 0;
        }

        internal long GetConfiguredSpecificMnemeGilCost(ulong sourceItemId)
        {
            var poolSize = GetLogogramSourcePoolSize(sourceItemId);
            return poolSize <= 0 ? 0L : checked((long)GetConfiguredLogogramSourceGilCost(sourceItemId) * poolSize);
        }

        internal void SetLogogramSourceGilCost(ulong sourceItemId, int gilCost)
        {
            var clampedGilCost = Math.Max(0, gilCost);
            if (GetConfiguredLogogramSourceGilCost(sourceItemId) == clampedGilCost)
            {
                return;
            }

            Configuration.LogogramSourceGilCosts[sourceItemId] = clampedGilCost;
            Configuration.Save();
            RebuildLogogramGilCosts();
        }

        internal void ResetLogogramSourceGilCosts()
        {
            var changed = false;
            foreach (var source in DefaultLogogramSourceDefinitions)
            {
                if (GetConfiguredLogogramSourceGilCost(source.ItemId) == source.DefaultGilCost)
                {
                    continue;
                }

                Configuration.LogogramSourceGilCosts[source.ItemId] = source.DefaultGilCost;
                changed = true;
            }

            if (!changed)
            {
                return;
            }

            Configuration.Save();
            RebuildLogogramGilCosts();
            SetStatus("Reset source logogram costs to defaults");
        }

        internal bool TryGetRecipeGilCost(IReadOnlyList<Recipe>? recipe, out long gilCost)
        {
            gilCost = 0;
            if (recipe == null || recipe.Count == 0)
            {
                return false;
            }

            long totalCost = 0;
            foreach (var ingredient in recipe)
            {
                if (!logogramGilCostById.TryGetValue(ingredient.LogogramID, out var perMnemeCost))
                {
                    return false;
                }

                totalCost = checked(totalCost + (perMnemeCost * ingredient.Quantity));
            }

            gilCost = totalCost;
            return true;
        }

        internal bool TryGetActionRecipeGilCost(uint actionId, out long gilCost)
        {
            gilCost = 0;

            var action = GetLogosAction(actionId);
            if (action == null)
            {
                return false;
            }

            var recipe = GetResolvedRecipe(action, out _);
            return TryGetRecipeGilCost(recipe, out gilCost);
        }

        internal bool TryGetPlateRecipeGilCost(uint? astralActionId, uint? umbralActionId, out long gilCost)
        {
            gilCost = 0;
            long totalCost = 0;
            var hasAnyAction = false;

            if (astralActionId.HasValue)
            {
                if (!TryGetActionRecipeGilCost(astralActionId.Value, out var astralCost))
                {
                    return false;
                }

                totalCost = checked(totalCost + astralCost);
                hasAnyAction = true;
            }

            if (umbralActionId.HasValue)
            {
                if (!TryGetActionRecipeGilCost(umbralActionId.Value, out var umbralCost))
                {
                    return false;
                }

                totalCost = checked(totalCost + umbralCost);
                hasAnyAction = true;
            }

            if (!hasAnyAction)
            {
                return false;
            }

            gilCost = totalCost;
            return true;
        }

        internal bool TryGetPendingPlateRecipeGilCost(out long gilCost)
        {
            return TryGetPlateRecipeGilCost(PendingAstralActionId, PendingUmbralActionId, out gilCost);
        }

        internal bool TryGetFavoritePlateRecipeGilCost(FavoritePlate plate, out long gilCost)
        {
            var (astralActionId, umbralActionId) = GetFavoritePlateActions(plate);
            return TryGetPlateRecipeGilCost(astralActionId, umbralActionId, out gilCost);
        }

        internal long GetRecipeGilCost(IReadOnlyList<Recipe>? recipe)
        {
            return TryGetRecipeGilCost(recipe, out var gilCost) ? gilCost : long.MaxValue;
        }

        internal string FormatGilCost(long gilCost)
        {
            return $"{gilCost:N0} gil";
        }

        internal List<Recipe>? GetResolvedRecipe(LogosAction action, out int recipeIndex)
        {
            recipeIndex = -1;
            if (action.Recipes.Count == 0)
            {
                return null;
            }

            var preferredRecipeIndex = GetPreferredRecipeIndex(action.Id);
            if (preferredRecipeIndex >= 0)
            {
                if (preferredRecipeIndex < action.Recipes.Count)
                {
                    recipeIndex = preferredRecipeIndex;
                    return action.Recipes[preferredRecipeIndex];
                }

                Configuration.PreferredRecipeIndexes.Remove(action.Id);
                Configuration.Save();
            }

            var cheapestRecipe = action.Recipes
                .Select((recipe, index) => new { recipe, index, cost = GetRecipeGilCost(recipe) })
                .OrderBy(x => x.cost)
                .ThenBy(x => x.index)
                .First();

            recipeIndex = cheapestRecipe.index;
            return cheapestRecipe.recipe;
        }

        internal int GetCraftableCount(IReadOnlyList<Recipe>? recipe)
        {
            if (recipe == null || recipe.Count == 0)
            {
                return 0;
            }

            var recipeCosts = recipe.Select(r => LogogramStock.GetValueOrDefault(r.LogogramID, 0) / r.Quantity);
            return recipeCosts.Any() ? recipeCosts.Min() : 0;
        }

        internal bool IsRecipeCraftable(IReadOnlyList<Recipe>? recipe)
        {
            return GetCraftableCount(recipe) > 0;
        }

        internal string FormatRecipe(IReadOnlyList<Recipe>? recipe)
        {
            if (recipe == null || recipe.Count == 0)
            {
                return "No recipe";
            }

            return string.Join(" + ", recipe.Select(r =>
                $"{r.Quantity}x {Logograms.GetValueOrDefault(r.LogogramID)?.Name ?? "Unknown"}"));
        }

        internal int GetPlateCraftableCount(uint? astralActionId, uint? umbralActionId)
        {
            if (!TryValidatePlateLayout(astralActionId, umbralActionId, out _))
            {
                return 0;
            }

            var selections = new List<PlateActionSelection>(2);

            if (astralActionId.HasValue)
            {
                if (!TryBuildPlateSelection(PlateSide.Astral, astralActionId.Value, out var selection, out _))
                {
                    return 0;
                }

                selections.Add(selection);
            }

            if (umbralActionId.HasValue)
            {
                if (!TryBuildPlateSelection(PlateSide.Umbral, umbralActionId.Value, out var selection, out _))
                {
                    return 0;
                }

                selections.Add(selection);
            }

            return GetCombinedPlateCraftableCount(selections);
        }

        internal string GetPendingPlateDescription()
        {
            return FormatPlateLabel(PendingAstralActionId, PendingUmbralActionId);
        }

        internal int GetPendingPlateCraftableCount()
        {
            return GetPlateCraftableCount(PendingAstralActionId, PendingUmbralActionId);
        }

        internal void SetPendingPlateSelection(PlateSide side, uint actionId)
        {
            if (side == PlateSide.Astral)
            {
                PendingAstralActionId = actionId;
            }
            else
            {
                PendingUmbralActionId = actionId;
            }

            SetStatus($"Set {GetActionName(actionId)} for the {side} Array");
        }

        internal void ClearPendingPlateSelection(PlateSide? side = null)
        {
            if (side == null || side == PlateSide.Astral)
            {
                PendingAstralActionId = null;
            }

            if (side == null || side == PlateSide.Umbral)
            {
                PendingUmbralActionId = null;
            }

            SetStatus("Cleared pending plate selection");
        }

        internal bool QueuePendingPlate()
        {
            return QueuePlate(PendingAstralActionId, PendingUmbralActionId, allowImmediateProcessing: true);
        }

        internal bool CanQueuePendingPlate(out string error)
        {
            return TryBuildPlateRequest(PendingAstralActionId, PendingUmbralActionId, out _, out error);
        }

        internal bool QueuePlate(uint? astralActionId, uint? umbralActionId, bool allowImmediateProcessing)
        {
            if (!TryBuildPlateRequest(astralActionId, umbralActionId, out var request, out var error))
            {
                Log.Warning(error);
                SetStatus(error);
                return false;
            }

            SynthesisQueue.Enqueue(request);
            SetStatus($"Queued plate: {request.Label}");
            Log.Information($"Queued plate: {request.Label}");

            if (!IsProcessingQueue && allowImmediateProcessing && IsManipulatorVisible())
            {
                StartNextQueuedSynthesis();
            }

            return true;
        }

        public bool QueueSynthesis(uint actionId)
        {
            return QueuePlate(actionId, null, allowImmediateProcessing: true);
        }

        public void ProcessSynthesisQueue()
        {
            if (IsProcessingQueue || SynthesisQueue.Count == 0)
            {
                return;
            }

            if (!CanStartNextQueuedSynthesis())
            {
                return;
            }

            StartNextQueuedSynthesis();
        }

        internal bool QueueFavoritePlate(FavoritePlate plate)
        {
            var (astralActionId, umbralActionId) = GetFavoritePlateActions(plate);
            return QueuePlate(astralActionId, umbralActionId, allowImmediateProcessing: true);
        }

        internal void UpsertFavoritePlate(string plateName, uint? astralActionId, uint? umbralActionId)
        {
            var trimmedName = plateName.Trim();
            if (string.IsNullOrWhiteSpace(trimmedName))
            {
                SetStatus("Favorite plate name cannot be empty");
                return;
            }

            if (!astralActionId.HasValue && !umbralActionId.HasValue)
            {
                SetStatus("Favorite plate needs at least one action");
                return;
            }

            var legacyActionIds = new List<uint>();
            if (astralActionId.HasValue)
            {
                legacyActionIds.Add(astralActionId.Value);
            }

            if (umbralActionId.HasValue)
            {
                legacyActionIds.Add(umbralActionId.Value);
            }

            var existing = Configuration.FavoritePlates.FirstOrDefault(x => x.Name.Equals(trimmedName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.AstralActionId = astralActionId;
                existing.UmbralActionId = umbralActionId;
                existing.ActionIds = legacyActionIds;
            }
            else
            {
                Configuration.FavoritePlates.Add(new FavoritePlate
                {
                    Name = trimmedName,
                    AstralActionId = astralActionId,
                    UmbralActionId = umbralActionId,
                    ActionIds = legacyActionIds,
                });
            }

            Configuration.Save();
            SetStatus($"Saved favorite plate '{trimmedName}'");
        }

        internal void UpsertFavoritePlate(string plateName, IEnumerable<uint> actionIds)
        {
            var actions = actionIds.ToList();
            uint? astralActionId = actions.Count > 0 ? actions[0] : null;
            uint? umbralActionId = actions.Count > 1 ? actions[1] : null;
            UpsertFavoritePlate(plateName, astralActionId, umbralActionId);
        }

        internal void SaveCurrentQueueAsFavoritePlate(string plateName)
        {
            UpsertFavoritePlate(plateName, PendingAstralActionId, PendingUmbralActionId);
        }

        internal void DeleteFavoritePlate(int plateIndex)
        {
            if (plateIndex < 0 || plateIndex >= Configuration.FavoritePlates.Count)
            {
                return;
            }

            var plateName = Configuration.FavoritePlates[plateIndex].Name;
            Configuration.FavoritePlates.RemoveAt(plateIndex);
            Configuration.Save();
            SetStatus($"Removed favorite plate '{plateName}'");
        }

        internal void RenameFavoritePlate(int plateIndex, string newName)
        {
            if (plateIndex < 0 || plateIndex >= Configuration.FavoritePlates.Count)
            {
                return;
            }

            var trimmedName = newName.Trim();
            if (string.IsNullOrWhiteSpace(trimmedName))
            {
                SetStatus("Favorite plate name cannot be empty");
                return;
            }

            var duplicateIndex = Configuration.FavoritePlates.FindIndex(x => x.Name.Equals(trimmedName, StringComparison.OrdinalIgnoreCase));
            if (duplicateIndex >= 0 && duplicateIndex != plateIndex)
            {
                SetStatus($"A favorite plate named '{trimmedName}' already exists");
                return;
            }

            var plate = Configuration.FavoritePlates[plateIndex];
            var oldName = plate.Name;
            if (oldName.Equals(trimmedName, StringComparison.Ordinal))
            {
                return;
            }

            plate.Name = trimmedName;
            Configuration.Save();
            SetStatus($"Renamed favorite plate '{oldName}' to '{trimmedName}'");
        }

        internal bool MoveFavoritePlate(int plateIndex, int targetIndex)
        {
            if (plateIndex < 0 || plateIndex >= Configuration.FavoritePlates.Count)
            {
                return false;
            }

            targetIndex = Math.Clamp(targetIndex, 0, Configuration.FavoritePlates.Count - 1);
            if (plateIndex == targetIndex)
            {
                return false;
            }

            var plate = Configuration.FavoritePlates[plateIndex];
            Configuration.FavoritePlates.RemoveAt(plateIndex);
            if (targetIndex > plateIndex)
            {
                targetIndex--;
            }

            Configuration.FavoritePlates.Insert(targetIndex, plate);
            Configuration.Save();
            SetStatus($"Moved favorite plate '{DescribeFavoritePlate(plate)}' to slot {targetIndex + 1}");
            return true;
        }

        internal bool InsertFavoritePlateAt(int plateIndex, int insertIndex)
        {
            if (plateIndex < 0 || plateIndex >= Configuration.FavoritePlates.Count)
            {
                return false;
            }

            insertIndex = Math.Clamp(insertIndex, 0, Configuration.FavoritePlates.Count);
            if (insertIndex == plateIndex || insertIndex == plateIndex + 1)
            {
                return false;
            }

            var plate = Configuration.FavoritePlates[plateIndex];
            Configuration.FavoritePlates.RemoveAt(plateIndex);
            if (insertIndex > plateIndex)
            {
                insertIndex--;
            }

            Configuration.FavoritePlates.Insert(insertIndex, plate);
            Configuration.Save();
            SetStatus($"Moved favorite plate '{DescribeFavoritePlate(plate)}' to slot {insertIndex + 1}");
            return true;
        }

        internal bool CancelAutoLogoAction()
        {
            if (!HasActiveOrQueuedAutoLogoAction)
            {
                SetStatus("No active Auto Logo Action to cancel");
                return false;
            }

            var cancelledLabel = currentPlateRequest?.Label;
            var clearedPlateCount = Math.Max(SynthesisQueue.Count, currentPlateRequest != null ? 1 : 0);
            SynthesisQueue.Clear();
            queueBlockReason = QueueBlockReason.None;
            ScheduleLogosActionRefresh(LogosActionRefreshDelayFrames, resetAttempts: true);

            var status = string.IsNullOrWhiteSpace(cancelledLabel)
                ? "Cancelled Auto Logo Action and cleared the queue"
                : $"Cancelled Auto Logo Action ({cancelledLabel}) and cleared the queue";
            Log.Information($"Force-stopped Auto Logo Action; cleared {clearedPlateCount} plate(s) from the active/queued run.");
            ResetQueueState(status);
            return true;
        }

        private void StartNextQueuedSynthesis()
        {
            if (SynthesisQueue.Count == 0)
            {
                IsProcessingQueue = false;
                queueStep = QueueStep.Idle;
                currentPlateRequest = null;
                currentQueuedSelections.Clear();
                SetStatus("Queue complete");
                return;
            }

            if (GetAddon("EurekaMagiciteItemShardList") == null || GetAddon("EurekaMagiciteItemSynthesis") == null)
            {
                SetStatus("Open the Logos Manipulator to process the queue");
                return;
            }

            currentPlateRequest = SynthesisQueue.Peek();
            currentQueuedSelections = currentPlateRequest.GetOrderedSelections().ToList();
            if (currentQueuedSelections.Count == 0)
            {
                SynthesisQueue.Dequeue();
                ResetQueueState("Skipped an empty plate request");
                return;
            }

            ResetCurrentPlateBuildProgress();
            pendingMnemeExtractionResult = MnemeExtractionResult.None;
            currentExtractionAttemptStockConsumed = false;
            retryUsesLoadedPlate = false;
            currentExtractionResultBaselineSignature = string.Empty;
            currentExtractionResultClearedSinceStart = true;
            waitingForPreviousExtractionPromptToClear = false;
            queueStep = QueueStep.PrepareManipulator;
            queueStepStartedAt = DateTime.UtcNow;
            nextQueueActionFrame = 0;
            pendingDestroyConfirmation = false;
            IsProcessingQueue = true;
            SetStatus($"Processing plate: {currentPlateRequest.Label}");
        }

        private bool CanStartNextQueuedSynthesis()
        {
            if (queueBlockReason == QueueBlockReason.None)
            {
                return true;
            }

            return TryClearQueueBlock();
        }

        private bool TryClearQueueBlock()
        {
            switch (queueBlockReason)
            {
                case QueueBlockReason.None:
                    return true;
                case QueueBlockReason.MagiaBoardFull:
                    if (!Configuration.AutoDestroyWhenMagiaBoardFull && IsMagiaBoardFull)
                    {
                        return false;
                    }

                    queueBlockReason = QueueBlockReason.None;
                    ScheduleLogosActionRefresh(LogosActionRefreshDelayFrames, resetAttempts: true);
                    SetStatus("Queue block cleared");
                    return true;
                case QueueBlockReason.PendingManualMnemePrompt:
                    if (IsMagiaBoardFullWarningVisible() || IsSelectYesNoVisible())
                    {
                        return false;
                    }

                    queueBlockReason = QueueBlockReason.None;
                    ScheduleLogosActionRefresh(LogosActionRefreshDelayFrames, resetAttempts: true);
                    SetStatus("Manual Logos prompt cleared");
                    return true;
                default:
                    return false;
            }
        }

        private PlateActionSelection? GetCurrentQueuedSelection()
        {
            if (currentSelectionIndex < 0 || currentSelectionIndex >= currentQueuedSelections.Count)
            {
                return null;
            }

            return currentQueuedSelections[currentSelectionIndex];
        }

        private void TickSynthesisQueue()
        {
            if (currentPlateRequest == null || currentQueuedSelections.Count == 0)
            {
                ResetQueueState("Queue reset");
                return;
            }

            if (currentFrameworkFrame < nextQueueActionFrame)
            {
                return;
            }

            switch (queueStep)
            {
                case QueueStep.PrepareManipulator:
                    TickPrepareManipulator();
                    break;
                case QueueStep.SelectArray:
                    TickSelectArray();
                    break;
                case QueueStep.SelectCategory:
                    TickSelectCategory();
                    break;
                case QueueStep.PlaceLogogram:
                    TickPlaceLogogram();
                    break;
                case QueueStep.VerifyPlacement:
                    TickVerifyPlacement();
                    break;
                case QueueStep.ExtractMneme:
                    TickExtractMneme();
                    break;
                case QueueStep.WaitForSuccess:
                    TickWaitForSuccess();
                    break;
                case QueueStep.WaitForRetryReady:
                    TickWaitForRetryReady();
                    break;
                case QueueStep.AcknowledgeSuccess:
                    TickAcknowledgeSuccess();
                    break;
            }
        }

        private static int ClampQueueStepFrameDelay(int frames)
        {
            var effectiveFrames = frames <= 0 ? DefaultQueueStepFrameDelay : frames;
            return Math.Clamp(effectiveFrames, MinimumQueueStepFrameDelay, MaximumQueueStepFrameDelay);
        }

        private int GetConfiguredQueueStepFrameDelay()
        {
            return ClampQueueStepFrameDelay(Configuration.QueueStepFrameDelay);
        }

        private void ScheduleQueueAfterFrames(int minimumFrames = 0)
        {
            var frameDelay = minimumFrames > 0 ? minimumFrames : GetConfiguredQueueStepFrameDelay();
            nextQueueActionFrame = currentFrameworkFrame + (ulong)Math.Max(0, frameDelay);
        }

        private void ScheduleStockScanAfterFrames(int minimumFrames = 0)
        {
            var frameDelay = minimumFrames > 0 ? minimumFrames : GetConfiguredQueueStepFrameDelay();
            nextStockScanFrame = currentFrameworkFrame + (ulong)Math.Max(0, frameDelay);
        }

        private void ScheduleLogosActionRefresh(int minimumFrames, bool resetAttempts)
        {
            if (resetAttempts)
            {
                pendingLogosActionRefreshAttempts = 0;
            }

            pendingLogosActionRefresh = true;
            nextLogosActionRefreshFrame = currentFrameworkFrame + (ulong)Math.Max(0, minimumFrames);
        }

        private void TickPrepareManipulator()
        {
            var pendingExtractionResult = GetMnemeExtractionResult();
            if (waitingForPreviousExtractionPromptToClear)
            {
                if (!IsManipulatorReadyForRetry())
                {
                    ScheduleQueueAfterFrames();
                    return;
                }

                waitingForPreviousExtractionPromptToClear = false;
            }

            if (pendingExtractionResult is MnemeExtractionResult.Success or MnemeExtractionResult.Failed)
            {
                if (!waitingForPreviousExtractionPromptToClear && TryAcknowledgeMnemeExtraction())
                {
                    waitingForPreviousExtractionPromptToClear = true;
                    ScheduleQueueAfterFrames();
                    SetStatus("Waiting for previous extraction result prompt to clear");
                    return;
                }

                ScheduleQueueAfterFrames();
                return;
            }

            if (!HasPendingManipulatorSelection())
            {
                queueStep = QueueStep.SelectArray;
                return;
            }

            if (TryClickVisibleAddonButton("EurekaMagiciteItemSynthesis", 3, "Destroy current selection"))
            {
                ScheduleQueueAfterFrames();
                SetStatus("Clearing previous manipulator selection");
                return;
            }

            FailCurrentRequest("Manipulator already had a selection and Destroy could not be clicked");
        }

        private void TickSelectArray()
        {
            var selection = GetCurrentQueuedSelection();
            if (selection == null)
            {
                FailCurrentRequest("No current plate side was available");
                return;
            }

            var shouldForceUmbralFocus = selection.Side == PlateSide.Umbral
                && currentArrayFocusAttempt == 0
                && GetManipulatorArrayFilledSlotCount(PlateSide.Astral) < 3;

            if (!shouldForceUmbralFocus && IsManipulatorArrayReadyForSelection(selection.Side))
            {
                currentArrayFocusAttempt = 0;
                currentSlotIndex = GetManipulatorArrayFilledSlotCount(selection.Side);
                currentPlacementAttempt = 0;
                queueStep = QueueStep.SelectCategory;
                ScheduleQueueAfterFrames();
                SetStatus($"Focused the {selection.Side} Array for {selection.ActionName}");
                return;
            }

            currentArrayFocusAttempt++;
            if (currentArrayFocusAttempt > MaxManipulatorFocusAttempts)
            {
                var promptNodeText = GetManipulatorArrayPromptText(GetManipulatorArrayNodeListIndex(selection.Side));
                var focusStateText = GetManipulatorArrayFocusStateText();
                FailCurrentRequest($"Could not focus the {selection.Side} Array (node prompt: '{promptNodeText}', focus state: {focusStateText})");
                return;
            }

            TryFocusManipulatorArray(selection.Side);
            ScheduleQueueAfterFrames();
            SetStatus($"Waiting for the {selection.Side} Array to accept logograms for {selection.ActionName} ({currentArrayFocusAttempt}/{MaxManipulatorFocusAttempts})");
        }

        private void TickSelectCategory()
        {
            var selection = GetCurrentQueuedSelection();
            if (selection == null)
            {
                return;
            }

            var ingredient = selection.Recipe[currentRecipeIndex];
            var requiredCategory = GetCategoryButtonIndexForLogogram(ingredient.LogogramID);
            if (requiredCategory == 0)
            {
                FailCurrentRequest($"Unknown category for {Logograms.GetValueOrDefault(ingredient.LogogramID)?.Name ?? ingredient.LogogramID.ToString()}");
                return;
            }

            if (currentCategoryNodeIndex != requiredCategory)
            {
                if (!ClickAddonButton("EurekaMagiciteItemShardList", requiredCategory))
                {
                    FailCurrentRequest($"Could not open shard category for {Logograms.GetValueOrDefault(ingredient.LogogramID)?.Name ?? ingredient.LogogramID.ToString()}");
                    return;
                }

                currentCategoryNodeIndex = requiredCategory;
                ScheduleQueueAfterFrames();
                SetStatus($"Waiting for shard category {requiredCategory} to load");
                return;
            }

            Log.Debug($"Using cached stock and filtered shard list for {Logograms.GetValueOrDefault(ingredient.LogogramID)?.Name ?? ingredient.LogogramID.ToString()}");
            queueStep = QueueStep.PlaceLogogram;
        }

        private void TickPlaceLogogram()
        {
            var selection = GetCurrentQueuedSelection();
            if (selection == null)
            {
                return;
            }

            var ingredient = selection.Recipe[currentRecipeIndex];
            var logogramName = Logograms.GetValueOrDefault(ingredient.LogogramID)?.Name;
            if (string.IsNullOrWhiteSpace(logogramName))
            {
                FailCurrentRequest($"Could not resolve a name for logogram {ingredient.LogogramID}");
                return;
            }

            var visibleIndex = GetLogogramIndexInShardList(ingredient.LogogramID);
            if (visibleIndex < 0)
            {
                FailCurrentRequest($"Could not find visible shard entry for logogram {ingredient.LogogramID}");
                return;
            }

            currentExpectedFilledSlotCount = GetManipulatorArrayFilledSlotCount(selection.Side);
            currentExpectedOtherArrayFilledSlotCount = GetManipulatorArrayFilledSlotCount(GetOppositePlateSide(selection.Side));
            Log.Information($"Placing {logogramName} into {selection.Side} slot {currentSlotIndex}");

            if (!TrySelectVisibleShardListEntry(visibleIndex, logogramName, currentPlacementAttempt))
            {
                FailCurrentRequest($"Could not click shard entry for logogram {ingredient.LogogramID}");
                return;
            }

            ScheduleQueueAfterFrames();
            queueStep = QueueStep.VerifyPlacement;
            SetStatus($"Waiting for {logogramName} to appear in the {selection.Side} Array");
        }

        private void TickVerifyPlacement()
        {
            var selection = GetCurrentQueuedSelection();
            if (selection == null)
            {
                return;
            }

            var ingredient = selection.Recipe[currentRecipeIndex];
            var logogramName = Logograms.GetValueOrDefault(ingredient.LogogramID)?.Name ?? ingredient.LogogramID.ToString();
            var filledSlotCount = GetManipulatorArrayFilledSlotCount(selection.Side);
            var oppositeSide = GetOppositePlateSide(selection.Side);
            var oppositeFilledSlotCount = GetManipulatorArrayFilledSlotCount(oppositeSide);

            if (filledSlotCount > currentExpectedFilledSlotCount)
            {
                Log.Information($"Verified {logogramName} in the {selection.Side} Array");
                currentQuantityIndex++;
                currentSlotIndex = filledSlotCount;
                currentPlacementAttempt = 0;

                if (currentQuantityIndex >= ingredient.Quantity)
                {
                    currentQuantityIndex = 0;
                    currentRecipeIndex++;
                }

                SetStatus($"Placed {logogramName} into the {selection.Side} Array");
                if (currentRecipeIndex >= selection.Recipe.Count)
                {
                    currentSelectionIndex++;
                    currentRecipeIndex = 0;
                    currentQuantityIndex = 0;
                    currentSlotIndex = 0;
                    currentPlacementAttempt = 0;
                    queueStep = currentSelectionIndex >= currentQueuedSelections.Count
                        ? QueueStep.ExtractMneme
                        : QueueStep.SelectArray;
                }
                else
                {
                    queueStep = QueueStep.SelectCategory;
                }

                ScheduleQueueAfterFrames();
                return;
            }

            if (oppositeFilledSlotCount > currentExpectedOtherArrayFilledSlotCount)
            {
                if (TryRecoverFromWrongArrayPlacement(selection.Side, oppositeSide, logogramName))
                {
                    return;
                }

                FailCurrentRequest($"Selected {logogramName} from the shard list but it populated the {oppositeSide} Array instead of the {selection.Side} Array");
                return;
            }

            currentPlacementAttempt++;
            if (currentPlacementAttempt >= MaxShardSelectionAttempts)
            {
                FailCurrentRequest($"Selected {logogramName} from the shard list but it never appeared in the {selection.Side} Array");
                return;
            }

            Log.Warning($"Selection attempt {currentPlacementAttempt} for '{logogramName}' did not populate the {selection.Side} Array; retrying");
            queueStep = QueueStep.PlaceLogogram;
            ScheduleQueueAfterFrames();
        }

        private void TickExtractMneme()
        {
            if (!Configuration.AutoDestroyWhenMagiaBoardFull && IsMagiaBoardFull)
            {
                BlockCurrentQueue("Logos Actions are full; enable Auto Destroy When Full or free a Logos Action slot", QueueBlockReason.MagiaBoardFull, dequeueCurrentRequest: false);
                return;
            }

            CaptureCurrentExtractionResultBaseline();
            if (TryExtractMneme())
            {
                var preserveConsumedStock = retryUsesLoadedPlate;
                retryUsesLoadedPlate = false;
                pendingMnemeExtractionResult = MnemeExtractionResult.None;
                if (!preserveConsumedStock)
                {
                    currentExtractionAttemptStockConsumed = false;
                }

                queueStep = QueueStep.WaitForSuccess;
                queueStepStartedAt = DateTime.UtcNow;
                ScheduleQueueAfterFrames();
                SetStatus($"Waiting for extraction result for {currentPlateRequest?.Label}");
                return;
            }

            currentExtractionResultBaselineSignature = string.Empty;
            currentExtractionResultClearedSinceStart = true;
            FailCurrentRequest("Could not click Extract Mneme");
        }

        private void TickWaitForSuccess()
        {
            if (DateTime.UtcNow - queueStepStartedAt > SuccessTimeout)
            {
                FailCurrentRequest("Timed out waiting for Mneme extraction");
                return;
            }

            var extractionResult = GetMnemeExtractionResult();
            if (extractionResult == MnemeExtractionResult.None)
            {
                currentExtractionResultClearedSinceStart = true;
                ScheduleQueueAfterFrames();
                return;
            }

            var currentResultSignature = GetCurrentMnemeExtractionResultSignature();
            if (!currentExtractionResultClearedSinceStart
                && !string.IsNullOrEmpty(currentExtractionResultBaselineSignature)
                && string.Equals(currentResultSignature, currentExtractionResultBaselineSignature, StringComparison.Ordinal))
            {
                ScheduleQueueAfterFrames();
                return;
            }

            ConsumeCurrentPlateAttemptStockIfNeeded();
            pendingMnemeExtractionResult = extractionResult;
            queueStep = QueueStep.AcknowledgeSuccess;
            queueStepStartedAt = DateTime.UtcNow;
            ScheduleQueueAfterFrames();

            switch (extractionResult)
            {
                case MnemeExtractionResult.Failed:
                    SetStatus($"Extraction failed for {currentPlateRequest?.Label}; acknowledging");
                    break;
                case MnemeExtractionResult.SuccessMagiaBoardFull:
                    SetStatus($"Mneme extracted for {currentPlateRequest?.Label}; Logos Actions are full");
                    break;
                default:
                    SetStatus($"Mneme extracted for {currentPlateRequest?.Label}; acknowledging");
                    break;
            }
        }

        private void TickWaitForRetryReady()
        {
            var label = currentPlateRequest?.Label ?? "current plate";
            if (DateTime.UtcNow - queueStepStartedAt > SuccessTimeout)
            {
                FailCurrentRequest($"Timed out waiting to retry failed extraction for {label}");
                return;
            }

            if (!IsManipulatorReadyForRetry())
            {
                ScheduleQueueAfterFrames();
                return;
            }

            if (!HasPendingManipulatorSelection())
            {
                retryUsesLoadedPlate = false;
                ResetCurrentPlateBuildProgress();
                queueStep = QueueStep.PrepareManipulator;
                queueStepStartedAt = DateTime.UtcNow;
                ScheduleQueueAfterFrames();
                SetStatus($"Retry prompt cleared for {label}; rebuilding the plate");
                return;
            }

            queueStep = QueueStep.ExtractMneme;
            queueStepStartedAt = DateTime.UtcNow;
            ScheduleQueueAfterFrames();
            SetStatus($"Retrying failed extraction for {label}");
        }

        private void TickAcknowledgeSuccess()
        {
            if (DateTime.UtcNow - queueStepStartedAt > SuccessTimeout)
            {
                var timeoutMessage = pendingMnemeExtractionResult == MnemeExtractionResult.Failed
                    ? "Timed out waiting to acknowledge failed Mneme extraction"
                    : "Timed out waiting to confirm Mneme extraction";
                FailCurrentRequest(timeoutMessage);
                return;
            }

            if (pendingMnemeExtractionResult == MnemeExtractionResult.Failed)
            {
                TickAcknowledgeFailedExtraction();
                return;
            }

            if (pendingDestroyConfirmation)
            {
                if (TryConfirmDestroyYesNo())
                {
                    queueStepStartedAt = DateTime.UtcNow;
                    ScheduleQueueAfterFrames();
                    SetStatus("Confirmed destruction of extracted mneme");
                    return;
                }

                if (IsSelectYesNoVisible() || IsMagiaBoardFullWarningVisible())
                {
                    ScheduleQueueAfterFrames();
                    return;
                }

                pendingDestroyConfirmation = false;
                CompleteCurrentRequest();
                return;
            }

            if (IsMagiaBoardFullWarningVisible())
            {
                if (Configuration.AutoDestroyWhenMagiaBoardFull)
                {
                    if (TryDestroyExtractedMneme())
                    {
                        pendingDestroyConfirmation = true;
                        queueStepStartedAt = DateTime.UtcNow;
                        ScheduleQueueAfterFrames();
                        SetStatus("Confirming destruction of extracted mneme");
                        return;
                    }

                    ScheduleQueueAfterFrames();
                    return;
                }

                BlockCurrentQueue("Mneme extracted but Logos Actions are full; use Destroy or Replace manually", QueueBlockReason.PendingManualMnemePrompt, dequeueCurrentRequest: true);
                return;
            }

            if (TryAcknowledgeMnemeExtraction())
            {
                CompleteCurrentRequest();
                return;
            }

            ScheduleQueueAfterFrames();
        }

        private void TickAcknowledgeFailedExtraction()
        {
            var label = currentPlateRequest?.Label ?? "current plate";

            if (!TryAcknowledgeMnemeExtraction())
            {
                ScheduleQueueAfterFrames();
                return;
            }

            pendingMnemeExtractionResult = MnemeExtractionResult.None;
            if (Configuration.AutoRetryFailedExtraction && currentQueuedSelections.Count > 0 && HasEnoughLogogramsForPlate(currentQueuedSelections))
            {
                Log.Warning($"Mneme extraction failed for {label}; retrying the same plate");
                retryUsesLoadedPlate = true;
                queueStep = QueueStep.WaitForRetryReady;
                queueStepStartedAt = DateTime.UtcNow;
                ScheduleQueueAfterFrames();
                SetStatus($"Waiting to retry failed extraction for {label}");
                return;
            }

            var failureMessage = Configuration.AutoRetryFailedExtraction
                ? $"Mneme extraction failed for {label}; not enough logograms remain to retry the same plate"
                : $"Mneme extraction failed for {label}";
            FailCurrentRequest(failureMessage);
        }

        private void CompleteCurrentRequest()
        {
            if (currentPlateRequest == null)
            {
                return;
            }

            ConsumeCurrentPlateAttemptStockIfNeeded();
            Log.Information($"Synthesis completed: {currentPlateRequest.Label}");
            SynthesisQueue.Dequeue();
            ScheduleLogosActionRefresh(LogosActionRefreshDelayFrames, resetAttempts: true);
            ResetQueueState($"Completed {currentPlateRequest.Label}");
        }

        private void FailCurrentRequest(string message)
        {
            Log.Error(message);
            if (SynthesisQueue.Count > 0)
            {
                SynthesisQueue.Dequeue();
            }

            ResetQueueState(message);
        }

        private void BlockCurrentQueue(string message, QueueBlockReason blockReason, bool dequeueCurrentRequest)
        {
            Log.Warning(message);

            if (dequeueCurrentRequest && SynthesisQueue.Count > 0)
            {
                SynthesisQueue.Dequeue();
            }

            queueBlockReason = blockReason;
            ResetQueueState(message);
        }

        private void ResetQueueState(string status)
        {
            currentPlateRequest = null;
            currentQueuedSelections.Clear();
            ResetCurrentPlateBuildProgress();
            pendingMnemeExtractionResult = MnemeExtractionResult.None;
            currentExtractionAttemptStockConsumed = false;
            retryUsesLoadedPlate = false;
            currentExtractionResultBaselineSignature = string.Empty;
            currentExtractionResultClearedSinceStart = true;
            waitingForPreviousExtractionPromptToClear = false;
            queueStep = QueueStep.Idle;
            queueStepStartedAt = DateTime.MinValue;
            nextQueueActionFrame = 0;
            pendingDestroyConfirmation = false;
            IsProcessingQueue = false;
            SetStatus(status);
        }

        private void SetStatus(string status)
        {
            LastStatus = status;
            Log.Debug(status);
        }

        private void ConsumePlateRequestStock(PlateQueueRequest request)
        {
            foreach (var selection in request.GetOrderedSelections())
            {
                foreach (var ingredient in selection.Recipe)
                {
                    if (LogogramStock.TryGetValue(ingredient.LogogramID, out var currentStock))
                    {
                        LogogramStock[ingredient.LogogramID] = Math.Max(0, currentStock - ingredient.Quantity);
                    }
                }
            }
        }

        private void ConsumeCurrentPlateAttemptStockIfNeeded()
        {
            if (currentExtractionAttemptStockConsumed || currentPlateRequest == null)
            {
                return;
            }

            ConsumePlateRequestStock(currentPlateRequest);
            currentExtractionAttemptStockConsumed = true;
        }

        private void ResetCurrentPlateBuildProgress()
        {
            currentSelectionIndex = 0;
            currentRecipeIndex = 0;
            currentQuantityIndex = 0;
            currentSlotIndex = 0;
            currentPlacementAttempt = 0;
            currentExpectedFilledSlotCount = 0;
            currentExpectedOtherArrayFilledSlotCount = 0;
            currentWrongArrayRecoveryCount = 0;
            currentArrayFocusAttempt = 0;
        }

        internal bool IsManipulatorVisible()
        {
            return GetAddon("EurekaMagiciteItemShardList") != null || GetAddon("EurekaMagiciteItemSynthesis") != null;
        }

        private AtkUnitBase* GetAddon(string addonName)
        {
            try
            {
                var visibleAddon = (AtkUnitBase*)GameGui.GetAddonByName(addonName, 1).Address;
                if (visibleAddon != null)
                {
                    return visibleAddon;
                }
            }
            catch
            {
            }

            try
            {
                return AtkStage.Instance()->RaptureAtkUnitManager->GetAddonByName(addonName);
            }
            catch
            {
                return null;
            }
        }

        internal string GetActionName(uint actionId)
        {
            var row = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRow(actionId);
            if (row.RowId != 0)
            {
                return ExtractText(row.Name);
            }

            return $"Action {actionId}";
        }

        private bool TryBuildPlateSelection(PlateSide side, uint actionId, out PlateActionSelection selection, out string error)
        {
            selection = new PlateActionSelection();
            error = string.Empty;

            var action = GetLogosAction(actionId);
            if (action == null)
            {
                error = $"Unknown action {actionId}";
                return false;
            }

            var recipe = GetResolvedRecipe(action, out var recipeIndex);
            if (recipe == null)
            {
                error = $"No recipe data for {GetActionName(actionId)}";
                return false;
            }

            selection = new PlateActionSelection
            {
                Side = side,
                ActionId = actionId,
                ActionName = GetActionName(actionId),
                Recipe = recipe,
                RecipeIndex = recipeIndex,
            };
            return true;
        }

        private bool TryBuildPlateRequest(uint? astralActionId, uint? umbralActionId, out PlateQueueRequest request, out string error)
        {
            request = new PlateQueueRequest();
            error = string.Empty;
            PlateActionSelection? astralSelection = null;
            PlateActionSelection? umbralSelection = null;
            PlateActionSelection builtAstralSelection = new();
            PlateActionSelection builtUmbralSelection = new();

            if (!TryValidatePlateLayout(astralActionId, umbralActionId, out error))
            {
                return false;
            }

            if (astralActionId.HasValue && !TryBuildPlateSelection(PlateSide.Astral, astralActionId.Value, out builtAstralSelection, out error))
            {
                return false;
            }
            else if (astralActionId.HasValue)
            {
                astralSelection = builtAstralSelection;
            }

            if (umbralActionId.HasValue && !TryBuildPlateSelection(PlateSide.Umbral, umbralActionId.Value, out builtUmbralSelection, out error))
            {
                return false;
            }
            else if (umbralActionId.HasValue)
            {
                umbralSelection = builtUmbralSelection;
            }

            var selections = new List<PlateActionSelection>(2);
            if (astralSelection != null)
            {
                selections.Add(astralSelection);
            }

            if (umbralSelection != null)
            {
                selections.Add(umbralSelection);
            }

            if (!HasEnoughLogogramsForPlate(selections))
            {
                error = PlateInsufficientLogogramsError;
                return false;
            }

            request = new PlateQueueRequest
            {
                Astral = astralSelection,
                Umbral = umbralSelection,
                Label = FormatPlateLabel(astralActionId, umbralActionId),
            };
            return true;
        }

        private static bool TryValidatePlateLayout(uint? astralActionId, uint? umbralActionId, out string error)
        {
            error = string.Empty;

            if (!astralActionId.HasValue && !umbralActionId.HasValue)
            {
                error = PlateNoActionsSelectedError;
                return false;
            }

            if (!astralActionId.HasValue && umbralActionId.HasValue)
            {
                error = PlateRequiresAstralError;
                return false;
            }

            return true;
        }

        private int GetCombinedPlateCraftableCount(IReadOnlyCollection<PlateActionSelection> selections)
        {
            if (selections.Count == 0)
            {
                return 0;
            }

            var combinedRequirements = new Dictionary<int, int>();
            foreach (var selection in selections)
            {
                foreach (var ingredient in selection.Recipe)
                {
                    combinedRequirements.TryGetValue(ingredient.LogogramID, out var existingRequiredCount);
                    combinedRequirements[ingredient.LogogramID] = existingRequiredCount + ingredient.Quantity;
                }
            }

            if (combinedRequirements.Count == 0)
            {
                return 0;
            }

            var craftableCount = int.MaxValue;
            foreach (var requirement in combinedRequirements)
            {
                var available = LogogramStock.GetValueOrDefault(requirement.Key, 0);
                craftableCount = Math.Min(craftableCount, available / requirement.Value);
            }

            return craftableCount == int.MaxValue ? 0 : craftableCount;
        }

        internal bool TryGetRecipeBareMinimumGilCost(IReadOnlyList<Recipe>? recipe, out long gilCost)
        {
            gilCost = 0;
            if (recipe == null || recipe.Count == 0)
            {
                return false;
            }

            long totalCost = 0;
            foreach (var ingredient in recipe)
            {
                if (!logogramSourceItemIdByLogogramId.TryGetValue(ingredient.LogogramID, out var sourceItemId))
                {
                    return false;
                }

                totalCost = checked(totalCost + (GetConfiguredLogogramSourceGilCost(sourceItemId) * ingredient.Quantity));
            }

            gilCost = totalCost;
            return true;
        }

        internal bool TryGetActionBareMinimumGilCost(uint actionId, out long gilCost)
        {
            gilCost = 0;

            var action = GetLogosAction(actionId);
            if (action == null)
            {
                return false;
            }

            var recipe = GetResolvedRecipe(action, out _);
            return TryGetRecipeBareMinimumGilCost(recipe, out gilCost);
        }

        internal bool TryGetPlateBareMinimumGilCost(uint? astralActionId, uint? umbralActionId, out long gilCost)
        {
            gilCost = 0;
            long totalCost = 0;
            var hasAnyAction = false;

            if (astralActionId.HasValue)
            {
                if (!TryGetActionBareMinimumGilCost(astralActionId.Value, out var astralCost))
                {
                    return false;
                }

                totalCost = checked(totalCost + astralCost);
                hasAnyAction = true;
            }

            if (umbralActionId.HasValue)
            {
                if (!TryGetActionBareMinimumGilCost(umbralActionId.Value, out var umbralCost))
                {
                    return false;
                }

                totalCost = checked(totalCost + umbralCost);
                hasAnyAction = true;
            }

            if (!hasAnyAction)
            {
                return false;
            }

            gilCost = totalCost;
            return true;
        }

        internal bool TryGetPendingPlateBareMinimumGilCost(out long gilCost)
        {
            return TryGetPlateBareMinimumGilCost(PendingAstralActionId, PendingUmbralActionId, out gilCost);
        }

        internal bool TryGetFavoritePlateBareMinimumGilCost(FavoritePlate plate, out long gilCost)
        {
            var (astralActionId, umbralActionId) = GetFavoritePlateActions(plate);
            return TryGetPlateBareMinimumGilCost(astralActionId, umbralActionId, out gilCost);
        }

        private bool HasEnoughLogogramsForPlate(IReadOnlyCollection<PlateActionSelection> selections)
        {
            return GetCombinedPlateCraftableCount(selections) > 0;
        }

        private (uint? AstralActionId, uint? UmbralActionId) GetFavoritePlateActions(FavoritePlate plate)
        {
            uint? astralActionId = plate.AstralActionId;
            uint? umbralActionId = plate.UmbralActionId;

            if (!astralActionId.HasValue && plate.ActionIds.Count > 0)
            {
                astralActionId = plate.ActionIds[0];
            }

            if (!umbralActionId.HasValue && plate.ActionIds.Count > 1)
            {
                umbralActionId = plate.ActionIds[1];
            }

            return (astralActionId, umbralActionId);
        }

        private string FormatPlateLabel(uint? astralActionId, uint? umbralActionId)
        {
            var labels = new List<string>();
            if (umbralActionId.HasValue)
            {
                labels.Add(GetActionName(umbralActionId.Value));
            }

            if (astralActionId.HasValue)
            {
                labels.Add(GetActionName(astralActionId.Value));
            }

            return labels.Count == 0 ? "Empty Plate" : string.Join(" / ", labels);
        }

        private static string GetCompactActionLabel(string actionName)
        {
            const string wisdomPrefix = "Wisdom of the ";
            return actionName.StartsWith(wisdomPrefix, StringComparison.OrdinalIgnoreCase)
                ? actionName[wisdomPrefix.Length..]
                : actionName;
        }

        private string FormatCompactPlateLabel(uint? astralActionId, uint? umbralActionId)
        {
            var labels = new List<string>();
            if (umbralActionId.HasValue)
            {
                labels.Add(GetCompactActionLabel(GetActionName(umbralActionId.Value)));
            }

            if (astralActionId.HasValue)
            {
                labels.Add(GetCompactActionLabel(GetActionName(astralActionId.Value)));
            }

            return labels.Count == 0 ? "Empty Plate" : string.Join(" / ", labels);
        }

        internal string DescribeFavoritePlate(FavoritePlate plate)
        {
            if (!string.IsNullOrWhiteSpace(plate.Name))
            {
                return plate.Name;
            }

            var (astralActionId, umbralActionId) = GetFavoritePlateActions(plate);
            return FormatPlateLabel(astralActionId, umbralActionId);
        }

        internal string DescribeFavoritePlateCompact(FavoritePlate plate)
        {
            if (!string.IsNullOrWhiteSpace(plate.Name))
            {
                return plate.Name;
            }

            var (astralActionId, umbralActionId) = GetFavoritePlateActions(plate);
            return FormatCompactPlateLabel(astralActionId, umbralActionId);
        }

        internal List<string> GetFavoritePlateActionNames(FavoritePlate plate)
        {
            var (astralActionId, umbralActionId) = GetFavoritePlateActions(plate);
            var names = new List<string>();
            if (umbralActionId.HasValue)
            {
                names.Add(GetActionName(umbralActionId.Value));
            }

            if (astralActionId.HasValue)
            {
                names.Add(GetActionName(astralActionId.Value));
            }

            return names;
        }

        internal bool TryGetSynthesisOverlayAnchor(out System.Numerics.Vector2 position)
        {
            position = System.Numerics.Vector2.Zero;

            var addon = GetAddon("EurekaMagiciteItemSynthesis");
            if (addon == null || !addon->IsVisible || addon->RootNode == null)
            {
                return false;
            }

            position = new System.Numerics.Vector2(addon->RootNode->ScreenX, Math.Max(0f, addon->RootNode->ScreenY - 48f));
            return true;
        }

        internal bool TryGetSynthesisAutomationOverlayAnchor(out System.Numerics.Vector2 position)
        {
            return TryGetVisibleSynthesisButtonAnchor(5, label => label.Contains("Extract", StringComparison.OrdinalIgnoreCase), out position)
                || TryGetVisibleSynthesisButtonAnchor(4, label => label.Contains("Extract", StringComparison.OrdinalIgnoreCase), out position);
        }

        private static string ExtractText(ReadOnlySeString value)
        {
            var text = value.ExtractText();
            return string.IsNullOrWhiteSpace(text) ? string.Empty : text;
        }

        private int GetCategoryButtonIndexForLogogram(int logogramId)
        {
            var name = Logograms.GetValueOrDefault(logogramId)?.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                return 0;
            }

            return categoryNodeByLogogramName.GetValueOrDefault(name, 0);
        }

        private unsafe int GetLogogramIndexInShardList(int logogramId)
        {
            try
            {
                var framework = Framework.Instance();
                if (framework == null)
                {
                    return -1;
                }

                var uiModule = framework->GetUIModule();
                if (uiModule == null)
                {
                    return -1;
                }

                var raptureAtkModule = uiModule->GetRaptureAtkModule();
                if (raptureAtkModule == null)
                {
                    return -1;
                }

                var numberArray = raptureAtkModule->AtkModule.AtkArrayDataHolder.NumberArrays[137];
                if (numberArray == null || numberArray->IntArray == null)
                {
                    return -1;
                }

                var count = numberArray->IntArray[0];
                if (count <= 0 || count > 200)
                {
                    return -1;
                }

                for (var i = 1; i <= count; i++)
                {
                    if (numberArray->IntArray[(4 * i) + 1] == logogramId)
                    {
                        return i - 1;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error in GetLogogramIndexInShardList: {ex.Message}");
            }

            return -1;
        }

        private unsafe MnemeExtractionResult GetMnemeExtractionResult()
        {
            var addon = GetAddon("EurekaMagiciteItemSynthesis");
            if (addon == null || !addon->IsVisible)
            {
                return MnemeExtractionResult.None;
            }

            var resultText = GetVisibleMnemeExtractionResultText(addon);
            return ParseMnemeExtractionResult(resultText, IsMagiaBoardFullWarningVisible());
        }

        private void CaptureCurrentExtractionResultBaseline()
        {
            currentExtractionResultBaselineSignature = GetCurrentMnemeExtractionResultSignature();
            currentExtractionResultClearedSinceStart = string.IsNullOrEmpty(currentExtractionResultBaselineSignature);
        }

        private unsafe string GetCurrentMnemeExtractionResultSignature()
        {
            var addon = GetAddon("EurekaMagiciteItemSynthesis");
            if (addon == null || !addon->IsVisible)
            {
                return string.Empty;
            }

            var resultText = GetVisibleMnemeExtractionResultText(addon);
            var hasFullWarning = IsMagiaBoardFullWarningVisible();
            var result = ParseMnemeExtractionResult(resultText, hasFullWarning);
            if (result == MnemeExtractionResult.None)
            {
                return string.Empty;
            }

            return $"{(int)result}|{resultText}|{(hasFullWarning ? 1 : 0)}";
        }

        private static MnemeExtractionResult ParseMnemeExtractionResult(string resultText, bool hasFullWarning)
        {
            if (ContainsMnemeExtractionFailureText(resultText))
            {
                return MnemeExtractionResult.Failed;
            }

            if (hasFullWarning)
            {
                return MnemeExtractionResult.SuccessMagiaBoardFull;
            }

            if (ContainsMnemeExtractionSuccessText(resultText))
            {
                return MnemeExtractionResult.Success;
            }

            return MnemeExtractionResult.None;
        }

        private unsafe bool IsMagiaBoardFullWarningVisible()
        {
            var addon = GetAddon("EurekaMagiciteItemSynthesis");
            if (addon == null || !addon->IsVisible)
            {
                return false;
            }

            var nodeText = FindVisibleSynthesisText(addon, ContainsMagiaBoardFullWarning);
            if (ContainsMagiaBoardFullWarning(nodeText))
            {
                return true;
            }

            if (IsManipulatorMainScreenVisible())
            {
                return false;
            }

            if (addon->AtkValues == null || addon->AtkValuesCount <= 32)
            {
                return false;
            }

            try
            {
                var value = &addon->AtkValues[32];
                var warningText = value->Type switch
                {
                    ValueType.String or ValueType.String8 or ValueType.ManagedString => CleanAddonText(value->String.ToString()),
                    _ => string.Empty,
                };

                return ContainsMagiaBoardFullWarning(warningText);
            }
            catch
            {
                return false;
            }
        }

        private bool IsSelectYesNoVisible()
        {
            var addon = GetAddon("SelectYesno");
            return addon != null && addon->IsVisible;
        }

        private static bool ContainsMagiaBoardFullWarning(string text)
        {
            return !string.IsNullOrWhiteSpace(text)
                && text.Contains("cannot accommodate any more logos actions", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsMnemeExtractionSuccessText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            return text.Contains("mneme extracted", StringComparison.OrdinalIgnoreCase)
                || text.Contains("mnemes extracted", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsMnemeExtractionFailureText(string text)
        {
            return !string.IsNullOrWhiteSpace(text)
                && text.Contains("extraction failed", StringComparison.OrdinalIgnoreCase);
        }

        private bool TryExtractMneme()
        {
            if (TryClickVisibleSynthesisButton(5, label => label.Contains("Extract", StringComparison.OrdinalIgnoreCase), "Extract Mneme"))
            {
                return true;
            }

            if (TryClickVisibleSynthesisButton(4, label => label.Contains("Extract", StringComparison.OrdinalIgnoreCase), "Extract Mneme"))
            {
                return true;
            }

            if (FireCallback("EurekaMagiciteItemSynthesis", 2, 0))
            {
                Log.Information("Triggered Extract Mneme via synthesis callback [2, 0]");
                return true;
            }

            return false;
        }

        private unsafe bool HasPendingManipulatorSelection()
        {
            var addon = GetAddon("EurekaMagiciteItemSynthesis");
            if (addon == null || !addon->IsVisible)
            {
                return false;
            }

            if (addon->UldManager.NodeListCount > 3)
            {
                var destroyNode = addon->UldManager.NodeList[3];
                if (destroyNode != null && destroyNode->IsVisible())
                {
                    return true;
                }
            }

            if (GetManipulatorArrayFilledSlotCount(PlateSide.Astral) > 0 || GetManipulatorArrayFilledSlotCount(PlateSide.Umbral) > 0)
            {
                return true;
            }

            return false;
        }

        private unsafe string GetManipulatorSuccessRateText()
        {
            var addon = GetAddon("EurekaMagiciteItemSynthesis");
            if (addon == null || !addon->IsVisible)
            {
                return string.Empty;
            }

            var visibleText = GetVisibleAddonTextNodeText(addon, 14);
            if (!string.IsNullOrWhiteSpace(visibleText))
            {
                return visibleText;
            }

            if (addon->AtkValues == null || addon->AtkValuesCount <= 36)
            {
                return string.Empty;
            }

            try
            {
                var value = &addon->AtkValues[36];
                return value->Type switch
                {
                    ValueType.String or ValueType.String8 or ValueType.ManagedString => CleanAddonText(value->String.ToString()),
                    _ => string.Empty,
                };
            }
            catch
            {
                return string.Empty;
            }
        }

        private unsafe bool IsManipulatorMainScreenVisible()
        {
            var addon = GetAddon("EurekaMagiciteItemSynthesis");
            if (addon == null || !addon->IsVisible)
            {
                return false;
            }

            var successRateLabel = GetVisibleAddonTextNodeText(addon, 15);
            var successRateValue = GetVisibleAddonTextNodeText(addon, 14);
            return successRateLabel.Contains("Success Rate", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(successRateValue);
        }

        private bool IsManipulatorReadyForRetry()
        {
            return IsManipulatorMainScreenVisible()
                && GetMnemeExtractionResult() == MnemeExtractionResult.None;
        }

        private unsafe string GetManipulatorArrayPromptText(int nodeListIndex)
        {
            var addon = GetAddon("EurekaMagiciteItemSynthesis");
            if (addon == null || !addon->IsVisible || nodeListIndex < 0 || nodeListIndex >= addon->UldManager.NodeListCount)
            {
                return string.Empty;
            }

            var node = (AtkComponentNode*)addon->UldManager.NodeList[nodeListIndex];
            if (node == null || node->Component == null)
            {
                return string.Empty;
            }

            try
            {
                var promptNode = (AtkTextNode*)node->Component->UldManager.SearchNodeById(13);
                if (promptNode == null || !promptNode->AtkResNode.IsVisible())
                {
                    return string.Empty;
                }

                return CleanAddonText(promptNode->NodeText.ToString());
            }
            catch
            {
                return string.Empty;
            }
        }

        private unsafe int GetAddonAtkValueAsInt(AtkUnitBase* addon, int valueIndex)
        {
            if (addon == null || !addon->IsVisible || addon->AtkValues == null || valueIndex < 0 || addon->AtkValuesCount <= valueIndex)
            {
                return 0;
            }

            try
            {
                var value = &addon->AtkValues[valueIndex];
                return value->Type switch
                {
                    ValueType.UInt => (int)value->UInt,
                    ValueType.Int => value->Int,
                    ValueType.Bool => value->Int,
                    _ => 0,
                };
            }
            catch
            {
                return 0;
            }
        }

        private unsafe string GetManipulatorArrayFocusStateText()
        {
            var addon = GetAddon("EurekaMagiciteItemSynthesis");
            if (addon == null || !addon->IsVisible)
            {
                return "unavailable";
            }

            var astralValue = GetAddonAtkValueAsInt(addon, ManipulatorAstralFocusValueIndex);
            var umbralValue = GetAddonAtkValueAsInt(addon, ManipulatorUmbralFocusValueIndex);
            return $"atk[{ManipulatorAstralFocusValueIndex}]={astralValue}, atk[{ManipulatorUmbralFocusValueIndex}]={umbralValue}";
        }

        private unsafe bool IsManipulatorArrayInputVisible(PlateSide side)
        {
            var nodeListIndex = GetManipulatorArrayNodeListIndex(side);
            var addon = GetAddon("EurekaMagiciteItemSynthesis");
            if (addon == null || !addon->IsVisible || nodeListIndex < 0 || nodeListIndex >= addon->UldManager.NodeListCount)
            {
                return false;
            }

            var componentNode = (AtkComponentNode*)addon->UldManager.NodeList[nodeListIndex];
            if (componentNode == null || componentNode->Component == null)
            {
                return false;
            }

            try
            {
                var tabNode = (AtkComponentNode*)componentNode->Component->UldManager.SearchNodeById(14);
                var textInputNode = tabNode != null && tabNode->Component != null
                    ? tabNode->Component->UldManager.SearchNodeById(4)
                    : componentNode->Component->UldManager.SearchNodeById(4);

                return textInputNode != null && textInputNode->IsVisible();
            }
            catch
            {
                return false;
            }
        }

        private unsafe bool IsManipulatorArrayReadyForSelection(PlateSide side)
        {
            var addon = GetAddon("EurekaMagiciteItemSynthesis");
            if (addon == null || !addon->IsVisible)
            {
                return false;
            }

            var astralValue = GetAddonAtkValueAsInt(addon, ManipulatorAstralFocusValueIndex);
            var umbralValue = GetAddonAtkValueAsInt(addon, ManipulatorUmbralFocusValueIndex);

            return side switch
            {
                PlateSide.Astral => astralValue > 0 && umbralValue <= 0,
                PlateSide.Umbral => umbralValue > 0 && astralValue <= 0,
                _ => false,
            };
        }

        private int GetManipulatorArrayNodeListIndex(PlateSide side)
        {
            return side == PlateSide.Astral ? 16 : 17;
        }

        private static PlateSide GetOppositePlateSide(PlateSide side)
        {
            return side == PlateSide.Astral ? PlateSide.Umbral : PlateSide.Astral;
        }

        private unsafe bool TryGetManipulatorArrayNode(PlateSide side, out AtkUnitBase* addon, out AtkComponentNode* arrayNode)
        {
            addon = GetAddon("EurekaMagiciteItemSynthesis");
            arrayNode = null;

            var nodeListIndex = GetManipulatorArrayNodeListIndex(side);
            if (addon == null || !addon->IsVisible || nodeListIndex < 0 || nodeListIndex >= addon->UldManager.NodeListCount)
            {
                return false;
            }

            arrayNode = (AtkComponentNode*)addon->UldManager.NodeList[nodeListIndex];
            return arrayNode != null && arrayNode->Component != null;
        }

        private unsafe bool TryFocusManipulatorArray(PlateSide side)
        {
            var nodeListIndex = GetManipulatorArrayNodeListIndex(side);
            var addon = GetAddon("EurekaMagiciteItemSynthesis");
            if (addon == null || !addon->IsVisible || nodeListIndex >= addon->UldManager.NodeListCount)
            {
                return false;
            }

            var node = (AtkComponentNode*)addon->UldManager.NodeList[nodeListIndex];
            if (node == null || node->Component == null)
            {
                return false;
            }

            try
            {
                if (side == PlateSide.Umbral && FireCallback("EurekaMagiciteItemSynthesis", true, 29, 1))
                {
                    Log.Information("Triggered Umbral Array focus callback [29, 1] with updateVisibility=true");
                    return true;
                }

                var callbackIndex = side == PlateSide.Astral ? 0 : 1;
                if (FireCallback("EurekaMagiciteItemSynthesis", 0, callbackIndex))
                {
                    Log.Information($"Triggered {side} Array focus callback [0, {callbackIndex}]");
                }

                var panelCollisionNode = node->Component->UldManager.SearchNodeById(21);
                if (side == PlateSide.Umbral)
                {
                    if (TryClickVisibleAddonButton("EurekaMagiciteItemSynthesis", nodeListIndex, $"{side} Array root component"))
                    {
                        return true;
                    }

                    if (TrySendAddonNodeMouseClick(addon, panelCollisionNode, $"{side} Array panel collision node 21"))
                    {
                        return true;
                    }

                    if (TrySendAddonNodeMouseClick(addon, (AtkResNode*)node, $"{side} Array root panel"))
                    {
                        return true;
                    }
                }

                var promptNode = node->Component->UldManager.SearchNodeById(13);
                if (TrySendAddonNodeMouseClick(addon, promptNode, $"{side} Array prompt node 13"))
                {
                    return true;
                }

                var tabNode = (AtkComponentNode*)node->Component->UldManager.SearchNodeById(14);
                var textInputNode = tabNode != null && tabNode->Component != null
                    ? tabNode->Component->UldManager.SearchNodeById(4)
                    : null;
                if (TrySendAddonNodeMouseClick(addon, textInputNode, $"{side} Array text input node 4"))
                {
                    return true;
                }

                if (TrySendAddonNodeMouseClick(addon, panelCollisionNode, $"{side} Array panel collision node 21"))
                {
                    return true;
                }

                if (TrySendAddonNodeMouseClick(addon, (AtkResNode*)node, $"{side} Array root panel"))
                {
                    return true;
                }

                if (tabNode != null && tabNode->Component != null)
                {
                    foreach (var searchNodeId in new uint[] { 6, 4 })
                    {
                        var focusNode = tabNode->Component->UldManager.SearchNodeById(searchNodeId);
                        if (focusNode == null || !focusNode->IsVisible())
                        {
                            continue;
                        }

                        if (TrySendAddonNodeMouseClick(addon, focusNode, $"{side} Array tab node {searchNodeId}"))
                        {
                            return true;
                        }

                        if (!DispatchAddonNodeEvent(addon, focusNode, (AtkEventType)25))
                        {
                            continue;
                        }

                        Log.Information($"Clicked {side} Array using tab focus node {searchNodeId}");
                        return true;
                    }
                }

                if (panelCollisionNode != null && panelCollisionNode->IsVisible() && DispatchAddonNodeEvent(addon, panelCollisionNode, (AtkEventType)25))
                {
                    Log.Information($"Clicked {side} Array using collision node 21");
                    return true;
                }

                if (TryClickVisibleAddonButton("EurekaMagiciteItemSynthesis", nodeListIndex, $"{side} Array"))
                {
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Log.Error($"TryFocusManipulatorArray error for {side}: {ex.Message}");
                return false;
            }
        }

        private unsafe bool TrySendAddonNodeMouseClick(AtkUnitBase* addon, AtkResNode* node, string description)
        {
            if (addon == null || node == null || !node->IsVisible())
            {
                return false;
            }

            try
            {
                var evt = node->AtkEventManager.Event;
                if (evt == null)
                {
                    return false;
                }

                var data = stackalloc AtkEventData[1];
                for (var i = 0; i < sizeof(AtkEventData); i++)
                {
                    ((byte*)data)[i] = 0;
                }

                addon->ReceiveEvent(AtkEventType.MouseDown, (int)evt->Param, evt, data);
                addon->ReceiveEvent(AtkEventType.MouseClick, (int)evt->Param, evt, data);
                addon->ReceiveEvent(AtkEventType.MouseUp, (int)evt->Param, evt, data);
                Log.Information($"Clicked {description} using mouse sequence param {evt->Param}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"TrySendAddonNodeMouseClick error for {description}: {ex.Message}");
                return false;
            }
        }

        private bool TryRecoverFromWrongArrayPlacement(PlateSide expectedSide, PlateSide actualSide, string logogramName)
        {
            currentWrongArrayRecoveryCount++;
            if (currentWrongArrayRecoveryCount >= MaxShardSelectionAttempts)
            {
                return false;
            }

            Log.Warning($"Selection for '{logogramName}' populated the {actualSide} Array instead of the {expectedSide} Array; clearing the temporary plate and retrying");

            if (!TryClickVisibleAddonButton("EurekaMagiciteItemSynthesis", 3, "Destroy current selection"))
            {
                return false;
            }

            ResetCurrentPlateBuildProgress();
            queueStep = QueueStep.PrepareManipulator;
            ScheduleQueueAfterFrames();
            SetStatus($"Retrying plate after {actualSide} focus failure");
            return true;
        }

        private unsafe int GetManipulatorArrayFilledSlotCount(PlateSide side)
        {
            var addon = GetAddon("EurekaMagiciteItemSynthesis");
            var nodeListIndex = GetManipulatorArrayNodeListIndex(side);
            if (addon == null || !addon->IsVisible || nodeListIndex >= addon->UldManager.NodeListCount)
            {
                return 0;
            }

            var node = (AtkComponentNode*)addon->UldManager.NodeList[nodeListIndex];
            if (node == null || node->Component == null)
            {
                return 0;
            }

            var filledSlotCount = 0;
            foreach (var nodeId in new uint[] { 10, 11, 12 })
            {
                var childNode = node->Component->UldManager.SearchNodeById(nodeId);
                if (childNode != null && childNode->IsVisible())
                {
                    filledSlotCount++;
                }
            }

            return filledSlotCount;
        }

        private bool TryAcknowledgeMnemeExtraction()
        {
            if (TryClickVisibleSynthesisButton(4, CanUseAsPostExtractionConfirmation, "Post-extraction Okay"))
            {
                return true;
            }

            if (TryClickVisibleSynthesisButton(5, CanUseAsPostExtractionConfirmation, "Post-extraction Okay"))
            {
                return true;
            }

            return false;
        }

        private bool TryDestroyExtractedMneme()
        {
            return TryClickVisibleSynthesisButton(3, label => label.Equals("Destroy", StringComparison.OrdinalIgnoreCase), "Destroy extracted mneme");
        }

        private unsafe bool TryConfirmDestroyYesNo()
        {
            var addon = (AddonSelectYesno*)GetAddon("SelectYesno");
            if (addon == null || !addon->AtkUnitBase.IsVisible)
            {
                return false;
            }

            try
            {
                addon->AtkUnitBase.FireCallbackInt(0);
                addon->AtkUnitBase.Close(true);
                Log.Information("Confirmed destroy prompt via SelectYesno: Yes");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"TryConfirmDestroyYesNo error: {ex.Message}");
                return false;
            }
        }

        private static bool CanUseAsPostExtractionConfirmation(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return false;
            }

            return !label.Contains("Replace", StringComparison.OrdinalIgnoreCase)
                && !label.Contains("Destroy", StringComparison.OrdinalIgnoreCase)
                && !label.Contains("Extract", StringComparison.OrdinalIgnoreCase);
        }

        private bool TryClickVisibleSynthesisButton(int nodeListIndex, Func<string, bool> labelPredicate, string description)
        {
            var label = GetAddonComponentButtonText("EurekaMagiciteItemSynthesis", nodeListIndex);
            if (!labelPredicate(label))
            {
                return false;
            }

            return TryClickVisibleAddonButton("EurekaMagiciteItemSynthesis", nodeListIndex, description);
        }

        private unsafe bool TryGetVisibleSynthesisButtonAnchor(int nodeListIndex, Func<string, bool> labelPredicate, out System.Numerics.Vector2 position)
        {
            position = System.Numerics.Vector2.Zero;

            var addon = GetAddon("EurekaMagiciteItemSynthesis");
            if (addon == null || !addon->IsVisible || addon->RootNode == null || nodeListIndex < 0 || nodeListIndex >= addon->UldManager.NodeListCount)
            {
                return false;
            }

            var label = GetAddonComponentButtonText("EurekaMagiciteItemSynthesis", nodeListIndex);
            if (!labelPredicate(label))
            {
                return false;
            }

            var buttonNode = addon->UldManager.NodeList[nodeListIndex];
            if (buttonNode == null || !buttonNode->IsVisible())
            {
                return false;
            }

            var buttonWidth = Math.Max(1f, buttonNode->Width * buttonNode->ScaleX);
            var buttonHeight = Math.Max(1f, buttonNode->Height * buttonNode->ScaleY);
            var windowWidth = Math.Max(1f, addon->RootNode->Width * addon->RootNode->ScaleX);
            position = new System.Numerics.Vector2(
                addon->RootNode->ScreenX + ((windowWidth - AutomationOverlayButtonWidth) * 0.49f),
                buttonNode->ScreenY + buttonHeight + AutomationOverlayVerticalOffset);
            return true;
        }

        private unsafe string GetAddonComponentButtonText(string addonName, int nodeListIndex)
        {
            var addon = GetAddon(addonName);
            if (addon == null || !addon->IsVisible || nodeListIndex < 0 || nodeListIndex >= addon->UldManager.NodeListCount)
            {
                return string.Empty;
            }

            var componentNode = (AtkComponentNode*)addon->UldManager.NodeList[nodeListIndex];
            if (componentNode == null || componentNode->Component == null)
            {
                return string.Empty;
            }

            try
            {
                var textNode = (AtkTextNode*)componentNode->Component->UldManager.SearchNodeById(2);
                if (textNode == null || !textNode->AtkResNode.IsVisible())
                {
                    return string.Empty;
                }

                return CleanAddonText(textNode->NodeText.ToString());
            }
            catch
            {
                return string.Empty;
            }
        }

        private unsafe string GetAddonTextNodeText(AtkUnitBase* addon, int nodeListIndex)
        {
            if (addon == null || !addon->IsVisible || nodeListIndex < 0 || nodeListIndex >= addon->UldManager.NodeListCount)
            {
                return string.Empty;
            }

            var node = addon->UldManager.NodeList[nodeListIndex];
            if (node == null || node->Type != NodeType.Text)
            {
                return string.Empty;
            }

            try
            {
                return CleanAddonText(((AtkTextNode*)node)->NodeText.ToString());
            }
            catch
            {
                return string.Empty;
            }
        }

        private unsafe string GetVisibleAddonTextNodeText(AtkUnitBase* addon, int nodeListIndex)
        {
            if (addon == null || !addon->IsVisible || nodeListIndex < 0 || nodeListIndex >= addon->UldManager.NodeListCount)
            {
                return string.Empty;
            }

            var node = addon->UldManager.NodeList[nodeListIndex];
            if (node == null || !node->IsVisible() || node->Type != NodeType.Text)
            {
                return string.Empty;
            }

            try
            {
                var textNode = (AtkTextNode*)node;
                return textNode->AtkResNode.IsVisible() ? CleanAddonText(textNode->NodeText.ToString()) : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private unsafe string GetVisibleMnemeExtractionResultText(AtkUnitBase* addon)
        {
            return FindVisibleSynthesisText(addon, text => ContainsMnemeExtractionFailureText(text) || ContainsMnemeExtractionSuccessText(text));
        }

        private unsafe string FindVisibleSynthesisText(AtkUnitBase* addon, Func<string, bool> predicate)
        {
            if (addon == null || !addon->IsVisible || predicate == null)
            {
                return string.Empty;
            }

            for (var i = 0; i < addon->UldManager.NodeListCount; i++)
            {
                var text = GetVisibleAddonTextNodeText(addon, i);
                if (!string.IsNullOrWhiteSpace(text) && predicate(text))
                {
                    return text;
                }
            }

            return string.Empty;
        }

        private unsafe bool TryClickVisibleAddonButton(string addonName, int nodeListIndex, string description)
        {
            var addon = GetAddon(addonName);
            if (addon == null || !addon->IsVisible)
            {
                return false;
            }

            if (nodeListIndex < 0 || nodeListIndex >= addon->UldManager.NodeListCount)
            {
                return false;
            }

            var node = addon->UldManager.NodeList[nodeListIndex];
            if (node == null || !node->IsVisible())
            {
                return false;
            }

            if (!ClickAddonButton(addonName, nodeListIndex))
            {
                return false;
            }

            Log.Information($"Clicked {description} using '{addonName}' node {nodeListIndex}");
            return true;
        }

        private bool TrySelectVisibleShardListEntry(int visibleIndex, string logogramName, int attempt)
        {
            return attempt switch
            {
                0 => TrySelectShardListEntryByTreeList(visibleIndex, logogramName, dispatchSelection: true, eventType: null),
                1 => TrySelectShardListEntryByTreeList(visibleIndex, logogramName, dispatchSelection: false, eventType: AtkEventType.ListItemClick),
                2 => TrySelectShardListEntryByTreeList(visibleIndex, logogramName, dispatchSelection: false, eventType: AtkEventType.ListItemSelect),
                3 => TrySelectShardListEntryByCallback(visibleIndex, logogramName, includeLeadingZero: true),
                _ => false,
            };
        }

        private bool TrySelectShardListEntryByCallback(int visibleIndex, string logogramName, bool includeLeadingZero)
        {
            if (includeLeadingZero)
            {
                if (FireCallback("EurekaMagiciteItemShardList", 0, visibleIndex))
                {
                    Log.Information($"Selected visible shard list entry {visibleIndex} for '{logogramName}' via shard-list callback [0, {visibleIndex}]");
                    return true;
                }

                return false;
            }

            if (FireCallback("EurekaMagiciteItemShardList", visibleIndex))
            {
                Log.Information($"Selected visible shard list entry {visibleIndex} for '{logogramName}' via shard-list callback [{visibleIndex}]");
                return true;
            }

            return false;
        }

        private unsafe bool TrySelectShardListEntryByTreeList(int visibleIndex, string logogramName, bool dispatchSelection, AtkEventType? eventType)
        {
            var shardAddon = GetAddon("EurekaMagiciteItemShardList");
            if (shardAddon == null || !shardAddon->IsVisible)
            {
                return false;
            }

            if (shardAddon->UldManager.NodeListCount <= 5)
            {
                return false;
            }

            var listComponentNode = (AtkComponentNode*)shardAddon->UldManager.NodeList[5];
            if (listComponentNode == null || listComponentNode->Component == null)
            {
                Log.Warning("Shard tree list component was null");
                return false;
            }

            var treeList = (AtkComponentTreeList*)listComponentNode->Component;
            try
            {
                var itemCount = treeList->GetItemCount();
                if (visibleIndex < 0 || visibleIndex >= itemCount)
                {
                    Log.Warning($"Visible shard list index {visibleIndex} for '{logogramName}' was out of range (count: {itemCount})");
                    return false;
                }

                treeList->ScrollToItem((short)visibleIndex);
                treeList->SelectItem(visibleIndex, dispatchSelection);

                if (eventType.HasValue)
                {
                    treeList->DispatchItemEvent(visibleIndex, eventType.Value);
                    Log.Information($"Selected visible shard list entry {visibleIndex} for '{logogramName}' via tree list event {eventType.Value}");
                }
                else
                {
                    Log.Information($"Selected visible shard list entry {visibleIndex} for '{logogramName}' via tree list SelectItem(dispatchEvent: {dispatchSelection})");
                }

                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"TrySelectShardListEntryByTreeList error for '{logogramName}': {ex.Message}");
                return false;
            }
        }

        private static string CleanAddonText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var chars = text.Where(c => !char.IsControl(c)).ToArray();
            return new string(chars).Trim();
        }

        private unsafe bool FireCallback(string addonName, params int[] values)
        {
            return FireCallback(addonName, updateVisibility: false, values);
        }

        private unsafe bool TryCountVisibleLogosActionSlots(out int occupiedSlotCount, out int visibleSlotCount)
        {
            occupiedSlotCount = 0;
            visibleSlotCount = 0;

            var addon = GetAddon("EurekaMagiciteItemAtherList");
            if (addon == null || !addon->IsVisible || addon->UldManager.NodeListCount <= 4)
            {
                return false;
            }

            var listComponentNode = (AtkComponentNode*)addon->UldManager.NodeList[4];
            if (listComponentNode == null || listComponentNode->Component == null)
            {
                return false;
            }

            var rowNodeCount = listComponentNode->Component->UldManager.NodeListCount;
            if (rowNodeCount <= 1)
            {
                return false;
            }

            visibleSlotCount = LogosActionSlotCapacity > 0
                ? Math.Min(LogosActionSlotCapacity, rowNodeCount - 1)
                : rowNodeCount - 1;

            if (visibleSlotCount <= 0)
            {
                return false;
            }

            for (var slotIndex = 1; slotIndex <= visibleSlotCount; slotIndex++)
            {
                var slotComponentNode = (AtkComponentNode*)listComponentNode->Component->UldManager.NodeList[slotIndex];
                if (slotComponentNode == null || slotComponentNode->Component == null)
                {
                    continue;
                }

                if (slotComponentNode->Component->UldManager.NodeListCount <= 3)
                {
                    continue;
                }

                var filledSlotWindow = slotComponentNode->Component->UldManager.NodeList[3];
                if (filledSlotWindow != null && filledSlotWindow->IsVisible())
                {
                    occupiedSlotCount++;
                }
            }

            return true;
        }

        private unsafe bool FireCallback(string addonName, bool updateVisibility, params int[] values)
        {
            var addon = GetAddon(addonName);
            if (addon == null || !addon->IsVisible)
            {
                return false;
            }

            try
            {
                var atkValues = stackalloc AtkValue[values.Length];
                for (var i = 0; i < values.Length; i++)
                {
                    atkValues[i] = default;
                    atkValues[i].Type = ValueType.Int;
                    atkValues[i].Int = values[i];
                }

                addon->FireCallback((uint)values.Length, atkValues, updateVisibility);
                if (updateVisibility)
                {
                    Log.Information($"FireCallback('{addonName}', updateVisibility={updateVisibility}, [{string.Join(", ", values)}])");
                }
                else
                {
                    Log.Information($"FireCallback('{addonName}', [{string.Join(", ", values)}])");
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"FireCallback error on '{addonName}': {ex.Message}");
                return false;
            }
        }

        private unsafe bool ClickAddonButton(string addonName, int nodeListIndex)
        {
            var addon = GetAddon(addonName);
            if (addon == null || !addon->IsVisible)
            {
                return false;
            }

            if (nodeListIndex < 0 || nodeListIndex >= addon->UldManager.NodeListCount)
            {
                return false;
            }

            var node = addon->UldManager.NodeList[nodeListIndex];
            if (node == null)
            {
                return false;
            }

            try
            {
                if (!DispatchAddonNodeEvent(addon, node, (AtkEventType)25))
                {
                    return false;
                }

                Log.Information($"ClickAddonButton('{addonName}', {nodeListIndex})");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"ClickAddonButton error on '{addonName}' node {nodeListIndex}: {ex.Message}");
                return false;
            }
        }

        private static unsafe bool DispatchAddonNodeEvent(AtkUnitBase* addon, AtkResNode* node, AtkEventType eventType)
        {
            if (addon == null || node == null)
            {
                return false;
            }

            var evt = node->AtkEventManager.Event;
            if (evt == null)
            {
                return false;
            }

            addon->ReceiveEvent(eventType, (int)evt->Param, evt);
            return true;
        }
    }
}
