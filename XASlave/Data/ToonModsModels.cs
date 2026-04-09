using System;
using System.Collections.Generic;

namespace XASlave.Data;

[Serializable]
public sealed class ToonModSavedList
{
    public string Name { get; set; } = string.Empty;
    public List<string> ModKeys { get; set; } = new();
}

[Serializable]
public sealed class XAModResolutionPreset
{
    public int Width { get; set; }
    public int Height { get; set; }
}
