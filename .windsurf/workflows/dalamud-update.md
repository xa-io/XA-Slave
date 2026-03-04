---
description: Research Dalamud/FFXIV patch changes and verify plugin compatibility
---

# Dalamud Update Research Workflow

When the user triggers this workflow, they should provide one or more of the following:
- The new game patch version (e.g. "7.2", "7.25 hotfix")
- The new Dalamud API level (e.g. "API 15")
- Any specific error messages or build failures they've encountered
- A link to the Dalamud changelog or "What's New" page if available

If the user doesn't provide these, ask them before proceeding.

## Steps

### 1. Read the version comparison guide
Read `docs/How To Compare Dalamud Versions.md` in the XA Slave project for the full comparison workflow and checklist.

### 2. Read the current API baseline
Read `docs/dalamud.api.list.md` and `docs/dalamud.api.md` in the XA Slave project. These are our snapshots of the current working API surface.

### 3. Fetch the Dalamud "What's New" page
If the user provided a URL, read it. Otherwise, attempt to read:
- `https://dalamud.dev/versions/` — summary of breaking changes per API level

### 4. Fetch FFXIVClientStructs breaking changes
Attempt to read:
- `https://ffxiv.wildwolf.dev/docs/breaking/` — per-patch struct changes

### 5. Scan our codebase for affected APIs
Search across both plugin projects for every unsafe/native call pattern:
- Search for: `unsafe`, `Instance()`, `HookFromSignature`, `AtkStage`, `AtkUnitBase`, `FireCallback`, `ProcessChatBoxEntry`, `AgentModule`, `InfoProxy`, `PlayerState`, `ActionManager`, `TargetSystem`
- Cross-reference each hit against the breaking changes identified in steps 3-4

Projects to scan:
- `c:\Users\Fuji\Desktop\Projects\python\FFXIV - XA Database\`
- `c:\Users\Fuji\Desktop\Projects\python\FFXIV - XA Slave\`

### 6. Check .csproj for package versions
Read both `.csproj` files and compare NuGet package versions against the latest available:
- `Dalamud.NET.Sdk`
- `Microsoft.Data.Sqlite`
- Any other NuGet references

### 7. Attempt a build (if user asks)
If the user wants to verify, suggest:
```
dotnet build -c Release
```
Then analyze any compiler errors (CS0618 = deprecated, CS0246 = missing type, etc.)

### 8. Generate an impact report
Produce a structured report:

```
## Dalamud Update Impact Report — [Date]

### Version Changes
- Game Patch: X.Y → X.Z
- Dalamud API Level: X → Y
- SDK Version: X.X.X → Y.Y.Y

### Breaking Changes Affecting Us
- [struct/service]: [what changed] → [which file(s) affected]

### Non-Breaking Changes of Interest
- [new APIs, deprecations to address proactively]

### Required Code Changes
- [ ] [file]: [specific change needed]

### IPC Dependencies to Verify
- [ ] AutoRetainer, Lifestream, vnavmesh, etc.

### Recommended Actions
1. [ordered list of fixes]
```

### 9. Apply fixes (if user approves)
After the user reviews the impact report, apply code changes as directed. Follow standard backup rules before modifying any file.

### 10. Update documentation
After fixes are confirmed working:
- Update `docs/dalamud.api.list.md` with any changed APIs
- Update `docs/dalamud.api.md` if our usage patterns changed
- Add a new log entry to `docs/How To Compare Dalamud Versions.md` Section 7
- Update `DalamudApiLevel` in plugin manifest `.json` files if API level changed
- Update `PluginRules.md` Section 12 if new version update patterns were discovered
