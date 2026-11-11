using System;
using System.Collections.Generic;

namespace XASlave.Services;

internal readonly record struct XagmanForecastSelectionCandidate(
    int Index, string CharacterKey, bool IsEligible, bool IsKnown, long Quantity);

internal static class XagmanForecastSelectionPolicy
{
    internal static bool TrySelectAtLeast(
        IEnumerable<XagmanForecastSelectionCandidate> candidates, long minimum, bool canEdit,
        out HashSet<int> selected, out int knownCount, out int unknownCount)
    {
        selected = new HashSet<int>();
        knownCount = 0;
        unknownCount = 0;
        if (!canEdit || minimum < 0)
            return false;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (candidate.Index < 0 || !candidate.IsEligible || string.IsNullOrWhiteSpace(candidate.CharacterKey)
                || !seen.Add(candidate.CharacterKey))
                continue;
            if (!candidate.IsKnown || candidate.Quantity < 0)
            {
                unknownCount++;
                continue;
            }
            knownCount++;
            if (candidate.Quantity >= minimum)
                selected.Add(candidate.Index);
        }
        // A failed/missing inventory source must never silently clear the user's selection.
        return knownCount > 0;
    }

    internal static bool RemoveCharacter(ISet<int> selectedIndices, IReadOnlyList<string> roster,
        string characterKey, bool canEdit)
    {
        if (!canEdit || string.IsNullOrWhiteSpace(characterKey))
            return false;
        var removed = false;
        for (var index = 0; index < roster.Count; index++)
        {
            if (string.Equals(roster[index], characterKey, StringComparison.OrdinalIgnoreCase))
                removed |= selectedIndices.Remove(index);
        }
        return removed;
    }
}
