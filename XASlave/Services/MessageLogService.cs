using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

internal readonly record struct MessageLogEntry(
    DateTime CapturedUtc,
    XivChatType ChatType,
    string Sender,
    string Message,
    bool WasHandled);

/// <summary>
/// Observes every chat record delivered by Dalamud, optionally mirrors a safe one-line
/// form to the plugin log, and retains a bounded recent window for detection consumers.
/// It never handles, suppresses, or mutates the original message.
/// </summary>
internal sealed class MessageLogService : IDisposable
{
    internal const int MaxRecentEntries = 512;
    internal const int MaxSenderLength = 256;
    internal const int MaxMessageLength = 4096;

    private readonly IChatGui chatGui;
    private readonly IPluginLog log;
    private readonly Func<bool> loggingEnabled;
    private readonly object syncRoot = new();
    private readonly Queue<MessageLogEntry> recentEntries = new(MaxRecentEntries);
    private bool disposed;

    internal MessageLogService(IChatGui chatGui, IPluginLog log, Func<bool> loggingEnabled)
    {
        this.chatGui = chatGui ?? throw new ArgumentNullException(nameof(chatGui));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.loggingEnabled = loggingEnabled ?? throw new ArgumentNullException(nameof(loggingEnabled));

        chatGui.CheckMessageHandled += OnChatMessageHandled;
        if (loggingEnabled())
            log.Information("[XA Slave] [Message Log] Logging enabled for all chat types.");
    }

    /// <summary>
    /// Future detectors can subscribe without adding another global chat hook.
    /// Subscriber exceptions are isolated and cannot escape into Dalamud's chat dispatch.
    /// </summary>
    internal event Action<MessageLogEntry>? MessageObserved;

    internal int RecentCount
    {
        get
        {
            lock (syncRoot)
                return recentEntries.Count;
        }
    }

    internal IReadOnlyList<MessageLogEntry> Snapshot()
    {
        lock (syncRoot)
            return recentEntries.ToArray();
    }

    internal bool TryFindRecent(
        Func<MessageLogEntry, bool> predicate,
        TimeSpan maxAge,
        out MessageLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (maxAge < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxAge));

        return TryFindRecent(Snapshot(), DateTime.UtcNow, maxAge, predicate, out entry);
    }

    internal static bool TryFindRecent(
        IReadOnlyList<MessageLogEntry> entries,
        DateTime nowUtc,
        TimeSpan maxAge,
        Func<MessageLogEntry, bool> predicate,
        out MessageLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(predicate);
        if (maxAge < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxAge));

        var cutoffUtc = nowUtc - maxAge;
        for (var index = entries.Count - 1; index >= 0; index--)
        {
            var candidate = entries[index];
            if (candidate.CapturedUtc < cutoffUtc)
                continue;
            if (!predicate(candidate))
                continue;

            entry = candidate;
            return true;
        }

        entry = default;
        return false;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        try
        {
            chatGui.CheckMessageHandled -= OnChatMessageHandled;
        }
        finally
        {
            lock (syncRoot)
                recentEntries.Clear();
            MessageObserved = null;
        }

        if (loggingEnabled())
            log.Information("[XA Slave] [Message Log] Monitoring stopped.");
    }

    private void OnChatMessageHandled(IHandleableChatMessage message)
    {
        if (disposed)
            return;

        try
        {
            var entry = CreateEntry(
                DateTime.UtcNow,
                message.LogKind,
                message.Sender.TextValue,
                message.Message.TextValue,
                message.IsHandled);

            lock (syncRoot)
            {
                while (recentEntries.Count >= MaxRecentEntries)
                    recentEntries.Dequeue();
                recentEntries.Enqueue(entry);
            }

            if (loggingEnabled())
                log.Information(FormatForLog(entry));
            NotifyObservers(entry);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XA Slave] [Message Log] Failed to capture a chat message.");
        }
    }

    internal static MessageLogEntry CreateEntry(
        DateTime capturedUtc,
        XivChatType chatType,
        string? sender,
        string? message,
        bool wasHandled)
    {
        return new MessageLogEntry(
            capturedUtc,
            chatType,
            NormalizeField(sender, MaxSenderLength),
            NormalizeField(message, MaxMessageLength),
            wasHandled);
    }

    internal static string FormatForLog(MessageLogEntry entry)
    {
        var sender = EscapeQuotedValue(entry.Sender);
        var message = EscapeQuotedValue(entry.Message);
        return $"[XA Slave] [Message Log] [{entry.ChatType} ({(ushort)entry.ChatType})] handled={entry.WasHandled.ToString().ToLowerInvariant()} sender=\"{sender}\" message=\"{message}\"";
    }

    internal static string NormalizeField(string? value, int maxLength)
    {
        if (maxLength < 1)
            throw new ArgumentOutOfRangeException(nameof(maxLength));
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        var previousWasSpace = false;
        foreach (var character in value)
        {
            var normalized = char.IsControl(character) || char.IsWhiteSpace(character)
                ? ' '
                : character;
            if (normalized == ' ' && previousWasSpace)
                continue;

            builder.Append(normalized);
            previousWasSpace = normalized == ' ';
            if (builder.Length >= maxLength)
                break;
        }

        var result = builder.ToString().Trim();
        if (value.Length <= maxLength || result.Length < maxLength)
            return result;

        return maxLength == 1 ? "…" : $"{result[..(maxLength - 1)]}…";
    }

    private static string EscapeQuotedValue(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private void NotifyObservers(MessageLogEntry entry)
    {
        var handlers = MessageObserved?.GetInvocationList();
        if (handlers is null)
            return;

        foreach (var handler in handlers)
        {
            try
            {
                ((Action<MessageLogEntry>)handler)(entry);
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[XA Slave] [Message Log] A message observer failed.");
            }
        }
    }
}
