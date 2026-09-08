using System;

namespace XASlave.Services;

/// <summary>A lazily read value that refreshes no more than once per interval.</summary>
internal sealed class PolledValue<T>
{
    private readonly string label;
    private readonly Func<T> read;
    private readonly TimeSpan interval;
    private readonly Func<DateTime> utcNow;
    private readonly Action<Exception, string> logWarning;
    private DateTime nextDueUtc = DateTime.MinValue;
    private T current;

    public PolledValue(string label, Func<T> read, TimeSpan interval)
        : this(label, read, interval, default!)
    {
    }

    public PolledValue(string label, Func<T> read, TimeSpan interval, T initialValue)
        : this(
            label,
            read,
            interval,
            initialValue,
            () => DateTime.UtcNow,
            (exception, message) => Plugin.Log.Warning(exception, message))
    {
    }

    internal PolledValue(
        string label,
        Func<T> read,
        TimeSpan interval,
        T initialValue,
        Func<DateTime> utcNow,
        Action<Exception, string>? logWarning = null)
    {
        this.label = label;
        this.read = read;
        this.interval = interval;
        this.utcNow = utcNow;
        this.logWarning = logWarning ?? ((_, _) => { });
        current = initialValue;
    }

    public T Value
    {
        get
        {
            var now = utcNow();
            if (now < nextDueUtc)
                return current;

            nextDueUtc = now + interval;
            try
            {
                current = read();
            }
            catch (Exception ex)
            {
                logWarning(ex, $"[XASlave] Polled value '{label}' refresh failed; retaining the previous value.");
            }

            return current;
        }
    }

    public void Invalidate()
    {
        nextDueUtc = DateTime.MinValue;
    }
}
