using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game;
using Dalamud.Game.ClientState.Objects.Enums;
using Lumina.Excel.Sheets;
using XASlave.Localization;

namespace XASlave.Services;

internal sealed record CityChatDestination(string ConfiguredName, uint AetheryteId, uint TerritoryId, string CommandName);
internal sealed record CityChatDestinationCandidate(uint AetheryteId, uint TerritoryId, string EnglishName, string LocalizedName);
internal enum CityChatDestinationResolution { Missing, Ambiguous, Resolved }

/// <summary>Resolves one city travel target and requires its own loaded aetheryte for arrival proof.</summary>
internal static class CityChatDestinationResolver
{
    internal const float ArrivalDistance = 30f;

    internal static bool TryResolve(string configuredName, out CityChatDestination destination, out UiMessage error)
    {
        destination = null!;
        error = UiMessage.Raw(string.Empty);
        try
        {
            var data = Plugin.DataManager;
            var aetherytes = data.GetExcelSheet<Aetheryte>();
            var englishPlaces = data.GetExcelSheet<PlaceName>(ClientLanguage.English);
            var localizedPlaces = data.GetExcelSheet<PlaceName>(data.Language);
            var candidates = new List<CityChatDestinationCandidate>();
            foreach (var row in aetherytes)
            {
                if (!row.IsAetheryte || row.RowId == 0 || row.Territory.RowId == 0)
                    continue;
                var placeId = row.PlaceName.RowId;
                var englishName = englishPlaces.GetRowOrDefault(placeId)?.Name.ExtractText().Trim() ?? string.Empty;
                var localizedName = localizedPlaces.GetRowOrDefault(placeId)?.Name.ExtractText().Trim() ?? string.Empty;
                candidates.Add(new(row.RowId, row.Territory.RowId, englishName, localizedName));
            }

            var result = ResolveCandidates(configuredName, candidates, out destination);
            if (result == CityChatDestinationResolution.Resolved)
                return true;
            error = result == CityChatDestinationResolution.Ambiguous
                ? UiMessage.Format(UiLocale.Key("City destination is ambiguous: {0}. Choose one exact aetheryte name."), configuredName)
                : UiMessage.Format(UiLocale.Key("City destination was not found: {0}. Use an English or current game-language aetheryte name."), configuredName);
            return false;
        }
        catch (Exception exception)
        {
            error = UiMessage.Format(UiLocale.Key("City destination data could not be read: {0}"), UiMessage.FromException(exception));
            return false;
        }
    }

    internal static CityChatDestinationResolution ResolveCandidates(string configuredName,
        IEnumerable<CityChatDestinationCandidate> candidates, out CityChatDestination destination)
    {
        destination = null!;
        configuredName ??= string.Empty;
        var requested = configuredName.Trim();
        if (requested.Length == 0)
            return CityChatDestinationResolution.Missing;
        CityChatDestinationCandidate? match = null;
        foreach (var candidate in candidates)
        {
            if (candidate.AetheryteId == 0 || candidate.TerritoryId == 0 || string.IsNullOrWhiteSpace(candidate.LocalizedName)
                || candidate.LocalizedName.IndexOfAny(['\r', '\n']) >= 0)
                continue;
            if (!string.Equals(requested, candidate.EnglishName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(requested, candidate.LocalizedName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (match != null && (match.AetheryteId != candidate.AetheryteId || match.TerritoryId != candidate.TerritoryId
                || !string.Equals(match.LocalizedName, candidate.LocalizedName, StringComparison.Ordinal)))
                return CityChatDestinationResolution.Ambiguous;
            match = candidate;
        }
        if (match == null)
            return CityChatDestinationResolution.Missing;
        destination = new(configuredName, match.AetheryteId, match.TerritoryId, match.LocalizedName);
        return CityChatDestinationResolution.Resolved;
    }

    internal static bool IsAt(CityChatDestination destination)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null || Plugin.ClientState.TerritoryType != destination.TerritoryId)
            return false;
        var position = player.Position;
        foreach (var gameObject in Plugin.ObjectTable)
        {
            // ObjectKind.Aetheryte uses Aetheryte.RowId as its BaseId.
            if (MatchesArrival(destination, Plugin.ClientState.TerritoryType, position,
                    gameObject.BaseId, gameObject.Position, gameObject.ObjectKind == ObjectKind.Aetheryte))
                return true;
        }
        return false;
    }

    internal static bool MatchesArrival(CityChatDestination destination, uint territoryId, Vector3 playerPosition,
        uint objectDataId, Vector3 objectPosition, bool isAetheryte)
        => destination.AetheryteId != 0 && destination.TerritoryId != 0
            && territoryId == destination.TerritoryId && isAetheryte && objectDataId == destination.AetheryteId
            && IsFinite(playerPosition) && IsFinite(objectPosition)
            && Vector3.DistanceSquared(playerPosition, objectPosition) <= ArrivalDistance * ArrivalDistance;

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
