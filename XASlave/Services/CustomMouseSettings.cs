using System;
using System.Numerics;

namespace XASlave.Services;

internal enum CustomMouseArtworkRole
{
    Normal = 0,
    Inventory = 1,
    WindowMove = 2,
    Interaction = 3,
    ChatInteraction = 4,
    AttackTargeting = 5,
}

/// <summary>Saved cursor appearance, local artwork selection and optional animation/trails.</summary>
public sealed class CustomMouseSettings
{
    public float Scale { get; set; } = 1f;
    public Vector4 BaseColor { get; set; } = Vector4.One;
    public Vector4 OutlineColor { get; set; } = new(0, 0, 0, 1);
    public bool UseCustomArtwork { get; set; }
    public string ArtworkPath { get; set; } = string.Empty;
    public string InventoryArtworkPath { get; set; } = string.Empty;
    public string WindowMoveArtworkPath { get; set; } = string.Empty;
    public string InteractionArtworkPath { get; set; } = string.Empty;
    public string ChatInteractionArtworkPath { get; set; } = string.Empty;
    public string AttackTargetingArtworkPath { get; set; } = string.Empty;
    public bool AnimateArtwork { get; set; } = true;
    public bool CursorTrails { get; set; }
    public bool KeepActiveOnClick { get; set; }
    public bool AllowWindowsMouse { get; set; }
    public bool SmoothMovement { get; set; } = true;

    public CustomMouseSettings Normalized() => new()
    {
        Scale = float.IsFinite(Scale) ? Math.Clamp(Scale, 0.1f, 5f) : 1f,
        BaseColor = NormalizeColor(BaseColor, Vector4.One),
        OutlineColor = NormalizeColor(OutlineColor, new(0, 0, 0, 1)),
        UseCustomArtwork = UseCustomArtwork,
        ArtworkPath = NormalizeArtworkPath(ArtworkPath),
        InventoryArtworkPath = NormalizeArtworkPath(InventoryArtworkPath),
        WindowMoveArtworkPath = NormalizeArtworkPath(WindowMoveArtworkPath),
        InteractionArtworkPath = NormalizeArtworkPath(InteractionArtworkPath),
        ChatInteractionArtworkPath = NormalizeArtworkPath(ChatInteractionArtworkPath),
        AttackTargetingArtworkPath = NormalizeArtworkPath(AttackTargetingArtworkPath),
        AnimateArtwork = AnimateArtwork,
        CursorTrails = CursorTrails,
        KeepActiveOnClick = KeepActiveOnClick,
        AllowWindowsMouse = AllowWindowsMouse,
        // Compatibility field: cursor artwork uses independent OS movement unless trails require software.
        SmoothMovement = true,
    };

    internal string GetArtworkPath(CustomMouseArtworkRole role) => (role switch
    {
        CustomMouseArtworkRole.Normal => ArtworkPath,
        CustomMouseArtworkRole.Inventory => InventoryArtworkPath,
        CustomMouseArtworkRole.WindowMove => WindowMoveArtworkPath,
        CustomMouseArtworkRole.Interaction => InteractionArtworkPath,
        CustomMouseArtworkRole.ChatInteraction => ChatInteractionArtworkPath,
        CustomMouseArtworkRole.AttackTargeting => AttackTargetingArtworkPath,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    }) ?? string.Empty;

    internal string ResolveArtworkPath(CustomMouseArtworkRole role)
    {
        var path = GetArtworkPath(role);
        return string.IsNullOrWhiteSpace(path) ? ArtworkPath ?? string.Empty : path;
    }

    internal void SetArtworkPath(CustomMouseArtworkRole role, string? path)
    {
        var normalized = NormalizeArtworkPath(path);
        switch (role)
        {
            case CustomMouseArtworkRole.Normal: ArtworkPath = normalized; break;
            case CustomMouseArtworkRole.Inventory: InventoryArtworkPath = normalized; break;
            case CustomMouseArtworkRole.WindowMove: WindowMoveArtworkPath = normalized; break;
            case CustomMouseArtworkRole.Interaction: InteractionArtworkPath = normalized; break;
            case CustomMouseArtworkRole.ChatInteraction: ChatInteractionArtworkPath = normalized; break;
            case CustomMouseArtworkRole.AttackTargeting: AttackTargetingArtworkPath = normalized; break;
            default: throw new ArgumentOutOfRangeException(nameof(role));
        }
    }

    private static string NormalizeArtworkPath(string? path) =>
        path is { Length: <= 1024 } ? path.Trim().Trim('"') : string.Empty;

    private static Vector4 NormalizeColor(Vector4 value, Vector4 fallback) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W)
            ? Vector4.Clamp(value, Vector4.Zero, Vector4.One) : fallback;

    /// <summary>Map dark ink to outline and light ink to base, retaining antialiasing and source alpha.</summary>
    internal byte[] Recolor(ReadOnlySpan<byte> bgra)
    {
        if (bgra.Length % 4 != 0) throw new ArgumentException("Expected BGRA pixels.", nameof(bgra));
        var settings = Normalized();
        var result = new byte[bgra.Length];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var light = (bgra[i] * 0.0722f + bgra[i + 1] * 0.7152f + bgra[i + 2] * 0.2126f) / 255f;
            var color = Vector4.Lerp(settings.OutlineColor, settings.BaseColor, light);
            result[i] = (byte)Math.Clamp((int)MathF.Round(color.Z * 255), 0, 255);
            result[i + 1] = (byte)Math.Clamp((int)MathF.Round(color.Y * 255), 0, 255);
            result[i + 2] = (byte)Math.Clamp((int)MathF.Round(color.X * 255), 0, 255);
            result[i + 3] = (byte)Math.Clamp((int)MathF.Round(bgra[i + 3] * color.W), 0, 255);
        }
        return result;
    }
}
