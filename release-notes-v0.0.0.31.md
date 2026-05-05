# XA Slave v0.0.0.31 Release Notes

> **Version**: v0.0.0.31, unreleased
> **From**: v0.0.0.30
> **Prepared**: 2026-05-04
> **Status**: Full v0.0.0.31 user-facing release-note sync for Update History and release packaging

## Overview

v0.0.0.31 is mostly about expanding XA Mods and making plugin reloads feel calmer. It adds five Game Mods from the XA Testbench work, improves startup so heavier hook-backed mods arm after the core plugin load, updates FreeShop icon support, and hardens Xagman targeting/focus behavior.

## Highlights

- Five new Game Mods are available: `Display Network Latency`, `Lock Game Window In Combat`, `Notify When Friend Is Near`, `Better Cast Bar`, and `Better Duty Finder`.
- Startup is smoother: expensive cutscene/camera hook work is delayed until after the core plugin load, then armed over later frames.
- The startup log is clearer: delayed mods show as scheduled/still arming, and the completion marker now waits until incremental arming actually finishes.
- `Display Network Latency`, `Special Rendering Modes`, FreeShop shop icons, and Xagman targeting all received compatibility and safety fixes.
- Xagman now uses XA-owned target/focus handling instead of relying on external target helpers.

## Changes

### New Game Mods

- **Display Network Latency** adds a DTR latency display under XA Mods > Game Mods and can be toggled with `/xa latency on|off`.
- **Lock Game Window In Combat** keeps the game window locked while in combat and can be toggled with `/xa lockcombat on|off`.
- **Notify When Friend Is Near** scans exact-name and `/regex/` player patterns, then emits local toast/chat output when a match appears nearby. The toast now reads `Friend nearby: Name` without an extra XA Slave prefix, and it does not send in-game chat messages.
- **Better Cast Bar** ports the local cast-bar restyle and slidecast marker controls into XA Mods.
- **Better Duty Finder** ports the Contents Finder and Raid Finder inline settings overlay into XA Mods, using two compact custom button rows that sit above the normal duty window controls.

### XA Mods Restore And Presets

- The new Game Mods participate in startup restore, saved XA Mod lists, preset export/import, titlebar favourite visibility, and the in-plugin Commands reference.
- New command shortcuts were added for the ported mods: `/xa latency`, `/xa lockcombat`, `/xa friendnear`, `/xa castbar`, and `/xa dutyfinder`.
- Saved settings for network latency format, friend-near patterns/cooldown, and cast-bar layout options are preserved through XA Mod preset flows.

### Smoother Startup

- Cheap startup restore actions now drain within a small per-frame budget instead of one action per frame.
- Heavier non-critical startup restores for `Custom Timestamp Format`, `Auto Skip Cutscenes`, and `Custom Sight Distance` now run in a staggered post-load activation phase.
- Additional hook-backed restores for queue position display, object hiding, background rendering pause, trade refusal, and map reveal now use the same post-load activation phase instead of running in the initial deferred startup drain.
- `Auto Skip Cutscenes` and `Custom Sight Distance` now create hooks and scan patches incrementally over upcoming framework ticks instead of doing all hook work in one startup action.
- The startup summary now separates armed, still-arming, and unavailable XA Mods so delayed hook work is no longer reported as unavailable just because it is still in progress.
- Post-load activation timing logs are debug-level unless a step crosses the configured warning threshold, reducing noisy reload warnings for expected hook creation.
- The final post-load completion log now waits until incremental arming has drained before reporting that post-load XA Mod activation is complete.

### Compatibility And Safety Fixes

- **Display Network Latency** now lazily acquires its DTR entry and retries stale/duplicate DTR-title cleanup instead of throwing during plugin construction.
- The latency DTR entry now receives text before it is shown and stays visible with a `Ping: -- ms` placeholder while the endpoint is still unavailable.
- Latency DTR text and visibility updates now publish back through the framework tick path from the monitor loop, and the XA Mods options report when the entry is hidden by `/xlsettings`.
- **Special Rendering Modes** now reports UI visibility and world fade support separately, keeps UI visibility controls available when possible, and disables only world-fade buttons when the native fade helper is missing.
- **Enable Item Icon In Shops** now matches the current FreeShop AtkValue layout and clamps reads/writes to the available AtkValue range.
- **Xagman targeting** now uses XA's target-by-name helper before falling back to `/target`, which improves patch-day behavior when external target helpers are unavailable.
- **Xagman focus handling** now assigns and clears focus through Dalamud target manager state instead of sending `/focustarget`.
- Xagman now keeps XA's target-command recovery effectively active while Xagman is running without changing the saved XA Mods toggle.

### Debug And Review Support

- Debug target/focus tester controls were added for validating TargetByName, quoted `/target` fallback, direct focus assignment, and focus clearing.
- Startup arming logs now identify individual cutscene/camera hook and patch steps, which makes reload reviews easier without treating expected work as a warning.
- The v0.0.0.31 Update History entry has been rewritten as a friendly full-release summary instead of only the first FreeShop/Xagman compatibility note.

## Comprehensive Review

### Authoritative Implementation Points

- `XASlave/Plugin.cs`
- `XASlave/Configuration.cs`
- `XASlave/Data/ToonModsModels.cs`
- `XASlave/Services/AutoDisplayNetworkLatencyService.cs`
- `XASlave/Services/AutoLockGameWindowService.cs`
- `XASlave/Services/NotifyWhenFriendIsNearService.cs`
- `XASlave/Services/BetterCastBarService.cs`
- `XASlave/Services/BetterDutyFinderSettingsService.cs`
- `XASlave/Services/AutoSkipCutsceneService.cs`
- `XASlave/Services/SightDistanceService.cs`
- `XASlave/Services/SystemWindowModsService.cs`
- `XASlave/Services/EnableItemIconInShopsService.cs`
- `XASlave/Services/TargetCommandFixService.cs`
- `XASlave/Windows/Panels/XAModsPanel.cs`
- `XASlave/Windows/Panels/CommandsPanel.cs`
- `XASlave/Windows/Tasks/XagmanPanel.cs`
- `XASlave/Windows/SlaveWindow.cs`
- `XASlave/Windows/SlaveWindow.Debug.cs`
- `XASlave/Windows/UpdatesWindow.cs`

### User-Facing Impact Summary

- Users get more Game Mods in one place, with normal XA Mod save/restore, commands, and favourites support.
- Reloads should feel less blocked because the largest hook-backed restores happen after the plugin's core startup path finishes.
- The log should be easier to understand: expected hook arming is debug-level, scheduled work is called out, and completion is only reported after delayed arming is done.
- Friend-near alerts remain local notifications and do not type into game chat.
- Better Duty Finder buttons are easier to scan because the overlay is raised and split into two rows instead of one long strip.
- Xagman should be less fragile around target selection and focus state during patch windows or helper-plugin outages.

### Internal / Release-Workflow Notes

- The old splash-page `What's New` block is intentionally not restored; Update History remains the in-plugin release-note surface for XA Slave.
- The v0.0.0.31 release notes now cover every v0.0.0.31 changelog entry after the v0.0.0.30 release-note sync.
- The release notes intentionally describe XA-owned behavior only and do not require users to compare external plugin repositories.
- `Special Rendering Modes` world fade remains dependent on the local native fade helper being available; the rest of that panel remains usable when supported.
- The one-time login `Export Data` framework-tick warning observed during testing is separate from the plugin-load startup work and is not part of this release-note sync.

## Validation

- `dotnet build .\XASlave.sln -c Debug --no-restore` passes with 4 existing `ImGuiHelpers.GlobalScaleSafe` warnings and 0 errors.
- `dotnet build .\XASlave.sln -c Release --no-restore` passes with 4 existing `ImGuiHelpers.GlobalScaleSafe` warnings and 0 errors.
- Runtime reload validation confirmed the post-load completion marker now appears after the final incremental arming step.
- `git diff --check` passes; Git reports existing LF-to-CRLF normalization warnings for dirty files.

## Discord Notification

```text
<@&1482529652634419230>
> <:xas:1478469315081801919> **__[XA-Slave](<https://github.com/xa-io/XA-Slave>) v0.0.0.31__**
>
> New Game Mods: latency, combat window lock, friend alerts, cast bar, and duty finder tools
> Startup is smoother: heavier hook-backed mods now arm after core plugin load
> Update History and logs now show scheduled, still-arming, and complete startup states more clearly
> Xagman target/focus handling is now XA-owned and more patch-window resilient
> FreeShop item icons and Special Rendering Modes received compatibility fixes
```

## Files Modified

- `README.md`
- `changelog.txt`
- `docs/release-notes-v0.0.0.31.md`
- `XASlave/Configuration.cs`
- `XASlave/Data/ToonModsModels.cs`
- `XASlave/Plugin.cs`
- `XASlave/Services/AutoDisplayNetworkLatencyService.cs`
- `XASlave/Services/AutoLockGameWindowService.cs`
- `XASlave/Services/NotifyWhenFriendIsNearService.cs`
- `XASlave/Services/BetterCastBarService.cs`
- `XASlave/Services/BetterDutyFinderSettingsService.cs`
- `XASlave/Services/AutoSkipCutsceneService.cs`
- `XASlave/Services/SightDistanceService.cs`
- `XASlave/Services/SystemWindowModsService.cs`
- `XASlave/Services/EnableItemIconInShopsService.cs`
- `XASlave/Services/TargetCommandFixService.cs`
- `XASlave/Windows/Panels/CommandsPanel.cs`
- `XASlave/Windows/Panels/XAModsPanel.cs`
- `XASlave/Windows/SlaveWindow.cs`
- `XASlave/Windows/SlaveWindow.Debug.cs`
- `XASlave/Windows/Tasks/XagmanPanel.cs`
- `XASlave/Windows/UpdatesWindow.cs`
- `XASlave/XASlave.csproj`
- `XASlave/XASlave.json`
