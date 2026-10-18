<p align="center">
  <img src="assets/stages/halyard-day.webp" alt="Halyard Marina at golden hour" width="100%">
</p>

<h1 align="center">INKWAVE</h1>

<p align="center">
  <b>English</b> · <a href="README.zh-CN.md">简体中文</a>
</p>

<p align="center">
  An original Splatoon-style 4v4 turf-war shooter.<br>
  Paint the ground, swim through your ink, out-turf the other team.
</p>

<p align="center">
  <a href="#controls">Controls</a> ·
  <a href="#running-locally">Run locally</a> ·
  <a href="#the-unity-build">The Unity build</a> ·
  <a href="#how-it-works">How it works</a> ·
  <a href="CONTRIBUTING.md">Contributing</a>
</p>

<p align="center">
  <a href="https://github.com/Blagii/inkwave/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/Blagii/inkwave/actions/workflows/ci.yml/badge.svg"></a>
  <img alt="Unity 6" src="https://img.shields.io/badge/Unity-6000.0-000000?logo=unity&logoColor=white">
  <img alt="URP" src="https://img.shields.io/badge/render-URP-000000">
  <a href="LICENSE"><img alt="MIT" src="https://img.shields.io/badge/license-MIT-blue"></a>
</p>

---

## Features

- **Game modes, 4 v 4.** Turf War (most ground painted wins) and Zone Control (hold the live zone to count down from 100 — rotating side zones, penalties, overtime). Play against bots on three difficulty levels.
- **Online with friends.** Create a private room, share the five-character code, and up to eight players line up in the lobby with their loadouts and looks. Empty slots fill with bots; if someone drops, a bot takes over their squidkid mid-match.
- **Squid form.** Hold to dive into your ink: swim fast, refill your tank, climb inked walls, dolphin-jump water gaps.
- **Twelve weapons**, each with its own feel: Spritzer, Twinfire Pistols, Canopy Brolly (shotgun + launchable shield), Popper Blaster, Squall Spinner, Glint Charger, Tideline Bow (tri-arrow, two charge rings), Swell Roller, Swish Brush, Brine Cutlass (charged one-hit blade), Sponge Mitts (ink fists, charged leap, wall cling) and Bilge Bucket. Mix any main with any of 15 subs and 19 specials.
- **Seven stages, day or dusk.** Tidewater Plaza, Kelpline Terminal, Halyard Marina, Saltpan Basin, Crossroads Market, Lockgate Canals and Terrace Heights, each a real place with its own layout. Some stages change a few pieces for Zone Control.
- **Ink that behaves like liquid.** Splats spread and settle, fresh ink is glossy and dries, drips run down walls, and swimming leaves a wake in the surface itself.
- **A map you can actually read.** Hold <kbd>Tab</kbd> and the camera cranes up into a tilt-shift diorama of the live stage, with pins for your team and one-click Super Jumps.
- **Locker.** Choose your squidkid: tentacle style, headgear, face, outfit.
- **Everything procedural.** Characters, animation, weapons, textures, props, sound effects and music are all generated in code. There are no downloaded assets except two fonts.

<p align="center">
  <img src="assets/stages/tidewater-day.webp" width="49%" alt="Tidewater Plaza">
  <img src="assets/stages/kelpline-dusk.webp" width="49%" alt="Kelpline Terminal at dusk">
</p>

## Controls

| Action | Keyboard / mouse | Gamepad |
|---|---|---|
| Move | <kbd>W</kbd> <kbd>A</kbd> <kbd>S</kbd> <kbd>D</kbd> | Left stick |
| Aim | Mouse | Right stick |
| Fire | Left click | RT |
| Squid form | <kbd>Shift</kbd> | LT |
| Jump / dodge roll | <kbd>Space</kbd> | A |
| Sub weapon (bomb) | Right click / <kbd>E</kbd> | RB |
| Special | <kbd>F</kbd> | Y |
| Map + Super Jump | Hold <kbd>Tab</kbd> or <kbd>M</kbd>, then <kbd>1</kbd>–<kbd>4</kbd> or click a pin | View |
| Pause | <kbd>Esc</kbd> | Start |

Gamepads work on the hosted (https) version. On a plain `http://` LAN address browsers block the Gamepad API.

## Playing online

From the main menu choose **Online**, then **Create a room** and send your friends the code (or **Join a room** and
type theirs). The host picks the stage, time of day, match length and whether bots fill empty slots; everyone else
picks a team, weapon and look and readies up. The lineup, emotes and ready state are live for everyone in the room.

Rooms run on a tiny relay (a Cloudflare Worker with one Durable Object per room, in [`server/`](server)). It only
forwards messages: every player simulates their own squidkid and streams it, and everyone else draws it through the
same animation system on a smoothed timeline about a tenth of a second behind. How that works, and the tools used to
measure it, are in [`docs/NET.md`](docs/NET.md).

To play online on your own network, run the relay next to the game:

```bash
npm install      # once: the relay runs on wrangler
npm run relay    # ws://<this machine>:8787
```

A page opened from `localhost` or a LAN address uses that relay automatically; `?relay=wss://…` points it anywhere else.

## Running locally

The game is a **Unity** project — open `unity/INKWAVE` with Unity 6000.0.40f1 (URP 17) and press Play.
There is nothing to install first: the maps, the tuning and the strings are already inside the project.

```bash
git clone https://github.com/jaydendavisnc/inkwave.git
cd inkwave
npm install           # only for the data tools (three.js, for baking the props)
npm run unity:export  # re-export the maps + the tuning from src/ into the Unity project
npm run unity:check   # verify that every tuning number the C# reads still exists
npm run unity:bake    # bake the prop geometry (the .glb files, a build artifact)
```

In Unity: **Tools → INKWAVE → Build Play Scene** (the scene + the materials), then Play.
**Tools → INKWAVE → Build Linux / Windows / macOS Player** builds the standalone players.

```bash
npm run check         # syntax-check every module + the Unity data
npm run check-maps    # sanity-check every stage layout (and its Zone Control variant)
npm run icon          # regenerate the icons
```

## Desktop builds and Steam

The desktop players are built from Unity (the CI builds the Linux one on every push). Steamworks moves to
`UnitySteamworks`: the 13 achievements, the Steam Cloud save (`inkwave-save.json`), the rich presence and the
store statistics — all of it optional, so the build runs exactly the same without Steam.

See [`docs/STEAM-PATH.md`](docs/STEAM-PATH.md) for the publishing plan and [`docs/UNITY-PORT.md`](docs/UNITY-PORT.md)
for what is still missing before a release.

## The Unity build

The game runs on **Unity** — same mechanics, same maps, same tuning. The rules that keep it identical to the
original web build:

- **The data is exported, never re-typed.** `npm run unity:export` imports the real JavaScript modules
  (`src/world/*`, `src/config.js`, `src/i18n/*`) and writes the maps, the tuning and the strings into
  `unity/INKWAVE/Assets/Resources/INKWAVE/`. The Unity stage *is* the web stage.
- **The simulation is ported 1:1.** The OBB physics, the character controller, the weapons, the ink and the match
  rules are the same numbers as in `src/` — not a rewrite and not Unity's PhysX (that would change the feel).
- **`npm run unity:check`** scans the C# and verifies that every tuning number it reads still exists in the
  exported data, so a rename in `src/config.js` cannot silently change a value.

The JavaScript in `src/` is kept as the **reference implementation** — it is where the data comes from and where
the rules are compared against. It is not part of the game any more: the web build, the Electron shell and the
three.js runtime are gone.

## How it works

- **Ink is painted in texture space.** Every paintable face owns a region of one atlas; the splats are drawn into
  it on the GPU while a coarse CPU grid keeps the turf score and the gameplay queries in sync. The level shader
  layers the ink over the surface with its own height, gloss and wetness. See
  `unity/INKWAVE/Assets/INKWAVE/Scripts/Gameplay/PaintSystem.cs` and `Shaders/InkSurface.shader`.
- **Stages are data.** A layout is a list of boxes and ramps for one half of the arena; the other half is the 180°
  rotation, so both teams always get an identical field. See `unity/INKWAVE/Assets/INKWAVE/Scripts/World/Level.cs`.
- **Characters are procedural.** The squid kid is built from code (body, head, eyes that follow the aim, arms,
  legs, the ink tank, the tentacles) and the squid form swaps in when you dive. See
  `unity/INKWAVE/Assets/INKWAVE/Scripts/Render/ActorRenderer.cs`.
- **The sound is procedural.** A small synth renders the effects at runtime (a pitch-swept tone, a filtered noise
  burst, a bubble) — no audio files. See `unity/INKWAVE/Assets/INKWAVE/Scripts/Audio/AudioSystem.cs`.
- **Systems talk through events.** Weapons, actors and the match emit typed events; the HUD, the audio and the
  effects subscribe.

## Languages

The game ships in English and Simplified Chinese: **Settings → General → Language** (or [README.zh-CN.md](README.zh-CN.md) for this document). The choice is stored locally and applies instantly.

## Contributing

Issues and pull requests are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) for the project layout and the checks to run first.

## License

[MIT](LICENSE) © 2026 Jayden Davis. INKWAVE is an independent project and is not affiliated with Nintendo; Splatoon is a trademark of Nintendo.
