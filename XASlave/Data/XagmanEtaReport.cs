using System;
using System.Collections.Generic;

namespace XASlave.Data;

// Optional, advisory telemetry. Older peers omit this without changing trade compatibility.
public sealed class XagmanEtaReport
{
    public int Revision { get; set; } = 1;
    public DateTime CapturedUtc { get; set; }
    public string CoordinatorInstanceId { get; set; } = string.Empty;
    public XagmanRunPhase Phase { get; set; }
    public bool Complete { get; set; }
    public bool UsesDefaultRate { get; set; }
    public bool HasUnfinishedWork { get; set; }
    public double AverageSecondsPerCharacter { get; set; }
    public int SampleCount { get; set; }
    public double RemainingSeconds { get; set; }
    public string ActiveDataCenter { get; set; } = string.Empty;
    public List<XagmanEtaRoute> Routes { get; set; } = new();
}

public sealed class XagmanEtaRoute
{
    public string DataCenter { get; set; } = string.Empty;
    public int RemainingCharacters { get; set; }
    public double RemainingSeconds { get; set; }
}
