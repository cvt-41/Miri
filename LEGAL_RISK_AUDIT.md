# Legal Risk Audit: Miri
*2026-11-24 · Examined: code + installer (no website exists) · Assumed users: you, plus friends you hand the installer to*
> Technical review, not legal advice. Have a lawyer confirm before relying on it.

## Summary
Miri is a local-only desktop app: no website, no trackers, no accounts, no payments, and the AI runs on 127.0.0.1 through Ollama. None of the big lawsuit waves (wiretap/session replay, pixels, cookie banners, VPPA, BIPA) apply. The real gaps were about **redistribution**: shipping other people's code without their license notices, and an installer that accepted license terms on the user's behalf. 0 Critical · 0 High · 2 Medium (fixed) · 3 Low.

**Fix first:** 1. Ship license notices (done) 2. Stop auto-accepting winget terms (done) 3. Add a LICENSE to the GitHub repo (your choice, see L-03).

## Findings
### [MEDIUM] L-01: Installer ships open-source code without its license notices (FIXED)
- **Where:** `dist/Miri` bundles the .NET runtime, FlaUI, Microsoft.Data.Sqlite and SQLitePCLRaw.
- **What happens:** MIT and Apache-2.0 allow redistribution only if the copyright and license notice travel with the binaries. The zip had none.
- **Why it matters:** Distributing without the notice falls outside the license, which is technically copyright infringement. Enforcement against a hobby app is rare, but the fix costs nothing.
- **Fix:** added `installer/THIRD-PARTY-NOTICES.txt` and `install.ps1` now copies it into the install folder.
- **Effort:** S

### [MEDIUM] L-02: Installer accepted Ollama's license agreement for the user (FIXED)
- **Where:** `installer/install.ps1:44`, `--accept-source-agreements --accept-package-agreements`.
- **What happens:** The user answered "install Ollama? y" and the script silently agreed to the package terms for them.
- **Why it matters:** An agreement the user never saw is weak for them and puts you in the position of having consented on someone else's behalf.
- **Fix:** removed both flags, so winget shows the terms and asks. The model download step now tells the user they're agreeing to the Qwen license and Gemma Terms of Use.
- **Effort:** S

### [LOW] L-03: The public GitHub repo had no LICENSE (FIXED: MIT added)
- **Where:** repo root.
- **What happens:** Without a license, nobody else may legally reuse your code. That protects you more than it exposes you, but it also leaves you without the "AS IS, no warranty" disclaimer that limits liability if Miri's clicking breaks something on someone's PC.
- **Fix:** add a LICENSE (MIT is the common choice, or "All rights reserved" plus a warranty disclaimer). Not done because it's your call.

### [LOW] L-04: Teasing changes windows on someone else's PC by default
- **Where:** `src/Miri.App/Mischief.cs`, `Settings.Mischief = true`.
- **What happens:** On a friend's PC, Miri switches windows or tabs unprompted. It's gated on 4+ s idle, so data loss is unlikely, but it's surprise behavior.
- **Fix applied:** disclosed in `installer/README.txt` with how to turn it off. Consider defaulting it to off for installer builds (not changed: it alters behavior you asked for).

### [LOW] L-05: Local action log stores typed text and window titles
- **Where:** `src/Miri.Hands/ActionLog.cs:50`, written to `%LOCALAPPDATA%\Miri\actions.log`.
- **What happens:** Stays on the PC; password fields and secret-looking text are already blocked (`SafetyPolicy.cs:51,71`). Risk only if someone shares the log.
- **Fix applied:** disclosed in the README.

### [LOW] L-06: Weather lookups send a location to Open-Meteo (added 2026-11-24, mitigated)
- **Where:** `src/Miri.Brain/Weather.cs`.
- **What happens:** Only when the user asks about the weather, Miri sends Open-Meteo either the city they named or Windows location rounded to 0.1° (~10 km). Their IP is visible to Open-Meteo, as with any web request. Nothing is stored.
- **Why it's acceptable:** The lookup is started by the user, minimized, and gated by Windows' own location permission. It's disclosed in the README and can be turned off from the tray ("Use my location for weather"). Open-Meteo is a Swiss/EU provider with no account or tracking, and its free API allows non-commercial use with CC BY 4.0 attribution, which every weather reply includes ("weather data: Open-Meteo.com").
- **Watch:** if Miri is ever sold or used commercially, the free Open-Meteo tier no longer covers it. You'd need their paid API plan.

## Checked and cleared
- **AI / data flow:** all LLM calls go to `http://127.0.0.1:11434` (`Settings.cs:9`); no screen content leaves the PC. The only internet call is the weather lookup (L-06). Not an AI-Act transparency issue: she's obviously an AI kitten.
- **Trackers, cookies, consent, GPC, VPPA:** none; there's no website. The scanner's "video" and "health" hits are the word *player* and .NET dependency names.
- **Passwords/cards:** password fields are blocked, password managers are on the blocklist (`Settings.cs:21`), and the "card" hit is the prompt telling the model *never* to type card numbers.
- **Models:** pulled by the user from Ollama, not redistributed by you, so their license terms bind the downloader (now disclosed).
- **Sprite art:** original pixel art generated in code.
- **`graphify-out/graph.html`** loads from unpkg: a local dev file, gitignored, never shipped.

## Couldn't verify
- Whether you'll publish Miri more widely (a store or a website). That would bring in privacy-policy and accessibility duties.
- `dist/Miri-Setup.zip` is now stale: rebuild it to include the new notices and installer changes.
