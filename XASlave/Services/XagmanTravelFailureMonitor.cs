using System;
using Dalamud.Game.Text;

namespace XASlave.Services;

internal readonly record struct XagmanTravelFailure(
    string Reason,
    string Message,
    string ExpectedCharacter,
    string Command,
    DateTime ObservedAtUtc);

/// <summary>
/// Latches a known travel failure for one armed character operation. Chat observation only
/// records evidence; the framework task that owns the operation decides how to recover.
/// </summary>
internal sealed class XagmanTravelFailureMonitor : IDisposable
{
    private readonly MessageLogService messages;
    private readonly Func<string> currentCharacter;
    private readonly object syncRoot = new();
    private long nextToken;
    private long activeToken;
    private DateTime armedAtUtc;
    private string expectedCharacter = string.Empty;
    private string command = string.Empty;
    private XagmanTravelFailure? pendingFailure;
    private bool disposed;

    internal XagmanTravelFailureMonitor(MessageLogService messages, Func<string> currentCharacter)
    {
        this.messages = messages ?? throw new ArgumentNullException(nameof(messages));
        this.currentCharacter = currentCharacter ?? throw new ArgumentNullException(nameof(currentCharacter));
        messages.MessageObserved += OnMessageObserved;
    }

    internal long Arm(string expectedCharacter, string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCharacter);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var normalizedCharacter = expectedCharacter.Trim();
        var separator = normalizedCharacter.IndexOf('@');
        if (separator <= 0 || separator == normalizedCharacter.Length - 1
            || string.IsNullOrWhiteSpace(normalizedCharacter[..separator])
            || string.IsNullOrWhiteSpace(normalizedCharacter[(separator + 1)..]))
        {
            throw new ArgumentException("A character name and home world are required.", nameof(expectedCharacter));
        }

        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            activeToken = checked(++nextToken);
            armedAtUtc = DateTime.UtcNow;
            this.expectedCharacter = normalizedCharacter;
            this.command = command.Trim();
            pendingFailure = null;
            return activeToken;
        }
    }

    internal bool TryTakeFailure(long token, out XagmanTravelFailure failure)
    {
        lock (syncRoot)
        {
            if (!disposed && token != 0 && token == activeToken && pendingFailure is { } captured)
            {
                failure = captured;
                ResetOperation();
                return true;
            }

            failure = default;
            return false;
        }
    }

    internal void End(long token)
    {
        lock (syncRoot)
        {
            if (token != 0 && token == activeToken)
                ResetOperation();
        }
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed)
                return;

            disposed = true;
            ResetOperation();
        }

        messages.MessageObserved -= OnMessageObserved;
    }

    private void OnMessageObserved(MessageLogEntry entry)
    {
        var reason = entry.ChatType switch
        {
            XivChatType.ErrorMessage when entry.Message.Equals(
                "Unable to teleport. Insufficient gil.", StringComparison.Ordinal) => "not enough gil",
            XivChatType.Urgent when entry.Message.StartsWith(
                "No attuned Aetheryte found for ", StringComparison.Ordinal) => "unable to teleport to location",
            _ => string.Empty,
        };
        if (reason.Length == 0)
            return;

        long observedToken;
        lock (syncRoot)
        {
            if (disposed || activeToken == 0 || pendingFailure.HasValue || entry.CapturedUtc < armedAtUtc)
                return;
            observedToken = activeToken;
        }

        // Do not invoke game-facing accessors under the monitor lock. Recheck the token afterward
        // in case the operation ended, was replaced, or was disposed while identity was read.
        var observedCharacter = currentCharacter();
        lock (syncRoot)
        {
            if (disposed || observedToken != activeToken || pendingFailure.HasValue
                || !string.Equals(observedCharacter?.Trim(), expectedCharacter, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            pendingFailure = new XagmanTravelFailure(
                reason, entry.Message, expectedCharacter, command, entry.CapturedUtc);
        }
    }

    private void ResetOperation()
    {
        activeToken = 0;
        armedAtUtc = default;
        expectedCharacter = string.Empty;
        command = string.Empty;
        pendingFailure = null;
    }
}
