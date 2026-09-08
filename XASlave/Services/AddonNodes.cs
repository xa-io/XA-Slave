namespace XASlave.Services;

/// <summary>
/// Patch-sensitive addon node indices verified against the current 7.x layouts.
/// Keep every shared magic node in one place so patch-day validation has one owner.
/// </summary>
internal static class AddonNodes
{
    public const int ContentsFinderMenuLeave = 43;
    public const int CharacterRecommendEquip = 74;
    public const int RecommendEquipConfirm = 3;
}
