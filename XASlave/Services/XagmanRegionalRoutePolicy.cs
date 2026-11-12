using System;
using System.Collections.Generic;
using System.Linq;
using XASlave.Data;

namespace XASlave.Services;

// Home DC remains identity data. Only the processing scope is regional.
internal static class XagmanRegionalRoutePolicy
{
    internal const int Revision = 1;

    internal static bool SameRegion(string firstDataCenter, string secondDataCenter)
    {
        var region = WorldData.GetRegionOfDataCenter(firstDataCenter);
        return region != null && region.Equals(WorldData.GetRegionOfDataCenter(secondDataCenter), StringComparison.OrdinalIgnoreCase);
    }

    internal static int RegionOrdinal(string dataCenter)
    {
        var index = Array.IndexOf(WorldData.RegionOrder, WorldData.GetRegionOfDataCenter(dataCenter));
        return index < 0 ? 999 : index;
    }

    internal static string GetMeetWorld(IReadOnlyDictionary<string, string>? routes, string region)
        => routes?.FirstOrDefault(pair => pair.Key.Equals(region, StringComparison.OrdinalIgnoreCase)).Value?.Trim() ?? string.Empty;

    internal static string GetMeetDataCenter(IReadOnlyDictionary<string, string>? routes, string homeDataCenter)
    {
        var region = WorldData.GetRegionOfDataCenter(homeDataCenter);
        if (region == null) return string.Empty;
        var world = WorldData.GetByName(GetMeetWorld(routes, region));
        return world != null && world.Region.Equals(region, StringComparison.OrdinalIgnoreCase)
            ? world.DataCenter : string.Empty;
    }
}
