# Changelog

## [0.4.0] - 2026-10-14

- **Widget Studio**: describe a widget and chat with the AI while it appears next to the conversation — live, at its real size, with your real data. Ask for changes until it's right, step back to earlier versions, pick a size or edit the JSON, then add it to the panel. Every draft's data is checked and problems come with a "fix this" button.
- **Liquid Glass**: the panel is now see-through glass over your blurred wallpaper, with bright edges, a soft sheen and a highlight that follows your pointer. Solid is still one click away in Settings › Look and feel.
- **Free-form widgets by default**: the AI designs each tile from rows, columns, text, icons, gauges, graphs, lists and buttons instead of a few fixed control types. New lists repeat a row for each item (forecast days, busiest apps, containers).
- **More built-in data, fewer broken widgets**: weather with a forecast (Open-Meteo, no key), network name/IP/live speed and the busiest apps join CPU, memory, disk, now playing and more. Scripts that feed tiles run on warm PowerShell hosts: faster, with no more timeouts from starting PowerShell every few seconds.
- **Every model, with logos**: the model picker lists all of each provider's models (live, with names, dates, context and prices from models.dev), searches across providers and shows each provider's and model maker's logo. Claude Code now offers every Claude model, not just three.
- **OpenCode's free models**: a new provider that uses the OpenCode app on this PC or your server — no account or key needed.
- **Run as administrator** (Settings › General): one Windows prompt at start, then widgets and the AI can change system settings without asking every time.
- **Calmer while working**: loading indicators keep a fixed size, values update in place and the panel no longer changes size when you click into the prompt — nothing jumps around.
- Fixed: switches in Settings (like "Download in the background") sometimes ignored clicks; whole rows are now clickable.

## [0.3.0] - 2026-10-14

- **Smoother open and close**: the panel grows out of the tray corner on one eased curve, and its shadow moves and fades with it instead of staying behind. Reopening mid-close turns around smoothly, the first frame never flashes the old panel, and the glass style lets Windows fade the blur away with the window.
- **Widgets with your own design**: the AI can now lay a tile out freely with rows, columns, text, icons, images, progress bars, gauges, history lines and buttons, styled with the panel's colours and type scale so it fits light and dark mode.
- **Live data for widgets**: CPU, memory, disk and uptime, what's playing (with cover art), the time, countdowns, ping, and values a widget remembers (counters, checklists). One script can feed a whole card by printing JSON.
- **Widgets that work the first time**: before saving, TileSmith runs the widget's queries and sends problems back to the AI, such as a toggle that can't tell on from off, a choice that never highlights, or a field that doesn't exist. The AI also starts from working examples close to your request.
- **Fix with AI**: when a tile fails, the notice offers to have the AI find the cause and repair it.

## [0.2.1] - 2026-10-11

- Projection gallery tile stays one full-width row. The laptop icon on "PC only" made that tile grow on PCs with an external display, which failed the 0.2.0 release tests.

## [0.2.0] - 2026-10-11

- **Pick any AI and model**: Claude Code, Cursor, Hermes Agent, the Anthropic, OpenAI, Gemini, OpenRouter, Groq, Mistral, xAI and DeepSeek APIs, Ollama, LM Studio, any OpenAI-compatible endpoint or a server command — with live model lists, a model picker right in the panel's header, recent picks and effort control for Claude.
- **Automatic fallback**: when the chosen AI can't be reached (offline, signed out, rate limited) the next ready one takes over and the panel says so.
- **Hermes on your server is found again**: scripts sent over SSH no longer break on Windows line endings, Hermes in Docker is detected with its model list, and an unchecked server is checked in the background.
- **Everything fits**: tiles are drawn from shared measurements that the AI is told about and the validator enforces; tiles grow instead of clipping, long text ends in "…" with a tooltip, and the panel no longer cuts off its right edge.
- **Hold to move**: press and hold a tile to lift it, drag it (the others spring aside, a placeholder shows where it lands, the board scrolls at the edges). Holding without moving opens the tile's menu — size, duplicate, edit JSON, remove.
- **Widget gallery**: 30 ready-made widgets (resolution, refresh rate, power mode, media, CPU, memory, server status…) added with one tap.
- **New Settings**: TileSmith's own design with an AI page (providers, keys, models, test), a Server page with Tailscale devices, live theme switching, accent swatches and Reduce motion. The Updates page no longer crashes.
- Buttons register reliably (edge clicks and slow clicks), springier motion throughout, prompt history (↑), shortcuts (Ctrl+K/G/E/M/,) and deep links (`--settings ai`, `--gallery`, `--prompt "…"`).

## [0.1.1] - 2026-10-11

- **Hermes in Docker**: TileSmith now finds Hermes Agent running in the official `nousresearch/hermes-agent` container and talks to it with `docker exec` — no setup needed.
- The server's AI engines are re-checked every time TileSmith connects, so a moved or reinstalled Hermes keeps working.
- If a Claude or Cursor sign-in fails, one tap switches to the AI on your server instead.
- Long agent sessions stay within Linux's argument-size limits.
- Settings and menus now use TileSmith's own colours and rounder shapes.
- Development builds no longer register themselves to start with Windows.

## [0.1.0] - 2026-10-11

First release.

- **Panel** in the taskbar corner: an Android-style sheet with a launcher grid of tiles — 1×1 icon tiles,
  2×1 Quick Settings tiles, full-width pill sliders and segmented bars, and cards for bigger widgets.
- **Rearrange by hand**: long-press a tile and drag it; drag the corner handle to resize; tap ✦ to change it with AI.
- **AI builds widgets for you**: describe what you want and the agent inspects your PC, writes and tests what it
  needs (PowerShell, files, admin through UAC) and drops the finished tile into the panel, with Undo.
- **Bring your own AI**: Hermes Agent or any OpenAI-compatible API on your server over SSH (great with Tailscale),
  or your Claude / Cursor subscription through their official CLIs.
- **Built-in actions** for display resolution & refresh rate, brightness, volume & mute, mic, dark mode, power mode,
  Wi-Fi & Bluetooth, media keys, shortcuts, apps, URLs, server commands and webhooks.
- **Automatic updates** from GitHub Releases with progress and one-click restart.
