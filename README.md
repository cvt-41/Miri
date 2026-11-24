<p align="center">
  <img src="docs/miri-purr.png" width="200" alt="Miri, a white pixel kitten, purring with her eyes closed while pink hearts float up">
</p>

<h1 align="center">Miri</h1>

<p align="center"><i>Pet her. She purrs. She clicks things for you.</i></p>

---

A tiny pixel kitten who lives on your Windows desktop. She walks along your taskbar and window title bars, naps, gets petted, eats, teases you now and then, and when you ask, she chats and works your apps for you. Her brain runs **entirely on your PC** through [Ollama](https://ollama.com), so no account and no cloud AI. (This app is just a habit, meant no serious effort, just enjoy it and send your feedback if you feels interested)

## What she does

| | |
|---|---|
| **Chat** | Click her, or press **Ctrl+Alt+K**, and type. She replies in your language (English or Vietnamese). |
| **Use your apps** | "Play Lofi Rain in Sonora", "open Notepad and type hello". She opens the app from its taskbar icon (or Start search), hops onto each button before pressing it, and asks before anything risky. **Ctrl+Alt+M** stops her. |
| **Look at your screen** | "What's on my screen?", "explain this error". |
| **Weather** | "What's the weather?", "thời tiết ở Đà Nẵng?". She uses the city you name, else your rough location. |
| **Pet her** | Rub the mouse back and forth over her (no click). She purrs, with hearts. |
| **Feed her** | Tray icon → **Feed Miri**. |
| **Pick her up** | Drag her. Drop her on a window title bar, a toolbar, a video player or a text box and she perches on its edge. |
| **Pick her brain** | On first start she suggests a chat model and a vision model that fit your PC (tray → **Choose models…** to change later). She needs both: one to answer, one to see your screen. |
| **Teasing** | Every so often, while you're idle, she switches your browser tab or app, then looks smug. Tray → **Let Miri tease me** turns it off. |

She never touches password fields or password managers, and never types anything that looks like a secret.

## Install (on any Windows 10/11 PC)

1. Get `Miri-Setup.zip` (see *Build* below) and extract **the whole zip**.
2. Double-click **Install.cmd**. No admin rights are needed.
3. Say yes when it offers to install Ollama and download her models (`qwen3.5:4b`, `gemma4:e4b`, several GB).

You need 64-bit Windows, about 8 GB RAM and about 10 GB of free disk space. To remove her: Settings → Apps → Miri, or Start → Miri → Uninstall Miri.

## Build

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet run --project src/Miri.App          # run from source
powershell -File installer/build.ps1       # make dist/Miri-Setup.zip
```

Code layout: `Miri.Pet` (sprite, physics, perching), `Miri.Brain` (Ollama chat, routing, weather, memory), `Miri.Hands` (UI Automation: reading and clicking apps, safety rules), `Miri.App` (WPF windows, tray, wiring). More detail in [PLAN.md](PLAN.md) and [docs/SPEC.md](docs/SPEC.md).

## Settings

`%LOCALAPPDATA%\Miri\settings.json`: models, hotkeys, sprite scale, `MusicApp`, `Mischief`, `WeatherUseLocation`, `WeatherCity`, `BlockedApps`. Restart Miri after editing it. Her action log (`actions.log`) and memory live in the same folder.

## Privacy

Everything stays on your PC except one thing: when you ask about the weather, she sends the city you named, or your location rounded to about 10 km (only if Windows location is allowed for desktop apps), to [Open-Meteo](https://open-meteo.com). Tray → **Use my location for weather** turns that off. Full details: [PRIVACY.md](PRIVACY.md).

## Credits & license

Miri is [MIT licensed](LICENSE). Weather data by [Open-Meteo.com](https://open-meteo.com) (CC BY 4.0). Bundled libraries are listed in [installer/THIRD-PARTY-NOTICES.txt](installer/THIRD-PARTY-NOTICES.txt). The AI models are downloaded by you under their own licenses (Qwen license, Gemma Terms of Use).
