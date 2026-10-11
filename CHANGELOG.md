# Changelog

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
