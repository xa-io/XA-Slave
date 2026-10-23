using System;
using System.Collections.Generic;
using System.Linq;

namespace XASlave.Services;

internal interface INearbyPlayerNotificationOutput
{
    bool IsCurrent(NearbyPlayerEmission emission, Guid ruleId);
    void LocalChat(string text);
    void Toast(string text);
    void Notification(string text);
    bool Speech(string text);
    bool SendEntry(string text);
}

// Framework-owned output queue. Every external call may reenter and cancel it.
internal sealed class NearbyPlayerActionDispatcher
{
    private sealed class Job
    {
        internal readonly NearbyPlayerEmission Emission;
        internal readonly NearbyPlayerCompiledRule Rule;
        internal readonly string[] Commands;
        internal int Index;
        internal bool Cancelled;
        internal Job(NearbyPlayerEmission emission, NearbyPlayerCompiledRule rule, string[] commands)
        { Emission = emission; Rule = rule; Commands = commands; }
    }
    private sealed class Batch
    {
        internal readonly Job[] Jobs;
        internal readonly long Created;
        internal bool Announced, SpeechDone;
        internal Batch(Job[] jobs, long created) { Jobs = jobs; Created = created; }
    }
    private readonly INearbyPlayerNotificationOutput output;
    private readonly Queue<Batch> batches = new();
    private long generation, nextEntry, nextSpeech;
    private bool draining;
    private int jobCount;
    internal int FreeJobs => Math.Max(0, 256 - jobCount);
    internal string Status { get; private set; } = global::XASlave.Localization.UiMessage.Raw(string.Empty).Original;
    private global::XASlave.Localization.UiMessage? uiStatus = global::XASlave.Localization.UiMessage.Raw(string.Empty);
    internal global::XASlave.Localization.UiMessage StatusMessage => global::XASlave.Localization.UiMessage.Capture(ref uiStatus, Status);
    internal string LocalizedStatus => global::XASlave.Localization.UiMessage.Resolve(ref uiStatus, Status);
    internal string SpeechStatus { get; private set; } = global::XASlave.Localization.UiMessage.Raw(string.Empty).Original;
    private global::XASlave.Localization.UiMessage? uiSpeechStatus = global::XASlave.Localization.UiMessage.Raw(string.Empty);
    internal global::XASlave.Localization.UiMessage SpeechStatusMessage => global::XASlave.Localization.UiMessage.Capture(ref uiSpeechStatus, SpeechStatus);
    internal string LocalizedSpeechStatus => global::XASlave.Localization.UiMessage.Resolve(ref uiSpeechStatus, SpeechStatus);

    internal NearbyPlayerActionDispatcher(INearbyPlayerNotificationOutput output) { this.output = output; }

    internal void Cancel()
    {
        generation++; batches.Clear(); jobCount = 0; Status = global::XASlave.Localization.UiMessage.Store(ref uiStatus, global::XASlave.Localization.UiMessage.Raw(string.Empty)); SpeechStatus = global::XASlave.Localization.UiMessage.Store(ref uiSpeechStatus, global::XASlave.Localization.UiMessage.Raw(string.Empty));
        // Keep rate deadlines across toggles/config edits; cancellation cannot bypass
        // the service-wide chat-entry or speech admission interval.
    }

    internal void CancelRule(Guid ruleId)
    {
        foreach (var batch in batches)
            foreach (var job in batch.Jobs)
                if (job.Rule.Id == ruleId) job.Cancelled = true;
    }

    internal void Enqueue(IReadOnlyList<NearbyPlayerEmission> emissions, long now)
    {
        var jobs = new List<Job>();
        var uniqueCommands = new HashSet<(string Name, uint World, string Command)>();
        foreach (var emission in emissions)
        {
            foreach (var rule in emission.Rules)
            {
                if (jobCount + jobs.Count >= 256) { Status = global::XASlave.Localization.UiMessage.Store(ref uiStatus, global::XASlave.Localization.UiMessage.Key("S45E72BE0FA6FFA08")); break; }
                var commands = new List<string>();
                if (rule.CommandsEnabled)
                {
                    try
                    {
                        // Expand the whole rule before accepting any of its entries.
                        // A malformed/oversized replacement never partially dispatches.
                        var expanded = rule.Templates.Select(template => template.Expand(emission.Player.Name, emission.Player.WorldName)).ToArray();
                        foreach (var command in expanded)
                            if (uniqueCommands.Add((emission.Key.Name, emission.Key.World, command))) commands.Add(command);
                    }
                    catch (Exception) { Status = global::XASlave.Localization.UiMessage.Store(ref uiStatus, global::XASlave.Localization.UiMessage.Key("SF6F7AF1A3D363BA7")); }
                }
                jobs.Add(new(emission, rule, commands.ToArray()));
            }
        }
        if (jobs.Count == 0) return;
        batches.Enqueue(new(jobs.ToArray(), now)); jobCount += jobs.Count;
    }

    internal void Drain(long now)
    {
        if (draining) return;
        draining = true;
        var admittedGeneration = generation;
        var entryAttempted = false;
        try
        {
            while (batches.Count != 0 && admittedGeneration == generation)
            {
                var batch = batches.Peek();
                if (now - batch.Created > 10000) { Drop(batch); continue; }
                if (!batch.Announced)
                {
                    batch.Announced = true;
                    Announce(batch, admittedGeneration);
                    if (admittedGeneration != generation) return;
                }
                if (!batch.SpeechDone && now >= nextSpeech)
                {
                    var text = TextFor(batch, job => job.Rule.Speech);
                    batch.SpeechDone = true;
                    if (text.Length != 0)
                    {
                        nextSpeech = now + 2000;
                        try { SpeechStatus = global::XASlave.Localization.UiMessage.Store(ref uiSpeechStatus, (output.Speech(text) ? global::XASlave.Localization.UiMessage.Key("S08E8DDAE134EFB99") : global::XASlave.Localization.UiMessage.Key("SD0204F7CC55AC3B3"))); }
                        catch { SpeechStatus = global::XASlave.Localization.UiMessage.Store(ref uiSpeechStatus, global::XASlave.Localization.UiMessage.Key("SAB9DEDD7331D7C72")); }
                        if (admittedGeneration != generation) return;
                    }
                }
                var pendingCommands = false;
                foreach (var job in batch.Jobs)
                {
                    if (!Current(job)) { job.Index = job.Commands.Length; continue; }
                    if (job.Index >= job.Commands.Length) continue;
                    pendingCommands = true;
                    if (entryAttempted || now < nextEntry) continue;
                    var command = job.Commands[job.Index++];
                    entryAttempted = true; nextEntry = now + 500;
                    bool sent;
                    try { sent = output.SendEntry(command); }
                    catch { sent = false; }
                    if (admittedGeneration != generation) return;
                    if (!sent)
                    {
                        job.Index = job.Commands.Length;
                        Status = global::XASlave.Localization.UiMessage.Store(ref uiStatus, global::XASlave.Localization.UiMessage.Key("S6E394E45B49591B2"));
                    }
                    else Status = global::XASlave.Localization.UiMessage.Store(ref uiStatus, global::XASlave.Localization.UiMessage.Key("SF3256682F1287D36"));
                }
                if (pendingCommands && batch.Jobs.Any(job => job.Index < job.Commands.Length)) return;
                if (!batch.SpeechDone)
                {
                    // No speech-capable current job means no utterance needs a slot.
                    if (TextFor(batch, job => job.Rule.Speech).Length == 0) batch.SpeechDone = true;
                    else return;
                }
                Drop(batch);
            }
        }
        finally { draining = false; }
    }

    private bool Current(Job job) => !job.Cancelled && job.Rule.Enabled && output.IsCurrent(job.Emission, job.Rule.Id);
    private string TextFor(Batch batch, Func<Job, bool> channel) => string.Join("\n", batch.Jobs
        .Where(job => channel(job) && Current(job)).Select(job => global::XASlave.Localization.UiMessage.Format("SE6BB7BCEBA5414C9", job.Emission.Player.Name, job.Emission.Player.WorldName).Original).Distinct(StringComparer.Ordinal));

    private void Announce(Batch batch, long admittedGeneration)
    {
        Emit(TextFor(batch, job => job.Rule.LocalChat), output.LocalChat, global::XASlave.Localization.UiMessage.Key("S1D979518DA0E679C"));
        if (admittedGeneration != generation) return;
        Emit(TextFor(batch, job => job.Rule.Toast), output.Toast, global::XASlave.Localization.UiMessage.Key("S34B86033459D0EA5"));
        if (admittedGeneration != generation) return;
        Emit(TextFor(batch, job => job.Rule.Notification), output.Notification, global::XASlave.Localization.UiMessage.Key("S7D31B83313991D4C"));
    }
    private void Emit(string text, Action<string> emit, global::XASlave.Localization.UiMessage channel)
    {
        if (text.Length == 0) return;
        try { emit(text); }
        catch { Status = global::XASlave.Localization.UiMessage.Store(ref uiStatus, global::XASlave.Localization.UiMessage.Concat(channel, global::XASlave.Localization.UiMessage.Key("SC412EBEE27544343"))); }
    }
    private void Drop(Batch batch) { batches.Dequeue(); jobCount -= batch.Jobs.Length; }
}
