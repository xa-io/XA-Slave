using System;

namespace XASlave;

internal static class ConfigurationMigrationEngine
{
    public static bool ApplyInOrder(
        int startingVersion,
        int targetVersion,
        Action<int> migrateToVersion,
        Action<int> setVersion,
        Action save)
    {
        ArgumentNullException.ThrowIfNull(migrateToVersion);
        ArgumentNullException.ThrowIfNull(setVersion);
        ArgumentNullException.ThrowIfNull(save);

        if (startingVersion > targetVersion)
        {
            throw new InvalidOperationException(
                $"Configuration schema v{startingVersion} is newer than supported v{targetVersion}.");
        }

        var version = Math.Max(0, startingVersion);
        var changed = false;
        while (version < targetVersion)
        {
            var nextVersion = version + 1;
            migrateToVersion(nextVersion);
            setVersion(nextVersion);
            save();
            version = nextVersion;
            changed = true;
        }

        return changed;
    }
}
