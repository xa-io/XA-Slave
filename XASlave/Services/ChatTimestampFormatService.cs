using System;
using System.Globalization;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace XASlave.Services;

public unsafe sealed class ChatTimestampFormatService : IDisposable
{
    public const string DefaultFormat = "[HH:mm:ss]";

    private const int MaximumFormatLength = 80;
    private const uint ChatTimestampTextId = 7840;
    private const uint ChatTimestampAlternateTextId = 7841;

    private readonly ISigScanner sigScanner;
    private readonly IGameInteropProvider interopProvider;
    private readonly IPluginLog log;

    private Hook<ApplyTextFormatDelegate>? applyTextFormatHook;
    private Utf8String* formattedTimestamp;
    private bool enabled;
    private bool hookInitialized;
    private string timestampFormat = DefaultFormat;

    private delegate byte* ApplyTextFormatDelegate(RaptureTextModule* raptureTextModule, uint addonTextId, int value);

    public ChatTimestampFormatService(
        ISigScanner sigScanner,
        IGameInteropProvider interopProvider,
        IPluginLog log)
    {
        this.sigScanner = sigScanner;
        this.interopProvider = interopProvider;
        this.log = log;
    }

    public string StatusText { get; private set; } = "Disabled";

    public static string NormalizeFormat(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? DefaultFormat
            : value.Trim();

        return normalized.Length <= MaximumFormatLength
            ? normalized
            : normalized[..MaximumFormatLength];
    }

    public void ApplyConfiguration(string? format)
    {
        timestampFormat = NormalizeFormat(format);
        if (enabled)
            StatusText = GetEnabledStatusText();
    }

    public bool SetEnabled(bool value)
    {
        if (!value)
        {
            enabled = false;
            UpdateHookState(false);
            StatusText = "Disabled";
            return false;
        }

        EnsureStringAllocated();
        EnsureHookInitialized();
        if (applyTextFormatHook == null)
        {
            enabled = false;
            StatusText = "Unavailable - chat timestamp formatter hook missing.";
            return false;
        }

        enabled = true;
        if (!UpdateHookState(true))
        {
            enabled = false;
            StatusText = "Unavailable - failed to enable the chat timestamp formatter hook.";
            return false;
        }

        StatusText = GetEnabledStatusText();
        return true;
    }

    public void Dispose()
    {
        enabled = false;
        UpdateHookState(false);

        if (applyTextFormatHook is { IsDisposed: false })
            applyTextFormatHook.Dispose();

        applyTextFormatHook = null;

        if (formattedTimestamp != null)
        {
            formattedTimestamp->Dtor(true);
            formattedTimestamp = null;
        }
    }

    private void EnsureStringAllocated()
    {
        if (formattedTimestamp == null)
            formattedTimestamp = Utf8String.FromString(string.Empty);
    }

    private void EnsureHookInitialized()
    {
        if (hookInitialized)
            return;

        hookInitialized = true;

        try
        {
            if (!sigScanner.TryScanText(Sigs.ApplyTextFormatSig, out var address) || address == nint.Zero)
                return;

            applyTextFormatHook = interopProvider.HookFromAddress<ApplyTextFormatDelegate>(address, FormatTextDetour);
        }
        catch (Exception ex)
        {
            applyTextFormatHook = null;
            log.Warning(ex, "[XASlave] Failed to create Custom Timestamp Format hook.");
        }
    }

    private bool UpdateHookState(bool shouldEnable)
    {
        if (applyTextFormatHook == null || applyTextFormatHook.IsDisposed)
            return !shouldEnable;

        try
        {
            if (shouldEnable)
            {
                if (!applyTextFormatHook.IsEnabled)
                    applyTextFormatHook.Enable();
            }
            else if (applyTextFormatHook.IsEnabled)
            {
                applyTextFormatHook.Disable();
            }

            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[XASlave] Failed to {(shouldEnable ? "enable" : "disable")} Custom Timestamp Format hook.");
            return false;
        }
    }

    private byte* FormatTextDetour(RaptureTextModule* raptureTextModule, uint addonTextId, int value)
    {
        try
        {
            if (enabled && formattedTimestamp != null && addonTextId is ChatTimestampTextId or ChatTimestampAlternateTextId)
            {
                var time = DateTimeOffset.FromUnixTimeSeconds(value).LocalDateTime;
                formattedTimestamp->SetString(FormatTimestamp(time));
                return formattedTimestamp->StringPtr;
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Custom Timestamp Format failed while formatting a chat timestamp.");
        }

        return applyTextFormatHook!.Original(raptureTextModule, addonTextId, value);
    }

    private string FormatTimestamp(DateTime time)
    {
        try
        {
            var text = time.ToString(timestampFormat, CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(text)
                ? string.Empty
                : text;
        }
        catch (FormatException)
        {
            return time.ToString(DefaultFormat, CultureInfo.InvariantCulture);
        }
    }

    private string GetEnabledStatusText()
        => $"Enabled - chat timestamps use `{timestampFormat}`.";
}
