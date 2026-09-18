using Dalamud.Game;

namespace XASlave.Services;

// Presentation only; neither inventory ownership nor deduplication uses these strings.
internal static class InventoryShuttleLabels
{
    internal static string Destination(InventoryShuttleFamily family, ClientLanguage language) => (language, family) switch
    {
        (ClientLanguage.Japanese, InventoryShuttleFamily.Player) => "所持品に移す",
        (ClientLanguage.Japanese, InventoryShuttleFamily.Saddlebag) => "チョコボかばんに移す",
        (ClientLanguage.Japanese, InventoryShuttleFamily.PremiumSaddlebag) => "チョコボかばん（追加分）に移す",
        (ClientLanguage.Japanese, InventoryShuttleFamily.Retainer) => "リテイナーに預ける",
        (ClientLanguage.German, InventoryShuttleFamily.Player) => "Ins Inventar verschieben",
        (ClientLanguage.German, InventoryShuttleFamily.Saddlebag) => "In die Chocobo-Satteltasche verschieben",
        (ClientLanguage.German, InventoryShuttleFamily.PremiumSaddlebag) => "In die erweiterte Chocobo-Satteltasche verschieben",
        (ClientLanguage.German, InventoryShuttleFamily.Retainer) => "An Gehilfen übergeben",
        (ClientLanguage.French, InventoryShuttleFamily.Player) => "Déplacer dans l'inventaire",
        (ClientLanguage.French, InventoryShuttleFamily.Saddlebag) => "Déplacer dans la sacoche chocobo",
        (ClientLanguage.French, InventoryShuttleFamily.PremiumSaddlebag) => "Déplacer dans la sacoche chocobo supplémentaire",
        (ClientLanguage.French, InventoryShuttleFamily.Retainer) => "Confier au servant",
        (_, InventoryShuttleFamily.Saddlebag) => "Move To Saddlebag",
        (_, InventoryShuttleFamily.PremiumSaddlebag) => "Move To Premium Saddlebag",
        (_, InventoryShuttleFamily.Retainer) => "Move To Retainer",
        _ => "Move To Inventory",
    };
}
