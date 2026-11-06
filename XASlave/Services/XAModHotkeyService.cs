using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using XASlave.Data;
using XASlave.Localization;

namespace XASlave.Services;

/// <summary>Observes shortcut presses; assignment alone owns keyboard input. Mutations use the existing XA Mods queue.</summary>
public unsafe sealed class XAModHotkeyService : IDisposable
{
    private const long InputFreshnessMilliseconds = 300;
    private static readonly int[] PrimaryKeys = Enumerable.Range(0, 256).Where(XAModHotkeyState.IsSupportedPrimaryKey).ToArray();
    private readonly Plugin plugin;
    private readonly IFramework framework;
    private readonly IKeyState keyState;
    private readonly IPluginLog log;
    private readonly Func<bool> isOwnerDisposed;
    private readonly object sync = new();
    private readonly Dictionary<int, XAModHotkeyState> keyStates = new();
    private readonly HashSet<int> drawHeldKeys = new();
    private List<XAModHotkeyBinding> observedBindings = new();
    private bool observedEnabled;
    private bool disposed;
    private long generation;
    private bool wasInputAllowed;
    private bool drawAvailable;
    private bool drawCapturesKeyboard = true;
    private XAModHotkeyModifiers drawModifiers;
    private long drawTimestamp;
    private bool nativeAvailable;
    private bool editorOpen;
    private long editorTimestamp;
    private string? captureModKey;
    private bool captureArmed;
    private int captureInputFrame = -1;
    private bool captureFocusPending;
    private bool captureInputActive;
    private XAModHotkeyBinding? captureCandidate;
    private XAModHotkeyBinding? capturedBinding;
    private UiMessage status = UiMessage.Authored("XA Mod hotkeys are ready.");
    private bool statusIsError;

    public XAModHotkeyService(Plugin plugin, IFramework framework, IKeyState keyState, IPluginLog log, Func<bool> isOwnerDisposed)
    {
        this.plugin = plugin;
        this.framework = framework;
        this.keyState = keyState;
        this.log = log;
        this.isOwnerDisposed = isOwnerDisposed;
        foreach (var key in PrimaryKeys) keyStates.Add(key, new XAModHotkeyState());
        observedBindings = CopyBindings();
        observedEnabled = plugin.Configuration.XAModHotkeysEnabled;
        framework.Update += OnUpdate;
    }

    internal UiMessage StatusMessage { get { lock (sync) return status; } }
    internal bool StatusIsError { get { lock (sync) return statusIsError; } }
    internal bool IsCapturing { get { lock (sync) return captureModKey != null; } }
    internal string? CaptureModKey { get { lock (sync) return captureModKey; } }

    internal IReadOnlyList<XAModHotkeyBinding> GetBindings()
    {
        lock (sync) return CopyBindings();
    }

    internal XAModHotkeyBinding? GetBinding(string modKey)
    {
        lock (sync) return FindBinding(modKey)?.Clone();
    }

    internal UiMessage? GetBindingIssue(string modKey)
    {
        lock (sync)
        {
            var binding = FindBinding(modKey);
            if (binding == null) return null;
            if ((plugin.Configuration.XAModHotkeyBindings?.Count(item => item != null && SameMod(item.ModKey, modKey)) ?? 0) > 1)
                return UiMessage.Authored("This XA Mod has more than one saved hotkey entry. Save or clear it to resolve the conflict.");
            return ValidateBinding(binding);
        }
    }

    internal bool TrySaveBinding(XAModHotkeyBinding binding, out UiMessage message)
    {
        lock (sync)
        {
            if (!CanEdit(out message)) return false;
            if (binding == null || !plugin.TryGetTitleBarFavXAModInfo(binding.ModKey ?? string.Empty, out var target))
                return Result(UiMessage.Authored("That XA Mod is unavailable."), true, out message);
            var candidate = binding.Clone();
            candidate.ModKey = target.Key;
            var issue = ValidateBinding(candidate);
            if (issue != null) return Result(issue, true, out message);
            var replacement = CopyBindings().Where(item => !SameMod(item.ModKey, candidate.ModKey)).ToList();
            replacement.Add(candidate);
            return PersistBindings(replacement, UiMessage.Authored("Hotkey saved."), out message);
        }
    }

    internal bool TryRemoveBinding(string modKey, out UiMessage message)
    {
        lock (sync)
        {
            if (!CanEdit(out message)) return false;
            var replacement = CopyBindings().Where(item => !SameMod(item.ModKey, modKey)).ToList();
            return PersistBindings(replacement, UiMessage.Authored("Hotkey cleared."), out message);
        }
    }

    internal bool TrySetBindingEnabled(string modKey, bool enabled, out UiMessage message)
    {
        lock (sync)
        {
            var binding = FindBinding(modKey)?.Clone();
            if (binding == null) return Result(UiMessage.Authored("No hotkey is assigned to this XA Mod."), true, out message);
            binding.Enabled = enabled;
            return TrySaveBinding(binding, out message);
        }
    }

    internal bool TrySetGlobalEnabled(bool enabled, out UiMessage message)
    {
        lock (sync)
        {
            if (!CanEdit(out message)) return false;
            var previous = plugin.Configuration.XAModHotkeysEnabled;
            Invalidate();
            plugin.Configuration.XAModHotkeysEnabled = enabled;
            try
            {
                plugin.Configuration.Save();
                observedEnabled = enabled;
                return Result(UiMessage.Authored(enabled ? "Hotkeys enabled." : "Hotkeys disabled."), false, out message);
            }
            catch (Exception ex)
            {
                plugin.Configuration.XAModHotkeysEnabled = previous;
                observedEnabled = previous;
                log.Error(ex, "[XASlave] Saving XA Mod hotkey settings failed; the previous settings were restored.");
                return Result(UiMessage.Format(UiLocale.Key("Could not save hotkey settings: {0}"), UiMessage.FromException(ex)), true, out message);
            }
        }
    }

    internal bool BeginCapture(string modKey, out UiMessage message)
    {
        lock (sync)
        {
            if (!CanEdit(out message)) return false;
            if (!plugin.TryGetTitleBarFavXAModInfo(modKey, out var target))
                return Result(UiMessage.Authored("That XA Mod is unavailable."), true, out message);
            Invalidate();
            captureModKey = target.Key;
            capturedBinding = null;
            captureArmed = false;
            captureInputFrame = -1;
            captureFocusPending = true;
            captureInputActive = false;
            captureCandidate = null;
            editorOpen = true;
            editorTimestamp = Environment.TickCount64;
            return Result(UiMessage.Authored("Press a key combination. Escape cancels."), false, out message);
        }
    }

    internal void RefreshEditorHeartbeat()
    {
        lock (sync)
        {
            if (disposed) return;
            var now = Environment.TickCount64;
            if (!editorOpen || !IsFresh(editorTimestamp, now)) Invalidate();
            editorOpen = true;
            editorTimestamp = now;
        }
    }

    internal void EndEditing()
    {
        lock (sync)
        {
            if (!editorOpen && captureModKey == null) return;
            CancelCaptureCore();
            capturedBinding = null;
            editorOpen = false;
            Invalidate();
        }
    }

    /// <summary>Allow the first submitted field to request focus; require active ownership on the following Draw.</summary>
    internal void ObserveCaptureInput(bool active)
    {
        lock (sync)
        {
            if (disposed || captureModKey == null) return;
            // SetKeyboardFocusHere queues navigation for the next frame; IsItemActive is still false
            // on the Assign click frame. Permit that one submission without accepting any keys.
            var awaitingFocus = captureFocusPending;
            captureFocusPending = false;
            if (!active && !awaitingFocus) { CancelCaptureCore(); return; }
            captureInputActive = active;
            captureInputFrame = ImGui.GetFrameCount();
        }
    }

    internal void CancelCapture()
    {
        lock (sync) CancelCaptureCore();
    }

    internal bool TryTakeCapturedBinding(out XAModHotkeyBinding binding)
    {
        lock (sync)
        {
            if (capturedBinding == null) { binding = null!; return false; }
            binding = capturedBinding.Clone();
            capturedBinding = null;
            return true;
        }
    }

    /// <summary>Called only from UiBuilder.Draw, after the windows have submitted their keyboard/editing state.</summary>
    internal void ObserveDraw()
    {
        lock (sync)
        {
            if (disposed || isOwnerDisposed() || framework.IsFrameworkUnloading) return;
            try
            {
                if (!ImGuiHelpers.IsImGuiInitialized)
                {
                    drawAvailable = false;
                    CancelCaptureCore();
                    Invalidate();
                    return;
                }
                var io = ImGui.GetIO();
                var now = Environment.TickCount64;
                drawTimestamp = now;
                drawAvailable = true;
                drawCapturesKeyboard = io.WantTextInput || io.WantCaptureKeyboard || io.KeySuper;
                drawModifiers = ReadModifiers(io.KeyCtrl, io.KeyAlt, io.KeyShift);
                drawHeldKeys.Clear();
                var observedKeys = captureModKey != null ? PrimaryKeys : ActivePrimaryKeys();
                foreach (var key in observedKeys)
                    if (ImGui.IsKeyDown(ToImGuiKey(key))) drawHeldKeys.Add(key);
                if (drawCapturesKeyboard && wasInputAllowed) Invalidate();
                if (captureModKey == null) return;
                if (!editorOpen || !IsFresh(editorTimestamp, now)
                    || captureInputFrame != ImGui.GetFrameCount() || !ReadGameWindowFocus())
                {
                    CancelCaptureCore();
                    return;
                }
                if (ImGui.IsKeyPressed(ImGuiKey.Escape, false))
                {
                    CancelCaptureCore();
                    return;
                }
                if (!captureInputActive) return;
                // Dalamud delivers ImGui keys and blocks game input while the owned writable field
                // requests text input. Native chat focus must not reject our own assignment field.
                if (!io.WantTextInput)
                {
                    captureArmed = false;
                    captureCandidate = null;
                    return;
                }
                if (io.KeySuper || HasUnsupportedCaptureKey())
                {
                    captureArmed = false;
                    captureCandidate = null;
                    SetStatus(UiMessage.Authored("That key cannot be used as a hotkey. Choose a supported key."), true);
                    return;
                }
                var pending = captureCandidate;
                if (pending != null)
                {
                    // Keep ownership until every key is released; do not hand a held chord back to the game.
                    if (drawHeldKeys.Any(key => key != pending.VirtualKey)
                        || (drawModifiers & ~pending.Modifiers) != 0)
                    {
                        captureCandidate = null;
                        captureArmed = false;
                        return;
                    }
                    if (drawHeldKeys.Count != 0 || drawModifiers != XAModHotkeyModifiers.None || !AreCaptureKeysReleased()) return;
                    capturedBinding = pending;
                    captureCandidate = null;
                    captureModKey = null;
                    captureArmed = false;
                    captureInputFrame = -1;
                    captureInputActive = false;
                    Invalidate();
                    SetStatus(UiMessage.Authored("Hotkey captured."), false);
                    return;
                }
                if (drawHeldKeys.Count == 0)
                {
                    // Before focus, Dalamud resets ImGui keys. A physical release also rejects
                    // an already-held movement key whose next OS repeat would look like a new press.
                    if (!captureArmed) captureArmed = drawModifiers == XAModHotkeyModifiers.None && AreCaptureKeysReleased();
                    return;
                }
                if (!captureArmed) return;
                captureArmed = false;
                // Ambiguous simultaneous primary keys are ignored until every primary is released again.
                if (drawHeldKeys.Count != 1) return;
                var keyCode = drawHeldKeys.First();
                var modifiers = ReadModifiers(io.KeyCtrl, io.KeyAlt, io.KeyShift);
                var current = FindBinding(captureModKey);
                captureCandidate = new XAModHotkeyBinding
                {
                    ModKey = captureModKey,
                    VirtualKey = keyCode,
                    Modifiers = modifiers,
                    Enabled = current?.Enabled ?? true,
                };
            }
            catch (Exception ex)
            {
                drawAvailable = false;
                CancelCaptureCore();
                Invalidate();
                SetStatus(UiMessage.Authored("Keyboard input is unavailable. Hotkeys are paused."), true);
                log.Debug(ex, "[XASlave] XA Mod hotkey Draw snapshot was unavailable.");
            }
        }
    }

    private void OnUpdate(IFramework _)
    {
        lock (sync)
        {
            if (disposed || isOwnerDisposed() || framework.IsFrameworkUnloading) return;
            var now = Environment.TickCount64;
            if (!ConfigurationMatchesObserved())
            {
                Invalidate();
                observedBindings = CopyBindings();
                observedEnabled = plugin.Configuration.XAModHotkeysEnabled;
            }
            if (editorOpen && !IsFresh(editorTimestamp, now))
            {
                CancelCaptureCore();
                capturedBinding = null;
                editorOpen = false;
                Invalidate();
            }
            nativeAvailable = ReadNativeSafety();
            var allowed = IsInputAllowed(now, nativeAvailable);
            if (!allowed)
            {
                if (wasInputAllowed) Invalidate();
                foreach (var state in keyStates.Values) state.Suppress();
                return;
            }
            wasInputAllowed = true;
            try
            {
                // Read generic modifier keys: left/right variants intentionally have identical binding semantics.
                if (!keyState.IsVirtualKeyValid(0x11) || !keyState.IsVirtualKeyValid(0x12) || !keyState.IsVirtualKeyValid(0x10))
                    throw new InvalidOperationException("The game keyboard modifier state is unavailable.");
                var modifiers = ReadModifiers(keyState[0x11], keyState[0x12], keyState[0x10]);
                var pressedKeys = new List<int>();
                foreach (var key in ActivePrimaryKeys())
                {
                    if (!keyState.IsVirtualKeyValid(key)) { keyStates[key].Suppress(); continue; }
                    var primaryDown = keyState[key];
                    var drawHasKey = drawHeldKeys.Contains(key);
                    var edge = keyStates[key].Observe(primaryDown || drawHasKey, true);
                    if (XAModHotkeyState.CanDispatchObservedEdge(edge, primaryDown, drawHasKey, modifiers, drawModifiers)) pressedKeys.Add(key);
                }
                foreach (var key in pressedKeys)
                {
                    var matching = observedBindings.Where(binding => binding.Enabled && binding.VirtualKey == key
                        && XAModHotkeyState.MatchesModifiers(binding.Modifiers, modifiers)).ToArray();
                    if (matching.Length != 1 || GetBindingIssue(matching[0].ModKey) != null) continue;
                    var binding = matching[0].Clone();
                    var requestGeneration = generation;
                    var queued = plugin.QueueXAModHotkeyToggleMessage(binding.ModKey, modifiers,
                        () => IsDispatchCurrent(requestGeneration, binding),
                        (success, result) =>
                        {
                            lock (sync) { if (!disposed) SetStatus(result, !success); }
                        }, out var message);
                    // Preserve the shared queue's one-accepted-mutation-per-update behavior even
                    // when a framework-thread hotkey now completes synchronously.
                    if (queued) break;
                    SetStatus(message, true);
                }
            }
            catch (Exception ex)
            {
                Invalidate();
                SetStatus(UiMessage.Authored("Keyboard input is unavailable. Hotkeys are paused."), true);
                log.Debug(ex, "[XASlave] XA Mod hotkey input was unavailable.");
            }
        }
    }

    private bool IsDispatchCurrent(long requestGeneration, XAModHotkeyBinding binding)
    {
        lock (sync)
        {
            var reason = GetDispatchRejectionReason(requestGeneration, binding);
            if (reason == null) return true;
            log.Debug("[XASlave] Hotkey {ModKey} rejected before apply: {Reason}; generation={Requested}/{Current}, drawAgeMs={DrawAge}, drawAvailable={DrawAvailable}, keyboardCaptured={KeyboardCaptured}, editing={Editing}, capturing={Capturing}",
                binding.ModKey, reason, requestGeneration, generation, Environment.TickCount64 - drawTimestamp,
                drawAvailable, drawCapturesKeyboard, editorOpen, captureModKey != null);
            return false;
        }
    }

    private string? GetDispatchRejectionReason(long requestGeneration, XAModHotkeyBinding binding)
    {
        if (disposed || isOwnerDisposed() || framework.IsFrameworkUnloading) return "plugin-unloading";
        if (generation != requestGeneration) return "input-session-changed";
        if (!ConfigurationMatchesObserved()) return "hotkey-settings-changed";
        var now = Environment.TickCount64;
        var nativeSafe = ReadNativeSafety();
        if (!IsInputAllowed(now, nativeSafe))
        {
            if (!nativeSafe) return "game-unfocused-or-native-text-input";
            if (!drawAvailable) return "draw-unavailable";
            if (!IsFresh(drawTimestamp, now)) return "draw-stale";
            if (drawCapturesKeyboard) return "imgui-keyboard-or-text-input";
            if (editorOpen || captureModKey != null) return "hotkey-editor-open";
            return "hotkeys-disabled";
        }
        var current = FindBinding(binding.ModKey);
        return current != null && current.Enabled && SameBinding(current, binding) && GetBindingIssue(binding.ModKey) == null
            ? null : "binding-changed-disabled-or-invalid";
    }

    private bool IsInputAllowed(long now, bool nativeSafe)
        => plugin.Configuration.XAModHotkeysEnabled && nativeSafe && drawAvailable && IsFresh(drawTimestamp, now)
            && !drawCapturesKeyboard && !editorOpen && captureModKey == null && observedBindings.Any(binding => binding.Enabled);

    private IEnumerable<int> ActivePrimaryKeys()
        => observedBindings.Where(binding => binding.Enabled && XAModHotkeyState.IsSupportedPrimaryKey(binding.VirtualKey))
            .Select(binding => binding.VirtualKey).Distinct();

    private static bool ReadNativeSafety()
    {
        try
        {
            var input = UIInputData.Instance();
            var module = RaptureAtkModule.Instance();
            return input != null && input->CursorInputs.IsGameWindowFocused && module != null && !module->IsTextInputActive();
        }
        catch { return false; }
    }

    private static bool ReadGameWindowFocus()
    {
        try
        {
            var input = UIInputData.Instance();
            return input != null && input->CursorInputs.IsGameWindowFocused;
        }
        catch { return false; }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    // Capture-only physical release check; never consumes, injects or changes game key state.
    private static bool AreCaptureKeysReleased()
    {
        foreach (var key in PrimaryKeys)
            if ((GetAsyncKeyState(key) & 0x8000) != 0) return false;
        return (GetAsyncKeyState(0x10) & 0x8000) == 0 && (GetAsyncKeyState(0x11) & 0x8000) == 0
            && (GetAsyncKeyState(0x12) & 0x8000) == 0 && (GetAsyncKeyState(0x5B) & 0x8000) == 0
            && (GetAsyncKeyState(0x5C) & 0x8000) == 0;
    }

    private UiMessage? ValidateBinding(XAModHotkeyBinding binding)
    {
        var modKey = binding.ModKey ?? string.Empty;
        if (!plugin.TryGetTitleBarFavXAModInfo(modKey, out _))
            return UiMessage.Authored("That XA Mod is unavailable.");
        if (!XAModHotkeyState.IsSupportedPrimaryKey(binding.VirtualKey))
            return UiMessage.Authored("Choose a supported keyboard key.");
        if (!XAModHotkeyState.IsValidModifiers(binding.Modifiers))
            return UiMessage.Authored("The hotkey modifier combination is invalid.");
        if (!XAModHotkeyState.CanApply(modKey, true, binding.Modifiers))
            return UiMessage.Authored("This XA Mod requires Ctrl+Shift in its hotkey.");
        try
        {
            if (!keyState.IsVirtualKeyValid(binding.VirtualKey)) return UiMessage.Authored("Choose a supported keyboard key.");
        }
        catch { return UiMessage.Authored("Keyboard input is unavailable. Hotkeys are paused."); }
        foreach (var other in plugin.Configuration.XAModHotkeyBindings ?? Enumerable.Empty<XAModHotkeyBinding>())
        {
            if (other == null || SameMod(other.ModKey, modKey) || other.VirtualKey != binding.VirtualKey
                || other.Modifiers != binding.Modifiers || !plugin.TryGetTitleBarFavXAModInfo(other.ModKey ?? string.Empty, out var target)) continue;
            return UiMessage.Format(UiLocale.Key("Hotkey {0} is already assigned to {1}."), FormatChord(binding), UiMessage.Authored(target.DisplayName));
        }
        return null;
    }

    private bool CanEdit(out UiMessage message)
    {
        if (disposed || isOwnerDisposed() || framework.IsFrameworkUnloading)
            return Result(UiMessage.Authored("Hotkey settings are unavailable while XA Slave is unloading."), true, out message);
        message = UiMessage.Raw(string.Empty);
        return true;
    }

    private bool PersistBindings(List<XAModHotkeyBinding> replacement, UiMessage success, out UiMessage message)
    {
        var previous = plugin.Configuration.XAModHotkeyBindings;
        Invalidate();
        plugin.Configuration.XAModHotkeyBindings = replacement;
        try
        {
            plugin.Configuration.Save();
            observedBindings = CopyBindings();
            return Result(success, false, out message);
        }
        catch (Exception ex)
        {
            plugin.Configuration.XAModHotkeyBindings = previous;
            observedBindings = CopyBindings();
            log.Error(ex, "[XASlave] Saving XA Mod hotkey bindings failed; the previous settings were restored.");
            return Result(UiMessage.Format(UiLocale.Key("Could not save hotkey settings: {0}"), UiMessage.FromException(ex)), true, out message);
        }
    }

    private XAModHotkeyBinding? FindBinding(string key)
        => plugin.Configuration.XAModHotkeyBindings?.FirstOrDefault(binding => binding != null && SameMod(binding.ModKey, key));

    private List<XAModHotkeyBinding> CopyBindings()
        => (plugin.Configuration.XAModHotkeyBindings ?? Enumerable.Empty<XAModHotkeyBinding>())
            .Where(binding => binding != null).Select(binding => binding.Clone()).ToList();

    private bool ConfigurationMatchesObserved()
    {
        if (plugin.Configuration.XAModHotkeysEnabled != observedEnabled) return false;
        var current = plugin.Configuration.XAModHotkeyBindings;
        if (current == null) return observedBindings.Count == 0;
        var index = 0;
        foreach (var binding in current)
        {
            if (binding == null) continue;
            if (index >= observedBindings.Count || !SameBinding(binding, observedBindings[index])) return false;
            index++;
        }
        return index == observedBindings.Count;
    }

    private static bool SameMod(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool SameBinding(XAModHotkeyBinding left, XAModHotkeyBinding right)
        => SameMod(left.ModKey, right.ModKey) && left.VirtualKey == right.VirtualKey
            && left.Modifiers == right.Modifiers && left.Enabled == right.Enabled;
    private static bool IsFresh(long timestamp, long now) => now >= timestamp && now - timestamp <= InputFreshnessMilliseconds;

    private void Invalidate()
    {
        generation++;
        wasInputAllowed = false;
        foreach (var state in keyStates.Values) state.Suppress();
    }

    private void CancelCaptureCore()
    {
        if (captureModKey == null && capturedBinding == null) return;
        captureModKey = null;
        capturedBinding = null;
        captureArmed = false;
        captureCandidate = null;
        captureInputFrame = -1;
        captureFocusPending = false;
        captureInputActive = false;
        Invalidate();
        SetStatus(UiMessage.Authored("Hotkey capture cancelled."), false);
    }

    private bool Result(UiMessage result, bool error, out UiMessage message)
    {
        SetStatus(result, error);
        message = result;
        return !error;
    }

    private void SetStatus(UiMessage result, bool error) { status = result; statusIsError = error; }

    internal static bool RequiresCtrlShift(string modKey) => XAModHotkeyState.RequiresCtrlShift(modKey);

    internal static string FormatChord(XAModHotkeyBinding binding)
    {
        var parts = new List<string>();
        if ((binding.Modifiers & XAModHotkeyModifiers.Control) != 0) parts.Add("Ctrl");
        if ((binding.Modifiers & XAModHotkeyModifiers.Alt) != 0) parts.Add("Alt");
        if ((binding.Modifiers & XAModHotkeyModifiers.Shift) != 0) parts.Add("Shift");
        var key = binding.VirtualKey;
        parts.Add(key >= 0x30 && key <= 0x39 || key >= 0x41 && key <= 0x5A ? ((char)key).ToString()
            : key >= 0x70 && key <= 0x7B ? $"F{key - 0x6F}"
            : key >= 0x60 && key <= 0x69 ? $"Num {key - 0x60}"
            : key switch
            {
                0x21 => "Page Up", 0x22 => "Page Down", 0x23 => "End", 0x24 => "Home",
                0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down", 0x2D => "Insert", 0x2E => "Delete",
                0x6A => "Num *", 0x6B => "Num +", 0x6D => "Num -", 0x6E => "Num .", 0x6F => "Num /", _ => $"VK {key}",
            });
        return string.Join("+", parts);
    }

    private static XAModHotkeyModifiers ReadModifiers(bool control, bool alt, bool shift)
        => (control ? XAModHotkeyModifiers.Control : XAModHotkeyModifiers.None)
            | (alt ? XAModHotkeyModifiers.Alt : XAModHotkeyModifiers.None)
            | (shift ? XAModHotkeyModifiers.Shift : XAModHotkeyModifiers.None);

    private static bool HasUnsupportedCaptureKey()
    {
        foreach (var key in Enum.GetValues<ImGuiKey>())
        {
            if (key < ImGuiKey.Tab || key > ImGuiKey.KeypadEqual
                || key == ImGuiKey.LeftCtrl || key == ImGuiKey.RightCtrl
                || key == ImGuiKey.LeftAlt || key == ImGuiKey.RightAlt
                || key == ImGuiKey.LeftShift || key == ImGuiKey.RightShift
                || key == ImGuiKey.Escape || !ImGui.IsKeyDown(key)) continue;
            if (!PrimaryKeys.Any(primary => ToImGuiKey(primary) == key)) return true;
        }
        return false;
    }

    private static ImGuiKey ToImGuiKey(int key)
    {
        if (key >= 0x30 && key <= 0x39) return (ImGuiKey)((int)ImGuiKey.Key0 + key - 0x30);
        if (key >= 0x41 && key <= 0x5A) return (ImGuiKey)((int)ImGuiKey.A + key - 0x41);
        if (key >= 0x60 && key <= 0x69) return (ImGuiKey)((int)ImGuiKey.Keypad0 + key - 0x60);
        if (key >= 0x70 && key <= 0x7B) return (ImGuiKey)((int)ImGuiKey.F1 + key - 0x70);
        return key switch
        {
            0x21 => ImGuiKey.PageUp, 0x22 => ImGuiKey.PageDown, 0x23 => ImGuiKey.End, 0x24 => ImGuiKey.Home,
            0x25 => ImGuiKey.LeftArrow, 0x26 => ImGuiKey.UpArrow, 0x27 => ImGuiKey.RightArrow, 0x28 => ImGuiKey.DownArrow,
            0x2D => ImGuiKey.Insert, 0x2E => ImGuiKey.Delete, 0x6A => ImGuiKey.KeypadMultiply, 0x6B => ImGuiKey.KeypadAdd,
            0x6D => ImGuiKey.KeypadSubtract, 0x6E => ImGuiKey.KeypadDecimal, 0x6F => ImGuiKey.KeypadDivide,
            _ => ImGuiKey.None,
        };
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            framework.Update -= OnUpdate;
            captureModKey = null;
            capturedBinding = null;
            captureCandidate = null;
            captureArmed = false;
            captureInputFrame = -1;
            captureFocusPending = false;
            captureInputActive = false;
            editorOpen = false;
            drawAvailable = false;
            Invalidate();
        }
    }
}
