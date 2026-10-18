// INKWAVE — Steam integration (desktop build only; the web build never sees it).
//
// The Steamworks SDK lives in the Electron main process (electron/steam.cjs). This module is the game's side of
// that bridge: it owns the achievement catalogue, watches the match events, pushes rich presence, and syncs the
// profile through Steam Cloud. Everything here is a no-op when Steam is not available, so the game code can call
// into it unconditionally.
//
// The API names below are the ones configured in the Steamworks partner backend — they must match there exactly
// (Steam's "achievement API name"). Keep this table and the partner page in sync.
//
//   steam.init(profile, save)        — called once at boot; wires the event listeners
//   steam.matchResult(info)          — called when a match ends (awards the end-of-match achievements)
//   steam.presence(text)              — rich presence line ("In a match — Tidewater Plaza")
//   steam.saveProfile() / loadProfile() — Steam Cloud sync of the profile
import { G, on } from '../core/ctx.js';

// The achievement catalogue. `test` runs against the accumulated stats object.
export const ACHIEVEMENTS = [
  { id: 'INK_FIRST_SPLAT', name: 'First Splash', desc: 'Splat your first enemy', test: (s) => s.splats >= 1 },
  { id: 'INK_FIRST_WIN', name: 'Warm Ink', desc: 'Win your first match', test: (s) => s.wins >= 1 },
  { id: 'INK_WIN_10', name: 'Getting Inked', desc: 'Win 10 matches', test: (s) => s.wins >= 10 },
  { id: 'INK_WIN_50', name: 'Turf Baron', desc: 'Win 50 matches', test: (s) => s.wins >= 50 },
  { id: 'INK_SPLAT_100', name: 'Century', desc: 'Splat 100 enemies', test: (s) => s.splats >= 100 },
  { id: 'INK_SPLAT_500', name: 'Ink Machine', desc: 'Splat 500 enemies', test: (s) => s.splats >= 500 },
  { id: 'INK_TURF_1000', name: 'Canvas', desc: 'Ink 1,000 m² of turf', test: (s) => s.totalTurf >= 1000 },
  { id: 'INK_LEVEL_10', name: 'Seasoned Squidkid', desc: 'Reach level 10', test: (s) => s.level >= 10 },
  { id: 'INK_LEVEL_25', name: 'Harbour Legend', desc: 'Reach level 25', test: (s) => s.level >= 25 },
  { id: 'INK_ZONE_CAPTURE', name: 'Point Taken', desc: 'Capture a zone in Zone Control', test: (s) => s.zonesCaptured >= 1 },
  { id: 'INK_BOSS_SLAYER', name: 'Shell Breaker', desc: 'Defeat HULLBREAKER', test: (s) => s.bossKilled >= 1 },
  { id: 'INK_ALL_MODES', name: 'Full Rotation', desc: 'Win in Turf War, Zone Control and Boss Battle', test: (s) => s.modesWon.length >= 3 },
  { id: 'INK_WEAPON_MASTER', name: 'Kitbash', desc: 'Win with 5 different main weapons', test: (s) => s.weaponsWon.length >= 5 },
];

// Stats the achievements are tested against. They live in the profile (profile.steam) so they survive a restart
// and travel with the profile through Steam Cloud.
const emptyStats = () => ({ splats: 0, weaponsWon: [], modesWon: [], zonesCaptured: 0, bossKilled: 0 });

class SteamClient {
  constructor() {
    this.enabled = false;
    this.info = { available: false, user: null, appId: 0, isDeck: false };
    this.bridge = null;
    this.profile = null;
    this._save = null;
    this._unlocked = new Set();
    this._lastPresence = null;
  }

  get stats() {
    if (!this.profile) return emptyStats();
    if (!this.profile.steam) this.profile.steam = emptyStats();
    const s = this.profile.steam;
    if (!Array.isArray(s.weaponsWon)) s.weaponsWon = [];
    if (!Array.isArray(s.modesWon)) s.modesWon = [];
    return s;
  }

  // Called once at boot. Safe to call when there is no Steam at all.
  async init(profile, save) {
    this.profile = profile;
    this._save = save;
    const bridge = typeof window !== 'undefined' ? window.inkwaveSteam : null;
    if (!bridge) return false;
    this.bridge = bridge;
    try { this.info = (await bridge.info()) || this.info; } catch { this.info = { available: false }; }
    this.enabled = !!this.info.available;
    if (!this.enabled) return false;
    console.log(`[inkwave] Steam: app ${this.info.appId}${this.info.isDeck ? ' (Steam Deck)' : ''}, user ${this.info.user || '?'}`);

    // A splat is a 'hit' event whose attacker is the local actor and that killed.
    on('hit', ({ attacker, victim, killed }) => {
      if (!killed || !attacker?.isLocal || attacker.isBot) return;
      if (attacker === victim) return;
      this.stats.splats++;
      this._check();
    });
    // A zone taken by the local player's team.
    on('zones:zone', ({ owner }) => {
      const local = G.match?.local;
      if (owner < 0 || !local || owner !== local.team) return;
      this.stats.zonesCaptured++;
      this._check();
    });
    return true;
  }

  // ---- achievements -------------------------------------------------------------
  async unlock(id) {
    if (!this.enabled || !id || this._unlocked.has(id)) return false;
    const ok = await this.bridge.unlock(id).catch(() => false);
    if (ok) this._unlocked.add(id);
    return !!ok;
  }

  // Test every achievement against the current stats and unlock the ones that now pass.
  async _check(extra = {}) {
    if (!this.enabled) return;
    const s = { ...this.stats, ...extra };
    for (const a of ACHIEVEMENTS) {
      if (this._unlocked.has(a.id)) continue;
      let pass = false;
      try { pass = !!a.test(s); } catch { pass = false; }
      if (pass) await this.unlock(a.id);
    }
  }

  // Called by main.js when a match ends.
  async matchResult({ win, mode, weapon, level, wins, matches, totalTurf }) {
    if (!this.enabled) return;
    const s = this.stats;
    if (win) {
      if (weapon && !s.weaponsWon.includes(weapon)) s.weaponsWon.push(weapon);
      if (mode && !s.modesWon.includes(mode)) s.modesWon.push(mode);
    }
    if (mode === 'boss' && win) s.bossKilled = Math.max(1, s.bossKilled);
    this._save?.();
    await this._check({ level, wins, matches, totalTurf });
    // store-page stats (optional but nice): visible in the Steam client
    this.bridge.stat?.('TotalWins', wins | 0);
    this.bridge.stat?.('TotalMatches', matches | 0);
    this.bridge.stat?.('TotalSplats', s.splats | 0);
    this.bridge.stat?.('TurfInked', Math.round(totalTurf || 0));
    this.saveProfile();
  }

  // ---- rich presence ------------------------------------------------------------
  presence(text) {
    if (!this.enabled) return;
    if (text === this._lastPresence) return;
    this._lastPresence = text;
    this.bridge.presence?.('steam_display', text || '');
    // "status" is the fallback key Steam shows when steam_display is not localized
    this.bridge.presence?.('status', text || '');
  }

  clearPresence() { this.presence(''); }

  // ---- cloud --------------------------------------------------------------------
  // The profile + the settings that matter (quality, sensitivity, language) travel as one JSON file.
  async loadProfile() {
    if (!this.enabled) return null;
    const data = await this.bridge.cloudRead?.().catch(() => null);
    if (!data || !data.profile) return null;
    return data;
  }

  saveProfile() {
    if (!this.enabled || !this.profile) return false;
    const payload = {
      savedAt: Date.now(),
      profile: { ...this.profile, steam: this.stats },
      settings: G.settings ? {
        lang: G.settings.lang, quality: G.settings.quality, sensitivity: G.settings.sensitivity,
        padSensitivity: G.settings.padSensitivity, invertY: G.settings.invertY, fov: G.settings.fov,
        colorblind: G.settings.colorblind, master: G.settings.master, music: G.settings.music, sfx: G.settings.sfx,
        aimAssist: G.settings.aimAssist, aimAssistMouse: G.settings.aimAssistMouse,
      } : null,
    };
    return this.bridge.cloudWrite?.(payload) || false;
  }
}

export const steam = new SteamClient();
