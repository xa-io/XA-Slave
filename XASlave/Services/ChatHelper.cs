using System;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace XASlave.Services;

/// <summary>
/// Sends chat commands through the game's native chat processor.
///
/// ICommandManager.ProcessCommand() only dispatches to Dalamud-registered plugin commands
/// (e.g. /at for TextAdvance, /ays for AutoRetainer). Native game commands like
/// /nastatus, /inventory, /armourychest, /saddlebag, /freecompanycmd, /gaction
/// are silently dropped by ProcessCommand.
///
/// This helper sends through the game's own chat system which handles everything:
/// both native game commands AND Dalamud plugin commands (via Dalamud's chat hook).
/// </summary>
public static class ChatHelper
{
    /// <summary>
    /// Send a chat message or slash command through the game's native chat system.
    /// This is the equivalent of typing the command in the chat box.
    /// Works for both native game commands and Dalamud plugin commands.
    /// </summary>
    public static unsafe void SendMessage(string message)
    {
        if (string.IsNullOrEmpty(message)) return;

        try
        {
            var utf8 = Utf8String.FromString(message);
            UIModule.Instance()->ProcessChatBoxEntry(utf8);
            utf8->Dtor(true);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[XASlave] ChatHelper.SendMessage failed: {ex.Message}");
        }
    }
}
