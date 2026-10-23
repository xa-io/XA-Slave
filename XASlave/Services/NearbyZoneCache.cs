using System;
using System.Diagnostics;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using XASlave.Data;

namespace XASlave.Services;

/// <summary>Framework-owned request policy and honest native-cache observations. Native observers attach separately.</summary>
internal sealed unsafe class NearbyZoneCache : IDisposable
{
    internal readonly record struct RequestStart(long Generation, long Epoch, NearbyZoneMode Mode, nint Proxy, bool Owned);
    private NearbyZoneNativeBinding? binding;
    private NearbyZoneObserverOwner? observer;
    private string? bindingError;
    private global::XASlave.Localization.UiMessage? bindingErrorMessage;
    private bool observersReady, eligible, pending, armed;
    private long epoch, deadline, attemptEpoch;
    private long sourceGeneration = -1;
    private byte attemptId;
    private bool attemptIdKnown;
    private nint attemptProxy;
    private readonly long[] nextAttempt = new long[2];
    private readonly nint[] lastProxy = new nint[2];
    private NearbyZoneMode mode;
    private uint territory;
    private ushort place;
    internal long Generation { get; private set; } = -1;
    internal NearbyZoneObservation? Observation { get; private set; }
    internal string Status { get; private set; } = global::XASlave.Localization.UiMessage.Key("S9D3AB32D7F0AE2DF").Original;
    private global::XASlave.Localization.UiMessage? uiStatus = global::XASlave.Localization.UiMessage.Key("S9D3AB32D7F0AE2DF");
    internal global::XASlave.Localization.UiMessage StatusMessage => global::XASlave.Localization.UiMessage.Capture(ref uiStatus, Status);
    internal string LocalizedStatus => global::XASlave.Localization.UiMessage.Resolve(ref uiStatus, Status);
    internal NearbyZoneNativeBinding? Binding => binding;

    internal void Update(long generation, uint currentTerritory, bool canSearch, bool contentMode, uint searchPlace, bool screenReady, long tick)
    {
        var currentMode = contentMode ? NearbyZoneMode.ContentMember : NearbyZoneMode.Search;
        if (generation != sourceGeneration || currentTerritory != territory || currentMode != mode)
        {
            Invalidate(); sourceGeneration = generation; territory = currentTerritory; mode = currentMode;
        }
        if (eligible && !canSearch) Invalidate();
        eligible = canSearch;
        place = searchPlace <= ushort.MaxValue ? (ushort)searchPlace : (ushort)0;
        if (!eligible) { observer?.Disable(); Observation = null; pending = false; Status = global::XASlave.Localization.UiMessage.Store(ref uiStatus, global::XASlave.Localization.UiMessage.Key("S0809AB8F94DDCDB6")); return; }
        if (pending && tick >= deadline) { pending = false; Status = global::XASlave.Localization.UiMessage.Store(ref uiStatus, global::XASlave.Localization.UiMessage.Key("SB7B3FA06F6125BD9")); }
        if (!screenReady) { Status = global::XASlave.Localization.UiMessage.Store(ref uiStatus, global::XASlave.Localization.UiMessage.Key("S8E3FC22FE62AA086")); return; }
        if (binding == null && bindingError == null)
        {
            try { binding = new(); }
            catch (Exception error) { bindingError = global::XASlave.Localization.UiMessage.Store(ref bindingErrorMessage, global::XASlave.Localization.UiMessage.FromException(error)); }
        }
        if (bindingError != null) { Status = global::XASlave.Localization.UiMessage.Store(ref uiStatus, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Key("S3DA9EDDC61708133"), global::XASlave.Localization.UiMessage.Capture(ref bindingErrorMessage, bindingError))); return; }
        try { (observer ??= new NearbyZoneObserverOwner(binding!, this)).Enable(); }
        catch (Exception error)
        {
            var reason = global::XASlave.Localization.UiMessage.FromException(error);
            try { observer?.Disable(); }
            catch (Exception cleanupError) { reason = global::XASlave.Localization.UiMessage.Concat(reason, global::XASlave.Localization.UiMessage.Key("S94D5A3593864B008"), global::XASlave.Localization.UiMessage.FromException(cleanupError)); }
            ReportUnavailable(reason);
            return;
        }
        if (!observersReady) { Status = global::XASlave.Localization.UiMessage.Store(ref uiStatus, global::XASlave.Localization.UiMessage.Key("S9D3AB32D7F0AE2DF")); return; }
        if (pending || tick < nextAttempt[(int)mode]) return;
        try
        {
            var proxy = binding!.CurrentProxy(mode);
            if (binding.Agent(mode)->IsAgentActive()) { Status = global::XASlave.Localization.UiMessage.Store(ref uiStatus, global::XASlave.Localization.UiMessage.Key("S9DDFDBAFAE429CA0")); return; }
            if (lastProxy[(int)mode] != (nint)proxy) { lastProxy[(int)mode] = (nint)proxy; nextAttempt[(int)mode] = 0; }
            nextAttempt[(int)mode] = tick + 60 * Stopwatch.Frequency;
            deadline = tick + 15 * Stopwatch.Frequency;
            pending = armed = true;
            attemptProxy = (nint)proxy;
            attemptIdKnown = false;
            Status = global::XASlave.Localization.UiMessage.Store(ref uiStatus, global::XASlave.Localization.UiMessage.Key("S23E1E92BB67C315D"));
            try { NearbyZoneRequestAdapter.Request(binding, mode, Generation, () => Generation, place); }
            finally { armed = false; }
            if (attemptEpoch != epoch) { pending = false; Status = global::XASlave.Localization.UiMessage.Store(ref uiStatus, global::XASlave.Localization.UiMessage.Key("S5152075EB4A18BBB")); }
        }
        catch (Exception error) { armed = pending = false; Status = global::XASlave.Localization.UiMessage.Store(ref uiStatus, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Key("SC2D048B0F73DA3AF"), global::XASlave.Localization.UiMessage.FromException(error))); }
    }

    // Called only after an owner has validated and activated every required native observer.
    internal void SetObserversReady(bool value)
    {
        observersReady = value;
        if (!value) Invalidate();
    }

    internal RequestStart EnterRequest(NearbyZoneMode requestMode, nint proxy)
    {
        if (!eligible || !observersReady || !Plugin.ClientState.IsLoggedIn || Plugin.ClientState.TerritoryType != territory)
            return new(Generation, -1, requestMode, proxy, false);
        // The other cache (or an unregistered proxy instance) cannot steal this proxy's attempt.
        if (requestMode != mode || binding == null || (nint)binding.CurrentProxy(requestMode) != proxy)
            return new(Generation, -1, requestMode, proxy, false);
        var owned = armed && pending && requestMode == mode && proxy == attemptProxy;
        armed = false;
        epoch++;
        if (owned) attemptEpoch = epoch;
        else if (pending) { pending = false; Status = global::XASlave.Localization.UiMessage.Store(ref uiStatus, global::XASlave.Localization.UiMessage.Key("S56656BD35AE88FA9")); }
        return new(Generation, epoch, requestMode, proxy, owned);
    }

    internal void RequestReturned(RequestStart start)
    {
        if (!start.Owned || start.Generation != Generation || start.Epoch != epoch || start.Mode != mode || !pending) return;
        var proxy = binding!.CurrentProxy(mode);
        if ((nint)proxy != start.Proxy) { pending = false; return; }
        attemptId = proxy->CurrentRequestId;
        attemptIdKnown = true;
    }

    // Invoke after Original EndRequest, synchronously in the callback's framework/generation scope.
    internal void ObserveCompleted(NearbyZoneMode completedMode, nint callbackProxy, long enteredGeneration)
    {
        if (!observersReady || !eligible || enteredGeneration != Generation || completedMode != mode || binding == null
            || !Plugin.ClientState.IsLoggedIn || Plugin.ClientState.TerritoryType != territory) return;
        var copy = binding.Copy((InfoProxyCommonList*)callbackProxy, completedMode, Generation, territory);
        if (enteredGeneration != Generation || !Plugin.ClientState.IsLoggedIn || Plugin.ClientState.TerritoryType != territory) return;
        Observation = copy;
        var matchedAttempt = pending && attemptProxy == callbackProxy && attemptEpoch == epoch
            && (mode == NearbyZoneMode.Search || (attemptIdKnown && ((InfoProxyCommonList*)callbackProxy)->CurrentRequestId == attemptId));
        if (matchedAttempt) pending = false;
        // Even a matching byte can wrap, and Search has no unique direct-request ID. Never claim freshness or transport success.
        Status = global::XASlave.Localization.UiMessage.Store(ref uiStatus, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Concat(((mode == NearbyZoneMode.Search ? global::XASlave.Localization.UiMessage.Key("SE978B00DE465A271") : global::XASlave.Localization.UiMessage.Key("S2B692F5EBEC2AE57"))), global::XASlave.Localization.UiMessage.Key("S1D1AEEEE9DAF660D")), global::XASlave.Localization.UiMessage.Raw(copy.ObservedUtc.ToString("HH:mm:ss 'UTC'"))), ((copy.PossiblyCapped ? global::XASlave.Localization.UiMessage.Key("SD99325749EEC6E69") : global::XASlave.Localization.UiMessage.Raw(string.Empty)))), ((matchedAttempt ? global::XASlave.Localization.UiMessage.Key("S3E21FDB965115CE3") : global::XASlave.Localization.UiMessage.Key("SD7E803CB7EAC4E90")))));
    }

    internal void Invalidate()
    {
        Generation++;
        epoch++;
        Observation = null;
        pending = armed = eligible = false;
        attemptIdKnown = false;
        Status = global::XASlave.Localization.UiMessage.Store(ref uiStatus, global::XASlave.Localization.UiMessage.Key("S9D3AB32D7F0AE2DF"));
    }

    internal void ReportUnavailable(string reason) => ReportUnavailable(global::XASlave.Localization.UiMessage.Raw(reason));

    internal void ReportUnavailable(global::XASlave.Localization.UiMessage reason)
    {
        pending = armed = false;
        Status = global::XASlave.Localization.UiMessage.Store(ref uiStatus, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Key("S3DA9EDDC61708133"), reason));
    }

    internal void Suspend()
    {
        Invalidate();
        observer?.Disable();
    }

    public void Dispose()
    {
        Invalidate();
        observer?.Dispose();
        observersReady = false;
    }
}
