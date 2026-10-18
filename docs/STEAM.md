# INKWAVE — Steam integration

The desktop build (Electron) talks to the Steamworks SDK. The web build never does — it is a normal web page.
Everything is optional: with no SDK installed the game runs exactly as before, just without achievements, cloud
saves and rich presence.

```
browser page  ──window.inkwaveSteam──▶  preload  ──ipc──▶  electron/steam.cjs  ──▶  Steamworks SDK
     ▲                                                                    │
     └────────────────── src/net/steam.js (achievements, presence, cloud) ◀─┘
```

| file | what |
|---|---|
| `electron/steam.cjs` | the only place that touches the SDK. Loads a binding, resolves the app id, exposes the API over IPC |
| `electron/preload.cjs` | exposes `window.inkwaveSteam` to the page (the web build never sees it) |
| `src/net/steam.js` | the game side: the achievement catalogue, the event wiring, rich presence, cloud sync |
| `src/main.js` | calls into it: `steam.init()` at boot, `steam.matchResult()` at the end of a match |
| `build/verify-steam.cjs` | `npm run steam:verify` — says what is missing |
| `build/prepare-steam.cjs` | writes the real app id into `steam_appid.txt` before packaging |
| `build/make-icon.cjs` | derives `icon.ico` / `icon.png` from the macOS `.icns` (Windows + Linux icons) |
| `tools/steam-test.mjs` | `npm run steam-test` — the achievement logic against a fake bridge |

---

## 1. Setup

### 1.1 The binding
It is already an optional dependency:

```bash
npm install        # installs steamworks-ffi-node
```

If the install fails (no prebuilt binary for your platform) the game still runs — Steam features stay off.

### 1.2 The Steamworks SDK redistributables
Valve's SDK is **not** in this repository (their Access Agreement). Download it from
[partner.steamgames.com](https://partner.steamgames.com/sdk/steamworks_sdk.zip) and copy **only** the
`redistributable_bin` folder next to the project:

```
steamworks_sdk/redistributable_bin/
├── win64/steam_api64.dll
├── linux64/libsteam_api.so
└── osx/libsteam_api.dylib
```

`steamworks_sdk/` is git-ignored. The build packages it (unpacked out of the asar — the OS loader cannot read a
DLL from inside an archive).

### 1.3 The app id
- Local development: nothing to do. The game falls back to **480** (Valve's Spacewar) when `steam_appid.txt` is
  missing, so the achievements still fire in the desktop build.
- A Steam build: `INKWAVE_STEAM_APP_ID=1234567 npm run prepare:steam` writes the real id into
  `steam_appid.txt`, which is packaged with the app. The file is git-ignored on purpose.

```bash
npm run steam:verify     # prints what is in place and what is missing
```

---

## 2. The achievements

The API names below are what the game sends to Steam. **Create exactly these names in the Steamworks partner
backend** (App → Stats & Achievements → Achievements), otherwise nothing will ever unlock.

| API name | display name | description | unlock condition |
|---|---|---|---|
| `INK_FIRST_SPLAT` | First Splash | Splat your first enemy | 1 splat |
| `INK_FIRST_WIN` | Warm Ink | Win your first match | 1 win |
| `INK_WIN_10` | Getting Inked | Win 10 matches | 10 wins |
| `INK_WIN_50` | Turf Baron | Win 50 matches | 50 wins |
| `INK_SPLAT_100` | Century | Splat 100 enemies | 100 splats |
| `INK_SPLAT_500` | Ink Machine | Splat 500 enemies | 500 splats |
| `INK_TURF_1000` | Canvas | Ink 1,000 m² of turf | 1,000 m² of total turf |
| `INK_LEVEL_10` | Seasoned Squidkid | Reach level 10 | profile level 10 |
| `INK_LEVEL_25` | Harbour Legend | Reach level 25 | profile level 25 |
| `INK_ZONE_CAPTURE` | Point Taken | Capture a zone in Zone Control | 1 zone captured |
| `INK_BOSS_SLAYER` | Shell Breaker | Defeat HULLBREAKER | boss defeated |
| `INK_ALL_MODES` | Full Rotation | Win in Turf War, Zone Control and Boss Battle | 3 modes won |
| `INK_WEAPON_MASTER` | Kitbash | Win with 5 different main weapons | 5 weapons won with |

The catalogue lives in `src/net/steam.js` (`ACHIEVEMENTS`). Adding one means adding a row there **and** a row in
the partner backend.

**Store-page statistics** (optional, published to the Steam client):

| stat | meaning |
|---|---|
| `TotalWins` | matches won |
| `TotalMatches` | matches played |
| `TotalSplats` | enemies splatted |
| `TurfInked` | m² of turf inked |

---

## 3. Rich presence

| key | value |
|---|---|
| `steam_display` | `In the menus` · `In a lobby` · `In a match — Tidewater Plaza` · `Practising — Halyard Marina` |
| `status` | the same string (the fallback key) |

---

## 4. Steam Cloud

One file, `inkwave-save.json`, holding the profile (level, XP, wins, unlocks, locker look, the achievement
counters) and the settings that matter (language, quality, sensitivity, FOV, colourblind mode, volumes).

Conflict rule: **the newer `savedAt` wins**. The local profile is authoritative until the cloud has something
newer, so a fresh install picks up the cloud copy on the first launch and a local change is pushed on the next
save.

The profile is written through `Game._saveProfile()` in `src/main.js` — every profile write goes through it, so
the cloud stays in step.

---

## 5. The overlay

The overlay is the one part that is genuinely awkward in Electron (Steam injects into a native window, Electron
renders in Chromium). `steamworks-ffi-node` ships a native bridge (Metal on macOS, OpenGL on Windows and
Linux/SteamOS).

- macOS: `steam.addElectronSteamOverlay(win)` needs an **offscreen** `BrowserWindow` (`webPreferences.offscreen`).
  That changes the rendering path, so it must be benchmarked before shipping.
- Windows / Linux: the native bridge hooks the window directly — no offscreen mode needed.
- Whatever works, the game keeps a fallback: achievements pop up in the HUD toast, and `steam.overlay.activateGameOverlay('Friends')`
  opens the overlay programmatically.

**Test the overlay on all three platforms before the release build** — it is the classic thing that works on the
developer's machine and nowhere else.

---

## 6. Steam Deck

The Deck is a 1280×800 handheld with a gyroscope and no mouse, so two things have to be true for the game to be
playable on it:

**1. Performance.** The APU cannot carry the high preset (GTAO + bloom + a 4K ink atlas at 1.5× density). The game
detects the Deck through Steam and starts on the **low** preset (0.75× density, no bloom, no AO, a 2K ink atlas).
The player can raise it in the settings; the choice is then left alone.

**2. Aiming.** The game reads a standard-mapping gamepad through the Gamepad API, so the buttons work out of the
box — but aiming needs a mouse. Steam Input can turn the **right trackpad** and the **gyroscope** into a mouse, and
`steam/controller_neptune_gamepad_mouse_gyro.vdf` does exactly that: it is Valve's own "Gamepad with Gyro"
template with INKWAVE's title, and the packaging step copies it next to the executable (Steam Input only looks
there).

| | |
|---|---|
| trackpad | mouse (absolute) — the coarse aim |
| gyro | mouse — the fine aim, on top of it |
| buttons | an xinput gamepad, read by the game as usual |
| quality | low preset on the Deck (see above) |

**Tuning it.** The gyro sensitivity lives in Steam Input, not in the game. On the Deck: *Steam button → Controller
Settings → Gyroscope → As Mouse*, and the trackpad is the pointer. The shipped layout is a starting point — every
player can change it in the Steam client, and a player who prefers pure stick aim can just turn the gyro off.

**Test it on the device.** The layout is Valve's template, but the gyro feel is a matter of taste: the Deck's
gyro-to-mouse sensitivity, the trackpad speed and the game's mouse sensitivity (Settings → Controls) all add up.
Expect one round of tuning.

---

## 7. What is not done yet

| item | state |
|---|---|
| achievements, cloud saves, rich presence, store stats | **done** (`npm run steam-test` covers the logic) |
| Windows / Linux / macOS packaging | **done** (`npm run package:mac|win|linux`) |
| Windows + Linux app icons | **done** (`npm run icon` — derived from the macOS `.icns`) |
| Steam Deck: low preset on detection | **done** |
| Steam Deck: Steam Input layout (trackpad + gyro as mouse) | **done** — `steam/controller_neptune_gamepad_mouse_gyro.vdf`, **needs a test on the device** |
| Steam lobbies / invites / P2P networking | **not done** — the Cloudflare relay works today and is unchanged |
| Overlay bridge on macOS (offscreen window) | **not done** — needs a performance check |
| Partner-backend achievement setup | **manual** — see the table above |
| Steam store page, capsules, trailer, demo | **not done** — the next big chunk of work |

