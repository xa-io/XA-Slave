using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XASlave.Services;

public unsafe sealed class BlacklistedPartyNameService : IDisposable
{
    private const string PartyListAddonName = "_PartyList";
    private static readonly ByteColor BlockedTextColor = new() { R = 255, G = 72, B = 72, A = 255 };
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(250);

    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Dictionary<nint, TextNodeState> touchedNodes = new();
    private readonly List<nint> staleNodeKeys = new();

    private bool enabled;
    private bool subscribed;
    private DateTime lastUpdateUtc = DateTime.MinValue;
    private DateTime lastWarningUtc = DateTime.MinValue;
    private nint trackedAddonAddress;

    public BlacklistedPartyNameService(IFramework framework, IPluginLog log)
    {
        this.framework = framework;
        this.log = log;
    }

    public string StatusText { get; private set; } = "Disabled";

    public bool SetEnabled(bool value)
    {
        if (!value)
        {
            enabled = false;
            RestoreTouchedNodes();
            UpdateFrameworkSubscription();
            StatusText = "Disabled";
            return false;
        }

        enabled = true;
        UpdateFrameworkSubscription();
        StatusText = "Enabled - waiting for _PartyList and blacklist data.";
        TryUpdatePartyList();
        return true;
    }

    public void Dispose()
    {
        enabled = false;
        RestoreTouchedNodes();
        UpdateFrameworkSubscription();
    }

    private void UpdateFrameworkSubscription()
    {
        var shouldSubscribe = enabled;
        if (shouldSubscribe == subscribed)
            return;

        if (shouldSubscribe)
            framework.Update += OnFrameworkUpdate;
        else
            framework.Update -= OnFrameworkUpdate;

        subscribed = shouldSubscribe;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!enabled)
            return;

        var now = DateTime.UtcNow;
        if (now - lastUpdateUtc < UpdateInterval)
            return;

        lastUpdateUtc = now;
        TryUpdatePartyList();
    }

    private void TryUpdatePartyList()
    {
        try
        {
            UpdatePartyList();
        }
        catch (Exception ex)
        {
            StatusText = "Enabled - failed to update _PartyList name text.";
            var now = DateTime.UtcNow;
            if (now - lastWarningUtc > TimeSpan.FromSeconds(10))
            {
                lastWarningUtc = now;
                log.Warning(ex, "[XASlave] Failed to update blacklisted party member display names.");
            }
        }
    }

    private void UpdatePartyList()
    {
        var aliases = CollectBlacklistedPartyAliases();
        if (aliases.Count == 0)
        {
            RestoreTouchedNodes();
            StatusText = "Enabled - no blacklisted party members detected.";
            return;
        }

        var addon = AddonHelper.GetAddon(PartyListAddonName);
        if (addon == null || !addon->IsVisible)
        {
            RestoreTouchedNodes();
            StatusText = $"Enabled - found {aliases.Count} blocked party member(s); {PartyListAddonName} is not visible.";
            return;
        }

        var addonAddress = (nint)addon;
        if (trackedAddonAddress != nint.Zero && trackedAddonAddress != addonAddress)
            touchedNodes.Clear();

        trackedAddonAddress = addonAddress;

        var activeNodes = new HashSet<nint>();
        var replacedCount = 0;
        for (var index = 0; index < addon->UldManager.NodeListCount; index++)
        {
            var node = addon->UldManager.NodeList[index];
            replacedCount += ApplyToNode(node, aliases, activeNodes, addonAddress);
        }

        RestoreInactiveNodes(activeNodes);
        StatusText = replacedCount == 0
            ? $"Enabled - found {aliases.Count} blocked party member(s); waiting for Unknown row text in {PartyListAddonName}."
            : $"Enabled - showing {replacedCount} blocked party-list name(s) in red.";
    }

    private static Dictionary<string, string> CollectBlacklistedPartyAliases()
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var blacklist = TryGetBlacklistProxy();
        if (blacklist == null)
            return aliases;

        var groupManager = GroupManager.Instance();
        var group = groupManager == null ? null : groupManager->GetGroup();
        if (group == null)
            return aliases;

        if (group->IsAlliance)
        {
            for (var index = 0; index < 20; index++)
                TryAddMemberAlias(group->GetAllianceMemberByIndex(index), blacklist, aliases);
        }
        else
        {
            for (var index = 0; index < 8; index++)
                TryAddMemberAlias(group->GetPartyMemberByIndex(index), blacklist, aliases);
        }

        return aliases;
    }

    private static InfoProxyBlacklist* TryGetBlacklistProxy()
    {
        var infoModule = InfoModule.Instance();
        return infoModule == null ? null : (InfoProxyBlacklist*)infoModule->GetInfoProxyById(InfoProxyId.Blacklist);
    }

    private static void TryAddMemberAlias(PartyMember* member, InfoProxyBlacklist* blacklist, IDictionary<string, string> aliases)
    {
        if (member == null || member->ContentId == 0)
            return;

        var unknownName = ReadNameOverride(member);
        if (string.IsNullOrWhiteSpace(unknownName) || !unknownName.StartsWith("Unknown ", StringComparison.OrdinalIgnoreCase))
            return;

        InfoProxyBlacklist.BlockResult blockResult = default;
        blacklist->GetBlockResult(&blockResult, member->AccountId, member->ContentId);
        if (blockResult.Type == InfoProxyBlacklist.BlockResultType.NotBlocked)
            return;

        var replacementName = blockResult.BlockedCharacterPtr == null
            ? string.Empty
            : ReadBlockedCharacterName(blockResult.BlockedCharacterPtr);
        if (string.IsNullOrWhiteSpace(replacementName))
            replacementName = member->NameString;

        if (string.IsNullOrWhiteSpace(replacementName) || string.Equals(replacementName, unknownName, StringComparison.Ordinal))
            return;

        aliases[unknownName] = replacementName;
    }

    private static string ReadNameOverride(PartyMember* member)
    {
        try
        {
            return member->NameOverride == null ? string.Empty : member->NameOverride->ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ReadBlockedCharacterName(InfoProxyBlacklist.BlockedCharacter* character)
    {
        try
        {
            return character->Name.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private int ApplyToNode(
        AtkResNode* node,
        IReadOnlyDictionary<string, string> aliases,
        ISet<nint> activeNodes,
        nint addonAddress,
        int depth = 0)
    {
        if (node == null || depth > 8)
            return 0;

        if (node->Type == NodeType.Text)
            return ApplyToTextNode((AtkTextNode*)node, aliases, activeNodes, addonAddress);

        if ((int)node->Type < 1000)
            return 0;

        var componentNode = (AtkComponentNode*)node;
        if (componentNode->Component == null)
            return 0;

        var updatedCount = 0;
        for (var index = 0; index < componentNode->Component->UldManager.NodeListCount; index++)
        {
            var child = componentNode->Component->UldManager.NodeList[index];
            updatedCount += ApplyToNode(child, aliases, activeNodes, addonAddress, depth + 1);
        }

        return updatedCount;
    }

    private int ApplyToTextNode(
        AtkTextNode* textNode,
        IReadOnlyDictionary<string, string> aliases,
        ISet<nint> activeNodes,
        nint addonAddress)
    {
        if (textNode == null)
            return 0;

        var nodeAddress = (nint)textNode;
        var currentText = ReadTextNode(textNode);
        if (string.IsNullOrEmpty(currentText))
            return 0;

        foreach (var alias in aliases)
        {
            if (currentText.Contains(alias.Key, StringComparison.Ordinal))
            {
                var nextText = currentText.Replace(alias.Key, alias.Value, StringComparison.Ordinal);
                if (!touchedNodes.ContainsKey(nodeAddress))
                {
                    touchedNodes[nodeAddress] = new TextNodeState(
                        addonAddress,
                        currentText,
                        nextText,
                        alias.Value,
                        textNode->TextColor);
                }
                else
                {
                    touchedNodes[nodeAddress] = touchedNodes[nodeAddress].WithAppliedText(nextText, alias.Value);
                }

                textNode->SetText(nextText);
                textNode->TextColor = BlockedTextColor;
                activeNodes.Add(nodeAddress);
                return 1;
            }

            if (touchedNodes.TryGetValue(nodeAddress, out var state)
                && state.AddonAddress == addonAddress
                && currentText.Contains(alias.Value, StringComparison.Ordinal)
                && state.OriginalText.Contains(alias.Key, StringComparison.Ordinal))
            {
                textNode->TextColor = BlockedTextColor;
                activeNodes.Add(nodeAddress);
                return 1;
            }
        }

        return 0;
    }

    private void RestoreTouchedNodes()
    {
        var addon = AddonHelper.GetAddon(PartyListAddonName);
        var addonAddress = addon == null ? nint.Zero : (nint)addon;
        if (addonAddress == nint.Zero || addonAddress != trackedAddonAddress)
        {
            touchedNodes.Clear();
            trackedAddonAddress = addonAddress;
            return;
        }

        foreach (var entry in touchedNodes)
            RestoreNode((AtkTextNode*)entry.Key, entry.Value);

        touchedNodes.Clear();
    }

    private void RestoreInactiveNodes(ISet<nint> activeNodes)
    {
        staleNodeKeys.Clear();
        foreach (var entry in touchedNodes)
        {
            if (!activeNodes.Contains(entry.Key))
                staleNodeKeys.Add(entry.Key);
        }

        foreach (var key in staleNodeKeys)
        {
            if (touchedNodes.TryGetValue(key, out var state))
                RestoreNode((AtkTextNode*)key, state);

            touchedNodes.Remove(key);
        }
    }

    private static void RestoreNode(AtkTextNode* textNode, TextNodeState state)
    {
        if (textNode == null)
            return;

        var currentText = ReadTextNode(textNode);
        if (string.Equals(currentText, state.AppliedText, StringComparison.Ordinal)
            || (!string.IsNullOrEmpty(state.AppliedName) && currentText.Contains(state.AppliedName, StringComparison.Ordinal)))
        {
            textNode->SetText(state.OriginalText);
        }

        textNode->TextColor = state.OriginalColor;
    }

    private static string ReadTextNode(AtkTextNode* textNode)
    {
        try
        {
            return textNode->NodeText.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private readonly record struct TextNodeState(
        nint AddonAddress,
        string OriginalText,
        string AppliedText,
        string AppliedName,
        ByteColor OriginalColor)
    {
        public TextNodeState WithAppliedText(string appliedText, string appliedName)
        {
            return this with
            {
                AppliedText = appliedText,
                AppliedName = appliedName,
            };
        }
    }
}
