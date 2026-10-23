using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace XASlave.Localization;

// TableHeadersRow and authored-choice Combo adapt Dear ImGui 1.88 helper flows.
// Copyright (c) 2014-2022 Omar Cornut. MIT license: THIRD-PARTY-NOTICES.txt.
internal static partial class UiNative
{
    private const int MaximumCaptionTransforms = 4096;
    private static readonly Dictionary<string, CaptionHashTransform> CaptionTransforms = new(StringComparer.Ordinal);
    private static readonly Dictionary<(uint Table, short Instance, int Column), string> TableLabels = new();
    private static readonly HashSet<(uint Table, short Instance)> TranslatedTables = new();
    private static int tableLabelFrame = -1;

    /// <summary>Lets the native widget render its caption while retaining its original final ID.</summary>
    internal ref struct RetainedCaption
    {
        internal readonly string Label;
        private bool pushed;

        internal RetainedCaption(string nativeLabel, string? display = null)
        {
            Label = nativeLabel;
            pushed = false;
            display ??= UiLocale.T(Caption(nativeLabel));
            if (display == nativeLabel || display == Caption(nativeLabel))
                return;

            var desired = ImGui.GetID(nativeLabel);
            if (!CaptionTransforms.TryGetValue(display, out var transform))
            {
                transform = new CaptionHashTransform(display);
                if (CaptionTransforms.Count >= MaximumCaptionTransforms)
                    CaptionTransforms.Clear();
                CaptionTransforms.Add(display, transform);
            }
            var seed = transform.SeedFor(desired);
            if (ImGuiP.ImHashStr(display, seed) != desired)
                throw new InvalidOperationException("The native UI caption hash did not preserve its original identity.");

            ImGuiP.PushOverrideID(seed);
            // Verify through the actual window API too, including its string termination behavior.
            if (ImGui.GetID(display) != desired)
            {
                ImGui.PopID();
                throw new InvalidOperationException("The native UI window did not preserve its original caption identity.");
            }
            Label = display;
            pushed = true;
        }

        public void Dispose()
        {
            if (!pushed)
                return;
            pushed = false;
            ImGui.PopID();
        }
    }

    /// <summary>Inverts the installed native hash's seed mapping, without assuming a CRC polynomial.</summary>
    private sealed class CaptionHashTransform
    {
        private readonly uint zero;
        private readonly uint[] basis = new uint[32];
        private readonly uint[] seeds = new uint[32];

        internal CaptionHashTransform(string label)
        {
            zero = ImGuiP.ImHashStr(label, 0);
            for (var index = 0; index < 32; index++)
            {
                var seed = 1u << index;
                var value = ImGuiP.ImHashStr(label, seed) ^ zero;
                for (var bit = 31; bit >= 0; bit--)
                {
                    if ((value & (1u << bit)) == 0)
                        continue;
                    if (basis[bit] != 0)
                    {
                        value ^= basis[bit];
                        seed ^= seeds[bit];
                    }
                    else
                    {
                        basis[bit] = value;
                        seeds[bit] = seed;
                        break;
                    }
                }
                if (value == 0)
                    throw new InvalidOperationException("The installed native UI hash cannot preserve translated caption identities.");
            }
        }

        internal uint SeedFor(uint desired)
        {
            var value = desired ^ zero;
            var seed = 0u;
            for (var bit = 31; bit >= 0; bit--)
            {
                if ((value & (1u << bit)) == 0)
                    continue;
                value ^= basis[bit];
                seed ^= seeds[bit];
            }
            return seed;
        }
    }

    internal static bool MenuItem(string nativeLabel, bool selected = false, bool enabled = true)
    {
        using var caption = new RetainedCaption(nativeLabel);
        return ImGui.MenuItem(caption.Label, selected, enabled);
    }

    internal static bool MenuItem(string nativeLabel, string shortcut, bool selected = false, bool enabled = true)
    {
        using var caption = new RetainedCaption(nativeLabel);
        return ImGui.MenuItem(caption.Label, shortcut, selected, enabled);
    }

    internal static bool InputTextMultiline(string nativeLabel, ref string value, int maxLength = 512, Vector2 size = default, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None, string? display = null)
    {
        using var caption = new RetainedCaption(nativeLabel, display);
        return ImGui.InputTextMultiline(caption.Label, ref value, maxLength, size, flags);
    }

    /// <summary>Authored choices keep their original selectable IDs; provider/user choices use ComboScope.</summary>
    internal static bool Combo(string nativeLabel, ref int currentItem, IReadOnlyList<string> authoredItems, int popupMaxHeightInItems = -1, string? display = null)
    {
        var context = ImGui.GetCurrentContext();
        if (popupMaxHeightInItems != -1 && (context.NextWindowData.Flags & ImGuiNextWindowDataFlags.HasSizeConstraint) == 0)
        {
            var style = ImGui.GetStyle();
            var height = popupMaxHeightInItems <= 0 ? float.MaxValue
                : (ImGui.GetFontSize() + style.ItemSpacing.Y) * popupMaxHeightInItems - style.ItemSpacing.Y + style.WindowPadding.Y * 2;
            ImGui.SetNextWindowSizeConstraints(Vector2.Zero, new Vector2(float.MaxValue, height));
        }

        var preview = currentItem >= 0 && currentItem < authoredItems.Count ? UiLocale.T(Caption(authoredItems[currentItem])) : string.Empty;
        var changed = false;
        using (var combo = ComboScope(nativeLabel, preview, display: display))
        {
            if (!combo)
                return false;
            for (var index = 0; index < authoredItems.Count; index++)
            {
                ImGui.PushID(index);
                try
                {
                    var selected = index == currentItem;
                    if (Selectable(authoredItems[index], selected))
                    {
                        currentItem = index;
                        changed = true;
                    }
                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }
                finally { ImGui.PopID(); }
            }
        }
        if (changed)
            ImGuiP.MarkItemEdited(context.LastItemData.ID);
        return changed;
    }

    internal static bool Combo(string nativeLabel, ref int currentItem, string authoredItemsSeparatedByZeros, int popupMaxHeightInItems = -1, string? display = null)
    {
        var items = authoredItemsSeparatedByZeros.Split('\0');
        var count = Array.IndexOf(items, string.Empty);
        if (count >= 0 && count != items.Length)
            Array.Resize(ref items, count);
        return Combo(nativeLabel, ref currentItem, (IReadOnlyList<string>)items, popupMaxHeightInItems, display);
    }

    internal static ComboDisposable ComboScope(string nativeLabel, string previewValue, ImGuiComboFlags flags = ImGuiComboFlags.None, string? display = null)
        => new(nativeLabel, previewValue, flags, display);

    internal ref struct ComboDisposable
    {
        internal readonly bool Success;
        private RetainedCaption caption;
        private bool alive;

        internal ComboDisposable(string nativeLabel, string previewValue, ImGuiComboFlags flags, string? display)
        {
            caption = new RetainedCaption(nativeLabel, display);
            Success = false;
            alive = false;
            try { Success = ImGui.BeginCombo(caption.Label, previewValue, flags); }
            catch { caption.Dispose(); throw; }
            if (Success)
                alive = true;
            else
                caption.Dispose();
        }

        public static implicit operator bool(ComboDisposable scope) => scope.Success;

        public void Dispose()
        {
            if (!alive)
                return;
            alive = false;
            // BeginCombo enters a popup window. Restore the parent window before popping its ID.
            try { ImGui.EndCombo(); }
            finally { caption.Dispose(); }
        }
    }

    /// <summary>For explicit, non-docking tab bars; their tab IDs use the current window ID stack.</summary>
    internal static TabDisposable TabScope(string nativeLabel, ImGuiTabItemFlags flags = ImGuiTabItemFlags.None)
        => new(nativeLabel, flags);

    internal ref struct TabDisposable
    {
        internal readonly bool Success;
        private RetainedCaption caption;
        private bool alive;

        internal TabDisposable(string nativeLabel, ImGuiTabItemFlags flags)
        {
            caption = new RetainedCaption(nativeLabel);
            Success = false;
            alive = false;
            try { Success = ImGui.BeginTabItem(caption.Label, flags); }
            catch { caption.Dispose(); throw; }
            if (Success)
                alive = true;
            else
                caption.Dispose();
        }

        public static implicit operator bool(TabDisposable scope) => scope.Success;

        public void Dispose()
        {
            if (!alive)
                return;
            alive = false;
            // EndTabItem restores the native tab ID before the caption restores its parent seed.
            try { ImGui.EndTabItem(); }
            finally { caption.Dispose(); }
        }
    }

    internal static TreeDisposable TreeScope(string nativeLabel, ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.None)
        => new(nativeLabel, flags);

    internal ref struct TreeDisposable
    {
        internal readonly bool Success;
        private bool alive;

        internal TreeDisposable(string nativeLabel, ImGuiTreeNodeFlags flags)
        {
            Success = TreeNode(nativeLabel, flags);
            alive = Success && (flags & ImGuiTreeNodeFlags.NoTreePushOnOpen) == 0;
        }

        public static implicit operator bool(TreeDisposable scope) => scope.Success;

        public void Dispose()
        {
            if (!alive)
                return;
            alive = false;
            ImGui.TreePop();
        }
    }

    internal static void TableSetupColumn(string nativeLabel, ImGuiTableColumnFlags flags = ImGuiTableColumnFlags.None, float initWidthOrWeight = 0, uint userId = 0)
    {
        var frame = ImGui.GetFrameCount();
        if (tableLabelFrame != frame)
        {
            tableLabelFrame = frame;
            TableLabels.Clear();
            TranslatedTables.Clear();
        }
        var table = ImGuiP.GetCurrentTable();
        var key = (Table: table.ID, Instance: table.InstanceCurrent);
        TableLabels[(key.Table, key.Instance, table.DeclColumnsCount)] = nativeLabel;
        var original = Caption(nativeLabel);
        var display = UiLocale.T(original);
        if (display != original)
            TranslatedTables.Add(key);
        ImGui.TableSetupColumn(display == original ? nativeLabel : display, flags, initWidthOrWeight, userId);
    }

    internal static void TableHeader(string nativeLabel, string? display = null)
    {
        using var caption = new RetainedCaption(nativeLabel, display);
        ImGui.TableHeader(caption.Label);
    }

    internal static void TableHeadersRow()
    {
        var table = ImGuiP.GetCurrentTable();
        if (tableLabelFrame != ImGui.GetFrameCount() || !TranslatedTables.Contains((table.ID, table.InstanceCurrent)))
        {
            ImGui.TableHeadersRow();
            return;
        }

        // Dear ImGui 1.88 documents TableHeadersRow as a template for custom headers.
        // Keep its row, column-instance IDs, native header behavior and trailing context menu.
        if (!table.IsLayoutLocked)
            ImGuiP.TableUpdateLayout(table);
        var rowY = ImGui.GetCursorScreenPos().Y;
        var rowHeight = ImGuiP.TableGetHeaderRowHeight();
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers, rowHeight);
        if (table.HostSkipItems)
            return;
        var count = ImGui.TableGetColumnCount();
        for (var index = 0; index < count; index++)
        {
            if (!ImGui.TableSetColumnIndex(index))
                continue;
            var hidden = (ImGui.TableGetColumnFlags(index) & ImGuiTableColumnFlags.NoHeaderLabel) != 0;
            var display = hidden ? string.Empty : ImGui.TableGetColumnName(index);
            var original = !hidden && TableLabels.TryGetValue((table.ID, table.InstanceCurrent, index), out var label) ? label : display;
            ImGui.PushID(table.InstanceCurrent * table.ColumnsCount + index);
            try { TableHeader(original, display); }
            finally { ImGui.PopID(); }
        }
        var mouse = ImGui.GetMousePos();
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Right) && ImGuiP.TableGetHoveredColumn() == count && mouse.Y >= rowY && mouse.Y < rowY + rowHeight)
            ImGuiP.TableOpenContextMenu(-1);
    }
}
