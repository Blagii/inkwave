// Checks that the exported Unity data still matches what the C# port reads.
//
// The port reads the tuning numbers by name (JObj.Float("runAccel") and friends), so a rename in src/config.js
// would silently change a number to its fallback. This script scans the C# sources for those reads and verifies
// every one of them against the exported JSON — it is the safety net for "the same numbers in both builds".
//
// The weapon check is kind-aware: the C# only reads a weapon's own numbers inside the branch for that kind
// (`case "shooter": …`), so a brush is not asked for the charger's `inkFull`.
//
//   node tools/unity-export/check-data.mjs
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const DATA = path.join(ROOT, 'unity/INKWAVE/Assets/Resources/INKWAVE');
const SCRIPTS = path.join(ROOT, 'unity/INKWAVE/Assets/INKWAVE/Scripts');

if (!fs.existsSync(DATA)) {
  console.error('✗ the exported data is missing — run: node tools/unity-export/export-data.mjs');
  process.exit(1);
}

const config = JSON.parse(fs.readFileSync(path.join(DATA, 'config.json'), 'utf8'));
const stages = JSON.parse(fs.readFileSync(path.join(DATA, 'stages.json'), 'utf8'));

// ------------------------------------------------------------------------------------- walk the C# sources
function walk(dir, out = []) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) walk(p, out);
    else if (e.name.endsWith('.cs')) out.push(p);
  }
  return out;
}

const READ = /\.(?:Float|Int|Bool|Str)\(\s*"([^"]+)"/g;

// The receiver of a config read → which JSON object it points at.
const CONTEXT = [
  [/\bP\.(?:Float|Int|Bool|Str)\(\s*"([^"]+)"/g, (key) => ['PLAYER', key]],
  [/\bGameData\.Sub\("([^"]+)"\)\.(?:Float|Int|Bool|Str)\(\s*"([^"]+)"/g, (a, b) => [`SUB:${a}`, b]],
  [/\bGameData\.Special\("([^"]+)"\)\.(?:Float|Int|Bool|Str)\(\s*"([^"]+)"/g, (a, b) => [`SPECIAL:${a}`, b]],
  [/\bmatch\.(?:Float|Int|Bool|Str)\(\s*"([^"]+)"/g, (key) => ['MATCH', key]],
  [/\bzones\.(?:Float|Int|Bool|Str)\(\s*"([^"]+)"/g, (key) => ['ZONES', key]],
];

const reads = new Map();          // context → Set(keys)
const weaponReads = { all: new Set(), byKind: new Map() };
// a key read with a fallback default (JObj.Float("x", 1.5)) may legitimately be missing from the data
const withDefault = new Set();

function add(map, key, value) {
  if (!map.has(key)) map.set(key, new Set());
  map.get(key).add(value);
}

for (const file of walk(SCRIPTS)) {
  const src = fs.readFileSync(file, 'utf8');
  for (const [re, make] of CONTEXT) {
    re.lastIndex = 0;
    let m;
    while ((m = re.exec(src))) {
      const [ctx, key] = make(...m.slice(1));
      add(reads, ctx, key);
    }
  }

  // ---- the keys the C# reads with a fallback default
  for (const m of src.matchAll(/\.(?:Float|Int|Bool|Str)\(\s*"([^"]+)"\s*,/g)) withDefault.add(m[1]);

  // ---- the weapon reads, split by the `case "<kind>":` branch they sit in
  const lines = src.split('\n');
  let kind = null;
  let braceDepth = 0;
  let caseDepth = null;
  for (const line of lines) {
    const cm = /case\s+"([a-z_]+)":/.exec(line);
    if (cm) {
      kind = cm[1];
      caseDepth = braceDepth;
    }
    if (kind !== null && caseDepth !== null && braceDepth < caseDepth) {
      kind = null; caseDepth = null;
    }
    // only the reads on a weapon-ish receiver
    for (const m of line.matchAll(/\b(?:w|weapon)\.(?:Float|Int|Bool|Str)\(\s*"([^"]+)"/g)) {
      const key = m[1];
      if (kind) add(weaponReads.byKind, kind, key);
      else weaponReads.all.add(key);
    }
    braceDepth += (line.match(/\{/g) || []).length - (line.match(/\}/g) || []).length;
  }
}

const problems = [];
const optional = [];
function check(obj, keys, label) {
  for (const k of keys) {
    if (k in obj) continue;
    if (withDefault.has(k)) { optional.push(`${label}: "${k}" is not in the data (the C# falls back)`); continue; }
    problems.push(`${label}: missing "${k}"`);
  }
}

// PLAYER / MATCH / ZONES
check(config.player, reads.get('PLAYER') || [], 'PLAYER');
check(config.match, reads.get('MATCH') || [], 'MATCH');
check(config.zones, reads.get('ZONES') || [], 'ZONES');

// SUBS / SPECIALS
for (const [ctx, keys] of reads) {
  if (ctx.startsWith('SUB:')) {
    const id = ctx.slice(4);
    if (!config.subs[id]) problems.push(`SUB ${id}: not in the config`);
    else check(config.subs[id], keys, `SUB ${id}`);
  } else if (ctx.startsWith('SPECIAL:')) {
    const id = ctx.slice(8);
    if (!config.specials[id]) problems.push(`SPECIAL ${id}: not in the config`);
    else check(config.specials[id], keys, `SPECIAL ${id}`);
  }
}

// WEAPONS
//  · a key read inside a `case "<kind>":` branch must exist for every weapon of that kind
//  · a key read in the generic path must exist for at least one weapon (so the name is not a typo); the weapons
//    that lack it fall back to the C# default — that is the documented behaviour of the not-yet-ported kinds
const allWeapons = Object.entries(config.weapons);
for (const [id, w] of allWeapons) {
  const kindKeys = weaponReads.byKind.get(w.kind);
  if (kindKeys) check(w, kindKeys, `WEAPON ${id} (${w.kind})`);
}
for (const key of weaponReads.all) {
  const owners = allWeapons.filter(([, w]) => key in w).map(([id]) => id);
  if (!owners.length) {
    if (withDefault.has(key)) { optional.push(`WEAPON (generic): no weapon has "${key}" (the C# falls back)`); continue; }
    problems.push(`WEAPON (generic): no weapon has "${key}"`);
  } else if (owners.length < allWeapons.length) {
    console.log(`  · the generic path reads "${key}" — only ${owners.join(', ')} have it ` +
                `(the rest use the C# fallback)`);
  }
}

// ------------------------------------------------------------------------------------- the stage data
const stageIds = stages.stages.map((s) => s.id);
for (const id of ['tidewater', 'kelpline', 'halyard', 'saltpan', 'crossmarket', 'lockgate', 'terraces', 'cargo']) {
  if (!stageIds.includes(id)) problems.push(`STAGE ${id}: missing from the export`);
}
for (const s of stages.stages) {
  if (!s.modes || !s.modes.turf) { problems.push(`STAGE ${s.id}: no "turf" mode`); continue; }
  for (const [mode, m] of Object.entries(s.modes)) {
    if (!Array.isArray(m.blocks)) problems.push(`STAGE ${s.id}/${mode}: blocks is not an array`);
    if (!Array.isArray(m.props)) problems.push(`STAGE ${s.id}/${mode}: props is not an array`);
    for (const b of m.blocks || []) {
      if (!['box', 'obox', 'ramp'].includes(b.kind)) problems.push(`STAGE ${s.id}/${mode}: unknown block kind "${b.kind}"`);
      if (b.kind === 'box' && (!b.min || !b.max)) problems.push(`STAGE ${s.id}/${mode}: a box without min/max`);
      if (b.kind === 'obox' && (!b.center || !b.size)) problems.push(`STAGE ${s.id}/${mode}: an obox without center/size`);
      if (b.kind === 'ramp' && (!b.low || !b.high)) problems.push(`STAGE ${s.id}/${mode}: a ramp without low/high`);
    }
    for (const p of m.props || []) {
      if (!p.type) problems.push(`STAGE ${s.id}/${mode}: a prop without a type`);
      if (!Array.isArray(p.pos)) problems.push(`STAGE ${s.id}/${mode}: a prop without a position`);
    }
  }
}

// ------------------------------------------------------------------------------------- report
const totalReads = [...reads.values()].reduce((n, s) => n + s.size, 0)
  + weaponReads.all.size
  + [...weaponReads.byKind.values()].reduce((n, s) => n + s.size, 0);

if (problems.length) {
  console.error(`✗ ${problems.length} problem(s) in the exported data:`);
  for (const p of problems) console.error('  - ' + p);
  process.exit(1);
}
for (const o of optional) console.log('  · ' + o);
console.log(`✓ data check ok — ${totalReads} config reads verified · ${stageIds.length} stages · ` +
            `${Object.keys(config.weapons).length} weapons`);
