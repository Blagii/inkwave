// Copies the Steam Input controller layouts next to the packaged game.
//
// Steam Input loads a `controller_*.vdf` file that sits in the same folder as the game's executable — that is how a
// game ships a default controller layout (gyro aim on the Steam Deck). electron-packager does not know about it,
// so this runs after the packaging step.
//
//   node build/copy-deck-configs.mjs            # every build in dist/
//   node build/copy-deck-configs.mjs dist/INKWAVE-win32-x64
import { readdirSync, existsSync, copyFileSync, mkdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = path.join(path.dirname(fileURLToPath(import.meta.url)), '..');
const SRC = path.join(ROOT, 'steam');

if (!existsSync(SRC)) {
  console.log('[deck] no steam/ folder — nothing to copy');
  process.exit(0);
}
const configs = readdirSync(SRC).filter((f) => f.startsWith('controller_') && f.endsWith('.vdf'));
if (!configs.length) {
  console.log('[deck] no controller_*.vdf in steam/ — nothing to copy');
  process.exit(0);
}

// where Steam Input looks, per platform (relative to the app folder)
const TARGETS = {
  win32: () => '.',                                      // next to the .exe
  linux: () => '.',                                      // next to the binary
  darwin: () => path.join('INKWAVE.app', 'Contents', 'Resources'),
};

function targetsFor(appDir) {
  const name = path.basename(appDir);
  const platform = name.includes('win32') ? 'win32' : name.includes('darwin') ? 'darwin' : name.includes('linux') ? 'linux' : null;
  if (!platform) return [];
  return [path.join(appDir, TARGETS[platform]())];
}

const roots = process.argv.slice(2);
if (!roots.length) {
  const dist = path.join(ROOT, 'dist');
  if (!existsSync(dist)) { console.log('[deck] dist/ does not exist — nothing to copy'); process.exit(0); }
  roots.push(...readdirSync(dist).map((d) => path.join(dist, d)).filter((d) => existsSync(d) && !d.endsWith('.dmg')));
}

let copied = 0;
for (const root of roots) {
  for (const dir of targetsFor(root)) {
    if (!existsSync(dir)) continue;
    for (const c of configs) {
      const dest = path.join(dir, c);
      copyFileSync(path.join(SRC, c), dest);
      copied++;
      console.log(`[deck] ${path.relative(ROOT, dest)}`);
    }
  }
}
console.log(copied ? `[deck] ${copied} controller layout(s) copied` : '[deck] nothing to copy (no matching build)');
