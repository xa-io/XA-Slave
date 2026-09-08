using System;
using System.Collections.Generic;

namespace XASlave.Services;

internal static class SelectionCompletion
{
    public static bool RemoveCompletedCharacter(
        IReadOnlyList<string> characters,
        ISet<int> selectedIndices,
        string completedCharacter)
    {
        ArgumentNullException.ThrowIfNull(characters);
        ArgumentNullException.ThrowIfNull(selectedIndices);

        for (var index = 0; index < characters.Count; index++)
        {
            if (!characters[index].Equals(completedCharacter, StringComparison.OrdinalIgnoreCase))
                continue;

            return selectedIndices.Remove(index);
        }

        return false;
    }
}
