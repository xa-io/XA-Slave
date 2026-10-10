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

    public string StatusText { get; private set; } = "Disabled";
    public string LastActionText { get; private set; } = "No actions yet.";

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

            LastActionText = $"Last action: selected {GetResponseLabel()} for an aetheryte ticket prompt at {DateTime.Now:HH:mm:ss}.";
            if (requiredByXagman)
                log.Information("[XASlave] Xagman Teleport Helper: declined aetheryte ticket usage (No).");
            UpdateStatusText(ticketPromptVisible: true);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Teleport Helper failed while handling the aetheryte ticket prompt.");
            LastActionText = $"Last action: failed to select {GetResponseLabel()} at {DateTime.Now:HH:mm:ss}.";
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
            StatusText = "Disabled";
            return;
        }

        if (requiredByXagman)
        {
            StatusText = "Required by Xagman - monitoring aetheryte ticket prompts and selecting No; saved mod settings are preserved.";
            return;
        }

        if (!clientState.IsLoggedIn || !playerState.IsLoaded)
        {
            StatusText = "Enabled - waiting for a logged-in character.";
            return;
        }

        StatusText = ticketPromptVisible
            ? $"Enabled - aetheryte ticket prompt detected; selecting {GetResponseLabel()}."
            : selectYesnoReady
                ? "Enabled - SelectYesno is open, but it is not the aetheryte ticket prompt."
                : $"Enabled - monitoring SelectYesno for aetheryte ticket prompts; selecting {GetResponseLabel()}.";
    }

    private string GetResponseLabel()
        => EffectiveSelectYes ? "Yes" : "No";
}
