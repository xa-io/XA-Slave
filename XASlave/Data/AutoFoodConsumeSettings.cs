using System;

namespace XASlave.Data;

[Serializable]
public sealed class AutoFoodConsumeSettings
{
    public uint ItemId { get; set; }
    public bool HighQuality { get; set; }
    public bool OnlyInSanctuary { get; set; }
    public bool OnlyInDuty { get; set; }
    public int RefreshThresholdSeconds { get; set; } = 30;

    public AutoFoodConsumeSettings Normalized() => new()
    {
        ItemId = ItemId,
        HighQuality = HighQuality,
        OnlyInSanctuary = OnlyInSanctuary,
        OnlyInDuty = OnlyInDuty,
        RefreshThresholdSeconds = Math.Clamp(RefreshThresholdSeconds, 0, 300),
    };
}
