# Contributing to INKWAVE

Thanks for your interest! INKWAVE is a **Unity** project (6000.0.40f1, URP) — open `unity/INKWAVE` and press
Play. The JavaScript in `src/` is the reference implementation: it is where the maps and the tuning come from.

## Running locally

```bash
git clone https://github.com/jaydendavisnc/inkwave.git
cd inkwave
npm install          # only for the data tools (three.js, for baking the props)
npm run unity:export # re-export the maps + the tuning from src/ into the Unity project
npm run unity:check  # every tuning number the C# reads still exists
npm run unity:bake   # bake the prop geometry (the .glb files)
```

In Unity: **Tools → INKWAVE → Build Play Scene**, then Play.

## Before opening a pull request

```bash
npm run check        # node --check on every module + the Unity data check
npm run check-maps   # sanity-check every stage layout
```

Keep pull requests focused. If you change gameplay tuning, change it in `src/config.js` (or `src/world/*`) and
re-export — the Unity build reads the exported data, so the two can never drift apart.

## Project map

| Path | What lives there |
|---|---|
| `unity/INKWAVE/Assets/INKWAVE/Scripts/Core` | the context, the event bus, the game loop |
| `unity/INKWAVE/Assets/INKWAVE/Scripts/Data` | the exported data (the stages, the config, the i18n) |
| `unity/INKWAVE/Assets/INKWAVE/Scripts/World` | the level, the own OBB physics, the ink |
| `unity/INKWAVE/Assets/INKWAVE/Scripts/Gameplay` | the actors, the weapons, the bots, the zones, the subs, the specials, the match |
| `unity/INKWAVE/Assets/INKWAVE/Scripts/Render` | the level renderer, the characters, the HUD |
| `unity/INKWAVE/Assets/INKWAVE/Scripts/Audio` | the procedural sound |
| `src` | the reference implementation (the data comes from here) |
| `tools/unity-export` | the data export + the verifier + the prop bake |
| `docs` | the event contract, the module contracts, the character rig reference, the Unity port |

## Code style

Match the surrounding code: 2-space indent, single quotes, no semicolon-free style, comments that explain *why*. No per-frame allocations in hot paths. New stages must keep both halves identical (the layout is mirrored by a 180° rotation).

## Reporting bugs

Open an issue with your browser + GPU, the stage, and steps to reproduce. A screenshot or short clip helps a lot.
