# XA Slave

A Dalamud plugin for FINAL FANTASY XIV that automates login tasks, UI window navigation, and scheduled data collection. Works alongside **XA Database** to collect data hands-free.

## Key Features

- **Automated Data Collection** — Opens saddlebag and FC windows, navigates tabs, and triggers saves automatically
- **Auto-Collection on Login** — Runs collection after login with configurable delay (3–30 seconds)
- **IPC Integration** — Communicates with XA Database via IPC to trigger saves and refreshes
- **Configurable Tasks** — Toggle saddlebag collection, FC window collection, and login automation independently

## Commands

| Command | Description |
|---------|-------------|
| `/xaslave` | Toggle the XA Slave window |

## Dependencies

- **Required:** [XA Database](https://github.com/xa-io/XA-Database) — Must be loaded for IPC communication

## This Plugin is in Development

This means that there are still features being implemented and enhanced. Suggestions and feature requests are welcome via GitHub Issues.

## Installation

1. Install [FFXIVQuickLauncher](https://github.com/goatcorp/FFXIVQuickLauncher) and enable Dalamud in its settings. You must run the game through FFXIVQuickLauncher for plugins to work.
2. Open Dalamud settings by typing `/xlsettings` in game chat.
3. Go to the **Experimental** tab.
4. In the **Custom Plugin Repositories** section, paste the following URL:

```
https://raw.githubusercontent.com/xa-io/MyDalamudPlugins/master/pluginmaster.json
```

5. Click **Save**.
6. Open the plugin installer with `/xlplugins`, go to **All Plugins**, and search for **XA Slave**.

## Architecture

```
XA Slave                    XA Database
┌──────────┐   IPC calls    ┌──────────┐
│ Collect  │ ────────────►  │  Save    │
│ Navigate │  Save/Refresh  │  Persist │
│ Automate │                │  Query   │
└──────────┘                └──────────┘
```

- **XA Slave** handles all automation (opening windows, navigating tabs, scheduling)
- **XA Database** handles all data collection and persistence

## Building from Source

```
git clone https://github.com/xa-io/XA-Slave.git
cd XA-Slave
dotnet build XASlave.sln -c Release
```

Output: `XASlave/bin/x64/Release/XASlave.dll`

## License

[AGPL-3.0-or-later](LICENSE)
