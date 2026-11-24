# Miri — a pixel kitten that lives on your desktop

A pixel-art white kitten desktop assistant for Windows. It answers questions using a local model (Ollama) and can read and use other apps' interfaces, asking for approval before every action.

## Decisions
- **App control:** read + act **with per-action approval** (never autonomous)
- **Input:** typed chat bubble (no voice)
- **Language:** reply in the language of the question (Vietnamese or English)
- **Target hardware:** Ryzen 5 4600H, integrated Radeon only (no dGPU), 16 GB RAM → everything runs on CPU

## 1. What it looks like
- **Sprite:** 32×32 pixel art, shown 3–4× larger with nearest-neighbor scaling. Based on the reference photo: white fur with blue-grey shading, big round bright-green eyes with a white sparkle, pink nose, cheeks and inner ears, small "ω" smile.
- **Palette:**
  | Use | Hex |
  |---|---|
  | Fur | `#F4F4F2` |
  | Shading | `#C9CDD6` |
  | Outline | `#5A5E6B` |
  | Eyes (iris / rim / sparkle) | `#7ED957` / `#2F7A3B` / `#FFFFFF` |
  | Pupils | `#1E1E1E` |
  | Nose / ears | `#E8A9A6` |
- **Animations:** sit (blink, tail flick), walk, sleep curled up, sit on a window title bar, paw-point (to show a button), think ("…" bubble), slow blink (happy), head tilt (confused).
- **"Deaf cat" joke:** ignores system sounds and notifications, but perks up when you type or move the mouse near it. Cat fact it knows: white cats with blue eyes are much more likely to be born deaf.
- **Behaviour:** walks along the top of the taskbar and window title bars, falls when a window moves out from under it, can be dragged, naps when you're idle. Clicks pass through the transparent space around the kitten.
- **Ledges inside apps:** drop the kitten on something big inside an app (a YouTube video player, an image, a panel) and she falls onto its bottom edge and roams along it. Found with UI Automation under the drop point; she rides along when it moves or resizes and falls off when it scrolls away, closes, minimizes or gets covered.
- **Reaches what she presses:** before every click or typing step (any app) she walks or does a real ballistic jump (crouch, rise, fall, landing squash) onto the target's top edge, then taps with the right pose: reach down, reach up or point sideways. The executor waits until she has landed. Her chat bubble flips below her when there is no room above. Apps open the way a person would: the pinned taskbar icon, else Win + type the name + Enter (types only once Start is really in front), else a direct launch.
- **Kitten things:** stroke the mouse back and forth over her to pet her (she purrs with hearts, even wakes up purring); tray "Feed Miri" (she eats from a bowl, then is full for 2 minutes). **Teasing** (tray "Let Miri tease me", on by default): every 15–40 min, only after you paused input 4 s–10 min, never fullscreen/elevated/while chatting, she paws your Chromium tab strip and presses Ctrl+Tab, or switches you to another app, then looks smug. Never types, clicks content or closes anything.
- **Weather:** "what's the weather (in Hanoi)?" / "thời tiết ở Đà Nẵng?" answers from Open-Meteo (free, no key): the named city, else Windows location rounded to ~10 km (tray "Use my location for weather"), else settings `WeatherCity`. Answered without the LLM, so it is instant.
- **Perches on more edges:** dropped on or just above a visible top edge (toolbar, card, text box, image) she stands on top of it; otherwise on the bottom edge of the panel she is inside (now down to 60 px tall, 1.3× her width).

## 2. Tech stack
| Part | Choice | Why |
|---|---|---|
| Desktop app | **C# .NET 8, WPF** | Transparent always-on-top windows, smooth sprite animation, native to Windows |
| Reading and using other apps | **FlaUI (Windows UI Automation, UIA3)** | Element names, types and positions as text, so the small model doesn't need screenshots |
| Fallback for apps UIA can't read (games, canvas apps) | **Windows built-in OCR** (`Windows.Media.Ocr`) | Offline; supports Vietnamese if the language pack is installed |
| Brain (default) | **Ollama + `qwen3.5:4b`** | Text + vision + tools in one model, 3.3 GB. Fastest chat streaming |
| Brain (alternate) | **`gemma4:e4b`** | Text + vision + tools, 6.6 GB. ~2.5× faster at tool-call decisions; switch from the tray menu |
| Brain (fallback) | `qwen3:4b` | Text + tools only; used if neither of the above is available |
| Knowledge search | Keyword search over Markdown cards (v1); `bge-m3` embeddings later | Multilingual retrieval, diacritics-insensitive |
| Screen vision | **Auto-switch to `gemma4:e4b`** (`VisionModel` setting) whenever a request needs a screenshot | Visual questions ("what's in this picture", "màu gì") or windows UIA can't read; screenshots only the target window. ~19 s warm, ~55 s if gemma has to load first |

### Model benchmark (2026-11-23, this laptop, `bench/`)
| | qwen3:4b | qwen3.5:4b | gemma4:e4b |
|---|---|---|---|
| Language match (EN/VI) | 4/4 | 4/4 | 4/4 |
| Tool calls | 5/5 | 5/5 | 5/5 |
| Vision (dialog buttons) | n/a | 3/3 (7.6 s) | 3/3 (4.9 s) |
| Chat speed | ~11.5 tok/s, ~45 s/reply | ~9.3 tok/s, 12–18 s/reply | ~7 tok/s, 12–23 s/reply |
| Tool decision time | 20–41 s | ~13 s | ~5 s |

Small models still make factual slips (e.g. reversed grind advice), so the persona prompt tells the model to follow the knowledge cards exactly.

## 3. How the brain works
```
You type → intent check (fast) ─┬─ Question → knowledge + memory lookup → answer
                                └─ Task on screen → agent loop (section 4)
```
- **Personality prompt:** Miri is a calm, slightly grumpy but helpful white cat; short answers; replies in the language you used.
- **Knowledge cards** (Markdown in `knowledge/`) to keep a small model from making things up:
  - *Coffee:* Vietnamese phin filter, pour-over, French press, moka pot, espresso — ratios, grind size, water temperature, cà phê sữa đá, cold brew.
  - *Adopting a kitten:* temperament vs. home and schedule, long- vs short-haired breeds, allergies, costs, shelter adoption checklist, health checks (including hearing tests for white cats).
- **Memory:** user preferences in SQLite (e.g. "likes strong phin coffee with condensed milk", "lives in an apartment, works from home"). Answers adapt to them; Miri asks a short follow-up when it lacks info.

## 4. Using other apps (read, then act with approval)
**Agent loop:** snapshot the active window's UI tree → trim to ~60 visible, usable elements with short IDs → model picks a tool → **user approves** → execute → re-snapshot to verify → repeat (max ~8 steps).

**Tools:** `list_windows`, `focus_window`, `get_ui(window)`, `read_text(id)`, `click(id)`, `type_text(id, text)`, `press_keys(combo)`, `scroll(id)`, `open_app(name)`

**Approval card:** Miri walks to the target, points at it, highlights it, and asks e.g. *"Click **Send** in Telegram?" [Yes] [No] [Yes to all for this task]*.

**Safety rules:**
- Never type into password fields (UIA `IsPassword`).
- Block list: banking apps, password managers, plus user-added apps.
- Words like send / delete / buy / pay / post → red warning; never covered by "yes to all".
- Global stop hotkey (e.g. `Ctrl+Alt+M`) and a log of every action.
- Windows (UIPI) blocks a normal app from controlling elevated/admin apps — accepted as a built-in safety limit.

## 5. Project layout
```
Miri/
  src/Miri.App      WPF: pet window, speech bubble, tray icon, settings
  src/Miri.Pet      sprite animation, behaviour state machine, walking on windows
  src/Miri.Brain    Ollama client, prompts, intent check, knowledge search, memory
  src/Miri.Hands    FlaUI snapshots, OCR, actions, approval and safety rules
  assets/sprites    sprite sheets (.png + frame data)
  knowledge/        coffee/*.md, cats/*.md
```

## 6. Build phases
1. **Kitten on the desktop** — sprite sheet, transparent click-through window, sit/walk/sleep, dragging, walking on the taskbar, tray icon.
2. **Chat** — speech bubble, hotkey to open it, streaming Ollama replies, personality, language matching.
3. **Knowledge and memory** — coffee and kitten cards, embedding search, saved preferences.
4. **Eyes** — read the active window; answer "what does this button do?" by pointing at it.
5. **Hands** — actions, approval cards, safety rules, stop hotkey, action log.
6. **Polish** — OCR fallback, optional vision model, settings window, start with Windows, installer.

## Risks
- **Small-model reliability on multi-step tasks** — mitigated by short tasks, element IDs instead of coordinates, verification after each step, and approval on every action.
- **Apps without UI Automation** (games, custom-drawn UIs) — fall back to OCR, then optional vision model.
- **RAM budget** — models load only when you ask something (no preload at startup) and unload 15 s after Miri answers (`ModelKeepAlive`); gemma4:e4b unloads right after each picture question, and Miri never keeps two models loaded at once. Idle RAM use is 0 GB for models. Cost: each question after a pause waits for qwen to load (~5–15 s extra).
