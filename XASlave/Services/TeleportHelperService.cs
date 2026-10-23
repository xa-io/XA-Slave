using System;
using System.Text;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

public sealed class TeleportHelperService : IDisposable
{
    private const string SelectYesnoAddonName = "SelectYesno";
    private const int ClickCooldownMs = 500;

    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IPluginLog log;

    private bool enabled;
    private bool requiredByXagman;
    private bool disposed;
    private bool subscribed;
    private bool selectYes;
    private long lastClickAttemptTick;
    private bool EffectiveEnabled => !disposed && (enabled || requiredByXagman);
    private bool EffectiveSelectYes => !requiredByXagman && selectYes;

    public TeleportHelperService(
        IFramework framework,
        IClientState clientState,
        IPlayerState playerState,
        IPluginLog log)
    {
        this.framework = framework;
        this.clientState = clientState;
        this.playerState = playerState;
        this.log = log;
        UpdateStatusText();
    }

    public string StatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? uiStatusText = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string LocalizedStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiStatusText, StatusText);
    internal global::XASlave.Localization.UiMessage StatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiStatusText, StatusText);
    public string LastActionText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S03F020CBB71754CC");
    private global::XASlave.Localization.UiMessage? uiLastActionText = global::XASlave.Localization.UiMessage.Key("S03F020CBB71754CC");
    internal string LocalizedLastActionText => global::XASlave.Localization.UiMessage.Resolve(ref uiLastActionText, LastActionText);
    internal global::XASlave.Localization.UiMessage LastActionTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiLastActionText, LastActionText);

    public void ApplyConfiguration(bool selectYes)
    {
        this.selectYes = selectYes;
        UpdateStatusText();
    }

    public bool SetEnabled(bool value)
    {
        if (disposed) return false;
        if (value == enabled)
            return enabled;

        enabled = value;
        ResetRuntimeState();
        UpdateSubscription(EffectiveEnabled);
        UpdateStatusText();
        return enabled;
    }

    public void SetRequiredByXagman(bool value)
    {
        if (disposed || requiredByXagman == value) return;
        requiredByXagman = value;
        ResetRuntimeState();
        UpdateSubscription(EffectiveEnabled);
        UpdateStatusText();
    }

    public void Dispose()
    {
        disposed = true;
        enabled = false;
        requiredByXagman = false;
        ResetRuntimeState();
        UpdateSubscription(false);
        UpdateStatusText();
    }

    private void UpdateSubscription(bool targetEnabled)
    {
        if (subscribed == targetEnabled)
            return;

        if (targetEnabled)
            framework.Update += OnFrameworkUpdate;
        else
            framework.Update -= OnFrameworkUpdate;

        subscribed = targetEnabled;
    }

    private void OnFrameworkUpdate(IFramework unusedFramework)
    {
        if (!EffectiveEnabled)
            return;

        if (!clientState.IsLoggedIn || !playerState.IsLoaded)
        {
            UpdateStatusText();
            return;
        }

        if (!AddonHelper.IsAddonReady(SelectYesnoAddonName))
        {
            UpdateStatusText();
            return;
        }

        if (!TryGetAetheryteTicketPrompt(out _))
        {
            UpdateStatusText(selectYesnoReady: true);
            return;
        }

        var now = Environment.TickCount64;
        if (now - lastClickAttemptTick < ClickCooldownMs)
            return;

        lastClickAttemptTick = now;
        try
        {
            if (!AddonHelper.ClickYesNo(EffectiveSelectYes, closeAfter: true))
                return;

            LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S6B58A24BF036B14E", GetResponseLabel(), DateTime.Now));
            if (requiredByXagman)
                log.Information("[XASlave] Xagman Teleport Helper: declined aetheryte ticket usage (No).");
            UpdateStatusText(ticketPromptVisible: true);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Teleport Helper failed while handling the aetheryte ticket prompt.");
            LastActionText = global::XASlave.Localization.UiMessage.Store(ref uiLastActionText, global::XASlave.Localization.UiMessage.Format("S050A8FCDEEB21FAB", GetResponseLabel(), DateTime.Now));
            UpdateStatusText(ticketPromptVisible: true);
        }
    }

    private static bool TryGetAetheryteTicketPrompt(out string promptText)
    {
        promptText = string.Empty;
        foreach (var entry in AddonHelper.GetAddonTextEntries(SelectYesnoAddonName))
        {
            var normalized = NormalizePromptText(entry);
            if (!IsAetheryteTicketPrompt(normalized))
                continue;

            promptText = normalized;
            return true;
        }

        return false;
    }

    private static bool IsAetheryteTicketPrompt(string text)
    {
        return text.Contains("aetheryte ticket", StringComparison.OrdinalIgnoreCase)
            && text.Contains("teleport", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePromptText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var builder = new StringBuilder(text.Length);
        var lastWasWhitespace = false;
        foreach (var character in text)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                if (!lastWasWhitespace)
                {
                    builder.Append(' ');
                    lastWasWhitespace = true;
                }

                continue;
            }

            builder.Append(character);
            lastWasWhitespace = false;
        }

        return builder.ToString().Trim();
    }

    private void ResetRuntimeState()
    {
        lastClickAttemptTick = 0;
    }

    private void UpdateStatusText(bool selectYesnoReady = false, bool ticketPromptVisible = false)
    {
        if (!EffectiveEnabled)
        {
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"));
            return;
        }

        if (requiredByXagman)
        {
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S284FAED8CBCBC7DB"));
            return;
        }

        if (!clientState.IsLoggedIn || !playerState.IsLoaded)
        {
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S53AFCAB4DE97AF4A"));
            return;
        }

        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, (ticketPromptVisible ? global::XASlave.Localization.UiMessage.Format("S188BD5B86C70EFC3", GetResponseLabel()) : (selectYesnoReady ? global::XASlave.Localization.UiMessage.Key("S250A6D90FBFA50AA") : global::XASlave.Localization.UiMessage.Format("SBD4EA23890837D39", GetResponseLabel()))));
    }

    private global::XASlave.Localization.UiMessage GetResponseLabel()
        => (EffectiveSelectYes ? global::XASlave.Localization.UiMessage.Key("S85A39AB345D672FF") : global::XASlave.Localization.UiMessage.Key("S1EA442A134B2A184"));
}
