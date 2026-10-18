// Finds a Chrome/Chromium/Edge binary and the right GPU flags for the current platform.
//
// The headless tools (smoke, botlab, net-test, the film/shots helpers) all drive a real browser, because the game
// needs WebGL. The binary used to be hard-coded to the macOS path, which made every one of them macOS-only —
// this helper makes them work on Linux (CI) and Windows too.
//
//   import { CHROME, chromeArgs } from './chrome.mjs';
import { existsSync } from 'node:fs';
import os from 'node:os';

const CANDIDATES = [
  process.env.CHROME_PATH,
  process.env.PUPPETEER_EXECUTABLE_PATH,
  // macOS
  '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
  '/Applications/Chromium.app/Contents/MacOS/Chromium',
  '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge',
  // Windows
  'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
  'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe',
  'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
  // Linux
  '/usr/bin/google-chrome',
  '/usr/bin/google-chrome-stable',
  '/usr/bin/chromium',
  '/usr/bin/chromium-browser',
  '/snap/bin/chromium',
  '/opt/google/chrome/chrome',
];

/** The first browser binary that exists on this machine, or null. */
export function findChrome() {
  for (const c of CANDIDATES) if (c && existsSync(c)) return c;
  return null;
}

/** The browser binary, or a message that says what to install. */
export function chromePath() {
  const p = findChrome();
  if (!p) {
    console.error('[chrome] no Chrome/Chromium/Edge found. Install one, or set CHROME_PATH to its binary.');
  }
  return p;
}

/** The GPU flags that make WebGL work on this platform. */
export function chromeArgs(extra = []) {
  const angle = os.platform() === 'darwin' ? 'metal' : os.platform() === 'win32' ? 'd3d11' : 'gl';
  return [
    `--use-angle=${angle}`,
    '--enable-gpu',
    '--ignore-gpu-blocklist',
    '--autoplay-policy=no-user-gesture-required',
    ...extra,
  ];
}

export const CHROME = (() => {
  const p = findChrome();
  if (!p) {
    console.error('[chrome] no Chrome / Chromium / Edge found on this machine.');
    console.error('        install one, or point CHROME_PATH at the binary.');
    process.exit(1);
  }
  return p;
})();
