<p align="center"><img src="src/TileSmith/Assets/AppIcon.png" width="96" alt="TileSmith icon"></p>

<h1 align="center">TileSmith</h1>

<p align="center"><b>Quick settings for Windows that your AI builds for you.</b><br>
A panel in the taskbar corner with Android-style tiles — and an agent that turns
“switch my laptop between 2560×1440 and 1920×1200” into a working tile in seconds.</p>

<p align="center"><img src="assets/panel.png" width="560" alt="The TileSmith panel"></p>

## Install

1. Download **`TileSmith-win-Setup.exe`** from the [latest release](https://github.com/T3lluz/TileSmith/releases/latest).
2. Run it. TileSmith installs for your user (no admin needed) and appears next to the clock.
   Windows SmartScreen may warn because the app isn't code-signed yet — choose *More info › Run anyway*.
3. Click the icon (or press **Win + Shift + Q**) to open the panel.

TileSmith keeps itself up to date: when a new version is out you'll see it in the panel, the tray icon's
dot turns green, and one click installs it.

## Use it

- **Tap** tiles to toggle, **drag** sliders, pick options in segmented bars.
- **Long-press** any tile to rearrange — drag it where you want it. The **corner handle** resizes
  (1×1, 2×1, 2×2, 4×1, 4×2). **✦** changes a tile with AI, **✕** removes it. Right-click for JSON editing.
- **Ask for anything** in the bar at the bottom. Try:
  - *“Build a widget where I can switch my laptop display between 2560×1440 and 1920×1200”*
  - *“A toggle for my VPN”* · *“Restart my Plex container on the server”* · *“Media controls”*

The agent looks at your PC, writes and tests whatever it needs (PowerShell scripts, files — with a UAC
prompt for anything that needs admin), then drops the finished tile into the panel. Every step is shown
live; **Stop** and **Undo** are always there. Prefer to approve each command? Settings › AI & server ›
*What the agent may do*.

## Connect your AI

Open **Settings › AI & server** and pick one:

| Engine | How it connects |
|---|---|
| **Hermes Agent** (Nous Research) | SSH to your server, runs `hermes chat --query-file …` |
| **OpenAI-compatible API** | Hermes API server, Ollama, vLLM, LM Studio, OpenRouter… — through an SSH tunnel or directly |
| **Custom command** | Any CLI on your server that reads a prompt (`claude -p`, `ollama run …`, `llm`) |
| **Claude** (your subscription) | The official Claude Code CLI on this PC — sign in with `claude auth login` |
| **Cursor** (your subscription) | The official Cursor Agent CLI on this PC — sign in with `cursor-agent login` |

For a server: type `you@hostname` (or pick it from your **Tailscale** tailnet), enter the password and press
**Connect**, then **Detect** to find the AI tools there. *Sign in with a key instead* swaps the password for a
dedicated SSH key. Passwords and API keys are encrypted with Windows (DPAPI) for your account only, and
server host keys are pinned on first connect.

## How it works

Widgets are small JSON documents (stored in `%AppData%\TileSmith\widgets.json`) made of controls —
`button`, `toggle`, `choice`, `slider`, `status` — bound to **actions**. Built-in actions cover display
resolution and refresh rate, brightness, volume and microphone, dark mode, power mode, Wi-Fi and Bluetooth,
media keys, shortcuts, apps, URLs, commands on your server and HTTP webhooks; anything else runs as a
PowerShell script. The model never touches your PC directly: TileSmith runs the agent's tools locally and
sends the results back.

```
src/TileSmith.Core   widget model & validator, actions (Win32 display/audio/power…), agent loop, AI backends, SSH
src/TileSmith        WinUI 3 app: tray icon, panel, tiles, settings, Velopack updates
tests/               unit tests incl. an end-to-end agent run against a scripted model
```

## Build from source

Requires Windows 10 1809+ and the .NET 10 SDK.

```bash
dotnet test --project tests/TileSmith.Core.Tests
```

```bash
dotnet run --project src/TileSmith -p:Platform=x64
```

Releasing: add a section to `CHANGELOG.md`, then push a tag like `v0.2.0`. The
[Release workflow](.github/workflows/release.yml) builds, packages with Velopack and publishes the
GitHub release that installed copies update from.

## License

MIT — see [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
