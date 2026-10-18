#!/usr/bin/env node
// Writes the Steam App ID into steam_appid.txt — the file the packaged app reads at start-up.
//
//   INKWAVE_STEAM_APP_ID=1234567 npm run prepare:steam
//
// The file is a build artifact (it is git-ignored): a developer checkout has no file at all and falls back to
// Valve's Spacewar id (480), while a Steam build must carry the real id. Keep the real id in the environment (or
// in steam/appid.txt, which is also git-ignored) so it never lands in git by accident.
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..');
const TARGET = path.join(ROOT, 'steam_appid.txt');
const FALLBACK = path.join(ROOT, 'steam', 'appid.txt');

function readId() {
  const env = process.env.INKWAVE_STEAM_APP_ID || process.env.STEAM_APP_ID;
  if (env && /^\d+$/.test(env.trim())) return env.trim();
  try {
    const t = fs.readFileSync(FALLBACK, 'utf8').trim();
    if (/^\d+$/.test(t)) return t;
  } catch { /* no local file */ }
  return null;
}

const id = readId();
if (!id) {
  console.log('[steam] no app id — steam_appid.txt left alone (the game falls back to 480 / Spacewar).');
  console.log('[steam] set INKWAVE_STEAM_APP_ID=… (or put the number in steam/appid.txt) and run this again.');
  process.exit(0);
}
if (id === '480') {
  console.log('[steam] 480 is Spacewar (the local test app) — writing it as-is.');
}
fs.writeFileSync(TARGET, id + '\n');
console.log(`[steam] steam_appid.txt ← ${id}`);
