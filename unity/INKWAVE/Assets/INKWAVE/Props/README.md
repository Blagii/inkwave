# Baked props

The set dressing (benches, cranes, boats, lamps — ~3000 placements across the eight stages) is procedural:
`src/world/props.js` builds every piece in code. The Unity build cannot run that code, so the geometry is baked
into glTF:

```bash
npm run unity:bake        # → this folder: <stage>-<mode>.glb
```

The `.glb` files are build artifacts and are **not committed**. Run the bake once
before opening the project, then `Tools → INKWAVE → Build Prop Prefabs` to turn them into prefabs the game loads.

The colliders the props contribute to the level are **not** baked — they come from `npm run unity:export`, which
runs the same prop builder and writes the collision boxes into `stages.json`. That is why the arenas collide
exactly like the web build's.
