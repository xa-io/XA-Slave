using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace XASlave.Services;

/// <summary>Optional Windows speech on an owned STA; no blocking COM work on the framework thread.</summary>
internal sealed class NearbySpeech : IDisposable
{
    private readonly object gate = new();
    private readonly AutoResetEvent wake = new(false);
    private string? pending;
    private bool stopped;
    private Thread? worker;
    private volatile string? error;
    internal string? Error => error;

    internal bool Queue(string text)
    {
        lock (gate)
        {
            if (stopped || Error != null) return false;
            pending = text.Length <= 256 ? text : text[..256];
            if (worker == null)
            {
                worker = new Thread(Run) { IsBackground = true, Name = "XA Nearby speech" };
                try { worker.SetApartmentState(ApartmentState.STA); worker.Start(); }
                catch (Exception failure) { worker = null; pending = null; error = failure.Message; return false; }
            }
            wake.Set();
            return true;
        }
    }

    private void Run()
    {
        object? voice = null;
        try
        {
            var type = Type.GetTypeFromProgID("SAPI.SpVoice") ?? throw new InvalidOperationException("Windows speech is unavailable.");
            voice = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Windows speech could not start.");
            while (true)
            {
                wake.WaitOne();
                string? text;
                lock (gate) { if (stopped) break; text = pending; pending = null; }
                if (text != null) type.InvokeMember("Speak", BindingFlags.InvokeMethod, null, voice, [text, 3]);
            }
        }
        catch (Exception failure) { error = failure.GetBaseException().Message; }
        finally
        {
            if (voice != null)
            {
                try { voice.GetType().InvokeMember("Speak", BindingFlags.InvokeMethod, null, voice, [string.Empty, 3]); } catch { }
                try { if (Marshal.IsComObject(voice)) Marshal.FinalReleaseComObject(voice); } catch { }
            }
            lock (gate) { stopped = true; pending = null; wake.Dispose(); }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (stopped) return;
            stopped = true;
            pending = null;
            if (worker == null) wake.Dispose();
            else wake.Set();
        }
    }
}
