using System;
using System.Globalization;
using System.Threading.Tasks;
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

    private readonly IFramework framework;
    private readonly ISigScanner sigScanner;
    private readonly IGameInteropProvider interopProvider;
    private readonly IPluginLog log;
    private readonly object startupArmingLock = new();

    private Hook<ApplyTextFormatDelegate>? applyTextFormatHook;
    private Task<Hook<ApplyTextFormatDelegate>?>? startupHookTask;
    private Utf8String* formattedTimestamp;
    private bool enabled;
    private bool hookInitialized;
    private bool startupArmingPending;
    private bool startupArmingSubscribed;
    private bool disposed;
    private string timestampFormat = DefaultFormat;

    private delegate byte* ApplyTextFormatDelegate(RaptureTextModule* raptureTextModule, uint addonTextId, int value);

    public ChatTimestampFormatService(
        IFramework framework,
        ISigScanner sigScanner,
        IGameInteropProvider interopProvider,
        IPluginLog log)
    {
        this.framework = framework;
        this.sigScanner = sigScanner;
        this.interopProvider = interopProvider;
        this.log = log;
    }

    public string StatusText { get; private set; } = "Disabled";

    public bool IsStartupArmingPending => startupArmingPending;

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
            CancelStartupArming(disposeCompletedResult: true);
            enabled = false;
            UpdateHookState(false);
            StatusText = "Disabled";
            return false;
        }

        if (startupArmingPending)
            return true;

        CancelStartupArming(disposeCompletedResult: true);
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

    public bool RestoreEnabledOnStartup()
    {
        if (startupArmingPending)
            return true;

        if (hookInitialized)
            return SetEnabled(true);

        EnsureStringAllocated();
        enabled = true;
        StatusText = "Arming - chat timestamp hook is initializing outside the framework tick.";
        StartStartupHookCreation();
        return true;
    }

    public void PrepareHookDuringPluginLoad()
    {
        if (hookInitialized || startupArmingPending)
            return;

        EnsureStringAllocated();
        EnsureHookInitialized();
        StatusText = applyTextFormatHook == null
            ? "Unavailable - chat timestamp formatter hook missing."
            : "Ready - chat timestamp formatter hook prepared for startup restore.";
    }

    public void Dispose()
    {
        disposed = true;
        CancelStartupArming(disposeCompletedResult: true);
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
        applyTextFormatHook = TryCreateApplyTextFormatHook();
    }

    private Hook<ApplyTextFormatDelegate>? TryCreateApplyTextFormatHook()
    {
        try
        {
            if (!sigScanner.TryScanText(Sigs.ApplyTextFormatSig, out var address) || address == nint.Zero)
                return null;

            return interopProvider.HookFromAddress<ApplyTextFormatDelegate>(address, FormatTextDetour);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Failed to create Custom Timestamp Format hook.");
            return null;
        }
    }

    private void StartStartupHookCreation()
    {
        lock (startupArmingLock)
        {
            startupArmingPending = true;
            startupHookTask ??= Task.Run(TryCreateApplyTextFormatHook);
        }

        SubscribeStartupArming();
    }

    private void SubscribeStartupArming()
    {
        if (startupArmingSubscribed)
            return;

        framework.Update += OnStartupArmingFrameworkUpdate;
        startupArmingSubscribed = true;
    }

    private void UnsubscribeStartupArming()
    {
        if (!startupArmingSubscribed)
            return;

        framework.Update -= OnStartupArmingFrameworkUpdate;
        startupArmingSubscribed = false;
    }

    private void CancelStartupArming(bool disposeCompletedResult)
    {
        Task<Hook<ApplyTextFormatDelegate>?>? task;
        lock (startupArmingLock)
        {
            startupArmingPending = false;
            task = startupHookTask;
            startupHookTask = null;
        }

        UnsubscribeStartupArming();

        if (!disposeCompletedResult || task == null)
            return;

        DisposeStartupHookTaskResult(task);
    }

    private static void DisposeStartupHookTaskResult(Task<Hook<ApplyTextFormatDelegate>?> task)
    {
        if (task.IsCompleted)
        {
            if (task.Status == TaskStatus.RanToCompletion)
                DisposeHook(task.Result);
            return;
        }

        task.ContinueWith(
            completedTask =>
            {
                if (completedTask.Status == TaskStatus.RanToCompletion)
                    DisposeHook(completedTask.Result);
            },
            TaskScheduler.Default);
    }

    private void OnStartupArmingFrameworkUpdate(IFramework _)
    {
        Task<Hook<ApplyTextFormatDelegate>?>? task;
        lock (startupArmingLock)
            task = startupHookTask;

        if (task == null)
        {
            CancelStartupArming(disposeCompletedResult: false);
            return;
        }

        if (!task.IsCompleted)
            return;

        Hook<ApplyTextFormatDelegate>? createdHook = null;
        try
        {
            if (task.Status == TaskStatus.RanToCompletion)
                createdHook = task.Result;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Custom Timestamp Format startup hook initialization failed.");
        }

        lock (startupArmingLock)
        {
            startupHookTask = null;
            startupArmingPending = false;
        }

        UnsubscribeStartupArming();
        hookInitialized = true;

        if (disposed || !enabled)
        {
            DisposeHook(createdHook);
            return;
        }

        applyTextFormatHook = createdHook;
        if (applyTextFormatHook == null)
        {
            enabled = false;
            StatusText = "Unavailable - chat timestamp formatter hook missing.";
            return;
        }

        if (!UpdateHookState(true))
        {
            enabled = false;
            StatusText = "Unavailable - failed to enable the chat timestamp formatter hook.";
            return;
        }

        StatusText = GetEnabledStatusText();
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

    private static void DisposeHook(Hook<ApplyTextFormatDelegate>? hook)
    {
        if (hook is { IsDisposed: false })
            hook.Dispose();
    }
}
