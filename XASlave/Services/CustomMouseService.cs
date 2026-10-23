using System;
using UiMessage = XASlave.Localization.UiMessage;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Config;
using Dalamud.Hooking;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Data.Files;
using GameCursor = FFXIVClientStructs.FFXIV.Client.System.Input.Cursor;
using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;

namespace XASlave.Services;

/// <summary>Customizes game and user artwork with private Windows cursors and an optional software trail renderer.</summary>
public sealed class CustomMouseService : IDisposable
{
    private const int MaximumArtworkFileBytes = 16 * 1024 * 1024;
    private static readonly CustomMouseArtworkRole[] ArtworkRoles = Enum.GetValues<CustomMouseArtworkRole>();
    private const int SoftwareRendererOffset = 0x2C8;
    // Installed 2026-09-17 client Cursor.Update: custom-handle flag and first HCURSOR slot.
    private const int CustomHardwareModeOffset = 0x08;
    private const int CustomHardwareHandlesOffset = 0x1B0;
    // Verified against the installed 2026-09-17 client. Resolve code, never use absolute addresses.
    private static readonly ProtectedSig CursorDrawSignature = ProtectedSig.Create(0x6E390712u,
        [0xDF, 0xC2, 0x65, 0x85, 0xFD, 0x8A, 0x01, 0xFB, 0xEC, 0xE4, 0xCC, 0x62, 0xCC, 0x6D, 0x84, 0x67, 0xDB, 0x42, 0x80, 0xBA, 0xC9],
        [0xE2, 0x5C, 0x03, 0x91, 0x1B, 0xA6, 0x61, 0x39, 0xC3, 0xD6, 0xA5, 0x84, 0xEB, 0xB5, 0x42, 0x39, 0xD9, 0xE1, 0x56, 0xBB, 0xA1]);
    private static readonly ProtectedSig SpriteDrawSignature = ProtectedSig.Create(0x1AF2086Du,
        [0x9E, 0x12, 0xC8, 0xEB, 0x87, 0x5C, 0x8A, 0x39, 0xCB, 0xEA, 0x2C, 0xD4, 0x37, 0x97, 0xEB, 0xAC, 0x94, 0x36, 0x0D, 0xB0, 0xC9, 0x89, 0x0B, 0xFA],
        [0x57, 0xF1, 0x53, 0xB6, 0x0A, 0xF0, 0x17, 0x3D, 0xBA, 0x57, 0xA6, 0x58, 0x87, 0x28, 0x0B, 0x4E, 0x81, 0x99, 0xCC, 0xDD, 0x4F, 0x7A, 0x8A, 0x89]);
    private static readonly ProtectedSig CursorUpdateSignature = ProtectedSig.Create(0x442C10F5u,
        [0x24, 0x60, 0xDB, 0x9A, 0xBA, 0x02, 0x55, 0xAB, 0x0A, 0xF5, 0x22, 0xF6, 0x91, 0x88, 0xCF, 0xD4, 0xD5, 0xD8, 0x57, 0x42, 0xB1, 0x9F, 0xF5, 0x22, 0x62, 0x3B, 0xC3, 0x3A, 0x67, 0x98, 0x6D, 0x9C, 0x1A, 0x9D, 0x7E],
        [0x81, 0x3C, 0x58, 0x68, 0x4C, 0x6D, 0x17, 0x66, 0x86, 0x5B, 0xE8, 0x85, 0x24, 0x7C, 0xE5, 0xCC, 0xC2, 0xA0, 0xEC, 0x78, 0x94, 0xCA, 0xE2, 0x68, 0x53, 0x92, 0xFE, 0x20, 0x70, 0xB8, 0xD4, 0xE5, 0x71, 0x6B, 0x95]);

    private readonly IFramework framework;
    private readonly IDataManager data;
    private readonly ITextureProvider textures;
    private readonly IPluginLog log;
    private readonly ISigScanner scanner;
    private readonly IGameInteropProvider interop;
    private readonly IGameConfig gameConfig;
    private readonly IUiBuilder uiBuilder;
    private readonly ITextureReadbackProvider readback;
    private readonly object sync = new();
    private readonly Dictionary<string, Atlas> atlases = new(StringComparer.Ordinal);
    private Hook<CursorDrawDelegate>? cursorDrawHook;
    private Hook<SpriteDrawDelegate>? spriteDrawHook;
    private Hook<CursorUpdateDelegate>? cursorUpdateHook;
    private CustomMouseSettings settings = new();
    private CancellationTokenSource cancellation = new();
    private bool enabled;
    private bool disposed;
    private bool invalidate;
    private bool faulted;
    private bool rendererModePending;
    private bool nativeDrawObserved;
    private volatile bool customHardwareActive;
    private long uiCaptureUntil;
    private bool ownsSoftwareSetting;
    private bool writingSoftwareSetting;
    private uint originalSoftwareSetting;
    private bool ownsTrailSetting;
    private bool trailSettingCaptured;
    private bool writingTrailSetting;
    private bool trailPreferencePending;
    private uint originalTrailSetting;
    private uint appliedTrailSetting;
    private string texturePath = string.Empty;
    private nint textureResourceAddress;
    private nint textureKernelAddress;
    private ushort textureWidth;
    private ushort textureHeight;
    private Atlas? currentAtlas;
    private int currentNativePreset = -1;
    private readonly Dictionary<string, ArtworkResource> artworkResources = new(StringComparer.OrdinalIgnoreCase);
    private readonly ArtworkResource?[] roleArtworks = new ArtworkResource?[ArtworkRoles.Length];
    private readonly HashSet<string> artworkReloadPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool artworkPathsPending = true;
    private long nextDiagnosticAt;
    [ThreadStatic] private static CustomMouseService? drawingService;
    [ThreadStatic] private static int drawingPreset;

    public string StatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? uiStatusText = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string LocalizedStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiStatusText, StatusText);
    internal global::XASlave.Localization.UiMessage StatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiStatusText, StatusText);
    public string ArtworkStatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S5744D0597F0C3DDA");
    private global::XASlave.Localization.UiMessage? uiArtworkStatusText = global::XASlave.Localization.UiMessage.Key("S5744D0597F0C3DDA");
    internal string LocalizedArtworkStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiArtworkStatusText, ArtworkStatusText);
    internal global::XASlave.Localization.UiMessage ArtworkStatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiArtworkStatusText, ArtworkStatusText);

    public CustomMouseService(IFramework framework, IDataManager data, ITextureProvider textures, IPluginLog log,
        ISigScanner scanner, IGameInteropProvider interop, IGameConfig gameConfig, IUiBuilder uiBuilder, ITextureReadbackProvider readback)
    {
        this.framework = framework;
        this.data = data;
        this.textures = textures;
        this.log = log;
        this.scanner = scanner;
        this.interop = interop;
        this.gameConfig = gameConfig;
        this.uiBuilder = uiBuilder;
        this.readback = readback;
        framework.Update += OnUpdate;
        gameConfig.SystemChanged += OnGameConfigChanged;
        uiBuilder.Draw += CaptureUiMouseOwnership;
        uiBuilder.HideUi += ReleaseUiMouseOwnership;
    }

    public void ApplyConfiguration(CustomMouseSettings? value, bool reloadArtwork = false) =>
        ApplyConfiguration(value, reloadArtwork, null);

    internal void ApplyConfiguration(CustomMouseSettings? value, bool reloadArtwork, CustomMouseArtworkRole? reloadRole)
    {
        lock (sync)
        {
            if (disposed) return;
            var next = (value ?? new()).Normalized();
            if (next.BaseColor != settings.BaseColor || next.OutlineColor != settings.OutlineColor
                || next.UseCustomArtwork != settings.UseCustomArtwork
                || next.AllowWindowsMouse != settings.AllowWindowsMouse || next.Scale != settings.Scale)
                invalidate = true;
            if (next.AllowWindowsMouse != settings.AllowWindowsMouse) rendererModePending = true;
            if (next.CursorTrails != settings.CursorTrails) trailPreferencePending = true;
            var pathsChanged = false;
            foreach (var role in ArtworkRoles)
                if (!StringComparer.OrdinalIgnoreCase.Equals(next.ResolveArtworkPath(role),
                    settings.ResolveArtworkPath(role))) pathsChanged = true;
            if (pathsChanged) artworkPathsPending = true;
            if (reloadArtwork && !pathsChanged)
            {
                if (reloadRole.HasValue) artworkReloadPaths.Add(next.ResolveArtworkPath(reloadRole.Value));
                else foreach (var path in GetDistinctArtworkPaths(next)) artworkReloadPaths.Add(path);
                artworkPathsPending = true;
            }
            if (next.AnimateArtwork != settings.AnimateArtwork)
                foreach (var resource in artworkResources.Values) resource.AnimationStartedAt = Environment.TickCount64;
            settings = next;
            nextDiagnosticAt = 0;
        }
    }

    public unsafe bool SetEnabled(bool value)
    {
        lock (sync)
        {
            if (disposed) return false;
            if (!value)
            {
                enabled = false;
                DisableHooks();
                RestoreSoftwareSetting();
                RestoreTrailSetting();
                ClearAtlases();
                SetStatus(global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"));
                return false;
            }
            if (enabled) return true;
            try
            {
                ApplyRendererMode();
                faulted = false;
                nativeDrawObserved = false;
                nextDiagnosticAt = 0;
                enabled = true;
                SetStatus(global::XASlave.Localization.UiMessage.Key("S7E4727E634180657"));
                log.Information("[Custom Mouse] Enabled native cursor renderer; scale={Scale:F2}x, base={Base}, outline={Outline}, software setting owned={Owned}.", settings.Scale, settings.BaseColor, settings.OutlineColor, ownsSoftwareSetting);
                return true;
            }
            catch (Exception ex)
            {
                enabled = false;
                DisableHooks();
                RestoreSoftwareSetting();
                RestoreTrailSetting();
                SetStatus(global::XASlave.Localization.UiMessage.Key("S69CB46434A483EC0"));
                log.Warning(ex, "[Custom Mouse] Could not initialize the native cursor renderer.");
                return false;
            }
        }
    }

    private unsafe void ApplyRendererMode()
    {
        cursorDrawHook ??= interop.HookFromAddress<CursorDrawDelegate>(scanner.ScanText(CursorDrawSignature), CursorDrawDetour);
        if (settings.AllowWindowsMouse)
        {
            cursorUpdateHook ??= interop.HookFromAddress<CursorUpdateDelegate>(scanner.ScanText(CursorUpdateSignature), CursorUpdateDetour);
            if (spriteDrawHook is { IsEnabled: true }) spriteDrawHook.Disable();
            cursorDrawHook.Enable();
            cursorUpdateHook.Enable();
            RestoreSoftwareSetting();
            RestoreTrailSetting();
        }
        else
        {
            cursorUpdateHook ??= interop.HookFromAddress<CursorUpdateDelegate>(scanner.ScanText(CursorUpdateSignature), CursorUpdateDetour);
            cursorUpdateHook.Enable();
            spriteDrawHook ??= interop.HookFromAddress<SpriteDrawDelegate>(scanner.ScanText(SpriteDrawSignature), SpriteDrawDetour);
            spriteDrawHook.Enable();
            cursorDrawHook.Enable();
            EnableSoftwareSetting();
            ApplyTrailSetting();
        }
        customHardwareActive = false;
        rendererModePending = false;
    }

    private void CaptureUiMouseOwnership()
    {
        if (!enabled || disposed) { ReleaseUiMouseOwnership(); return; }
        var io = ImGui.GetIO();
        var canChangeCursor = (io.ConfigFlags & ImGuiConfigFlags.NoMouseCursorChange) == 0;
        var captured = canChangeCursor && (io.MouseDrawCursor || (io.WantCaptureMouse && uiBuilder.OverrideGameCursor));
        // Frame snapshots avoid touching ImGui from the native game/render callbacks.
        Volatile.Write(ref uiCaptureUntil, captured ? Environment.TickCount64 + 500 : 0);
    }

    private void ReleaseUiMouseOwnership() => Volatile.Write(ref uiCaptureUntil, 0);

    private bool IsUiMouseOwned => Volatile.Read(ref uiCaptureUntil) > Environment.TickCount64;

    private unsafe bool ShouldKeepVisibleOnClick(GameCursor* cursor)
    {
        if (!settings.KeepActiveOnClick || cursor == null || cursor->IsCursorOutsideViewPort || IsUiMouseOwned) return false;
        var input = UIInputData.Instance();
        return input != null && ShouldKeepVisibleOnClick(settings.KeepActiveOnClick, input->CursorInputs.IsGameWindowFocused,
            cursor->IsCursorOutsideViewPort, IsUiMouseOwned, input->CurrentMouseDragButtons);
    }

    internal static bool ShouldKeepVisibleOnClick(bool requested, bool focused, bool outsideViewport, bool uiOwned, byte dragButtons) =>
        requested && focused && !outsideViewport && !uiOwned
        && (dragButtons & (byte)(MouseButtonFlags.LBUTTON | MouseButtonFlags.RBUTTON)) != 0;

    private void EnableSoftwareSetting()
    {
        if (!gameConfig.TryGet(SystemConfigOption.SystemMouseOperationSoftOn, out uint before))
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SC0381DD0943D6D66"), originalMessage => new InvalidOperationException(originalMessage));
        if (before != 0) return;
        originalSoftwareSetting = before;
        // Own the write before performing it so partial failure can still be rolled back.
        ownsSoftwareSetting = true;
        writingSoftwareSetting = true;
        try { gameConfig.Set(SystemConfigOption.SystemMouseOperationSoftOn, 1u); }
        finally { writingSoftwareSetting = false; }
    }

    private void RestoreSoftwareSetting()
    {
        if (!ownsSoftwareSetting) return;
        ownsSoftwareSetting = false;
        try
        {
            // An independent game-setting change supersedes our temporary selection.
            if (gameConfig.TryGet(SystemConfigOption.SystemMouseOperationSoftOn, out uint current) && current == 1)
            {
                writingSoftwareSetting = true;
                try { gameConfig.Set(SystemConfigOption.SystemMouseOperationSoftOn, originalSoftwareSetting); }
                finally { writingSoftwareSetting = false; }
                log.Information("[Custom Mouse] Restored previous game software cursor setting: {Value}.", originalSoftwareSetting);
            }
        }
        catch (Exception ex) { log.Warning(ex, "[Custom Mouse] Could not restore the previous software cursor setting."); }
    }

    private void OnGameConfigChanged(object? sender, ConfigChangeEvent args)
    {
        if (args.Option is not SystemConfigOption option) return;
        lock (sync)
        {
            if (option == SystemConfigOption.SystemMouseOperationSoftOn && !writingSoftwareSetting && ownsSoftwareSetting)
            {
                ownsSoftwareSetting = false;
                log.Information("[Custom Mouse] Software cursor setting changed independently; restore ownership released.");
            }
            if (option == SystemConfigOption.SystemMouseOperationTrajectory && !writingTrailSetting && trailSettingCaptured)
            {
                ownsTrailSetting = trailSettingCaptured = trailPreferencePending = false;
                log.Information("[Custom Mouse] Cursor trails changed independently; restore ownership released.");
            }
        }
    }

    private void ApplyTrailSetting()
    {
        if (!gameConfig.TryGet(SystemConfigOption.SystemMouseOperationTrajectory, out uint before))
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SC7B26E55D1FA58C3"), originalMessage => new InvalidOperationException(originalMessage));
        if (!trailSettingCaptured)
        {
            originalTrailSetting = before;
            trailSettingCaptured = true;
        }
        appliedTrailSetting = settings.CursorTrails ? 1u : 0u;
        trailPreferencePending = false;
        if (before == appliedTrailSetting) return;
        ownsTrailSetting = true;
        writingTrailSetting = true;
        try { gameConfig.Set(SystemConfigOption.SystemMouseOperationTrajectory, appliedTrailSetting); }
        finally { writingTrailSetting = false; }
        log.Information("[Custom Mouse] Cursor trails: {Enabled}.", settings.CursorTrails);
    }

    private void RestoreTrailSetting()
    {
        var restore = ownsTrailSetting;
        ownsTrailSetting = trailSettingCaptured = trailPreferencePending = false;
        if (!restore) return;
        try
        {
            if (gameConfig.TryGet(SystemConfigOption.SystemMouseOperationTrajectory, out uint current) && current == appliedTrailSetting)
            {
                writingTrailSetting = true;
                try { gameConfig.Set(SystemConfigOption.SystemMouseOperationTrajectory, originalTrailSetting); }
                finally { writingTrailSetting = false; }
                log.Information("[Custom Mouse] Restored previous cursor trail setting: {Value}.", originalTrailSetting);
            }
        }
        catch (Exception ex) { log.Warning(ex, "[Custom Mouse] Could not restore the previous cursor trail setting."); }
    }

    private unsafe void OnUpdate(IFramework sender)
    {
        lock (sync)
        {
            if (disposed) return;
            _ = CustomMouseHardwareCursor.RegisterGameThread();
            _ = CustomMouseHardwareCursor.TryDrainRetiredOnGameThread(out _);
            if (invalidate) { ClearAtlases(); invalidate = false; }
            if (!enabled) return;
            if (faulted)
            {
                enabled = false;
                DisableHooks();
                RestoreSoftwareSetting();
                RestoreTrailSetting();
                ClearAtlases();
                SetStatus(global::XASlave.Localization.UiMessage.Key("S0990B1101D90CA2F"));
                return;
            }
            try
            {
                if (rendererModePending) ApplyRendererMode();
                if (settings.AllowWindowsMouse)
                {
                    ArtworkStatusText = global::XASlave.Localization.UiMessage.Store(ref uiArtworkStatusText, global::XASlave.Localization.UiMessage.Key("SC3D99EDE79D66BF2"));
                    SetStatus(global::XASlave.Localization.UiMessage.Key("S7D9324FA8CBF1442"));
                    return;
                }
                if (trailPreferencePending) ApplyTrailSetting();
                UpdateArtwork();
                var cursor = GameCursor.Instance();
                if (cursor == null) { SetStatus(global::XASlave.Localization.UiMessage.Key("SD362A29105877EA7")); return; }
                if (!cursor->UseSoftwareCursor) { SetStatus(global::XASlave.Localization.UiMessage.Key("S8BF1024A8330AB79")); return; }
                var resource = cursor->SoftwareCursorTexture;
                if (resource != null && resource->Texture != null)
                {
                    if (textureResourceAddress != (nint)resource || textureKernelAddress != (nint)resource->Texture
                        || textureWidth != resource->Header.Width || textureHeight != resource->Header.Height)
                    {
                        textureResourceAddress = (nint)resource;
                        textureKernelAddress = (nint)resource->Texture;
                        textureWidth = resource->Header.Width;
                        textureHeight = resource->Header.Height;
                        texturePath = resource->FileName.ToString();
                        currentAtlas = null;
                        log.Information("[Custom Mouse] Native cursor atlas: {Path} ({Width}x{Height}).", texturePath, resource->Header.Width, resource->Header.Height);
                    }
                    var path = texturePath;
                    var preset = *(int*)((byte*)cursor + SoftwareRendererOffset + 0x90);
                    currentNativePreset = preset;
                    if (preset is >= 0 and <= 2 && !string.IsNullOrEmpty(path))
                    {
                        if (currentAtlas == null || currentAtlas.Preset != preset)
                        {
                            // A complete 5x native preset can reserve 27 MiB; retire old presets before preparing another.
                            foreach (var previousAtlas in atlases.Values) previousAtlas.Dispose();
                            atlases.Clear();
                            customHardwareActive = false;
                            _ = CustomMouseHardwareCursor.TryDrainRetiredOnGameThread(out _);
                            var atlasCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                            currentAtlas = new Atlas(LoadAtlasAsync(path, preset, textureWidth, textureHeight, settings.Normalized(), atlasCancellation.Token), preset, atlasCancellation);
                            atlases.Add(path, currentAtlas);
                        }
                    }
                    else currentAtlas = null;
                }
                else
                {
                    textureResourceAddress = textureKernelAddress = 0;
                    textureWidth = textureHeight = 0;
                    texturePath = string.Empty;
                    currentAtlas = null;
                }
                foreach (var atlas in atlases.Values)
                {
                    if (atlas.Wrap != null || atlas.Failed || !atlas.Pending.IsCompleted) continue;
                    try
                    {
                        var loaded = atlas.Pending.GetAwaiter().GetResult();
                        atlas.Wrap = loaded.Wrap;
                        atlas.HardwareCursors = loaded.HardwareCursors;
                        atlas.HardwareFailure = loaded.HardwareFailure;
                        atlas.Convert(textures);
                        log.Information("[Custom Mouse] Private native cursor atlas ready; hardware types={Types}.", atlas.HardwareCursors.Length);
                    }
                    catch (Exception ex)
                    {
                        atlas.Failed = true;
                        log.Warning(ex, "[Custom Mouse] Could not prepare a private cursor texture; original colors retained.");
                    }
                }
                var now = Environment.TickCount64;
                if (now >= nextDiagnosticAt)
                {
                    nextDiagnosticAt = now + 1000;
                    if (IsUiMouseOwned)
                        SetStatus(global::XASlave.Localization.UiMessage.Key("SC0F8BFCD937FF87D"));
                    else if (customHardwareActive)
                        SetStatus(global::XASlave.Localization.UiMessage.Key("SBD8FBCD42A726BF3"));
                    else if (nativeDrawObserved && settings.CursorTrails)
                        SetStatus(global::XASlave.Localization.UiMessage.Key("SBCDE22C9FA6BF256"));
                    else if (nativeDrawObserved && currentAtlas?.HardwareFailure != null)
                        SetStatus(global::XASlave.Localization.UiMessage.Key("S9F69128F3C145A85"));
                    else if (nativeDrawObserved)
                        SetStatus((settings.UseCustomArtwork ? (GetArtworkResource(cursor->ActiveCursorType)?.Current?.Ready == true ? global::XASlave.Localization.UiMessage.Key("S6D1A1A1E863EC093") : (GetArtworkResource(cursor->ActiveCursorType)?.Failed == true ? global::XASlave.Localization.UiMessage.Key("S46557BB798AC6EA8") : global::XASlave.Localization.UiMessage.Key("S8C51C15FE1833399"))) : (NeedsRecolor ? (currentAtlas?.Ready == true ? global::XASlave.Localization.UiMessage.Key("SC12EB29D41C23E9B") : (currentAtlas?.Failed == true ? global::XASlave.Localization.UiMessage.Key("SA4F462BD971F259E") : global::XASlave.Localization.UiMessage.Key("SB0C41F2726FB4E74"))) : global::XASlave.Localization.UiMessage.Key("S84A88542B1E54949"))));
                    else if (!cursor->IsCursorVisible || cursor->IsCursorOutsideViewPort)
                        SetStatus(global::XASlave.Localization.UiMessage.Key("S6CA8CADB9799A7CB"));
                    else
                        SetStatus(global::XASlave.Localization.UiMessage.Key("S53448704C3F0F323"));
                    nativeDrawObserved = false;
                }
            }
            catch (Exception ex)
            {
                faulted = true;
                log.Warning(ex, "[Custom Mouse] Cursor observation failed; customization will be disabled.");
            }
        }
    }

    private bool NeedsRecolor => settings.BaseColor != Vector4.One || settings.OutlineColor != new Vector4(0, 0, 0, 1);

    internal global::XASlave.Localization.UiMessage GetArtworkStatusMessage(CustomMouseArtworkRole role)
    {
        lock (sync)
        {
            if ((uint)role >= roleArtworks.Length) return global::XASlave.Localization.UiMessage.Key("SF1A9423632901C3E");
            if (settings.AllowWindowsMouse) return global::XASlave.Localization.UiMessage.Key("S07255DE5B16A5BAF");
            if (!settings.UseCustomArtwork) return global::XASlave.Localization.UiMessage.Key("S5744D0597F0C3DDA");
            if (string.IsNullOrWhiteSpace(settings.ResolveArtworkPath(role)))
                return global::XASlave.Localization.UiMessage.Key("S597E4CF4AAF804A3");
            if (artworkPathsPending) return global::XASlave.Localization.UiMessage.Key("S6FEAD7D2E7219B3B");
            var resource = roleArtworks[(int)role];
            var status = resource?.StatusMessage ?? global::XASlave.Localization.UiMessage.Key("SA58AC1BD56949C40");
            return (role != CustomMouseArtworkRole.Normal && string.IsNullOrWhiteSpace(settings.GetArtworkPath(role)) ? global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Key("SFF5D5079552EC57B"), status) : status);
        }
    }

    internal string GetArtworkStatus(CustomMouseArtworkRole role) => GetArtworkStatusMessage(role).Original;
    internal string GetLocalizedArtworkStatus(CustomMouseArtworkRole role) => GetArtworkStatusMessage(role).Display;

    internal static string[] GetDistinctArtworkPaths(CustomMouseSettings appearance)
    {
        var result = new List<string>(ArtworkRoles.Length);
        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in ArtworkRoles)
        {
            var path = appearance.ResolveArtworkPath(role);
            if (!string.IsNullOrWhiteSpace(path) && distinct.Add(path)) result.Add(path);
        }
        return result.ToArray();
    }

    private void ReconcileArtworkPaths()
    {
        var desired = new HashSet<string>(GetDistinctArtworkPaths(settings), StringComparer.OrdinalIgnoreCase);
        var removed = new List<string>();
        foreach (var pair in artworkResources)
            if (!desired.Contains(pair.Key) || artworkReloadPaths.Contains(pair.Key)) removed.Add(pair.Key);
        foreach (var path in removed)
        {
            artworkResources[path].Dispose();
            artworkResources.Remove(path);
            customHardwareActive = false;
        }
        foreach (var path in desired)
        {
            if (artworkResources.ContainsKey(path)) continue;
            var resource = new ArtworkResource(CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token));
            resource.Pending = LoadArtworkAsync(path, settings.Normalized(), resource.Cancellation.Token);
            artworkResources.Add(path, resource);
        }
        foreach (var role in ArtworkRoles)
            roleArtworks[(int)role] = artworkResources.GetValueOrDefault(settings.ResolveArtworkPath(role));
        artworkReloadPaths.Clear();
        artworkPathsPending = false;
        _ = CustomMouseHardwareCursor.TryDrainRetiredOnGameThread(out _);
        RefreshArtworkStatus();
    }

    private void UpdateArtwork()
    {
        if (!settings.UseCustomArtwork)
        {
            ArtworkStatusText = global::XASlave.Localization.UiMessage.Store(ref uiArtworkStatusText, global::XASlave.Localization.UiMessage.Key("S5744D0597F0C3DDA"));
            return;
        }
        if (artworkPathsPending) ReconcileArtworkPaths();
        var changed = false;
        var now = Environment.TickCount64;
        foreach (var resource in artworkResources.Values)
        {
            if (resource.Pending is { IsCompleted: true })
            {
                var completed = resource.Pending;
                resource.Pending = null;
                changed = true;
                try
                {
                    var loaded = completed.GetAwaiter().GetResult();
                    try { loaded.Convert(textures); }
                    catch { loaded.Dispose(); throw; }
                    resource.Current = loaded;
                    resource.AnimationStartedAt = now;
                    resource.FrameIndex = 0;
                    resource.Status = global::XASlave.Localization.UiMessage.Store(ref resource.StatusToken, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Format("S5124647E7C719E69", loaded.Model.Frames.Count, loaded.Model.Steps.Count), ((loaded.PreservesPngColors ? global::XASlave.Localization.UiMessage.Key("S3CA27D935F281007") : global::XASlave.Localization.UiMessage.Raw(string.Empty)))), ((loaded.HardwareFailure != null ? global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Key("S2CB6371AFA119D18"), loaded.HardwareFailure) : global::XASlave.Localization.UiMessage.Raw(string.Empty)))));
                    log.Information("[Custom Mouse] {ArtworkStatus}", resource.Status);
                }
                catch (Exception ex)
                {
                    resource.Failed = true;
                    resource.Status = global::XASlave.Localization.UiMessage.Store(ref resource.StatusToken, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Key("S64BD7E00F25D8407"), global::XASlave.Localization.UiMessage.FromException(ex)));
                    log.Warning(ex, "[Custom Mouse] Cursor role artwork unavailable; its game cursor is retained.");
                }
            }
            if (resource.Current?.Ready == true)
                resource.FrameIndex = resource.Current.Model.GetFrameIndex(now - resource.AnimationStartedAt, settings.AnimateArtwork);
        }
        if (changed) RefreshArtworkStatus();
    }

    private void RefreshArtworkStatus()
    {
        var ready = 0;
        var failed = 0;
        foreach (var resource in artworkResources.Values)
        {
            if (resource.Current?.Ready == true) ready++;
            if (resource.Failed) failed++;
        }
        ArtworkStatusText = global::XASlave.Localization.UiMessage.Store(ref uiArtworkStatusText, (artworkResources.Count == 0 ? global::XASlave.Localization.UiMessage.Key("SFC0A987D0DC61A95") : global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Format("S2755687CE981093C", ready, artworkResources.Count), ((failed == 0 ? global::XASlave.Localization.UiMessage.Raw(string.Empty) : global::XASlave.Localization.UiMessage.Format("S05FB2FB4DB3BA62D", failed))))));
    }

    private ArtworkResource? GetArtworkResource(uint cursorType)
    {
        var role = GetArtworkRole(cursorType);
        return role.HasValue ? roleArtworks[(int)role.Value] : null;
    }

    private async Task<Artwork> LoadArtworkAsync(string path, CustomMouseSettings appearance, CancellationToken token)
    {
        // Local I/O and RIFF/DIB parsing happen off the framework thread. Only managed bytes cross awaits.
        var model = await Task.Run(() => CustomMouseArtwork.Load(ReadArtworkFile(path, token)), token).ConfigureAwait(false);
        var wraps = new List<IDalamudTextureWrap>(model.Frames.Count);
        var hardware = new List<CustomMouseHardwareCursor>(model.Frames.Count);
        var recolor = appearance.BaseColor != Vector4.One || appearance.OutlineColor != new Vector4(0, 0, 0, 1);
        var preservesPngColors = false;
        UiMessage? hardwareFailure = null;
        var prepareHardware = true;
        if (prepareHardware)
        {
            long scaledPixels = 0;
            foreach (var frame in model.Frames)
                scaledPixels += (long)Math.Ceiling(frame.Width * appearance.Scale) * (long)Math.Ceiling(frame.Height * appearance.Scale);
            // Input bounds do not bound scaled animation memory: 64 x 256px at 5x is over 400 MiB.
            if (scaledPixels > 16 * 1024 * 1024)
            {
                prepareHardware = false;
                hardwareFailure = global::XASlave.Localization.UiMessage.Key("S6EAD7F59AD24449B");
            }
        }
        try
        {
            foreach (var frame in model.Frames)
            {
                token.ThrowIfCancellationRequested();
                IDalamudTextureWrap wrap;
                byte[]? pixels = null;
                var pitch = frame.Width * 4;
                if (frame.PixelsBgra != null)
                {
                    pixels = recolor ? appearance.Recolor(frame.PixelsBgra) : frame.PixelsBgra;
                    wrap = await textures.CreateFromRawAsync(RawImageSpecification.Bgra32(frame.Width, frame.Height),
                        pixels, debugName: "XA Custom Mouse artwork", cancellationToken: token).ConfigureAwait(false);
                }
                else
                {
                    wrap = await textures.CreateFromImageAsync(frame.EncodedPng ?? throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S433DB91C9A6BA7BD"), originalMessage => new InvalidDataException(originalMessage)),
                        debugName: "XA Custom Mouse PNG artwork", cancellationToken: token).ConfigureAwait(false);
                    preservesPngColors = true;
                }
                wraps.Add(wrap);
                if (wrap.Width != frame.Width || wrap.Height != frame.Height)
                    throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S9552D2204ADAAB11"), originalMessage => new InvalidDataException(originalMessage));
                if (prepareHardware)
                {
                    try
                    {
                        if (pixels == null)
                        {
                            var decoded = await readback.GetRawImageAsync(wrap, new TextureModificationArgs { DxgiFormat = 87 },
                                leaveWrapOpen: true, cancellationToken: token).ConfigureAwait(false);
                            if (decoded.Specification.Width != frame.Width || decoded.Specification.Height != frame.Height
                                || decoded.Specification.DxgiFormat != 87)
                                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S1892BFECBD18F32B"), originalMessage => new InvalidDataException(originalMessage));
                            var sourcePitch = decoded.Specification.Pitch;
                            if (sourcePitch < pitch || (long)(frame.Height - 1) * sourcePitch + pitch > decoded.RawData.Length)
                                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S5E9951142C5A9037"), originalMessage => new InvalidDataException(originalMessage));
                            pixels = new byte[checked(pitch * frame.Height)];
                            for (var row = 0; row < frame.Height; row++)
                                decoded.RawData.AsSpan(row * sourcePitch, pitch).CopyTo(pixels.AsSpan(row * pitch, pitch));
                        }
                        var handlePixels = pixels;
                        var handlePitch = pitch;
                        hardware.Add(await Task.Run(() => CustomMouseHardwareCursor.Create(frame.Width, frame.Height,
                            frame.HotspotX, frame.HotspotY, handlePixels, handlePitch, appearance.Scale), token).ConfigureAwait(false));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        prepareHardware = false;
                        hardwareFailure = global::XASlave.Localization.UiMessage.FromException(ex);
                        foreach (var handle in hardware) handle.Dispose();
                        hardware.Clear();
                        log.Warning(ex, "[Custom Mouse] Private hardware cursor could not be prepared; software artwork retained.");
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            return new Artwork(model, wraps.ToArray(), preservesPngColors, hardware.ToArray(), hardwareFailure);
        }
        catch
        {
            foreach (var handle in hardware) handle.Dispose();
            foreach (var wrap in wraps) wrap.Dispose();
            throw;
        }
    }

    private static byte[] ReadArtworkFile(string path, CancellationToken token)
    {
        if (path.Length > 1024 || !Path.IsPathFullyQualified(path))
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S10A9D0A1ACEA09F9"), originalMessage => new InvalidDataException(originalMessage));
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (root == null || root.Length != 3 || !char.IsAsciiLetter(root[0]) || root[1] != ':'
            || new DriveInfo(root).DriveType == DriveType.Network)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S8F2A8FF7E57A98F7"), originalMessage => new InvalidDataException(originalMessage));
        var extension = Path.GetExtension(fullPath);
        if (!extension.Equals(".ani", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".cur", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SF8DE68F492092400"), originalMessage => new InvalidDataException(originalMessage));
        using var source = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.SequentialScan);
        if (source.Length > MaximumArtworkFileBytes)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SEBCCAEE1E1D816F3"), originalMessage => new InvalidDataException(originalMessage));
        using var output = new MemoryStream((int)source.Length);
        var buffer = new byte[8192];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (output.Length + read > MaximumArtworkFileBytes)
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SEBCCAEE1E1D816F3"), originalMessage => new InvalidDataException(originalMessage));
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private async Task<NativeAtlas> LoadAtlasAsync(string path, int preset, int expectedWidth, int expectedHeight, CustomMouseSettings appearance, CancellationToken token)
    {
        // Decode and crop managed pixels outside native callbacks. Default colors retain the source exactly.
        var decoded = await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var file = data.GetFile<TexFile>(path) ?? throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SC6A74E6F127FA8CE"), originalMessage => new InvalidOperationException(originalMessage));
            var width = (int)file.Header.Width;
            var height = (int)file.Header.Height;
            if (width < 1 || height < 1 || width > 2048 || height > 2048)
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S26700914D41C612B"), originalMessage => new InvalidOperationException(originalMessage));
            if (width != expectedWidth || height != expectedHeight)
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S3378EE955D1FA4CD"), originalMessage => new InvalidOperationException(originalMessage));
            var pixels = file.TextureBuffer.Filter(mip: 0, z: 0, format: TexFile.TextureFormat.B8G8R8A8).RawData;
            if (pixels.Length != checked(width * height * 4)) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S2CEE0B15A02379F5"), originalMessage => new InvalidOperationException(originalMessage));
            var recolor = appearance.BaseColor != Vector4.One || appearance.OutlineColor != new Vector4(0, 0, 0, 1);
            return (width, height, pixels: recolor ? appearance.Recolor(pixels) : pixels);
        }, token).ConfigureAwait(false);
        IDalamudTextureWrap? wrap = null;
        var hardware = new List<CustomMouseHardwareCursor>(CustomMouseNativeArtwork.CursorTypeCount);
        UiMessage? hardwareFailure = null;
        try
        {
            wrap = await textures.CreateFromRawAsync(RawImageSpecification.Bgra32(decoded.width, decoded.height),
                decoded.pixels, debugName: "XA Custom Mouse native", cancellationToken: token).ConfigureAwait(false);
            try
            {
                await Task.Run(() =>
                {
                    for (uint type = 0; type < CustomMouseNativeArtwork.CursorTypeCount; type++)
                    {
                        token.ThrowIfCancellationRequested();
                        var frame = CustomMouseNativeArtwork.Extract(decoded.pixels, decoded.width, decoded.height, preset, type);
                        hardware.Add(CustomMouseHardwareCursor.Create(frame.Width, frame.Height, frame.HotspotX, frame.HotspotY,
                            frame.Bgra, frame.Width * 4, appearance.Scale));
                    }
                }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                hardwareFailure = global::XASlave.Localization.UiMessage.FromException(ex);
                foreach (var handle in hardware) handle.Dispose();
                hardware.Clear();
                log.Warning(ex, "[Custom Mouse] Native hardware cursor preparation failed; software cursor retained.");
            }
            token.ThrowIfCancellationRequested();
            return new NativeAtlas(wrap, hardware.ToArray(), hardwareFailure);
        }
        catch
        {
            foreach (var handle in hardware) handle.Dispose();
            wrap?.Dispose();
            throw;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CursorDrawDelegate(nint state, float elapsed, int x, int y, byte outsideViewport);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CursorUpdateDelegate(nint cursorAddress);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate nint SpriteDrawDelegate(nint renderer, Vector4* rectangle, int priority, uint color,
        Vector2* uvStart, Vector2* uvEnd, uint mode, DrawMaterial* material);

    private unsafe void CursorDrawDetour(nint state, float elapsed, int x, int y, byte outsideViewport)
    {
        // Never wait for a texture/configuration update on the game's cursor path. A contested frame uses native output.
        if (!Monitor.TryEnter(sync))
        {
            cursorDrawHook!.Original(state, elapsed, x, y, outsideViewport);
            return;
        }
        try
        {
            var previousService = drawingService;
            var previousPreset = drawingPreset;
            byte* rendererVisible = null;
            byte originalVisible = 0;
            drawingService = null;
            try
            {
                var cursor = GameCursor.Instance();
                if (enabled && !disposed && !faulted && cursor != null && state == (nint)((byte*)cursor + SoftwareRendererOffset))
                {
                    var suppressSoftwareDraw = settings.AllowWindowsMouse || IsUiMouseOwned || customHardwareActive;
                    rendererVisible = (byte*)state + 0xA1;
                    originalVisible = *rendererVisible;
                    var cursorHeldVisible = !suppressSoftwareDraw && ShouldKeepVisibleOnClick(cursor);
                    if (suppressSoftwareDraw) *rendererVisible = 0;
                    else if (cursorHeldVisible) *rendererVisible = 1;
                    // The renderer stores the loaded 64/96/128px atlas selection here.
                    var preset = *(int*)((byte*)state + 0x90);
                    if (!suppressSoftwareDraw && cursor->UseSoftwareCursor && preset is >= 0 and <= 2)
                    {
                        drawingService = this;
                        drawingPreset = preset;
                    }
                }
            }
            catch (Exception ex) { RecordDetourFailure(ex); }
            try { cursorDrawHook!.Original(state, elapsed, x, y, outsideViewport); }
            finally
            {
                if (rendererVisible != null) *rendererVisible = originalVisible;
                drawingService = previousService;
                drawingPreset = previousPreset;
            }
        }
        finally { Monitor.Exit(sync); }
    }

    private unsafe void CursorUpdateDetour(nint cursorAddress)
    {
        if (!Monitor.TryEnter(sync))
        {
            customHardwareActive = false;
            cursorUpdateHook!.Original(cursorAddress);
            return;
        }
        try
        {
            GameCursor* overriddenCursor = null;
            var software = false;
            var osHardware = false;
            uint cursorType = 0;
            var visible = false;
            byte customHandleMode = 0;
            nint originalHandle = 0;
            var customHandleOverridden = false;
            nint selectedHardware = 0;
            customHardwareActive = false;
            try
            {
                var cursor = GameCursor.Instance();
                if (enabled && !disposed && !faulted && !IsUiMouseOwned && cursor != null && (nint)cursor == cursorAddress)
                {
                    var input = UIInputData.Instance();
                    var focused = input != null && input->CursorInputs.IsGameWindowFocused;
                    var nativePreset = *(int*)((byte*)cursor + SoftwareRendererOffset + 0x90);
                    var handle = focused ? GetCustomHardwareHandle(cursor, nativePreset) : 0;
                    if (focused && (settings.AllowWindowsMouse || handle != 0))
                    {
                        var keepVisible = ShouldKeepVisibleOnClick(cursor);
                        software = cursor->UseSoftwareCursor;
                        osHardware = cursor->UseOsHardwareCursor;
                        cursorType = cursor->ActiveCursorType;
                        visible = cursor->IsCursorVisible;
                        overriddenCursor = cursor;
                        cursor->UseSoftwareCursor = false;
                        cursor->UseOsHardwareCursor = true;
                        cursor->ActiveCursorType = (uint)AtkCursor.CursorType.Arrow;
                        if (keepVisible) cursor->IsCursorVisible = true;
                        customHandleMode = *((byte*)cursor + CustomHardwareModeOffset);
                        originalHandle = *(nint*)((byte*)cursor + CustomHardwareHandlesOffset);
                        customHandleOverridden = true;
                        if (handle != 0 && !settings.AllowWindowsMouse)
                        {
                            // Select private slot zero only for this call so the game's SetCursor import retains Dalamud filtering.
                            *((byte*)cursor + CustomHardwareModeOffset) = 1;
                            *(nint*)((byte*)cursor + CustomHardwareHandlesOffset) = handle;
                            selectedHardware = handle;
                        }
                        else *((byte*)cursor + CustomHardwareModeOffset) = 0;
                    }
                }
            }
            catch (Exception ex) { RecordDetourFailure(ex); }
            try
            {
                cursorUpdateHook!.Original(cursorAddress);
                // Native Update applies viewport/visibility gates itself; suppress software only after that selection call.
                customHardwareActive = selectedHardware != 0 && !faulted && CustomMouseHardwareCursor.IsSelected(selectedHardware);
            }
            finally
            {
                if (overriddenCursor != null)
                {
                    if (customHandleOverridden)
                    {
                        *((byte*)overriddenCursor + CustomHardwareModeOffset) = customHandleMode;
                        *(nint*)((byte*)overriddenCursor + CustomHardwareHandlesOffset) = originalHandle;
                    }
                    overriddenCursor->UseSoftwareCursor = software;
                    overriddenCursor->UseOsHardwareCursor = osHardware;
                    overriddenCursor->ActiveCursorType = cursorType;
                    overriddenCursor->IsCursorVisible = visible;
                }
            }
        }
        finally { Monitor.Exit(sync); }
    }

    private unsafe nint GetCustomHardwareHandle(GameCursor* cursor, int nativePreset)
    {
        var cursorType = cursor->ActiveCursorType;
        if (settings.CursorTrails || CustomMouseNativeArtwork.GetCell(cursorType) < 0) return 0;
        if (settings.UseCustomArtwork)
        {
            var resource = GetArtworkResource(cursorType);
            var artwork = resource?.Current;
            // A selected custom file that is ready keeps its own software fallback when hardware preparation failed.
            if (artwork?.Ready == true)
                return artwork.HardwareCursors.Length == artwork.Model.Frames.Count
                    ? artwork.HardwareCursors[resource!.FrameIndex].Handle : 0;
        }
        return currentAtlas?.Ready == true && currentAtlas.Preset == nativePreset && nativePreset == currentNativePreset
            && cursor->SoftwareCursorTexture != null && (nint)cursor->SoftwareCursorTexture == textureResourceAddress
            && (nint)cursor->SoftwareCursorTexture->Texture == textureKernelAddress
            && cursor->SoftwareCursorTexture->Header.Width == textureWidth && cursor->SoftwareCursorTexture->Header.Height == textureHeight
            && currentAtlas.HardwareCursors.Length == CustomMouseNativeArtwork.CursorTypeCount
            ? currentAtlas.HardwareCursors[(int)cursorType].Handle : 0;
    }

    internal static CustomMouseArtworkRole? GetArtworkRole(uint cursorType) => cursorType switch
    {
        (uint)AtkCursor.CursorType.Arrow => CustomMouseArtworkRole.Normal,
        (uint)AtkCursor.CursorType.Clickable => CustomMouseArtworkRole.Inventory,
        (uint)AtkCursor.CursorType.Hand or (uint)AtkCursor.CursorType.Grab => CustomMouseArtworkRole.WindowMove,
        (uint)AtkCursor.CursorType.Interact => CustomMouseArtworkRole.Interaction,
        (uint)AtkCursor.CursorType.ChatPointer => CustomMouseArtworkRole.ChatInteraction,
        (uint)AtkCursor.CursorType.Attack => CustomMouseArtworkRole.AttackTargeting,
        _ => null,
    };

    // Text, resize, no-access and other unassigned states keep their existing meaning and artwork.
    internal static bool IsCustomPointerType(uint cursorType) => GetArtworkRole(cursorType).HasValue;

    private unsafe nint SpriteDrawDetour(nint renderer, Vector4* rectangle, int priority, uint color,
        Vector2* uvStart, Vector2* uvEnd, uint mode, DrawMaterial* material)
    {
        // Other game/UI sprites pass through without a lock or allocation.
        if (drawingService != this || !enabled || faulted || rectangle == null || material == null)
            return spriteDrawHook!.Original(renderer, rectangle, priority, color, uvStart, uvEnd, mode, material);
        var adjustedRectangle = *rectangle;
        var adjustedMaterial = *material;
        var adjustedUvStart = Vector2.Zero;
        var adjustedUvEnd = Vector2.One;
        var customArtwork = false;
        var adjusted = false;
        try
        {
            var cursor = GameCursor.Instance();
            if (cursor != null && cursor->SoftwareCursorTexture != null && material->Texture == cursor->SoftwareCursorTexture->Texture)
            {
                var width = adjustedRectangle.Z - adjustedRectangle.X;
                var height = adjustedRectangle.W - adjustedRectangle.Y;
                if (float.IsFinite(width) && float.IsFinite(height) && width > 0 && height > 0 && width <= 128 && height <= 128)
                {
                    // Preset entries: (hotspotX, hotspotY, cellSize) = (32,24,64), (48,36,96), (64,48,128).
                    // Trail sprites shrink around that hotspot; derive their individual weight from width.
                    var cellSize = 64f + 32f * drawingPreset;
                    var weight = width / (cellSize - 1f);
                    var pivot = new Vector2(adjustedRectangle.X + cellSize * 0.5f * weight,
                        adjustedRectangle.Y + cellSize * 0.375f * weight);
                    // Check the current pointer type and the sprite cell so old contextual trail sprites stay native.
                    var resource = GetArtworkResource(cursor->ActiveCursorType);
                    var artwork = resource?.Current;
                    if (settings.UseCustomArtwork && artwork?.Ready == true
                        && uvStart != null
                        && IsCustomPointerCell(cursor->ActiveCursorType, *uvStart, cellSize, material->Texture->ActualWidth, material->Texture->ActualHeight))
                    {
                        var frameIndex = resource!.FrameIndex;
                        var frame = artwork.Model.Frames[frameIndex];
                        var artworkScale = settings.Scale * weight;
                        var left = pivot.X - frame.HotspotX * artworkScale;
                        var top = pivot.Y - frame.HotspotY * artworkScale;
                        adjustedRectangle = new Vector4(left, top, left + frame.Width * artworkScale, top + frame.Height * artworkScale);
                        adjustedMaterial.Texture = (KernelTexture*)artwork.NativeTextures[frameIndex];
                        customArtwork = true;
                    }
                    else
                    {
                        adjustedRectangle = ScaleRectangle(adjustedRectangle, pivot, settings.Scale);
                    }
                    if (!customArtwork && NeedsRecolor && currentAtlas?.Ready == true
                        && currentAtlas.Texture->ActualWidth == material->Texture->ActualWidth
                        && currentAtlas.Texture->ActualHeight == material->Texture->ActualHeight)
                    {
                        adjustedMaterial.Texture = currentAtlas.Texture;
                    }
                    nativeDrawObserved = true;
                    adjusted = true;
                }
            }
        }
        catch (Exception ex) { RecordDetourFailure(ex); }
        // Invoke once with stack-local changes. Native assets and resource handles are never modified.
        return adjusted
            ? spriteDrawHook!.Original(renderer, &adjustedRectangle, priority, color,
                customArtwork ? &adjustedUvStart : uvStart, customArtwork ? &adjustedUvEnd : uvEnd, mode, &adjustedMaterial)
            : spriteDrawHook!.Original(renderer, rectangle, priority, color, uvStart, uvEnd, mode, material);
    }

    internal static Vector4 ScaleRectangle(Vector4 rectangle, Vector2 pivot, float ratio) => new(
        pivot.X + (rectangle.X - pivot.X) * ratio,
        pivot.Y + (rectangle.Y - pivot.Y) * ratio,
        pivot.X + (rectangle.Z - pivot.X) * ratio,
        pivot.Y + (rectangle.W - pivot.Y) * ratio);

    internal static bool IsCustomPointerCell(uint cursorType, Vector2 uvStart, float cellSize, uint atlasWidth, uint atlasHeight)
    {
        var cell = cursorType switch
        {
            (uint)AtkCursor.CursorType.Arrow => 1,
            (uint)AtkCursor.CursorType.Hand => 2,
            (uint)AtkCursor.CursorType.Clickable => 4,
            (uint)AtkCursor.CursorType.Grab => 3,
            (uint)AtkCursor.CursorType.Interact => 9,
            (uint)AtkCursor.CursorType.ChatPointer => 7,
            (uint)AtkCursor.CursorType.Attack => 11,
            _ => -1,
        };
        if (cell < 0 || atlasWidth == 0 || atlasHeight == 0) return false;
        const int columns = 4; // Native renderer uses cell&3 and cell>>2.
        return MathF.Abs(uvStart.X - cell % columns * cellSize / atlasWidth) < 0.0001f
            && MathF.Abs(uvStart.Y - cell / columns * cellSize / atlasHeight) < 0.0001f;
    }

    private void RecordDetourFailure(Exception ex)
    {
        if (faulted) return;
        faulted = true;
        log.Warning(ex, "[Custom Mouse] Native cursor override failed; original draw retained.");
    }

    private void SetStatus(global::XASlave.Localization.UiMessage value)
    {
        if (StatusText == value.Original) return;
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, value);
        log.Information("[Custom Mouse] {Status}", value.Original);
    }

    private void DisableHooks()
    {
        if (cursorDrawHook is { IsDisposed: false, IsEnabled: true }) cursorDrawHook.Disable();
        if (spriteDrawHook is { IsDisposed: false, IsEnabled: true }) spriteDrawHook.Disable();
        if (cursorUpdateHook is { IsDisposed: false, IsEnabled: true }) cursorUpdateHook.Disable();
    }

    private void ClearAtlases()
    {
        customHardwareActive = false;
        currentNativePreset = -1;
        currentAtlas = null;
        textureResourceAddress = textureKernelAddress = 0;
        textureWidth = textureHeight = 0;
        texturePath = string.Empty;
        cancellation.Cancel();
        cancellation.Dispose();
        cancellation = new();
        foreach (var atlas in atlases.Values) atlas.Dispose();
        atlases.Clear();
        foreach (var resource in artworkResources.Values) resource.Dispose();
        artworkResources.Clear();
        Array.Clear(roleArtworks);
        artworkReloadPaths.Clear();
        artworkPathsPending = true;
        ArtworkStatusText = global::XASlave.Localization.UiMessage.Store(ref uiArtworkStatusText, (settings.UseCustomArtwork ? global::XASlave.Localization.UiMessage.Key("SA58AC1BD56949C40") : global::XASlave.Localization.UiMessage.Key("S5744D0597F0C3DDA")));
        _ = CustomMouseHardwareCursor.TryDrainRetiredOnGameThread(out _);
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            enabled = false;
            DisableHooks();
            RestoreSoftwareSetting();
            RestoreTrailSetting();
            gameConfig.SystemChanged -= OnGameConfigChanged;
            framework.Update -= OnUpdate;
            uiBuilder.Draw -= CaptureUiMouseOwnership;
            uiBuilder.HideUi -= ReleaseUiMouseOwnership;
            ReleaseUiMouseOwnership();
            cursorDrawHook?.Dispose();
            spriteDrawHook?.Dispose();
            cursorUpdateHook?.Dispose();
            ClearAtlases();
            cancellation.Dispose();
            disposed = true;
        }
        if (framework.IsInFrameworkUpdateThread) _ = CustomMouseHardwareCursor.TryDrainRetiredOnGameThread(out _);
        else _ = framework.RunOnFrameworkThread(() => CustomMouseHardwareCursor.TryDrainRetiredOnGameThread(out _));
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x18)]
    private unsafe struct DrawMaterial
    {
        [FieldOffset(0)] public ulong Parameters;
        [FieldOffset(8)] public KernelTexture* Texture;
        [FieldOffset(0x10)] public ulong Flags;
    }

    private sealed record NativeAtlas(IDalamudTextureWrap Wrap, CustomMouseHardwareCursor[] HardwareCursors, UiMessage? HardwareFailure);

    private sealed unsafe class Atlas(Task<NativeAtlas> pending, int preset, CancellationTokenSource cancellation) : IDisposable
    {
        internal readonly Task<NativeAtlas> Pending = pending;
        private readonly CancellationTokenSource cancellation = cancellation;
        internal readonly int Preset = preset;
        private bool disposed;
        internal CustomMouseHardwareCursor[] HardwareCursors = [];
        internal UiMessage? HardwareFailure;
        internal IDalamudTextureWrap? Wrap;
        internal bool Failed;
        internal KernelTexture* Texture;
        internal bool Ready => Wrap != null && !Failed && Texture != null;

        internal void Convert(ITextureProvider provider)
        {
            Texture = (KernelTexture*)provider.ConvertToKernelTexture(Wrap!, true);
            if (Texture == null) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SC0D37FE5E83D0893"), originalMessage => new InvalidOperationException(originalMessage));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            cancellation.Cancel();
            cancellation.Dispose();
            // Kernel textures inherit the game's DelayedReleaseClassBase; never free their memory directly.
            if (Texture != null) { Texture->DecRef(); Texture = null; }
            foreach (var cursor in HardwareCursors) cursor.Dispose();
            HardwareCursors = [];
            if (Wrap != null) { Wrap.Dispose(); Wrap = null; }
            else _ = Pending.ContinueWith(task =>
            {
                if (task.Status == TaskStatus.RanToCompletion)
                {
                    foreach (var cursor in task.Result.HardwareCursors) cursor.Dispose();
                    task.Result.Wrap.Dispose();
                }
                else if (task.IsFaulted) _ = task.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private sealed class ArtworkResource(CancellationTokenSource cancellation) : IDisposable
    {
        internal readonly CancellationTokenSource Cancellation = cancellation;
        internal Task<Artwork>? Pending;
        internal Artwork? Current;
        internal bool Failed;
        internal int FrameIndex;
        internal long AnimationStartedAt;
        internal string Status = global::XASlave.Localization.UiLocale.Native("S86FAA7DB9C401EA0");
        internal global::XASlave.Localization.UiMessage? StatusToken = global::XASlave.Localization.UiMessage.Key("S86FAA7DB9C401EA0");
        internal global::XASlave.Localization.UiMessage StatusMessage => global::XASlave.Localization.UiMessage.Capture(ref StatusToken, Status);

        public void Dispose()
        {
            Cancellation.Cancel();
            Cancellation.Dispose();
            Current?.Dispose();
            Current = null;
            if (Pending != null)
            {
                _ = Pending.ContinueWith(task =>
                {
                    if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose();
                    else if (task.IsFaulted) _ = task.Exception;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                Pending = null;
            }
        }
    }

    private sealed class Artwork : IDisposable
    {
        internal readonly CustomMouseArtwork Model;
        internal readonly nint[] NativeTextures;
        internal readonly bool PreservesPngColors;
        internal readonly CustomMouseHardwareCursor[] HardwareCursors;
        internal readonly UiMessage? HardwareFailure;
        internal bool Ready { get; private set; }
        private readonly IDalamudTextureWrap[] wraps;
        private bool disposed;

        internal Artwork(CustomMouseArtwork model, IDalamudTextureWrap[] wraps, bool preservesPngColors,
            CustomMouseHardwareCursor[] hardwareCursors, UiMessage? hardwareFailure)
        {
            Model = model;
            this.wraps = wraps;
            NativeTextures = new nint[wraps.Length];
            PreservesPngColors = preservesPngColors;
            HardwareCursors = hardwareCursors;
            HardwareFailure = hardwareFailure;
        }

        internal unsafe void Convert(ITextureProvider provider)
        {
            for (var i = 0; i < wraps.Length; i++)
            {
                NativeTextures[i] = provider.ConvertToKernelTexture(wraps[i], true);
                if (NativeTextures[i] == 0) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SD543E11B3A356B1B"), originalMessage => new InvalidOperationException(originalMessage));
            }
            Ready = true;
        }

        public unsafe void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Ready = false;
            foreach (var handle in HardwareCursors) handle.Dispose();
            for (var i = 0; i < NativeTextures.Length; i++)
            {
                if (NativeTextures[i] != 0)
                {
                    ((KernelTexture*)NativeTextures[i])->DecRef();
                    NativeTextures[i] = 0;
                }
                wraps[i].Dispose();
            }
        }
    }
}
