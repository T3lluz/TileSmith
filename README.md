<p align="center"><img src="src/TileSmith/Assets/AppIcon.png" width="96" alt="TileSmith icon"></p>

<h1 align="center">TileSmith</h1>

<p align="center"><b>Quick settings for Windows that your AI builds for you.</b><br>
A Liquid Glass panel in the taskbar corner — and Widget Studio, where you describe a widget, watch it appear
with your real data, and keep chatting until it's exactly right.</p>

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
- **Press and hold** a tile to lift it, then drag it where you want it; hold without moving for its menu
  (size, duplicate, edit JSON, remove). In edit mode (pencil) the **corner handle** resizes and **✦** changes a tile with AI.
- **+ Add widget** opens a gallery of ready-made tiles; the chip in the header picks the AI and model.
- Shortcuts: Ctrl+K ask the AI · Ctrl+G gallery · Ctrl+E edit · Ctrl+M model · Ctrl+, settings · ↑ previous prompts.
- **Ask for anything** in the bar at the bottom — it opens **Widget Studio**. Try:
  - *“Weather where I live with the next few days”* · *“A dashboard with CPU, memory and disk gauges”*
  - *“Switch my laptop display between 2560×1440 and 1920×1200”* · *“A toggle for my VPN”*
  - *“What's playing, with the cover art”* · *“The five apps using the most CPU”*

## Widget Studio

Chat on the left, your widget on the right — live, at its real size, with its real data (taps in the preview
run for real). The AI looks at your PC, writes and tests whatever it needs, and before every draft TileSmith
runs the widget's queries and checks that the tile can read them. Then:

- **Keep chatting** — “make it more compact”, “add a graph”, “use green when it's online” — the AI changes only what you ask.
- **Step back** to any earlier version (v1, v2…), pick another size, or edit the JSON.
- **Add to panel** when you like it. Tiles that fail later offer **Fix with AI**, which hands the error back to the Studio.

The AI designs each tile freely from rows, columns, text, icons, images, gauges, progress bars, history lines,
lists and buttons — within styling rules (colour tokens, a type scale, spacing) that keep everything native in
light and dark mode. Live data comes from built-ins (CPU, memory, disk, network speed, busiest apps, weather,
what's playing, battery, countdowns, ping, values a widget remembers) or any script that prints JSON.

## Connect your AI

Open **Settings › AI** and pick a provider — each shows whether it's ready, and its live model list:

| Engine | How it connects |
|---|---|
| **OpenCode** (free models) | The official `opencode` CLI on this PC or your server — free models without an account; sign in to OpenCode Zen for more |
| **Hermes Agent** (Nous Research) | SSH to your server, runs `hermes chat --query-file …` |
| **OpenAI-compatible API** | Hermes API server, Ollama, vLLM, LM Studio, OpenRouter… — through an SSH tunnel or directly |
| **Custom command** | Any CLI on your server that reads a prompt (`claude -p`, `ollama run …`, `llm`) |
| **Claude** (your subscription) | The official Claude Code CLI on this PC — sign in with `claude auth login` |
| **Cursor** (your subscription) | The official Cursor Agent CLI on this PC — sign in with `cursor-agent login` |
| **Anthropic, OpenAI, Gemini, OpenRouter, Groq, Mistral, xAI, DeepSeek** | Your API key, stored encrypted with DPAPI |
| **Ollama, LM Studio** | Models running on this PC |

The model picker (the chip in the panel's header or the Studio) shows every model each provider offers — live
from the provider, with names, release dates, context sizes and prices from [models.dev](https://models.dev) —
and searches across all of them. If the chosen AI can't be reached, TileSmith falls back to the next ready one
(Settings › AI › Behaviour).

**Run as administrator** (Settings › General) starts TileSmith elevated — Windows asks once — so widgets and the
AI can change system settings without a prompt every time.

For a server: type `you@hostname` (or pick it from your **Tailscale** tailnet), enter the password and press
**Connect**, then **Detect** to find the AI tools there. *Sign in with a key instead* swaps the password for a
dedicated SSH key. Passwords and API keys are encrypted with Windows (DPAPI) for your account only, and
server host keys are pinned on first connect.

## How it works

Widgets are small JSON documents (stored in `%AppData%\TileSmith\widgets.json`): a **layout** of elements fed by
live **data**, or for simple switches a single control (`toggle`, `slider`, `choice`, `button`, `status`), all bound
to **actions**. Built-in actions cover display resolution and refresh rate, brightness, volume and microphone, dark
mode, power mode, Wi-Fi and Bluetooth, media keys and now playing, system stats, network, weather, shortcuts, apps,
URLs, commands on your server and HTTP webhooks; anything else runs as a PowerShell script (queries on two warm
PowerShell hosts, so tiles that refresh every few seconds stay fast). The model never touches your PC directly:
TileSmith runs the agent's tools locally and sends the results back.

```
src/TileSmith.Core   widget model & validator, actions (Win32 display/audio/power…), agent loop, AI backends, SSH
src/TileSmith        WinUI 3 app: tray icon, Liquid Glass panel, tiles, Widget Studio, settings, Velopack updates
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
