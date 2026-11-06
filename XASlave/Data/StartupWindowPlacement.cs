using System;

namespace XASlave.Data;

/// <summary>Saved outer-window position in desktop pixels, relative to a named display's work area.</summary>
[Serializable]
public sealed class StartupWindowPlacement
{
    public string MonitorDeviceName { get; set; } = string.Empty;
    public int X { get; set; }
    public int Y { get; set; }
    public int MonitorWorkLeft { get; set; }
    public int MonitorWorkTop { get; set; }
}
