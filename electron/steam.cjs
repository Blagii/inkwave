// Steamworks integration for the desktop build (Electron main process).
//
// This module is the *only* place that talks to the Steamworks SDK. It is deliberately defensive: the game must
// keep working when the SDK is not installed (the web build, a normal desktop install, CI, a contributor without a
// Steamworks account). Everything degrades to a no-op when Steam is unavailable.
//
// Two bindings are supported, in order of preference:
//   * steamworks-ffi-node — Koffi FFI, no native build step, Steamworks SDK 1.6x, full API (achievements, cloud,
//     rich presence, overlay, lobbies). Needs the SDK's `redistributable_bin` next to the app.
//   * steamworks.js      — older Rust/NAPI binding; only used as a fallback (no cloud, no lobby API).
//
// The Steamworks SDK redistributables are NOT in this repo (Valve's SDK Access Agreement). Put them in
// `steamworks_sdk/redistributable_bin/` (see docs/STEAM-PATH.md) or set INKWAVE_STEAM_SDK to their parent folder.
//
// App id resolution: INKWAVE_STEAM_APP_ID → steam_appid.txt → 480 (Spacewar, for local development).

const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..');
const DEV_APP_ID = 480;            // Valve's Spacewar — the standard "just testing" app id
const CLOUD_FILE = 'inkwave-save.json';

function readAppId() {
  const env = process.env.INKWAVE_STEAM_APP_ID || process.env.STEAM_APP_ID;
  if (env && /^\d+$/.test(env.trim())) return parseInt(env.trim(), 10);
  try {
    const t = fs.readFileSync(path.join(ROOT, 'steam_appid.txt'), 'utf8').trim();
    if (/^\d+$/.test(t)) return parseInt(t, 10);
  } catch { /* no file: dev without Steam */ }
  return DEV_APP_ID;
}

// The SDK folder (the one that contains redistributable_bin/) — set before init().
function sdkPath() {
  const env = process.env.INKWAVE_STEAM_SDK;
  if (env) return env;
  const local = path.join(ROOT, 'steamworks_sdk');
  if (fs.existsSync(path.join(local, 'redistributable_bin'))) return local;
  return null;
}

// Load a binding. Returns { api, kind } or { api: null, kind: null, errors: [...] }.
function loadBinding() {
  const errors = [];
  for (const name of ['steamworks-ffi-node', 'steamworks.js']) {
    let mod;
    try { mod = require(name); } catch (e) { errors.push(`${name}: ${e.message.split('\n')[0]}`); continue; }
    // CommonJS builds of an ES module expose the class on .default
    const cls = mod?.default && typeof mod.default.getInstance === 'function' ? mod.default : mod;
    if (typeof cls?.getInstance !== 'function') { errors.push(`${name}: no getInstance()`); continue; }
    try { return { api: cls.getInstance(), kind: name, errors }; } catch (e) { errors.push(`${name}: ${e.message.split('\n')[0]}`); }
  }
  return { api: null, kind: null, errors };
}

class Steam {
  constructor() {
    this.available = false;
    this.kind = null;          // which binding is in use
    this.restarted = false;    // the Steam client asked us to relaunch through it — the app must quit now
    this.appId = DEV_APP_ID;
    this.user = null;          // Steam persona name
    this.isDeck = false;
    this.reason = '';          // why Steam is unavailable (log only)
    this._unlocked = new Set();
    this._cbTimer = null;
  }

  // Called once from app.whenReady(). Never throws.
  init() {
    const { api, kind, errors } = loadBinding();
    if (!api) {
      this.reason = `no Steamworks binding (${errors.join('; ')})`;
      console.log(`[inkwave] Steam integration off: ${this.reason}`);
      return false;
    }
    this.kind = kind;
    this._api = api;
    this.appId = readAppId();
    const sdk = sdkPath();
    if (sdk) { try { api.setSdkPath(sdk); } catch (e) { console.warn('[inkwave] setSdkPath failed', e.message); } }
    try { if (api.setDebug) api.setDebug(!!process.env.INKWAVE_STEAM_DEBUG); } catch { /* optional */ }

    // A real Steam build must be started by the Steam client (that is how the overlay and the identity arrive).
    // Local dev (app id 480 / steam_appid.txt) is never restarted, and the check is skipped entirely when the
    // SDK is missing so the game still opens.
    if (this.appId !== DEV_APP_ID && api.restartAppIfNecessary) {
      try {
        if (api.restartAppIfNecessary(this.appId)) { this.reason = 'relaunching through the Steam client'; this.restarted = true; return false; }
      } catch (e) { console.warn('[inkwave] restartAppIfNecessary failed', e.message); }
    }

    let ok = false;
    try { ok = !!api.init({ appId: this.appId }); } catch (e) { this.reason = e.message.split('\n')[0]; }
    if (!ok) {
      this.reason = this.reason || 'steam.init() failed (SDK redistributables missing?)';
      console.log(`[inkwave] Steam integration off: ${this.reason}`);
      return false;
    }
    this.available = true;
    try { this.user = api.friends?.getPersonaName?.() || null; } catch { /* not fatal */ }
    try { this.isDeck = !!api.utils?.isSteamRunningOnSteamDeck?.(); } catch { /* not fatal */ }
    // Callbacks pump (Steam's event queue). 1 Hz is enough for achievements / overlay / rich presence.
    this._cbTimer = setInterval(() => { try { api.runCallbacks(); } catch { /* keep the game alive */ } }, 1000);
    if (this._cbTimer.unref) this._cbTimer.unref();
    console.log(`[inkwave] Steam ready (app ${this.appId}${this.isDeck ? ', Steam Deck' : ''}, user ${this.user || '?'})`);
    return true;
  }

  shutdown() {
    if (this._cbTimer) { clearInterval(this._cbTimer); this._cbTimer = null; }
    if (!this.available) return;
    try { this.api.shutdown(); } catch { /* ignore */ }
    this.available = false;
  }

  get api() { return this._api; }

  // ---- achievements -------------------------------------------------------------
  async unlock(name) {
    if (!this.available || !name) return false;
    try {
      const r = await this._api.achievements.unlockAchievement(name);
      if (r) this._unlocked.add(name);
      return !!r;
    } catch (e) { console.warn('[inkwave] achievement failed', name, e.message); return false; }
  }

  async isUnlocked(name) {
    if (!this.available || !name) return false;
    if (this._unlocked.has(name)) return true;
    try {
      const r = await this._api.achievements.isAchievementUnlocked?.(name);
      if (r) this._unlocked.add(name);
      return !!r;
    } catch { return false; }
  }

  async all() {
    if (!this.available) return [];
    try {
      const list = await this._api.achievements.getAllAchievements();
      return (list || []).map((a) => ({ apiName: a.apiName, unlocked: !!a.unlocked }));
    } catch { return []; }
  }

  // ---- rich presence ------------------------------------------------------------
  setPresence(key, value) {
    if (!this.available) return false;
    try { return !!this._api.richPresence.setRichPresence(key, value ?? null); } catch { return false; }
  }

  clearPresence() {
    if (!this.available) return;
    try { this._api.richPresence.clearRichPresence?.(); } catch { /* optional */ }
  }

  // ---- overlay ------------------------------------------------------------------
  overlay(dialog) {
    if (!this.available) return false;
    try { this._api.overlay.activateGameOverlay(dialog || 'Friends'); return true; } catch { return false; }
  }

  // ---- cloud --------------------------------------------------------------------
  // One JSON file holds the profile + settings. Newest write wins (savedAt timestamp).
  cloudRead() {
    if (!this.available) return null;
    try {
      const r = this._api.cloud.fileRead(CLOUD_FILE);
      if (!r || !r.success || !r.data) return null;
      const obj = JSON.parse(r.data.toString('utf8'));
      return obj && typeof obj === 'object' ? obj : null;
    } catch (e) { console.warn('[inkwave] cloud read failed', e.message); return null; }
  }

  cloudWrite(data) {
    if (!this.available) return false;
    try {
      const buf = Buffer.from(JSON.stringify(data), 'utf8');
      return !!this._api.cloud.fileWrite(CLOUD_FILE, buf);
    } catch (e) { console.warn('[inkwave] cloud write failed', e.message); return false; }
  }

  // ---- stats (optional, for the Steam store page graphs) -------------------------
  setStat(key, value) {
    if (!this.available) return false;
    try {
      if (Number.isInteger(value)) return !!this._api.stats.setStatInt(key, value);
      return !!this._api.stats.setStatFloat(key, value);
    } catch { return false; }
  }
}

module.exports = { Steam, DEV_APP_ID, CLOUD_FILE };
