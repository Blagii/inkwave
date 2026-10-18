// Bakes the set dressing into glTF for the Unity build.
//
// The prop kit is procedural (src/world/props.js): every bench, crane, boat and lamp is built in code. The Unity
// build cannot run that code, so the geometry is baked here — the same builder, the same parameters, the same
// vertex colours (the baked ambient occlusion is already folded into them) — and written out as glTF.
//
//   node tools/unity-export/bake-props.mjs
//
// Output: unity/INKWAVE/Assets/INKWAVE/Props/<stage>-<mode>.glb  (one file per stage and mode)
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.join(HERE, '../..');
const OUT = path.join(ROOT, 'unity/INKWAVE/Assets/INKWAVE/Props');

const { MAP_LAYOUTS } = await import('../../src/world/maps.js');
const { dressingFor } = await import('../../src/world/dressing.js');
const { STAGES } = await import('../../src/world/stages/index.js');
const { layoutFor, inMode } = await import('../../src/world/variants.js');
const { PropKit, bakePropGeometry } = await import('../../src/world/props.js');
const { mergeVertices } = await import('three/addons/utils/BufferGeometryUtils.js');

const MODES = ['turf', 'zones'];

// ------------------------------------------------------------------------------------------- glTF writer
// A minimal glTF 2.0 (GLB) writer: one buffer, one mesh with one primitive per material.
const MATERIALS = {
  paint: { roughness: 0.6, metallic: 0.0, doubleSided: false },
  gloss: { roughness: 0.36, metallic: 0.0, doubleSided: false },
  metal: { roughness: 0.33, metallic: 0.7, doubleSided: false },
  wood: { roughness: 0.74, metallic: 0.0, doubleSided: false },
  rubber: { roughness: 0.86, metallic: 0.0, doubleSided: false },
  foliage: { roughness: 0.7, metallic: 0.0, doubleSided: true },
  cloth: { roughness: 0.9, metallic: 0.0, doubleSided: true },
  glow: { roughness: 1.0, metallic: 0.0, doubleSided: false, unlit: true },
  blink: { roughness: 1.0, metallic: 0.0, doubleSided: false, unlit: true },
};

class Glb {
  constructor() {
    this.chunks = [];      // { buffer, byteOffset }
    this.length = 0;
    this.json = {
      asset: { version: '2.0', generator: 'inkwave-bake-props' },
      extensionsRequired: ['KHR_mesh_quantization'],
      scene: 0,
      scenes: [{ nodes: [] }],
      nodes: [],
      meshes: [],
      materials: [],
      accessors: [],
      bufferViews: [],
      buffers: [],
      extensionsUsed: ['KHR_mesh_quantization'],
    };
    this.matIndex = new Map();
  }

  align(n = 4) {
    const pad = (n - (this.length % n)) % n;
    if (pad) { this.chunks.push(Buffer.alloc(pad)); this.length += pad; }
  }

  view(buffer) {
    this.align(4);
    const byteOffset = this.length;
    this.chunks.push(buffer);
    this.length += buffer.length;
    this.json.bufferViews.push({ buffer: 0, byteOffset, byteLength: buffer.length });
    return this.json.bufferViews.length - 1;
  }

  accessor(buffer, componentType, type, count, extra = {}) {
    const view = this.view(buffer);
    const a = { bufferView: view, componentType, count, type, ...extra };
    this.json.accessors.push(a);
    return this.json.accessors.length - 1;
  }

  material(name) {
    if (this.matIndex.has(name)) return this.matIndex.get(name);
    const m = MATERIALS[name] || MATERIALS.paint;
    const mat = {
      name,
      pbrMetallicRoughness: {
        baseColorFactor: [1, 1, 1, 1],
        metallicFactor: m.metallic,
        roughnessFactor: m.roughness,
      },
      doubleSided: !!m.doubleSided,
    };
    if (m.unlit) mat.extensions = { KHR_materials_unlit: {} };
    this.json.materials.push(mat);
    if (m.unlit && !this.json.extensionsUsed.includes('KHR_materials_unlit')) {
      this.json.extensionsUsed.push('KHR_materials_unlit');
    }
    this.matIndex.set(name, this.json.materials.length - 1);
    return this.json.materials.length - 1;
  }

  // A primitive, quantised (KHR_mesh_quantization — Unity's glTFast importer reads it):
  //   POSITION  SHORT      (the node carries the scale that maps the integer range back to metres)
  //   NORMAL    BYTE      normalized
  //   COLOR_0   UNSIGNED_BYTE normalized
  // The UVs are dropped: without the sign atlas they carry nothing.
  addPrimitive(geometry, materialName) {
    const g = mergeVertices(geometry, 1e-4);
    const pos = g.attributes.position;
    const nor = g.attributes.normal;
    const col = g.attributes.color;
    const n = pos.count;

    const pmin = boxMin(pos.array, n, 3);
    const pmax = boxMax(pos.array, n, 3);
    const scale = [0, 1, 2].map((k) => (pmax[k] - pmin[k]) / 65535 || 1e-6);

    const posBuf = Buffer.alloc(n * 6);
    const norBuf = Buffer.alloc(n * 3);
    const colBuf = Buffer.alloc(n * 3);
    for (let i = 0; i < n; i++) {
      for (let k = 0; k < 3; k++) {
        const v = (pos.array[i * 3 + k] - pmin[k]) / scale[k];
        posBuf.writeInt16LE(Math.max(-32767, Math.min(32767, Math.round(v))), i * 6 + k * 2);
        const nv = nor.array[i * 3 + k];
        norBuf.writeInt8(Math.max(-127, Math.min(127, Math.round(nv * 127))), i * 3 + k);
        const cv = col.array[i * 3 + k];
        colBuf.writeUInt8(Math.max(0, Math.min(255, Math.round(cv * 255))), i * 3 + k);
      }
    }

    let idx;
    if (g.index) {
      const src = g.index.array;
      idx = n > 65535 ? Buffer.from(new Uint32Array(src)) : Buffer.from(new Uint16Array(src));
    } else {
      const a = new Uint32Array(n);
      for (let i = 0; i < n; i++) a[i] = i;
      idx = Buffer.from(a);
    }

    const aPos = this.accessor(posBuf, 5122, 'VEC3', n, { min: pmin, max: pmax });
    const aNor = this.accessor(norBuf, 5120, 'VEC3', n, { normalized: true });
    const aCol = this.accessor(colBuf, 5121, 'VEC3', n, { normalized: true });
    const aIdx = this.accessor(idx, n > 65535 ? 5125 : 5123, 'SCALAR', idx.length / (n > 65535 ? 4 : 2));

    this.json.mesheshes = undefined;
    this.json.meshes.push({
      name: materialName,
      primitives: [{
        attributes: { POSITION: aPos, NORMAL: aNor, COLOR_0: aCol },
        indices: aIdx,
        material: this.material(materialName),
        mode: 4,
      }],
    });
    this.json.nodes.push({
      mesh: this.json.meshes.length - 1,
      name: materialName,
      translation: pmin,
      scale,
    });
    this.json.scenes[0].nodes.push(this.json.nodes.length - 1);
    if (g !== geometry) g.dispose();
  }

  write(file) {
    const bin = Buffer.concat(this.chunks);
    this.json.buffers = [{ byteLength: bin.length }];
    const jsonText = JSON.stringify(this.json);
    const jsonBuf = Buffer.from(jsonText, 'utf8');
    const pad = (4 - (jsonBuf.length % 4)) % 4;
    const jsonPad = Buffer.alloc(pad, 0x20);
    const total = 12 + 8 + jsonBuf.length + jsonPad.length + 8 + bin.length;
    const head = Buffer.alloc(12);
    head.writeUInt32LE(0x46546c67, 0);   // 'glTF'
    head.writeUInt32LE(2, 4);
    head.writeUInt32LE(total, 8);
    const jHead = Buffer.alloc(8);
    jHead.writeUInt32LE(jsonBuf.length + jsonPad.length, 0);
    jHead.writeUInt32LE(0x4e4f534a, 4); // 'JSON'
    const bHead = Buffer.alloc(8);
    bHead.writeUInt32LE(bin.length, 0);
    bHead.writeUInt32LE(0x004e4942, 4); // 'BIN'
    fs.writeFileSync(file, Buffer.concat([head, jHead, jsonBuf, jsonPad, bHead, bin]));
    return { bytes: total, meshes: this.json.meshes.length };
  }
}

function boxMin(arr, n, stride) {
  const m = [Infinity, Infinity, Infinity];
  for (let i = 0; i < n; i++) for (let k = 0; k < 3; k++) m[k] = Math.min(m[k], arr[i * stride + k]);
  return m;
}
function boxMax(arr, n, stride) {
  const m = [-Infinity, -Infinity, -Infinity];
  for (let i = 0; i < n; i++) for (let k = 0; k < 3; k++) m[k] = Math.max(m[k], arr[i * stride + k]);
  return m;
}

// ------------------------------------------------------------------------------------------------- bake
fs.mkdirSync(OUT, { recursive: true });

let totalTris = 0, totalBytes = 0;
for (const def of Object.values((await import('../../src/config.js')).MAPS)) {
  const layout = MAP_LAYOUTS[def.id];
  if (!layout) continue;
  for (const mode of MODES) {
    const placements = [];
    for (const p of [...(dressingFor(def.id) || []), ...(STAGES[def.id]?.PLACEMENTS || [])]) {
      if (!inMode(p, mode)) continue;
      placements.push(p);
    }
    const kit = new PropKit(null, { headless: true, quality: 'high' });
    for (const p of placements) kit.add(p.type, p);
    const baked = bakePropGeometry(kit);

    const glb = new Glb();
    let tris = 0;
    for (const { material, geometry } of baked) {
      glb.addPrimitive(geometry, material);
      tris += (geometry.index ? geometry.index.count : geometry.attributes.position.count) / 3;
    }
    const file = path.join(OUT, `${def.id}-${mode}.glb`);
    const { bytes } = glb.write(file);
    totalTris += tris; totalBytes += bytes;
    console.log(`[bake] ${path.basename(file)} — ${placements.length} props · ${tris} tris · ${(bytes / 1048576).toFixed(2)} MB`);
  }
}
console.log(`[bake] done — ${(totalTris / 1000).toFixed(0)}k tris · ${(totalBytes / 1048576).toFixed(1)} MB`);
