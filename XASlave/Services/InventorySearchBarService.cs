using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;
using XASlave.Localization;

namespace XASlave.Services;

internal sealed unsafe class InventorySearchBarService : IDisposable
{
    private readonly SlaveNativeUiLibrary library;
    private readonly Func<InventorySearchFilter> visibleFilters;
    private readonly Func<int> nonmatchingOpacityPercent;
    private readonly Func<bool> focusShortcutEnabled;
    private readonly Func<bool> showTooltips;
    private readonly Action unavailable;
    private readonly Dictionary<string, long> epochs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SearchControl> controls = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, string> itemNames = new();
    private volatile bool enabled, disposed, retiring;
    private bool controlFWasDown, lifecycleSubscribed;
    private long nextScan, drawTimestamp;
    private bool drawBlocked = true;
    private ulong contentId, retainerId;
    internal UiMessage StatusTextMessage { get; private set; } = UiMessage.Key("S75081B593D15CF6E");
    internal string StatusText => StatusTextMessage.Original;
    internal string LocalizedStatusText => StatusTextMessage.Display;

    internal InventorySearchBarService(SlaveNativeUiLibrary library, Func<InventorySearchFilter> visibleFilters,
        Func<int> nonmatchingOpacityPercent, Func<bool> focusShortcutEnabled, Func<bool> showTooltips, Action unavailable)
    {
        this.library = library; this.visibleFilters = visibleFilters; this.unavailable = unavailable;
        this.nonmatchingOpacityPercent = nonmatchingOpacityPercent;
        this.focusShortcutEnabled = focusShortcutEnabled; this.showTooltips = showTooltips;
    }

    internal static string LocalizedSearchInstructions => UiLocale.T("Search by literal item name or item ID. Click the field to type. Escape clears search and filters. T: tradable; NT: untradable; B: bound (excluding collectables); U: unique. Filters combine with search; T and NT cannot be combined.");
    internal static string LocalizedFocusInstructions => UiLocale.T("When enabled, Ctrl+F focuses search in the active storage window.");

    internal bool SetEnabled(bool value)
    {
        if (disposed) return false;
        if (enabled == value) return enabled;
        enabled = value;
        drawBlocked = true; drawTimestamp = 0; controlFWasDown = true;
        if (value)
        {
            Plugin.Framework.Update += Update;
            Plugin.PluginInterface.UiBuilder.Draw += ObserveDraw;
            Plugin.ClientState.Logout += Logout;
            if (!lifecycleSubscribed)
            {
                lifecycleSubscribed = true;
                foreach (var name in InventorySearchNative.Hosts.Concat(InventorySearchNative.Grids))
                {
                    Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, name, Observe);
                    Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, name, Observe);
                }
                foreach (var name in InventorySearchNative.Hosts)
                    Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostHide, name, Observe);
            }
            StatusTextMessage = UiMessage.Authored("Inventory search ready. Open your inventory, saddlebag, a retainer inventory, or your Free Company chest.");
            nextScan = 0;
        }
        else
        {
            Plugin.Framework.Update -= Update;
            Plugin.PluginInterface.UiBuilder.Draw -= ObserveDraw;
            Plugin.ClientState.Logout -= Logout;
            // Keep PreFinalize observation until the framework has retired every control.
            // Input callbacks immediately reject enabled=false, including off-thread disable.
            QueueRetirement();
            StatusTextMessage = UiMessage.Key("S75081B593D15CF6E");
        }
        return enabled;
    }

    private long Epoch(string name) => epochs.GetValueOrDefault(name);

    private void ObserveDraw()
    {
        if (!enabled || disposed) return;
        try
        {
            var io = ImGui.GetIO();
            Volatile.Write(ref drawBlocked, io.WantTextInput || io.WantCaptureKeyboard);
            Interlocked.Exchange(ref drawTimestamp, Environment.TickCount64);
        }
        catch { Volatile.Write(ref drawBlocked, true); }
    }

    private void Observe(AddonEvent kind, AddonArgs args)
    {
        if ((!enabled || disposed) && controls.Count == 0) return;
        try
        {
            if (kind == AddonEvent.PostHide)
            {
                if (controls.TryGetValue(args.AddonName, out var hidden) && hidden.Host.Address == args.Addon.Address)
                    hidden.ClearQuery();
                return;
            }
            // Restore before the addon releases native storage. A new epoch rejects reused addresses.
            if (kind == AddonEvent.PreFinalize)
            {
                foreach (var control in controls.Values.ToArray()) control.RestoreGrid(args.AddonName, args.Addon.Address);
                if (controls.TryGetValue(args.AddonName, out var owned) && owned.Host.Address == args.Addon.Address)
                { controls.Remove(args.AddonName); owned.Dispose(); }
            }
            else if (controls.Remove(args.AddonName, out var replaced))
                replaced.Dispose();
            epochs[args.AddonName] = Epoch(args.AddonName) + 1;
            nextScan = 0;
        }
        catch (Exception error) { Fail(error); }
    }

    private void Logout(int type, int code)
    {
        contentId = 0; retainerId = 0; itemNames.Clear();
        QueueRetirement();
    }

    private void Update(IFramework framework)
    {
        if (!enabled || disposed || retiring) return;
        try
        {
            var currentContent = Plugin.PlayerState.IsLoaded ? Plugin.PlayerState.ContentId : 0UL;
            if (currentContent != contentId)
            {
                RetireAllControls(); itemNames.Clear(); contentId = currentContent; retainerId = 0;
            }
            if (currentContent == 0) return;
            var retainerManager = RetainerManager.Instance();
            var retainer = retainerManager == null ? null : retainerManager->GetActiveRetainer();
            var currentRetainer = retainer == null ? 0UL : retainer->RetainerId;
            if (retainerId != currentRetainer)
            {
                foreach (var control in controls.Values.Where(control => control.IsRetainer)) control.ClearQuery();
                retainerId = currentRetainer;
            }
            var now = Environment.TickCount64;
            if (now >= nextScan)
            {
                nextScan = now + 100;
                foreach (var name in InventorySearchNative.Hosts)
                {
                    var host = InventorySearchNative.CaptureHost(name, Epoch(name));
                    if (controls.TryGetValue(name, out var existing) && host != existing.Host)
                    {
                        controls.Remove(name); existing.Dispose();
                    }
                    if (host == null) continue;
                    if (!controls.TryGetValue(name, out var control))
                    {
                        if (!library.EnsureReady()) continue;
                        if (!enabled || disposed || retiring) return;
                        control = new SearchControl(host.Value, Epoch, itemNames, () => nextScan = 0, () => enabled && !disposed,
                            visibleFilters, nonmatchingOpacityPercent, focusShortcutEnabled, showTooltips);
                        controls.Add(name, control);
                    }
                    control.Update();
                }
            }
            CheckFocusShortcut(now);
        }
        catch (Exception error) { Fail(error); }
    }

    private void CheckFocusShortcut(long now)
    {
        var down = (GetAsyncKeyState(0x46) & 0x8000) != 0 && (GetAsyncKeyState(0x11) & 0x8000) != 0;
        var pressed = down && !controlFWasDown;
        controlFWasDown = down;
        // Observe releases while disabled, but never focus or consume the key.
        if (!focusShortcutEnabled()) return;
        if (!pressed || Volatile.Read(ref drawBlocked) || now - Interlocked.Read(ref drawTimestamp) is < 0 or > 500
            || (GetAsyncKeyState(0x12) & 0x8000) != 0 || (GetAsyncKeyState(0x10) & 0x8000) != 0) return;
        var input = UIInputData.Instance();
        var manager = RaptureAtkUnitManager.Instance();
        var module = RaptureAtkModule.Instance();
        if (input == null || !input->CursorInputs.IsGameWindowFocused || manager == null || module == null) return;
        var count = manager->FocusedUnitsList.Count;
        if (count < 1 || count > manager->FocusedUnitsList.Entries.Length) return;
        var focused = manager->FocusedUnitsList.Entries[count - 1].Value;
        if (focused == null || focused->Id == 0) return;
        foreach (var control in controls.Values)
        {
            var host = InventorySearchNative.Resolve(control.Host, Epoch, true);
            if (host == null || focused->Id != host->Id && focused->ParentId != host->Id) continue;
            if (module->IsTextInputActive() && !control.IsFocused) return;
            if (control.Focus()) Plugin.KeyState[VirtualKey.F] = false;
            return;
        }
    }

    private void Fail(Exception error)
    {
        try { SetEnabled(false); }
        catch (Exception cleanupError) { Plugin.Log.Error(cleanupError, "Inventory search restoration was incomplete."); }
        unavailable();
        StatusTextMessage = UiMessage.Format(UiLocale.Key("Inventory search is unavailable: {0}"), UiMessage.FromException(error));
        Plugin.Log.Warning(error, "Inventory search stopped at a native lifecycle boundary.");
    }

    private void QueueRetirement()
    {
        if (Plugin.Framework.IsInFrameworkUpdateThread) { RetireAllControls(); return; }
        if (retiring) return;
        retiring = true;
        _ = Plugin.Framework.Run(() =>
        {
            try { RetireAllControls(); }
            catch (Exception error) { Plugin.Log.Error(error, "Inventory search control retirement failed."); }
            finally { retiring = false; }
        });
    }

    internal void RetireAllControls()
    {
        Plugin.AssertGameThread();
        Exception? failure = null;
        foreach (var pair in controls.ToArray())
        {
            pair.Value.StopCallbacks();
            try { pair.Value.Dispose(); controls.Remove(pair.Key); }
            catch (Exception error) { failure ??= error; }
        }
        if (!enabled && controls.Count == 0 && lifecycleSubscribed)
        {
            Plugin.AddonLifecycle.UnregisterListener(Observe);
            lifecycleSubscribed = false;
        }
        if (failure != null) throw failure;
    }

    public void Dispose()
    {
        if (disposed) return;
        SetEnabled(false); disposed = true;
    }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);

    private sealed class SearchControl : IDisposable
    {
        private readonly TextInputNode input;
        private readonly Func<string, long> epoch;
        private readonly Dictionary<uint, string> names;
        private readonly Func<bool> ownerActive;
        private readonly Func<InventorySearchFilter> visibleFilters;
        private readonly Func<int> nonmatchingOpacityPercent;
        private readonly Func<bool> focusShortcutEnabled;
        private readonly Func<bool> showTooltips;
        private readonly Dictionary<InventorySearchSlot, InventorySearchSlotDimState> dimmed = new();
        private readonly List<(InventorySearchFilter Filter, CompactFilterButton Node, string Tooltip, float Width)> filterButtons = new();
        private string query = string.Empty;
        private InventorySearchFilter filters;
        private bool retired, disposed, clearRequested;
        internal InventorySearchHost Host { get; }
        internal bool IsRetainer => Host.Name.StartsWith("InventoryRetainer", StringComparison.Ordinal);
        internal bool IsFocused
        {
            get
            {
                var stage = AtkStage.Instance();
                return !retired && ownerActive() && InventorySearchNative.Resolve(Host, epoch, true) != null
                    && stage != null && stage->AtkInputManager != null && input.IsFocused;
            }
        }

        internal SearchControl(InventorySearchHost host, Func<string, long> epoch, Dictionary<uint, string> names,
            Action changed, Func<bool> ownerActive, Func<InventorySearchFilter> visibleFilters,
            Func<int> nonmatchingOpacityPercent, Func<bool> focusShortcutEnabled, Func<bool> showTooltips)
        {
            Host = host; this.epoch = epoch; this.names = names; this.ownerActive = ownerActive;
            this.visibleFilters = visibleFilters;
            this.nonmatchingOpacityPercent = nonmatchingOpacityPercent;
            this.focusShortcutEnabled = focusShortcutEnabled; this.showTooltips = showTooltips;
            var addon = InventorySearchNative.Resolve(host, epoch, true);
            if (addon == null) throw UiMessage.CreateException(UiMessage.Key(UiLocale.Key("The inventory window changed before the search bar could be attached.")), text => new InvalidOperationException(text));
            input = new TextInputNode();
            try
            {
                input.MaxCharacters = 128; input.EnableCompletion = false; input.AutoSelectAll = false;
                input.ShowLimitText = false; input.PlaceholderString = UiLocale.T("Search items...");
                input.OnInputReceived = text =>
                {
                    // Event callbacks only change managed input state. Native resolution and
                    // restoration happen in the next framework callback, never via captured pointers.
                    if (retired || !ownerActive() || epoch(Host.Name) != Host.Epoch) return;
                    query = text.ExtractText(); changed();
                };
                input.OnEscapeEntered = () =>
                {
                    if (retired || !ownerActive() || epoch(Host.Name) != Host.Epoch) return;
                    query = string.Empty; filters = InventorySearchFilter.None; clearRequested = true; changed();
                };
                AddFilterButton(InventorySearchFilter.Tradable, "T", 26,
                    "Tradable: show items that can currently be traded. Combines with search text and other filters.", changed);
                AddFilterButton(InventorySearchFilter.Untradable, "NT", 32,
                    "Untradable: show items that cannot currently be traded. Combines with search text and other filters.", changed);
                AddFilterButton(InventorySearchFilter.Bound, "B", 26,
                    "Bound: show spiritbonded items, excluding collectables. Combines with search text and other filters.", changed);
                AddFilterButton(InventorySearchFilter.Unique, "U", 26,
                    "Unique: show items marked Unique. Combines with search text and other filters.", changed);
                Position(addon);
                input.AttachNode(addon);
                foreach (var button in filterButtons) button.Node.AttachNode(addon);
            }
            catch { StopCallbacks(); DisposeControls(); throw; }
        }

        private void AddFilterButton(InventorySearchFilter filter, string label, float width, string tooltip, Action changed)
        {
            var button = new CompactFilterButton();
            // Register ownership immediately so partial constructor failures retire every node.
            filterButtons.Add((filter, button, tooltip, width));
            button.Label.String = label;
            button.CollisionNode.TextTooltip = showTooltips() ? UiLocale.T(tooltip) : string.Empty;
            button.OnClick = () =>
            {
                if (retired || !ownerActive() || epoch(Host.Name) != Host.Epoch) return;
                var visible = visibleFilters();
                if ((visible & filter) == 0) return;
                filters = InventorySearchFilterPolicy.Toggle(filters & visible, filter);
                changed();
            };
        }

        private void Position(AtkUnitBase* addon)
        {
            var header = addon->WindowHeaderCollisionNode;
            if (header == null) throw UiMessage.CreateException(UiMessage.Key(UiLocale.Key("The inventory title bar is unavailable.")), text => new InvalidOperationException(text));
            NearbyZoneNativeBinding.RequireReadable((nint)header, sizeof(AtkCollisionNode));
            NearbyZoneNativeBinding.RequireReadable((nint)addon->RootNode, sizeof(AtkResNode));
            var width = addon->RootNode->Width / 2f;
            if (width < 80 || width > 1200 || header->Width < width + 50)
                throw UiMessage.CreateException(UiMessage.Key(UiLocale.Key("The inventory title bar geometry is unsupported.")), text => new InvalidOperationException(text));
            // Preserve the established right edge so filter controls never expand
            // toward the window buttons. Framed buttons stay inside their native
            // hit rectangles, separated from the input and each other by 4px.
            const float gap = 4;
            var visible = visibleFilters();
            // Hidden filters must never keep narrowing the results or reselect on show.
            filters &= visible;
            var filterWidth = filterButtons.Where(button => (visible & button.Filter) != 0).Sum(button => button.Width + gap);
            // Keep the original footprint even in narrow windows; only the input grows.
            var groupWidth = Math.Max(width, filterButtons.Sum(button => button.Width + gap) + 64);
            var right = header->Width / 2f + width / 2f + 25;
            var left = right - groupWidth;
            if (left < 8 || right + 3 > header->Width - 16)
                throw UiMessage.CreateException(UiMessage.Key(UiLocale.Key("The inventory title bar geometry is unsupported.")), text => new InvalidOperationException(text));
            input.Size = new Vector2(groupWidth - filterWidth, 28);
            input.Position = new Vector2(left, header->Height / 2f - 14 + 10);
            input.PlaceholderString = UiLocale.T("Search items...");
            // Keep hover events on the native focus target. Registering them on the
            // outer component makes it a competing mouse target above the text input.
            var tooltips = showTooltips();
            input.CollisionNode.TextTooltip = tooltips
                ? LocalizedSearchInstructions + (focusShortcutEnabled() ? " " + LocalizedFocusInstructions : string.Empty)
                : string.Empty;
            input.IsVisible = true;
            var x = input.Position.X + input.Width;
            foreach (var button in filterButtons)
            {
                button.Node.IsVisible = (visible & button.Filter) != 0;
                button.Node.IsSelected = (filters & button.Filter) != 0;
                button.Node.CollisionNode.TextTooltip = tooltips ? UiLocale.T(button.Tooltip) : string.Empty;
                if (!button.Node.IsVisible) continue;
                x += gap;
                button.Node.Size = new Vector2(button.Width, 24);
                button.Node.Position = new Vector2(x, input.Position.Y + 2);
                x += button.Width;
            }
        }

        internal void Update()
        {
            if (retired || !ownerActive()) return;
            var addon = InventorySearchNative.Resolve(Host, epoch, true);
            if (addon == null) return;
            if (clearRequested)
            {
                clearRequested = false; ClearQuery();
                var stage = AtkStage.Instance();
                if (stage != null && stage->AtkInputManager != null) input.ClearFocus();
            }
            Position(addon);
            if (string.IsNullOrWhiteSpace(query) && filters == InventorySearchFilter.None) { RestoreAll(); return; }
            var matches = InventorySearchNative.CaptureMatches(Host, epoch, query, names, filters);
            var current = matches.Select(match => match.Slot).ToHashSet();
            foreach (var old in dimmed.Keys.Where(slot => !current.Contains(slot)).ToArray()) Restore(old);
            var opacityPercent = nonmatchingOpacityPercent();
            foreach (var match in matches)
            {
                var node = InventorySearchNative.ResolveSlot(match.Slot, epoch);
                if (node == null) continue;
                if (!dimmed.TryGetValue(match.Slot, out var state))
                {
                    if (!match.Dim) continue;
                    dimmed.Add(match.Slot, state = new InventorySearchSlotDimState());
                }
                if (state.TryResetForItem(match.Item, node->Color.A, out var restored)) node->SetAlpha(restored);
                // Read the actual current alpha again after restoring the previous
                // content's owned appearance; do not reuse its captured value.
                if (state.TryApply(node->Color.A, match.Dim, out var replacement, opacityPercent)) node->SetAlpha(replacement);
                if (!match.Dim) dimmed.Remove(match.Slot);
            }
        }

        internal bool Focus()
        {
            var addon = InventorySearchNative.Resolve(Host, epoch, true);
            if (retired || !ownerActive() || addon == null) return false;
            var stage = AtkStage.Instance();
            if (stage == null || stage->AtkInputManager == null) return false;
            stage->AtkInputManager->SetFocus(input.FocusNode, addon, 0);
            return input.IsFocused;
        }

        internal void ClearQuery()
        {
            query = string.Empty; filters = InventorySearchFilter.None;
            if (InventorySearchNative.Resolve(Host, epoch) != null)
            {
                input.String = string.Empty;
                foreach (var button in filterButtons) button.Node.IsSelected = false;
            }
            RestoreAll();
        }

        internal void RestoreGrid(string name, nint address)
        {
            foreach (var slot in dimmed.Keys.Where(slot => slot.Grid.Name == name && slot.Grid.Address == address).ToArray()) Restore(slot);
        }

        private void Restore(InventorySearchSlot slot)
        {
            if (!dimmed.TryGetValue(slot, out var state)) return;
            var node = InventorySearchNative.ResolveSlot(slot, epoch);
            if (node != null && state.TryRestore(node->Color.A, out var restored)) node->SetAlpha(restored);
            dimmed.Remove(slot);
        }

        private void RestoreAll()
        {
            Exception? failure = null;
            foreach (var slot in dimmed.Keys.ToArray())
            {
                try { Restore(slot); }
                catch (Exception error) { failure ??= error; }
            }
            if (failure != null) throw failure;
        }

        internal void StopCallbacks()
        {
            retired = true; input.OnInputReceived = null; input.OnEscapeEntered = null;
            foreach (var button in filterButtons) button.Node.OnClick = null;
        }

        private void DisposeControls()
        {
            Exception? failure = null;
            foreach (var button in filterButtons)
            {
                try { button.Node.Dispose(); }
                catch (Exception error) { failure ??= error; }
            }
            try { input.Dispose(); }
            catch (Exception error) { failure ??= error; }
            if (failure != null) throw failure;
        }

        public void Dispose()
        {
            if (disposed) return;
            StopCallbacks();
            try { RestoreAll(); }
            finally
            {
                try { DisposeControls(); }
                finally { disposed = true; }
            }
        }
    }

    // The stock TextButtonNode reserves 32px for its label padding. These small
    // filters use the toolkit's native square-button frame and a compact label
    // timeline, so they remain visibly framed even when neither hovered nor selected.
    private sealed class CompactFilterButton : ButtonBase
    {
        private readonly NineGridNode background;
        internal TextNode Label { get; }

        internal bool IsSelected
        {
            set
            {
                // Native hover/press timelines animate AddColor/MultiplyColor and
                // alpha. Keep persistent selection in separate RGB/text colors.
                background.Color = value ? new Vector4(1f, 0.82f, 0.42f, 1f) : Vector4.One;
                Label.TextColor = value ? new Vector4(1f, 0.9f, 0.5f, 1f) : ColorHelper.GetColor(50);
            }
        }

        internal CompactFilterButton()
        {
            try
            {
                // Same framed texture region and cap sizes as KamiToolKit IconButtonNode.
                background = new SimpleNineGridNode
                {
                    TexturePath = "ui/uld/BgParts.tex",
                    TextureSize = new Vector2(32, 32),
                    TextureCoordinates = new Vector2(33, 65),
                    TopOffset = 8, LeftOffset = 8, RightOffset = 8, BottomOffset = 8,
                };
                background.AttachNode(this);
                Label = new TextNode
                {
                    FontSize = 12,
                    AlignmentType = AlignmentType.Center,
                    Position = new Vector2(2, 2),
                    TextColor = ColorHelper.GetColor(50),
                };
                Label.AttachNode(this);
                LoadThreePartTimelines(this, background, Label, new Vector2(2, 2));
                Data->Nodes[0] = Label.NodeId;
                Data->Nodes[1] = background.NodeId;
                InitializeComponentEvents();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        protected override void OnSizeChanged()
        {
            base.OnSizeChanged();
            if (background != null) background.Size = Size;
            if (Label != null) Label.Size = new Vector2(Math.Max(0, Width - 4), Math.Max(0, Height - 4));
        }
    }
}
