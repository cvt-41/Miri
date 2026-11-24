# Miri privacy notice

*Last updated: 2026-11-25*

Miri is a free, open-source desktop pet. **The developer collects nothing.** There are no accounts, analytics, crash reports or ads, and no data is ever sent to the developer.

## What stays on your PC

- **Your chats** with Miri and the short notes she remembers about you (SQLite, `%LOCALAPPDATA%\Miri`).
- **What she sees:** when you ask her to look at or use an app, she reads that window through Windows UI Automation (and takes a screenshot for "what's on my screen?"). This goes only to the AI model running locally in Ollama (`127.0.0.1`), and is never uploaded.
- **Action log:** `%LOCALAPPDATA%\Miri\actions.log` records what she clicked and typed, so you can check her work. Password fields are never read or typed into.
- **Settings:** `%LOCALAPPDATA%\Miri\settings.json`.

To delete everything, uninstall Miri and delete the `%LOCALAPPDATA%\Miri` folder.

## What leaves your PC: weather

Only when you ask about the weather, Miri asks [Open-Meteo](https://open-meteo.com) (a Swiss weather service, free, no account) for the forecast. She sends:
- the city you named, **or**
- your approximate location, rounded to about 10 km. She does this only if Windows location is allowed for desktop apps and the tray option **"Use my location for weather"** is on.

Like any web request, this shows Open-Meteo your IP address. See [Open-Meteo's terms and privacy](https://open-meteo.com/en/terms). Turn location off in the tray menu, or set a fixed `WeatherCity` in settings.

## Update check

Once a day when Miri starts (and when you click tray → **Check for updates…**), she reads the public list of Miri releases on GitHub (`api.github.com`). Nothing about you is sent beyond what any web request carries (your IP address, seen by GitHub; see [GitHub's privacy statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement)). She never downloads or installs anything by herself. Turn the daily check off by setting `"CheckForUpdates": false` in settings.

## Third-party software you install yourself

The installer can install **Ollama** and download AI models. They run locally under their own terms ([Ollama](https://ollama.com)). Miri doesn't change their settings.

## Children

Miri isn't aimed at children and collects no data from anyone.

## Changes and contact

Changes to this notice appear in this file's history on GitHub. Questions: open an issue on the project's GitHub page.
