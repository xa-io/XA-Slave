using System;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Game.Gui.ContextMenu;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XASlave.Services;

// Captures the menu's item identity synchronously. Callers retain only the IDs,
// never the inventory slot, addon or agent pointers, for their menu callbacks.
// Native reads remain behind ViewSingleItemPreview's exact game/SDK gate.
internal static unsafe class ViewSingleItemContextSource
{
    private const int ItemContextVtableRva = 0x21C5110;
    // Exact-build native Inspect ReceiveEvent toggles this byte and selects GlamourItems.
    private const int InspectViewingGlamoursOffset = 0x582;

    internal static bool TryResolve(IMenuOpenedArgs args, out uint itemId)
        => TryResolve(args, out itemId, out _);

    internal static bool TryResolve(IMenuOpenedArgs args, out uint itemId, out uint glamourItemId)
    {
        NearbyZoneNativeBinding.RequireFramework();
        itemId = glamourItemId = 0;
        if (args.MenuType == ContextMenuType.Inventory)
        {
            if (args.Target is not MenuTargetInventory target || target.TargetItem is not { } item || item.IsEmpty)
                return false;
            itemId = ViewSingleItemShopSource.NormalizeItemId(item.ItemId);
            if (itemId == 0) return false;
            glamourItemId = ViewSingleItemShopSource.NormalizeItemId(item.GlamourId);
            if (glamourItemId == itemId) glamourItemId = 0;
            return itemId != 0;
        }

        if (args.MenuType != ContextMenuType.Default || args.Target is not MenuTargetDefault defaultTarget
            || defaultTarget.TargetContentId != 0 || string.IsNullOrEmpty(args.AddonName)
            || args.AddonPtr == 0 || args.AgentPtr == 0) return false;

        var addon = (AtkUnitBase*)args.AddonPtr;
        NearbyZoneNativeBinding.RequireReadable(args.AddonPtr, sizeof(AtkUnitBase));
        if (!addon->IsVisible || addon->Id == 0 || addon->NameString != args.AddonName
            || Plugin.GameGui.GetAddonByName(args.AddonName, 1).Address != args.AddonPtr
            || args.AgentPtr != (nint)AgentContext.Instance()) return false;
        NearbyZoneNativeBinding.RequireReadable(args.AgentPtr, sizeof(AgentContext));
        if (((AgentContext*)args.AgentPtr)->OwnerAddon != addon->Id) return false;

        // The same bound item-context object is used across shop, currency,
        // exchange-dialog, market, housing and other menus. Its +2C is the
        // actual native Try On/Link item; it does not depend on shop AtkValue
        // layouts, buy-list positions, purchase selection or hover caches.
        var bound = TryBoundItemContext(args, addon->Id, out itemId, out var found);
        if (found && !bound) return false; // Never override conflicting/stale handlers.

        var recipeContext = AgentRecipeItemContext.Instance();
        if (recipeContext != null && args.EventInterfaces.Contains((nint)recipeContext))
        {
            NearbyZoneNativeBinding.RequireReadable((nint)recipeContext, sizeof(AgentRecipeItemContext));
            var recipeItemId = ViewSingleItemShopSource.NormalizeItemId(recipeContext->ResultItemId);
            if (recipeItemId == 0 || (itemId != 0 && itemId != recipeItemId)) { itemId = 0; return false; }
            itemId = recipeItemId;
            return true;
        }
        if (bound) return true;

        // These agents publish a dedicated context-menu selection. A general
        // HoveredItem fallback would attach the previous hovered item to an
        // unrelated menu, so other sources must provide the bound handler.
        switch (args.AddonName)
        {
            case "ItemSearch":
            {
                var search = AgentItemSearch.Instance();
                if (!OwnsAgent(args, (nint)search, sizeof(AgentItemSearch))) return false;
                // Item Search binds the context menu to the clicked item via
                // this parameter; ResultItemId can still describe an earlier
                // price-result page. Other addons give this field other uses.
                var selectedItem = ((AgentContext*)args.AgentPtr)->UpdateCheckerParam;
                if (selectedItem <= 0 || selectedItem >= 2_000_000) return false;
                itemId = ViewSingleItemShopSource.NormalizeItemId((uint)selectedItem);
                break;
            }
            case "ChatLog":
            {
                var chat = AgentChatLog.Instance();
                if (!OwnsAgent(args, (nint)chat, sizeof(AgentChatLog))) return false;
                itemId = ViewSingleItemShopSource.NormalizeItemId(chat->ContextItemId);
                break;
            }
            case "RecipeNote":
            {
                var recipes = AgentRecipeNote.Instance();
                if (!OwnsAgent(args, (nint)recipes, sizeof(AgentRecipeNote))) return false;
                itemId = ViewSingleItemShopSource.NormalizeItemId(recipes->ContextMenuResultItemId);
                break;
            }
            case "CharacterInspect":
            {
                var inspect = AgentInspect.Instance();
                if (!OwnsAgent(args, (nint)inspect, sizeof(AgentInspect)) || inspect->IsBuddyInspect
                    || !addon->IsReady || !args.EventInterfaces.Contains((nint)inspect)
                    || inspect->CurrentEntityId == 0 || inspect->CurrentEntityId == 0xE0000000
                    || inspect->AddonId != addon->Id
                    || (inspect->FetchCharacterDataStatus != 0 && inspect->FetchCharacterDataStatus != 2)) return false;
                var slot = inspect->SelectedItemSlot;
                if (slot < 0 || slot >= inspect->Items.Length) return false;
                var item = inspect->Items[slot];
                // Match the active equipment/glamour view, not an earlier hover or player menu.
                var viewingGlamours = Marshal.ReadByte((nint)inspect + InspectViewingGlamoursOffset);
                if (viewingGlamours > 1) return false;
                if (viewingGlamours == 1 && item.GlamourItemId != 0)
                    item = inspect->GlamourItems[slot];
                itemId = ViewSingleItemShopSource.NormalizeItemId(item.ItemId);
                if (itemId == 0) return false;
                glamourItemId = ViewSingleItemShopSource.NormalizeItemId(item.GlamourItemId);
                if (glamourItemId == itemId) glamourItemId = 0;
                break;
            }
            default:
                return false;
        }
        return itemId != 0;
    }

    private static bool OwnsAgent(IMenuOpenedArgs args, nint agent, int size)
    {
        if (agent == 0 || Plugin.GameGui.FindAgentInterface(args.AddonName!).Address != agent) return false;
        NearbyZoneNativeBinding.RequireReadable(agent, size);
        return true;
    }

    private static bool TryBoundItemContext(IMenuOpenedArgs args, ushort addonId, out uint itemId, out bool found)
    {
        itemId = 0;
        found = false;
        foreach (var handler in args.EventInterfaces)
        {
            if (handler == 0) continue;
            NearbyZoneNativeBinding.RequireReadable(handler, sizeof(nint));
            if (Marshal.ReadIntPtr(handler) != Plugin.SigScanner.Module.BaseAddress + ItemContextVtableRva) continue;
            found = true;
            NearbyZoneNativeBinding.RequireReadable(handler, 0x38);
            if (Marshal.ReadIntPtr(handler + 0x18) != args.AgentPtr
                || unchecked((ushort)Marshal.ReadInt16(handler + 0x28)) != addonId) { itemId = 0; return false; }
            var candidate = ViewSingleItemShopSource.NormalizeItemId(unchecked((uint)Marshal.ReadInt32(handler + 0x2C)));
            if (candidate == 0 || (itemId != 0 && candidate != itemId)) { itemId = 0; return false; }
            itemId = candidate;
        }
        return found && itemId != 0;
    }
}
