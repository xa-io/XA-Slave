using System.Text.Json.Nodes;

namespace XASlave.Services;

// Build the wire contract explicitly. Reflection-based serialization of anonymous
// objects can fail after protection removes constructor parameter metadata.
internal sealed record IpcActivitySnapshot(
    IpcActivityIdentity Development, bool Busy, string Source, string Detail,
    IpcTaskActivity Task, IpcWorkerActivity ArPostProcess, IpcWorkerActivity AutoCollector,
    IpcWorkerActivity MoogleMail, IpcDeliveryActivity ExpertDelivery, IpcUiSnapshot? Ui = null)
{
    public string ToJson() => new JsonObject
    {
        ["available"] = true,
        ["development"] = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["internalName"] = "XASlave",
            ["assemblyMvid"] = Development.AssemblyMvid,
            ["loadId"] = Development.LoadId,
            ["startedAtUtc"] = Development.StartedAtUtc,
            ["ready"] = Development.Ready,
        },
        ["busy"] = Busy,
        ["source"] = Source,
        ["detail"] = Detail,
        ["task"] = new JsonObject
        {
            ["running"] = Task.Running,
            ["name"] = Task.Name,
            ["status"] = Task.Status,
            ["currentStep"] = Task.CurrentStep,
            ["totalSteps"] = Task.TotalSteps,
            ["completedItems"] = Task.CompletedItems,
            ["totalItems"] = Task.TotalItems,
            ["currentItem"] = Task.CurrentItem,
        },
        ["arPostProcess"] = Worker(ArPostProcess),
        ["autoCollector"] = Worker(AutoCollector),
        ["moogleMail"] = Worker(MoogleMail),
        ["expertDelivery"] = new JsonObject
        {
            ["running"] = ExpertDelivery.Running,
            ["runId"] = ExpertDelivery.RunId,
            ["status"] = ExpertDelivery.Status,
            ["delivered"] = ExpertDelivery.Delivered,
            ["summary"] = ExpertDelivery.Summary,
            ["outcome"] = ExpertDelivery.Outcome,
            // The existing nested public result contract uses PascalCase keys
            // and a numeric enum. Keep that shape, without reflecting over it.
            ["result"] = ExpertDelivery.Result is { } result ? new JsonObject
            {
                ["RunId"] = result.RunId,
                ["Outcome"] = result.Outcome,
                ["Delivered"] = result.Delivered,
                ["Protected"] = result.Protected,
                ["CapBlocked"] = result.CapBlocked,
                ["ElapsedMilliseconds"] = result.ElapsedMilliseconds,
                ["Message"] = result.Message,
            } : null,
        },
        ["error"] = string.Empty,
        ["ui"] = Ui?.ToJson(),
    }.ToJsonString();

    private static JsonObject Worker(IpcWorkerActivity value) => new()
    {
        ["running"] = value.Running,
        ["status"] = value.Status,
        ["lastAction"] = value.LastAction,
    };
}

internal sealed record IpcActivityIdentity(string AssemblyMvid, string LoadId, string StartedAtUtc, bool Ready);
internal sealed record IpcTaskActivity(bool Running, string Name, string Status, int CurrentStep, int TotalSteps,
    int CompletedItems, int TotalItems, string CurrentItem);
internal sealed record IpcWorkerActivity(bool Running, string Status, string LastAction);
internal sealed record IpcDeliveryActivity(bool Running, long RunId, string Status, int Delivered, string Summary,
    string? Outcome, IpcDeliveryResult? Result);
internal sealed record IpcDeliveryResult(long RunId, int Outcome, int Delivered, int Protected, int CapBlocked,
    long ElapsedMilliseconds, string Message);

internal sealed record IpcUiSnapshot(bool MainOpen, string Panel, string RenderedPanel, long RenderRevision)
{
    public JsonObject ToJson() => new()
    {
        ["schemaVersion"] = 1,
        ["mainOpen"] = MainOpen,
        ["panel"] = Panel,
        ["renderedPanel"] = RenderedPanel,
        ["renderRevision"] = RenderRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
