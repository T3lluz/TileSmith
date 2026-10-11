# Changelog

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
