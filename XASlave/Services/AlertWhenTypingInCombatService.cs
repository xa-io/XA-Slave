using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using XASlave.Data;

namespace XASlave.Services;

public sealed class AlertWhenTypingInCombatService : IDisposable
{
    public const int DefaultCooldownSeconds = 300;
    public const int MinimumCooldownSeconds = 30;
    public const int MaximumCooldownSeconds = 3600;
    public const int DefaultToneId = 2;
    public const int DefaultBeepCount = 3;
    public const int MinimumBeepCount = 1;
    public const int MaximumBeepCount = 10;
    public const float DefaultVolume = 0.45f;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan FocusReadWarningInterval = TimeSpan.FromMinutes(1);

    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IToastGui toastGui;
    private readonly IPluginLog log;
    private bool enabled;
    private bool subscribed;
    private int cooldownSeconds = DefaultCooldownSeconds;
    private int toneId = DefaultToneId;
    private int beepCount = DefaultBeepCount;
    private float volume = DefaultVolume;
    private DateTime lastPollUtc = DateTime.MinValue;
    private DateTime lastAlertUtc = DateTime.MinValue;
    private DateTime nextAlertUtc = DateTime.MinValue;
    private DateTime nextFocusReadWarningUtc = DateTime.MinValue;
    private int soundPlaybackGeneration;
    private bool wasLoggedIn;
    private bool isDisposed;

    public AlertWhenTypingInCombatService(
        IFramework framework,
        IClientState clientState,
        ICondition condition,
        IToastGui toastGui,
        IPluginLog log)
    {
        this.framework = framework;
        this.clientState = clientState;
        this.condition = condition;
        this.toastGui = toastGui;
        this.log = log;
    }

    public string StatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? uiStatusText = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string LocalizedStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiStatusText, StatusText);
    internal global::XASlave.Localization.UiMessage StatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiStatusText, StatusText);
    public string LastActionText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S512584869DF41B6F");
    private global::XASlave.Localization.UiMessage? uiLastActionText = global::XASlave.Localization.UiMessage.Key("S512584869DF41B6F");
    internal string LocalizedLastActionText => global::XASlave.Localization.UiMessage.Resolve(ref uiLastActionText, LastActionText);
    internal global::XASlave.Localization.UiMessage LastActionTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiLastActionText, LastActionText);
    public bool IsInCombat { get; private set; }
    public bool IsChatLogFocused { get; private set; }

    public int CooldownRemainingSeconds
    {
        get
        {
            if (!enabled || nextAlertUtc == DateTime.MinValue)
                return 0;

            return Math.Max(0, (int)Math.Ceiling((nextAlertUtc - DateTime.UtcNow).TotalSeconds));
        }
    }

    public static int NormalizeCooldownSeconds(int value)
    {
        return Math.Clamp(value, MinimumCooldownSeconds, MaximumCooldownSeconds);
    }

    public static int NormalizeToneId(int value)
    {
        return Math.Clamp(value, 1, XAPeepData.MaxSoundEffectId);
    }

    public static int NormalizeBeepCount(int value)
    {
        return Math.Clamp(value, MinimumBeepCount, MaximumBeepCount);
    }

    public static float NormalizeVolume(float value)
    {
        return Math.Clamp(value, 0f, 1f);
    }

    public void ApplyConfiguration(int cooldownSeconds, int toneId, int beepCount, float volume)
    {
        this.cooldownSeconds = NormalizeCooldownSeconds(cooldownSeconds);
        this.toneId = NormalizeToneId(toneId);
        this.beepCount = NormalizeBeepCount(beepCount);
        this.volume = NormalizeVolume(volume);

        if (lastAlertUtc != DateTime.MinValue)
            nextAlertUtc = lastAlertUtc.AddSeconds(this.cooldownSeconds);

        if (enabled)
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, BuildStatusText());
    }

    public bool SetEnabled(bool value)
    {
        if (value == enabled)
            return enabled;

        if (!value)
        {
            enabled = false;
            Unsubscribe();
            soundPlaybackGeneration++;
            XAPeepSoundPlayer.StopTonePlayback();
            ResetRuntimeState();
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"));
            return false;
        }

        enabled = true;
        ResetRuntimeState();
        Subscribe();
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, BuildStatusText());
        return true;
    }

    public void PreviewAlert()
    {
        ShowAlert(isPreview: true);
    }

    public void PreviewSound()
    {
        PlayConfiguredSound();
    }

    public void Dispose()
    {
        isDisposed = true;
        enabled = false;
        Unsubscribe();
        soundPlaybackGeneration++;
        XAPeepSoundPlayer.StopTonePlayback();
        ResetRuntimeState();
    }

    private global::XASlave.Localization.UiMessage BuildStatusText()
    {
        return global::XASlave.Localization.UiMessage.Format("S23FF8679F3328533", cooldownSeconds);
    }

    private void Subscribe()
    {
        if (subscribed)
            return;

        framework.Update += OnFrameworkUpdate;
        subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!subscribed)
            return;

        framework.Update -= OnFrameworkUpdate;
        subscribed = false;
    }

    private void ResetRuntimeState()
    {
        IsInCombat = false;
        IsChatLogFocused = false;
        lastPollUtc = DateTime.MinValue;
        lastAlertUtc = DateTime.MinValue;
        nextAlertUtc = DateTime.MinValue;
        wasLoggedIn = false;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!enabled)
            return;

        var nowUtc = DateTime.UtcNow;
        if (nowUtc - lastPollUtc < PollInterval)
            return;

        lastPollUtc = nowUtc;

        if (!clientState.IsLoggedIn)
        {
            if (wasLoggedIn)
            {
                soundPlaybackGeneration++;
                XAPeepSoundPlayer.StopTonePlayback();
            }

            wasLoggedIn = false;
            IsInCombat = false;
            IsChatLogFocused = false;
            lastAlertUtc = DateTime.MinValue;
            nextAlertUtc = DateTime.MinValue;
            return;
        }

        wasLoggedIn = true;

        IsInCombat = condition[ConditionFlag.InCombat];
        try
        {
            IsChatLogFocused = IsChatLogAddonFocused();
        }
        catch (Exception ex)
        {
            IsChatLogFocused = false;
            if (nowUtc >= nextFocusReadWarningUtc)
            {
                nextFocusReadWarningUtc = nowUtc.Add(FocusReadWarningInterval);
                log.Warning(ex, "[XASlave] Alert When Typing In Combat could not read ChatLog focus.");
            }

            return;
        }

        if (!IsInCombat || !IsChatLogFocused || nowUtc < nextAlertUtc)
            return;

        lastAlertUtc = nowUtc;
        nextAlertUtc = lastAlertUtc.AddSeconds(cooldownSeconds);
        ShowAlert(isPreview: false);
    }

    private static unsafe bool IsChatLogAddonFocused()
    {
        var stage = AtkStage.Instance();
        if (stage == null)
            return false;

        var manager = stage->RaptureAtkUnitManager;
        if (manager == null)
            return false;

        var chatLog = manager->GetAddonByName("ChatLog");
        return chatLog != null
            && chatLog->IsReady
            && manager->FocusedAddon == chatLog;
    }

    private void ShowAlert(bool isPreview)
    {
        var message = global::XASlave.Localization.UiMessage.Key("S9D12C65000511B0B");
        try
        {
            toastGui.ShowNormal(((isPreview ? global::XASlave.Localization.UiMessage.Format("S7A199109136A1ADC", message) : message)).Original);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Alert When Typing In Combat could not show its toast.");
        }

        PlayConfiguredSound();

        LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, (isPreview ? global::XASlave.Localization.UiMessage.Format("S0551BB441B00CC0B", DateTime.Now) : global::XASlave.Localization.UiMessage.Format("SCFEB115948D71D01", DateTime.Now)));

        if (!isPreview)
            log.Information($"[XASlave] Alert When Typing In Combat fired with tone {toneId}, {beepCount} beep(s), and a {cooldownSeconds}s cooldown.");
    }

    private void PlayConfiguredSound()
    {
        var generation = ++soundPlaybackGeneration;
        if (!XAPeepSoundPlayer.TryPlayTone(
                toneId,
                beepCount,
                volume,
                log,
                () => RequestFallbackSound(generation)))
        {
            TryPlayFallbackSound(generation);
        }
    }

    private void RequestFallbackSound(int generation)
    {
        framework.RunOnFrameworkThread(() =>
        {
            if (!isDisposed && generation == soundPlaybackGeneration)
                TryPlayFallbackSound(generation);
        });
    }

    private void TryPlayFallbackSound(int generation)
    {
        for (var index = 0; index < beepCount; index++)
        {
            var delay = TimeSpan.FromMilliseconds(index * 280d);
            if (index == 0)
            {
                TryPlayFallbackBeep(generation);
                continue;
            }

            framework.RunOnTick(() => TryPlayFallbackBeep(generation), delay);
        }
    }

    private unsafe void TryPlayFallbackBeep(int generation)
    {
        if (isDisposed || generation != soundPlaybackGeneration)
            return;

        try
        {
            UIGlobals.PlaySoundEffect(XAPeepData.GetSoundEffectValue(toneId));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Alert When Typing In Combat could not play its fallback in-game sound.");
        }
    }
}
