// Steam integration test — runs the real src/net/steam.js against a fake Steamworks bridge.
//
//   node tools/steam-test.mjs
//
// It checks the two things that can silently break: the game must be a complete no-op without Steam, and with a
// bridge the achievements must unlock from the real game events.
import { ACHIEVEMENTS } from '../src/net/steam.js';

let failures = 0;
const check = (name, cond, extra = '') => {
  if (cond) console.log(`  ok   ${name}`);
  else { failures++; console.log(`  FAIL ${name} ${extra}`); }
};

// ---------------------------------------------------------------- 1. no Steam at all
{
  console.log('\n[1] without Steam the game is inert');
  const { steam } = await import('../src/net/steam.js');
  check('init() reports no Steam', (await steam.init({ name: 'P' }, () => {})) === false);
  check('presence() does not throw', steam.presence('x') === undefined);
  check('matchResult() does not throw', (await steam.matchResult({ win: true, mode: 'turf', weapon: 'shooter', level: 2, wins: 1, matches: 1, totalTurf: 5 })) === undefined);
  check('no achievement ids were touched', steam._unlocked.size === 0);
}

// ---------------------------------------------------------------- 2. with a fake bridge
{
  console.log('\n[2] with a bridge the achievements unlock');
  const unlocked = new Set();
  const writes = [];
  const bridge = {
    info: async () => ({ available: true, user: 'Tester', appId: 1234, isDeck: false }),
    unlock: async (name) => { unlocked.add(name); return true; },
    unlocked: async (name) => unlocked.has(name),
    all: async () => [...unlocked].map((apiName) => ({ apiName, unlocked: true })),
    presence: (k, v) => { writes.push(['presence', k, v]); return true; },
    clearPresence: () => true,
    overlay: () => true,
    cloudRead: async () => null,
    cloudWrite: async (d) => { writes.push(['cloud', d]); return true; },
    stat: (k, v) => { writes.push(['stat', k, v]); return true; },
  };
  globalThis.window = { inkwaveSteam: bridge };

  const { steam } = await import('../src/net/steam.js?test=1');
  const profile = { name: 'P', level: 1, xp: 0, wins: 0, matches: 0, totalTurf: 0, weapon: 'shooter' };
  const saved = [];
  check('init() enables Steam', (await steam.init(profile, () => saved.push(1))) === true);

  // a splat by the local player (the module's own 'hit' listener does the counting)
  const { emit } = await import('../src/core/ctx.js');
  const local = { isLocal: true, isBot: false };
  emit('hit', { attacker: local, victim: { isLocal: false }, damage: 50, killed: true, weaponId: 'shooter' });
  check('a local kill is counted', steam.stats.splats === 1, `got ${steam.stats.splats}`);

  // a win in a match
  await steam.matchResult({ win: true, mode: 'turf', weapon: 'shooter', level: 1, wins: 1, matches: 1, totalTurf: 120 });
  check('INK_FIRST_SPLAT unlocked', unlocked.has('INK_FIRST_SPLAT'));
  check('INK_FIRST_WIN unlocked', unlocked.has('INK_FIRST_WIN'));
  check('INK_TURF_1000 not unlocked yet', !unlocked.has('INK_TURF_1000'));
  check('the weapon was recorded', steam.stats.weaponsWon.includes('shooter'));
  check('the mode was recorded', steam.stats.modesWon.includes('turf'));
  check('a stat was published', writes.some((w) => w[0] === 'stat' && w[1] === 'TotalWins'));
  check('the profile was saved', saved.length > 0);

  // level 10
  await steam.matchResult({ win: true, mode: 'zones', weapon: 'roller', level: 10, wins: 2, matches: 2, totalTurf: 2000 });
  check('INK_TURF_1000 unlocked', unlocked.has('INK_TURF_1000'));
  check('INK_LEVEL_10 unlocked', unlocked.has('INK_LEVEL_10'));
  check('two modes recorded', steam.stats.modesWon.length === 2);

  // the boss
  await steam.matchResult({ win: true, mode: 'boss', weapon: 'charger', level: 10, wins: 3, matches: 3, totalTurf: 2000 });
  check('INK_BOSS_SLAYER unlocked', unlocked.has('INK_BOSS_SLAYER'));
  check('INK_ALL_MODES unlocked', unlocked.has('INK_ALL_MODES'));

  // every id in the catalogue is a valid Steam API name
  const bad = ACHIEVEMENTS.filter((a) => !/^[A-Z0-9_]{1,64}$/.test(a.id));
  check('every API name is legal', bad.length === 0, JSON.stringify(bad.map((b) => b.id)));
  check('the API names are unique', new Set(ACHIEVEMENTS.map((a) => a.id)).size === ACHIEVEMENTS.length);
}

console.log(failures ? `\nSTEAM TEST FAILED (${failures})\n` : '\nSTEAM TEST OK\n');
process.exit(failures ? 1 : 0);
