// Exports the INKWAVE game data for the Unity build.
//
//   node tools/unity-export/export-data.mjs
//
// The Unity project does NOT hand-copy the maps, the weapon tables or the tuning numbers: this script imports the
// very same JavaScript modules the web build uses and writes their contents as JSON. The Unity side reads that
// JSON, so a change in src/ lands in Unity on the next export — the two builds can never drift apart.
//
//   src/world/maps.js        → the stage layouts (blocks, ramps, turned boxes)
//   src/world/dressing.js    → the prop placements
//   src/world/stages/*/props.js → the per-stage prop placements
//   src/config.js            → weapons, subs, specials, player feel, match rules
//   src/i18n/                → the string dictionaries
//
// Output: unity/INKWAVE/Assets/Resources/INKWAVE/{stages,config,i18n}.json
//          (Resources/ so the game can load them at runtime with Resources.Load)
import { writeFileSync, mkdirSync, existsSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.join(HERE, '../..');
const OUT = path.join(ROOT, 'unity/INKWAVE/Assets/Resources/INKWAVE');

const { MAP_LAYOUTS } = await import('../../src/world/maps.js');
const { dressingFor } = await import('../../src/world/dressing.js');
const { STAGES } = await import('../../src/world/stages/index.js');
const { PropKit } = await import('../../src/world/props.js');
const { layoutFor, inMode, hasVariant } = await import('../../src/world/variants.js');
const CFG = await import('../../src/config.js');
const { ZH_PHRASES } = await import('../../src/i18n/zh.js');
const { EN_PHRASES } = await import('../../src/i18n/strings.js');

const MODES = ['turf', 'zones'];

// -------------------------------------------------------------------------------------------------- helpers
// The web build mirrors a stage's `half` by a 180° rotation about Y ((x,z) → (-x,-z)) so both teams get an
// identical field (src/world/level.js → mirrorDef). The mirror is applied HERE, so the Unity data already holds
// the final block list and the C# side has no mirroring rule to get wrong.
function mirrorDef(d) {
  const mural = d.mural ? d.mural.map((m) => ({ ...m, n: [-m.n[0], m.n[1], -m.n[2]] })) : undefined;
  const noPaint = d.noPaint ? d.noPaint.map((m) => [-m[0], m[1], -m[2]]) : undefined;
  if (d.kind === 'box') return { ...d, mural, noPaint, min: [-d.max[0], d.min[1], -d.max[2]], max: [-d.min[0], d.max[1], -d.max[2]] };
  if (d.kind === 'obox') return { ...d, mural, noPaint, center: [-d.center[0], d.center[1], -d.center[2]] };
  return { ...d, mural, noPaint, low: [-d.low[0], d.low[1], -d.low[2]], high: [-d.high[0], d.high[1], -d.high[2]] };
}

// Keep only the fields the Unity side reads, so the JSON stays small and stable.
const BLOCK_FIELDS = [
  'kind', 'color', 'pattern', 'tag', 'mural', 'noPaint', 'bevel',
  'paint', 'solid', 'grate', 'roof', 'perch', 'noNav', 'rail', 'hidden', 'thin', 'thickness', 'variant',
];

function packBlock(d) {
  const o = {};
  for (const f of BLOCK_FIELDS) if (d[f] !== undefined) o[f] = d[f];
  if (d.kind === 'box') { o.min = d.min; o.max = d.max; }
  else if (d.kind === 'obox') { o.center = d.center; o.size = d.size; o.rotY = d.rotY; }
  else { o.low = d.low; o.high = d.high; o.width = d.width; }
  return o;
}

// The prop placements: the shared dressing list plus the stage's own PLACEMENTS.
function packProp(p) {
  const o = {};
  for (const [k, v] of Object.entries(p)) {
    if (v === undefined) continue;
    if (typeof v === 'number' && !Number.isFinite(v)) continue;
    o[k] = v;
  }
  return o;
}

function propsFor(stageId, mode) {
  const list = [];
  const dressing = dressingFor(stageId) || [];
  const own = STAGES[stageId]?.PLACEMENTS || [];
  for (const p of [...dressing, ...own]) {
    if (!inMode(p, mode)) continue;
    list.push(packProp(p));
  }
  return list;
}

// The solid props hand back collision boxes that become part of the level (physics, nav, paint) — the web build
// feeds them to the Level as `extra` blocks (src/main.js → new Level(layout, colliders)). The Unity build gets the
// same boxes, so the arenas collide exactly like the web build's.
function colliderBlocks(colliders) {
  return colliders.map((c) => {
    const b = c.obox
      ? { kind: 'obox', center: c.center, size: c.size, rotY: c.rotY }
      : { kind: 'box', min: c.min, max: c.max };
    b.paint = false; b.solid = true; b.hidden = true;
    if (c.roof) b.roof = true;
    if (c.rail) b.rail = true;
    if (c.perch) b.perch = true;
    return b;
  });
}

function propColliders(placementList) {
  // The prop kit runs headless here (no WebGL, no canvas): only the geometry and the colliders are needed.
  const kit = new PropKit(null, { headless: true, quality: 'high' });
  const out = [];
  for (const p of placementList) {
    const r = kit.add(p.type, p);
    if (r && r.colliders) out.push(...r.colliders);
  }
  return out;
}

// --------------------------------------------------------------------------------------------------- stages
const stages = [];
for (const def of CFG.MAPS) {
  const layout = MAP_LAYOUTS[def.id];
  if (!layout) { console.warn(`[export] no layout for stage "${def.id}" — skipped`); continue; }
  const stage = {
    id: def.id,
    name: def.name,
    blurb: def.blurb,
    theme: def.theme,
    times: def.times,
    onlineOnly: !!def.onlineOnly,
    noBots: !!def.noBots,
    noBoss: !!def.noBoss,
    bounds: layout.bounds,
    spawnPads: layout.spawnPads,
    spawnBarrier: layout.spawnBarrier,
    intro: layout.intro || null,
    water: layout.water || null,
    zones: layout.zones || null,
    modes: {},
  };
  for (const mode of MODES) {
    const L = layoutFor(layout, mode);
    const props = propsFor(def.id, mode);
    const extra = colliderBlocks(propColliders(props));
    const blocks = [...L.single, ...L.half, ...L.half.map(mirrorDef), ...extra].map(packBlock);
    stage.modes[mode] = {
      variant: hasVariant(layout, dressingFor(def.id), mode),
      blocks,
      props,
    };
  }
  stages.push(stage);
}

// --------------------------------------------------------------------------------------------------- config
const config = {
  version: CFG.VERSION,
  title: CFG.GAME_TITLE,
  subtitle: CFG.GAME_SUBTITLE,
  teamPalettes: CFG.TEAM_PALETTES,
  colorblindPalette: CFG.COLORBLIND_PALETTE,
  teamNames: CFG.TEAM_NAMES,
  player: CFG.PLAYER,
  weapons: CFG.WEAPONS,
  weaponOrder: CFG.WEAPON_ORDER,
  subs: CFG.SUBS,
  subOrder: CFG.SUB_ORDER,
  specials: CFG.SPECIALS,
  specialOrder: CFG.SPECIAL_ORDER,
  match: CFG.MATCH,
  zones: CFG.ZONES,
  difficulty: CFG.DIFFICULTY,
  progression: CFG.PROGRESSION,
  defaultSettings: CFG.DEFAULT_SETTINGS,
  quality: CFG.QUALITY,
  times: CFG.TIMES,
  maps: CFG.MAPS.map((m) => ({ id: m.id, name: m.name, blurb: m.blurb, theme: m.theme, times: m.times, onlineOnly: !!m.onlineOnly, noBots: !!m.noBots, noBoss: !!m.noBoss })),
};

// ----------------------------------------------------------------------------------------------------- i18n
const i18n = { languages: [{ id: 'en', label: 'English' }, { id: 'zh', label: '中文' }], en: EN_PHRASES, zh: ZH_PHRASES };

// --------------------------------------------------------------------------------------------------- write
if (!existsSync(OUT)) mkdirSync(OUT, { recursive: true });
const write = (name, data) => {
  const file = path.join(OUT, name);
  writeFileSync(file, JSON.stringify(data, null, 1));
  console.log(`[export] ${path.relative(ROOT, file)} (${(JSON.stringify(data).length / 1024).toFixed(0)} kB)`);
};
write('stages.json', { stages });
write('config.json', config);
write('i18n.json', i18n);

// a quick summary, so the export log says something useful
const blockCount = stages.reduce((a, s) => a + s.modes.turf.blocks.length, 0);
const propCount = stages.reduce((a, s) => a + s.modes.turf.props.length, 0);
console.log(`[export] ${stages.length} stages · ${blockCount} blocks · ${propCount} props · ${Object.keys(config.weapons).length} weapons`);
