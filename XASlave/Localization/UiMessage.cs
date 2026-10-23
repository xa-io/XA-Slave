using System;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace XASlave.Localization;

/// <summary>Retains original service text for logic/logging and resolves its presentation at draw time.</summary>
internal sealed class UiMessage
{
    private static readonly ConditionalWeakTable<Exception, UiMessage> ExceptionMessages = new();
    private readonly string? key;
    private readonly object?[] arguments;
    private readonly bool formatted;
    private readonly UiMessage? boundedSource;
    private readonly int maximumLength;
    private readonly bool appendEllipsis;
    internal string Original { get; }

    private UiMessage(string? key, object?[] arguments, string original, bool formatted = false)
    {
        this.key = key;
        this.arguments = arguments;
        Original = original;
        this.formatted = formatted;
    }

    internal static UiMessage Raw(string value) => new(null, Array.Empty<object?>(), value);

    private UiMessage(UiMessage source, int maximumLength, bool appendEllipsis)
        : this(null, Array.Empty<object?>(), Bound(source.Original, maximumLength, appendEllipsis))
    {
        boundedSource = source;
        this.maximumLength = maximumLength;
        this.appendEllipsis = appendEllipsis;
    }

    internal static UiMessage Truncate(UiMessage message, int maxLength, bool ellipsis = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);
        return new UiMessage(message, maxLength, ellipsis);
    }

    private static string Bound(string value, int maxLength, bool ellipsis)
        => value.Length <= maxLength ? value : maxLength == 0 ? string.Empty
            : ellipsis ? value[..(maxLength - 1)] + "…" : value[..maxLength];

    internal static UiMessage Value(object? value) => Raw(value?.ToString() ?? string.Empty);

    /// <summary>Only for explicitly authored labels already admitted to the immutable catalog.</summary>
    internal static UiMessage Authored(string value)
    {
        var key = UiLocale.Key(value);
        return UiLocale.HasOriginal(key) ? Key(key) : Raw(value);
    }

    internal static UiMessage Concat(params UiMessage[] pieces)
        => new(null, pieces.Cast<object?>().ToArray(), string.Concat(pieces.Select(piece => piece.Original)));

    internal static UiMessage Join(string separator, System.Collections.Generic.IEnumerable<UiMessage> messages)
    {
        var pieces = new System.Collections.Generic.List<UiMessage>();
        foreach (var message in messages)
        {
            if (pieces.Count != 0)
                pieces.Add(Raw(separator));
            pieces.Add(message);
        }
        return Concat(pieces.ToArray());
    }

    internal static UiMessage Capture(UiMessage? message, string current)
        => message != null && string.Equals(message.Original, current, StringComparison.Ordinal) ? message : Raw(current);

    internal static UiMessage Capture(ref UiMessage? message, string current)
        => Capture(Volatile.Read(ref message), current);

    internal static TException Attach<TException>(TException exception, UiMessage message) where TException : Exception
    {
        ExceptionMessages.AddOrUpdate(exception, message);
        return exception;
    }

    internal static TException CreateException<TException>(UiMessage message, Func<string, TException> create) where TException : Exception
        => Attach(create(message.Original), message);

    internal static UiMessage FromException(Exception exception)
    {
        if (!ExceptionMessages.TryGetValue(exception, out var message))
            return Raw(exception.Message);
        var original = exception.Message;
        if (string.Equals(original, message.Original, StringComparison.Ordinal))
            return message;
        // BCL argument exceptions append parameter/actual-value diagnostics. Only
        // retain our explicit attached prefix; the generated suffix stays opaque.
        return message.Original.Length > 0 && original.StartsWith(message.Original, StringComparison.Ordinal)
            ? Concat(message, Raw(original[message.Original.Length..])) : Raw(original);
    }

    internal static UiMessage Key(string key)
        => new(key, Array.Empty<object?>(), UiLocale.Native(key));

    internal static UiMessage Format(string key, params object?[] arguments)
    {
        // Existing interpolation uses the calling thread's culture. Keep that exact
        // original text for task logic and diagnostics; only Display uses UI culture.
        var originals = arguments.Select(argument => argument is UiMessage message ? message.Original : argument).ToArray();
        var original = string.Format(CultureInfo.CurrentCulture, UiLocale.Native(key), originals);
        return new UiMessage(key, (object?[])arguments.Clone(), original, formatted: true);
    }

    internal string Display => boundedSource != null ? Bound(boundedSource.Display, maximumLength, appendEllipsis)
        : key == null ? arguments.Length == 0 ? Original : string.Concat(arguments.Cast<UiMessage>().Select(piece => piece.Display)) : !formatted
        ? UiLocale.Get(key)
        : UiLocale.FormatKey(key, arguments.Select(argument => argument is UiMessage message ? message.Display : argument).ToArray());

    internal static string Store(ref UiMessage? destination, UiMessage message)
    {
        Volatile.Write(ref destination, message);
        return message.Original;
    }

    internal static string Resolve(UiMessage? message, string current)
        => message != null && string.Equals(message.Original, current, StringComparison.Ordinal) ? message.Display : current;

    internal static string Resolve(ref UiMessage? message, string current)
        => Resolve(Volatile.Read(ref message), current);

    public override string ToString() => Original;
}
