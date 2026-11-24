Miri - pixel kitten assistant

Install:  extract this whole zip, then double-click Install.cmd.
          No admin rights needed. Installs to %LOCALAPPDATA%\Programs\Miri,
          adds Start Menu + Desktop shortcuts, and offers to install Ollama
          and download the models (qwen3.5:4b, gemma4:e4b) Miri needs.
Needs:    Windows 10/11 64-bit, ~8 GB RAM, ~10 GB free disk for the models.
Remove:   Settings > Apps > Miri, or Start Menu > Miri > Uninstall Miri.

Privacy:  Miri runs entirely on this PC. She reads the window you ask her to work in
          (through Windows UI Automation) and sends it only to Ollama on 127.0.0.1.
          The one exception is weather: when you ask about it, she sends the
          city you named, or your rough location (rounded to ~10 km, only if
          Windows location is allowed for desktop apps), to Open-Meteo.com
          (free, no account). Turn location off: tray > "Use my location for
          weather". Weather data by Open-Meteo.com, CC BY 4.0. She keeps a local log of what she clicked
          and typed in %LOCALAPPDATA%\Miri\actions.log; delete it any time.
Teasing:  By default Miri sometimes switches your window or browser tab as a joke
          while you are idle. Turn it off: tray icon > "Let Miri tease me".
Licenses: Miri is MIT (Miri\LICENSE); bundled parts in THIRD-PARTY-NOTICES.txt.
          Full privacy notice: PRIVACY.txt.
