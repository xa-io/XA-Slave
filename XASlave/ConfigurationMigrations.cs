using System;
using Dalamud.Plugin.Services;

namespace XASlave;

internal static class ConfigurationMigrations
{
    public static bool Apply(Configuration configuration, IPluginLog log, Action<Configuration> migrateToV1)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(migrateToV1);

        if (configuration.Version > Configuration.CurrentVersion)
        {
            log.Error(
                $"[XASlave] Configuration schema v{configuration.Version} is newer than supported v{Configuration.CurrentVersion}; refusing to load it so the older plugin cannot overwrite unknown settings.");
        }

        if (configuration.Version < 0)
        {
            log.Warning($"[XASlave] Invalid configuration schema v{configuration.Version}; resuming migrations from v0.");
            configuration.Version = 0;
        }

        return ConfigurationMigrationEngine.ApplyInOrder(
            configuration.Version,
            Configuration.CurrentVersion,
            target =>
            {
                log.Information($"[XASlave] Applying configuration migration v{configuration.Version} -> v{target}.");
                switch (target)
                {
                    case 1:
                        migrateToV1(configuration);
                        break;
                    default:
                        throw new InvalidOperationException($"No XA Slave configuration migration is registered for v{target}.");
                }
            },
            target => configuration.Version = target,
            configuration.Save);
    }
}
