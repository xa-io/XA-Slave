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
    internal string Status { get; private set; } = "Zone results unavailable";
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
        if (!eligible) { observer?.Disable(); Observation = null; pending = false; Status = "Zone search is not available in this area"; return; }
        if (pending && tick >= deadline) { pending = false; Status = "Refresh timed out; native cache may be stale"; }
        if (!screenReady) { Status = "Waiting for the game screen"; return; }
        if (binding == null && bindingError == null)
        {
            try { binding = new(); }
            catch (Exception error) { bindingError = error.Message; }
        }
        if (bindingError != null) { Status = "Zone results unavailable: " + bindingError; return; }
        try { (observer ??= new NearbyZoneObserverOwner(binding!, this)).Enable(); }
        catch (Exception error)
        {
            var reason = error.Message;
            try { observer?.Disable(); }
            catch (Exception cleanupError) { reason += " Cleanup also failed: " + cleanupError.Message; }
            ReportUnavailable(reason);
            return;
        }
        if (!observersReady) { Status = "Zone results unavailable"; return; }
        if (pending || tick < nextAttempt[(int)mode]) return;
        try
        {
            var proxy = binding!.CurrentProxy(mode);
            if (binding.Agent(mode)->IsAgentActive()) { Status = "Automatic refresh paused while the search window is active"; return; }
            if (lastProxy[(int)mode] != (nint)proxy) { lastProxy[(int)mode] = (nint)proxy; nextAttempt[(int)mode] = 0; }
            nextAttempt[(int)mode] = tick + 60 * Stopwatch.Frequency;
            deadline = tick + 15 * Stopwatch.Frequency;
            pending = armed = true;
            attemptProxy = (nint)proxy;
            attemptIdKnown = false;
            Status = "Refresh attempted; waiting for a native cache observation";
            try { NearbyZoneRequestAdapter.Request(binding, mode, Generation, () => Generation, place); }
            finally { armed = false; }
            if (attemptEpoch != epoch) { pending = false; Status = "Refresh ownership changed; native cache may be stale"; }
        }
        catch (Exception error) { armed = pending = false; Status = "Zone refresh unavailable: " + error.Message; }
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
        else if (pending) { pending = false; Status = "Another native request started; cache freshness is uncertain"; }
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
        Status = (mode == NearbyZoneMode.Search ? "Search results" : "Content-member cache") + " observed at " + copy.ObservedUtc.ToString("HH:mm:ss 'UTC'")
            + (copy.PossiblyCapped ? "; possibly capped at 200" : string.Empty)
            + (matchedAttempt ? "; request attribution is not guaranteed" : "; unsolicited or late cache observation");
    }

    internal void Invalidate()
    {
        Generation++;
        epoch++;
        Observation = null;
        pending = armed = eligible = false;
        attemptIdKnown = false;
        Status = "Zone results unavailable";
    }

    internal void ReportUnavailable(string reason)
    {
        pending = armed = false;
        Status = "Zone results unavailable: " + reason;
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
