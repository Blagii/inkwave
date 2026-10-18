#!/usr/bin/env node
// Checks whether this checkout can talk to Steam, and prints exactly what is missing when it cannot.
//
//   npm run steam:verify
//
// It never fails the build: a checkout without the SDK is a perfectly normal state (the web build, CI, a
// contributor). It only tells you whether the Steam features will be live in a build made from this tree.
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..');
const ok = (s) => `\x1b[32m✔\x1b[0m ${s}`;
const bad = (s) => `\x1b[31m✘\x1b[0m ${s}`;
const warn = (s) => `\x1b[33m!\x1b[0m ${s}`;

console.log('\nINKWAVE — Steam readiness\n');

// 1. the binding
let binding = null;
for (const name of ['steamworks-ffi-node', 'steamworks.js']) {
  try {
    const m = require(name);
    const cls = m?.default && typeof m.default.getInstance === 'function' ? m.default : m;
    if (typeof cls?.getInstance === 'function') { binding = name; break; }
  } catch { /* not installed */ }
}
console.log(binding ? ok(`binding: ${binding}`) : warn('binding: none installed (npm i steamworks-ffi-node) — Steam features stay off'));

// 2. the SDK redistributables
const sdkEnv = process.env.INKWAVE_STEAM_SDK;
const sdkDir = sdkEnv || path.join(ROOT, 'steamworks_sdk');
const redist = path.join(sdkDir, 'redistributable_bin');
const hasSdk = fs.existsSync(redist);
console.log(hasSdk ? ok(`SDK: ${path.relative(ROOT, redist) || redist}`) : warn(`SDK: ${path.relative(ROOT, redist)} missing — download the Steamworks SDK and copy redistributable_bin there`));
if (hasSdk) {
  for (const f of ['win64/steam_api64.dll', 'linux64/libsteam_api.so', 'osx/libsteam_api.dylib']) {
    if (fs.existsSync(path.join(redist, f))) console.log(`   ${ok(f)}`);
  }
}

// 3. the app id
const idFile = path.join(ROOT, 'steam_appid.txt');
const envId = process.env.INKWAVE_STEAM_APP_ID;
let appId = null;
if (envId && /^\d+$/.test(envId.trim())) appId = envId.trim();
else if (fs.existsSync(idFile)) {
  const t = fs.readFileSync(idFile, 'utf8').trim();
  if (/^\d+$/.test(t)) appId = t;
}
console.log(appId ? ok(`app id: ${appId}${appId === '480' ? '  (480 = Spacewar, local dev only)' : ''}`) : warn('app id: none — Steam features stay off'));

// 4. the achievement catalogue vs. what the game ships
try {
  const src = fs.readFileSync(path.join(ROOT, 'src/net/steam.js'), 'utf8');
  const ids = [...src.matchAll(/id:\s*'(INK_[A-Z0-9_]+)'/g)].map((m) => m[1]);
  console.log(ok(`achievements: ${ids.length} defined (${ids.join(', ')})`));
  console.log(warn('  -> create the same API names in the Steamworks partner backend or they will never unlock'));
} catch (e) { console.log(warn(`achievements: could not read src/net/steam.js (${e.message})`)); }

// 5. the packaging scripts
const pkg = JSON.parse(fs.readFileSync(path.join(ROOT, 'package.json'), 'utf8'));
for (const s of ['package:mac', 'package:win', 'package:linux']) {
  console.log(pkg.scripts?.[s] ? ok(`script: npm run ${s}`) : warn(`script: npm run ${s} missing`));
}
for (const f of ['build/icon.ico', 'build/icon.png', 'build/icon.icns']) {
  console.log(fs.existsSync(path.join(ROOT, f)) ? ok(`icon: ${f}`) : warn(`icon: ${f} missing (npm run icon)`));
}
console.log('');
