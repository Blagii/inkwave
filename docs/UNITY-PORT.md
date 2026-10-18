# INKWAVE — Unity port

Datum: 2026-10-18 · repo: `Blagii/inkwave` · Unity projekat: `unity/INKWAVE/`

> **Odluka (2026-10-18): prelazimo na Unity.** Mehanika, mape i balans ostaju **isti**, a **three.js i Electron
> se izbacuju** iz proizvoda. Unity zamenjuje renderer (i renderuje mapu), a desktop ljusku zamenjuje Unity
> build + Steamworks.
>
> Ovaj dokument je živi plan porta: šta je prebačeno, kako se obe verzije drže u sync-u i šta još fali.

---

## 0. Rezime (TL;DR)

Tri pravila definišu ovaj port:

1. **Podaci se ne prepisuju — izvoze se.** JS je jedini izvor istine za mape i balans.
   `tools/unity-export/export-data.mjs` uvozi prave JS module (`src/world/*`, `src/config.js`, `src/i18n/*`)
   i piše `unity/INKWAVE/Assets/Resources/INKWAVE/{stages,config,i18n}.json`.
   Mapa u Unity-u **jeste** mapa iz web builda — ista geometrija, iste flage, isti mirroring, iste zone.
2. **Simulacija se prenosi 1:1, ne prepisuje "na oko".** OBB fizika, character controller, boje, forme,
   pisanje ink-a, bodovi i match flow se prebacuju iz JS-a u C# sa **istim brojevima** — ne preko PhysX-a
   (Unity collideri bi promenili osećaj: step-up na ivice, klizanje po zidovima, ground snap).
3. **Rendering se zamenjuje.** Meshes, materijali, URP, ink atlas + splat shader, kamera. Unity renderuje
   mapu, likove i UI.

Ovaj fajl je i "kako da se ne pokvari": `npm run unity:check` skenira C# kod i proverava da **svaki broj koji
C# čita postoji u izvezenim podacima**. Ako se neko ime promeni u `src/config.js`, check to odmah prijavi.

---

## 1. Mapa JS → C#

| JS (web/Electron) | C# (Unity) | Stanje |
|---|---|---|
| `src/core/ctx.js` (`G` + event bus) | `Scripts/Core/GameContext.cs` | ✅ |
| `src/core/input.js` | `Scripts/Input/PlayerInput.cs` (Unity Input System) | ✅ (-bindinzi isti) |
| `src/config.js` (PLAYER/WEAPONS/SUBS/…) | izvezen u `config.json` → `JObj` | ✅ |
| `src/i18n/strings.js`, `zh.js` | izvezen u `i18n.json` | ✅ (UI još nije) |
| `src/world/level.js` (blocks, faces, hash) | `Scripts/World/Level.cs` | ✅ |
| `src/game/physics.js` | `Scripts/World/Physics.cs` | ✅ |
| `src/world/paint.js` (CPU grid + atlas) | `Scripts/Gameplay/PaintSystem.cs` + `Shaders/InkSplat.shader` | ✅ (grid 1:1) |
| `src/game/actor.js` | `Scripts/Gameplay/Actor.cs` | ✅ (pokret, forme, ink, hp) |
| `src/game/weapons.js` | `Scripts/Gameplay/WeaponRunner.cs` | ◐ shooter porodica gotova; specijalne vrste padaju na generički put |
| `src/game/match.js` | `Scripts/Gameplay/Match.cs` | ✅ (turf tok; zone/boss nisu) |
| `src/game/bots.js` | `Scripts/Gameplay/Match.cs` → `BotBrain` | ◐ uprošćen bot (ciljanje + pucanje) |
| `src/game/cameraRig.js` | `Scripts/Gameplay/CameraRig.cs` | ✅ (follow) |
| `src/world/stages/*`, `dressing.js` | izvezen u `stages.json` (+ bake u glTF) | ✅ |
| `src/game/character*.js` (likovi) | `Scripts/Render/ActorRenderer.cs` (placeholder kapsula) | ⬜ |
| `src/ui/*` (meniji, HUD) | — | ⬜ |
| `src/net/*` (relay, online) | — | ⬜ |
| `electron/*` (shell + Steamworks) | Unity build + `UnitySteamworks` | ⬜ |

**Bez three.js.** C# ne uvozi nijedan three.js modul — three.js je bio samo renderer u web buildu.
**Bez Electron-a.** Unity pravi `.app` / `.exe` build; Electron shell i njegov Steamworks sloj se brišu
iz finalnog proizvoda (dok god Unity build ne proradi, stari Electron build ostaje u repou kao fallback).

---

## 2. Podaci: exporter je jedini izvor istine

```
node tools/unity-export/export-data.mjs     # → Assets/Resources/INKWAVE/*.json
node tools/unity-export/check-data.mjs      # → da li C# čita brojeve koji postoje
```

Izlaz (trenutno):

| Fajl | Sadržaj | Veličina |
|---|---|---|
| `stages.json` | 8 stage-ova · 6371 blokova (uključujući **kolizije propova**) · 2962 propa | ~2.4 MB |
| `config.json` | 15 oružja, svi PLAYER/MATCH/ZONES/DIFFICULTY brojevi, 8 mapa | ~35 kB |
| `i18n.json` | EN (119 fraza) + ZH | ~59 kB |

**Kolizije propova idu uz podatke.** U web buildu solidni propovi vraćaju kolizione kutije koje postaju deo nivoa
(`src/main.js → new Level(layout, colliders)`). Exporter pokreće **isti** prop builder (headless, bez WebGL-a) i
dopisuje te kutije u `stages.json` kao `hidden` blokove — pa se arena u Unity-ju sudara **identično** kao u webu.

**Geometrija propova se bake-uje u glTF.** Propovi su proceduralni (≈3000 postavljanja, 6.1M trouglova za turf),
pa ih Unity ne može da izgeneriše: `npm run unity:bake` ih bake-uje u `unity/INKWAVE/Assets/INKWAVE/Props/*.glb`
(KHR_mesh_quantization, vertex boje nose paletu i ugrađeni AO). `.glb` fajlovi su **build artefakti i nisu u
repo-u** (kao muzika u `songs/`) — tool je u repou, bake se radi lokalno, pa
`Tools → INKWAVE → Build Prop Prefabs` napravi prefabe koje igra učitava.

Exporter već primenjuje **mirror** (`half` → 180° oko Y, kao `src/world/level.js → mirrorDef`) i **filter po
modu** (`turf` / `zones`, kao `src/world/variants.js`), pa C# nema pravilo koje može da se pokvari.

Kako se nova mapa dodaje: napravi se u `src/world/stages/<ime>/` kao i do sad → `npm run unity:export` →
mapa je u Unity-u. Nema ručnog prepisivanja.

---

## 3. Faze

### ✅ Faza U0 — Paket i podaci
- Unity 6000.0.x projekat (`unity/INKWAVE/`): URP 17, Input System, Newtonsoft JSON, jedan asmdef `INKWAVE`.
- Exporter + data check (gore).
- Mapa: level iz blokova, OBB fizika, ground probe, rail feet, spawn barrier.

### ✅ Faza U1 — Simulacija
- `Actor`: S-curve ubrzanje, plant-and-reverse, heading slew, jump buffer + coyote time, apex hang,
  hard-landing, forma (kid/squid/swim), enemy-ink šteta i regen, ink refill, posebni meter.
- `PaintSystem`: CPU mreža 0.25 m po licu, ista `blobWobble` ivica kao GPU, brojanje turfa po timu.
- `WeaponRunner` + `Projectiles`: shooter porodica (balistička korekcija, konus razlaza, "straight pa gravitacija",
  trail kapljice, impact splat, damage).
- `Match`: uvod → igra → kraj → ocena (turf odlučuje), spawnovi, međusobno guranje.
- `CameraRig`: pivot na oprugama, soft probe za dužinu booma, aim zrak iz renderovane kamere.

### ◐ Faza U2 — Rendering i sadržaj (u toku)
- ✅ Level mesh iz blokova (svaka farba ima UV u ink atlasu).
- ✅ `InkSplat.shader` (URP) + `InkSurface.shader` (URP) + `VertexColorLit.shader` (propovi).
- ✅ **Prop geometrija**: bake-ovana u glTF (`npm run unity:bake`), 16 fajlova (8 stage-ova × 2 moda).
  Nedostaje: tekstura natpisa (canvas atlas) i lančana ograda (oba traže canvas teksturu).
- ⬜ **Likovi** (character art, 4062 linije u web buildu): trenutno placeholder kapsula.
- ⬜ **HUD/UI**: Unity UI Toolkit; skor, timer, mapa, oružje, specijal.

### ⬜ Faza U3 — Online, Steam, build
- Netcode: isti Cloudflare Durable Object WebSocket protokol (`server/src/index.js`) u C#.
- Steamworks: `UnitySteamworks` (13 dostignuća, Rich Presence, Cloud `inkwave-save.json`), **gracful
  degradation** — build radi i bez Steama (kao danas web build).
- Steam Deck: quality preset po detekciji + `controller_neptune_gamepad_mouse_gyro.vdf` (već postoji u repou).
- Unity build pipeline (desktop) + CI job.

---

## 4. Rizici i otvorene tačke

| # | Tačka | Uticaj | Plan |
|---|---|---|---|
| 1 | Prop teksture (natpisi, ograda) | Propovi su bez štampa i ograda | Canvas atlas u Unity-ju (UI Toolkit / proceduran) |
| 2 | Likovi (character art) | Vizuelno daleko od web builda | Port character-geo/mats/outfit ili novi modeli |
| 3 | Oružja: roller/twins/brush/slosher/splatling/brolly/bow/blade/mitts | Ponašanje ≠ web build | Port `_roller`/`_twins`/`_brush`/… iz `weapons.js` po vrsti |
| 4 | Bot AI (2088 linija) | Botovi su slabiji | Port `BotBrain` (navigacija, farbanje ink-a, sub tajming) |
| 5 | Zone Control + Boss mod | Nisu u portu | `src/game/zones.js`, `src/boss/*` |
| 6 | Super jump / wall climb / specijali | Nisu u portu | `actor.js` (`_updateClimb`, `_updateSuperJump`), `specials.js` |
| 7 | Proceduralni zvuk | Nema zvuka | Unity audio + port `src/audio/*` |
| 8 | C# kompilacija | Sandbox nema Unity/kompajler | `npm run unity:check` pokriva podatke; kompilacija se radi lokalno |

**Redosled prioriteta:** (1) propovi → (3) oružja → (2) likovi → (4) botovi → (5/6) modeovi → (7) zvuk.

---

## 5. Kako se radi

```bash
# 1. promeniš nešto u src/ (mapa, balans, tekst)
npm run unity:export        # podaci se prebacuju u Unity projekat
npm run unity:check         # provjera da C# čita brojeve koji postoje
npm run unity:bake          # (opcijeno) propovi → glTF — samo ako se menja scena/propovi

# 2. otvori Unity
open unity/INKWAVE          # Unity Hub → projekat
#   Tools → INKWAVE → Build Prop Prefabs  (ako je radio bake)
#   Tools → INKWAVE → Build Play Scene    (scena + materijali)
#   Play
```

Build: `File → Build Settings` → Windows / macOS / Linux. Steam build se pravi iz istog Unity build-a
(UnitySteamworks), pa Electron ljuska postaje nepotrebna.

---

## 6. Šta se NE menja

- **Mehanika**: pokret, pucanje, bojenje, turf, forma i plivanje, oružja/sup/specijali, botovi, match tok.
- **Mape**: svih 8 (tidewater, kelpline, halyard, saltpan, crossmarket, lockgate, terraces, cargo),
  isti izvoz, ista geometrija, iste zone.
- **Balans**: `src/config.js` je jedini izvor brojeva.
- **Igra radi van Steama**: build se pokreće bez Steam klijenta (dostignuća/oblak/lobiji se tiho isključe).
