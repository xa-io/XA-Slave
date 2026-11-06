using System;
using System.Collections.Generic;

namespace XASlave.Data;

/// <summary>A named selection of existing character identities, without roster or trading settings.</summary>
[Serializable]
public sealed class XagmanCharacterSelection
{
    public string Format { get; set; } = "XASlave.Xagman.CharacterSelection";
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = string.Empty;
    public XagmanRole Role { get; set; }
    public List<XagmanCharacterSelectionMember> Characters { get; set; } = new();
}

[Serializable]
public sealed class XagmanCharacterSelectionMember
{
    public ulong ContentId { get; set; }
    public string CharacterNameWorld { get; set; } = string.Empty;
}
