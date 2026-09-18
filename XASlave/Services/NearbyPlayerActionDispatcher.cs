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
    internal string Status { get; private set; } = string.Empty;
    internal string SpeechStatus { get; private set; } = string.Empty;

    internal NearbyPlayerActionDispatcher(INearbyPlayerNotificationOutput output) { this.output = output; }

    internal void Cancel()
    {
        generation++; batches.Clear(); jobCount = 0; Status = string.Empty; SpeechStatus = string.Empty;
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
                if (jobCount + jobs.Count >= 256) { Status = "Action capacity reached; additional jobs suppressed."; break; }
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
                    catch (Exception) { Status = "Command expansion failed; this rule's entries were not queued."; }
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
                        try { SpeechStatus = output.Speech(text) ? "Speech requested through EdgeTTS." : "Speech unavailable: EdgeTTS provider is missing or incompatible."; }
                        catch { SpeechStatus = "Speech unavailable: EdgeTTS request failed."; }
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
                        Status = "Command dispatch failed; remaining entries in this job were cancelled without retry.";
                    }
                    else Status = "Command submitted locally; downstream completion is not confirmed.";
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
        .Where(job => channel(job) && Current(job)).Select(job => $"Player nearby: {job.Emission.Player.Name}@{job.Emission.Player.WorldName}").Distinct(StringComparer.Ordinal));

    private void Announce(Batch batch, long admittedGeneration)
    {
        Emit(TextFor(batch, job => job.Rule.LocalChat), output.LocalChat, "Local chat");
        if (admittedGeneration != generation) return;
        Emit(TextFor(batch, job => job.Rule.Toast), output.Toast, "Toast");
        if (admittedGeneration != generation) return;
        Emit(TextFor(batch, job => job.Rule.Notification), output.Notification, "Notification");
    }
    private void Emit(string text, Action<string> emit, string channel)
    {
        if (text.Length == 0) return;
        try { emit(text); }
        catch { Status = channel + " failed; other enabled outputs remain eligible."; }
    }
    private void Drop(Batch batch) { batches.Dequeue(); jobCount -= batch.Jobs.Length; }
}
