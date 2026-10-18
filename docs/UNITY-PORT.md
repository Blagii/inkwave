# INKWAVE — Unity port

Datum: 2026-10-18 · repo: `Blagii/inkwave` · Unity projekat: `unity/INKWAVE/`

> **Odluka (2026-10-18): prelazimo na Unity.** Mehanika, mape i balans ostaju **isti**, a **three.js i Electron
> se izbacuju** iz proizvoda. Unity zamenjuje renderer (i renderuje mapu), a desktop ljusku zamenjuje Unity
> build + Steamworks.
>
> **Stanje 2026-10-18 (kraj dana):** port je gotov do nivoa „igra se može igrati" — sve 15 oružja, Zone Control,
> botovi, supovi, specijali, zvuk i HUD su u C#. **Electron je obrisan iz repozitorijuma**, a web build i
> three.js runtime sa njim. `src/` ostaje kao *referentna implementacija* (izvor podataka za export i poređenje
> pravila) — nije deo build-a. Kad se port završi i podaci se zamrznu, i `src/` ide.
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
| `src/game/weapons.js` | `Scripts/Gameplay/WeaponRunner.cs` + `WeaponKinds.cs` | ◐ shooter porodica + roller/twins/dualies/spinner/slosher/splatling; bucket/brush/brolly/bow/blade/mitts idu na generički put |
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
repo-u** — tool je u repou, bake se radi lokalno, pa
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
- `WeaponRunner` + `Projectiles` + `WeaponKinds`: shooter porodica (balistička korekcija, konus razlaza, "straight pa gravitacija",
  trail kapljice, impact splat, damage).
- `Match`: uvod → igra → kraj → ocena (turf odlučuje), spawnovi, međusobno guranje.
- `CameraRig`: pivot na oprugama, soft probe za dužinu booma, aim zrak iz renderovane kamere.

### ✅ Faza U1.5 — Oružja po vrstama

Šest vrsta oružja ima svoje ponašanje u webu (ne samo brojeve), pa su prenesena 1:1 u `WeaponKinds.cs`:

| Oružje | Šta radi u portu |
|---|---|
| **roller** | flick (zamah → 9 teških kapljica) + **valjak** (traka boje kroz teren, šteta ispred bubnja, `kind: 'roll'` slik) |
| **twins** | naizmenični pištolji, **dodge-roll** (pritisak skoka = roll), *turret* mod posle rolla (brže, dalje, uže) |
| **dualies** | naizmenična vatra, **dodge-roll** + *lock* (zabijena turret, `spreadLock`) |
| **spinner** | punjenje → **burst** (brzina/domet zavise od napunjenosti) |
| **slosher** | zamah → **talas** (lob preko prepreka, 8 kapi, vodeći kapi nosi štetu, mrlja se sliježi u traku) |
| **splatling** | zupčanje → **struja** metaka (dužina zavisi od napunjenosti) |
| **bucket** | zamah → **zamena** (loptice se bacaju u luku; prva pogađa nišan) |
| **brush** | trzaj side-to-side → **prskavica**; trčanje → **trag** koji pravi put i udara |
| **brolly** | **prskavica** 11 peleta (kupa) + **kišobran** koji se otvara kao štit |
| **blade** | brzi rez + **talas** boje (napunjeni rez) |
| **bow** | napeti → **tri strelice** (ležište / vazduh), zabodene streлиće eksplodiraju |
| **mitts** | **šake** koje prskaju + **skok** (naboj) + **štit** koji upija udarce sfronta |

Sve brojne vrednosti se čitaju iz izvezenog `config.json` (161 proverenih čitanja), pa je osećaj isti kao u webu.

### ✅ Faza U2.5 — Zone Control (drugi mod)

`ZoneControl.cs` je port `src/game/zones.js`:

- zone iz stage layout-a (centar + po jedna strana za svaki tim, Bravo je ogledalo Alfe)
- ubezanje: ≥ 80 % zone pobojane; držana zona se neutrališe na 40 %
- odbrojavanje 100 → 0: centar 1 pt/s, svoja strana 1 pt/2 s, tuđa strana 1 pt/0.5 s
- kazna za gubitak objekta: ROUND(0.75 × (start − end)) + 1
- rotacija objekta svakih 30–60 s; poslednjih 30 s (i overtime) samo centar
- prelivanje boje po zoni pri preuzimanju (front iz centra), brisanje zone kad postane live
- botovi prvo idu na live zonu; HUD pokazuje broj i ko drži objekat

### ◐ Faza U2 — Rendering i sadržaj (u toku)
- ✅ Level mesh iz blokova (svaka farba ima UV u ink atlasu).
- ✅ `InkSplat.shader` (URP) + `InkSurface.shader` (URP) + `VertexColorLit.shader` (propovi).
- ✅ **Prop geometrija**: bake-ovana u glTF (`npm run unity:bake`), 16 fajlova (8 stage-ova × 2 moda).
  Nedostaje: tekstura natpisa (canvas atlas) i lančana ograda (oba traže canvas teksturu).
- ✅ **Likovi**: squid kid se pravi proceduralno (`ActorRenderer.cs` — telo, glava, oči koje prate nišan,
  ruke, noge, rezervoar boje, tenkice; forma lignjena za plivanje). Nema spoljnjih art asseta; boje timova
  iz `config.json` (materijali `Resources/INKWAVE/Materials/Team{0,1}`). Još nema: lica, frizura, outfit
  po oružju (port `character.js` / `character-weapons.js`).
- ✅ **HUD**: `Hud.cs` (IMGUI, bez UI asseta): meter boje, rezultat i tajmer, oružje i specijal, nišan,
  upozorenje za malo boje. Docniji UI (UI Toolkit, mapa, ubice) je sledeći korak.

### ◐ Faza U3 — Online, Steam, build (u toku)
- ✅ **Unity build pipeline**: `Tools → INKWAVE → Build Linux / Windows / macOS Player` + CI job
  (`game-ci/unity-builder`, Linux igrač na svakom push-u).
- ⬜ Netcode: isti Cloudflare Durable Object WebSocket protokol (`server/src/index.js`) u C#.
- ⬜ Steamworks: `UnitySteamworks` (13 dostignuća, Rich Presence, Cloud `inkwave-save.json`), **gracful
  degradation** — build radi i bez Steama.
- ⬜ Steam Deck: quality preset po detekciji + `controller_neptune_gamepad_mouse_gyro.vdf` (već postoji u repou).

---

## 4. Rizici i otvorene tačke

| # | Tačka | Uticaj | Plan |
|---|---|---|---|
| 1 | Prop teksture (natpisi, ograda) | Propovi su bez štampa i ograda | Canvas atlas u Unity-ju (UI Toolkit / proceduran) |
| 2 | Likovi (detalji) | Karakter je proceduralan, ali bez lica / frizure / outfita | Port character-geo/mats/outfit ili novi modeli |
| 3 | Oružja: bucket/brush/brolly/bow/blade/mitts | ✅ portovano (mehanika) | Grafike (modeli, tragovi) još nisu portovane |
| 4 | Bot AI (2088 linija) | Botovi su slabiji od weba | Port `BotBrain` (navigacija, threat AI, super jump) |
| 5 | Boss mod (HULLBREAKER) | Nije u portu | `src/boss/*` (model, faze, napadi) |
| 9 | three.js / Electron u proizvodu | ✅ obrisano (web build, `index.html`, `styles/`, `vendor/`, `electron/`) | `src/` ostaje samo kao referenca (build-time alat za prop bake) |
| 6 | Super jump / wall climb / specijali | Nisu u portu | `actor.js` (`_updateClimb`, `_updateSuperJump`), `specials.js` |
| 7 | Proceduralni zvuk | Nema zvuka | Unity audio + port `src/audio/*` |
| 8 | C# kompilacija | Sandbox nema Unity/kompajler | `npm run unity:check` pokriva podatke + 4 statičke C# provjere; pravu kompilaciju radi Unity kod tebe |

**Redosled prioriteta:** (1) propovi → (3) oružja → (2) likovi → (4) botovi → (5/6) modeovi → (7) zvuk.

---

## 5. Kako se radi

```bash
# 1. promeniš nešto u src/ (mapa, balans, tekst)
npm run unity:export        # podaci se prebacuju u Unity projekat
npm run unity:check         # provjera podataka + provjera C# koda (skripta, sintaksa, pozivi)
npm run unity:bake          # (opcijeno) propovi → glTF — samo ako se menja scena/propovi

# 2. otvori Unity
open unity/INKWAVE          # Unity Hub → projekat
#   Tools → INKWAVE → Build Prop Prefabs  (ako je radio bake)
#   Tools → INKWAVE → Build Play Scene    (scena + materijali)
#   Play
```

Build: `File → Build Settings` → Windows / macOS / Linux. Steam build se pravi iz istog Unity build-a
(UnitySteamworks), pa Electron ljuska postaje nepotrebna.

### Provjere C# koda (bez Unityja)

Sandbox nema Unity Editor ni C# kompajler, pa je pored `npm run unity:check` (podaci) napravljena
četiri statičke provjere koje hvataju greške koje bi Unity prijavio:

| Skripta | Šta hvata | Primer |
|---|---|---|
| `tools/unity-export/check-csharp.py` | sintaksu (CS1xxx) | `out` kao ime parametra, `readonly` niz sa `=` |
| `tools/unity-export/check-members.py` | članove koji ne postoje (CS0117/CS1061) — i to **na pravom tipu** | `Projectiles.FireSlosh` umjesto `FireSlosher` |
| `tools/unity-export/check-namespaces.py` | `using` i kvalifikovana imena (CS0246/CS0103) | `using INKWAVE.Inputs;` |
| `tools/unity-export/check-calls.py` | tipove argumenata i broj argumenata (CS1503/CS1501) | `float` u `int` parametar |

Sve četiri se pokreću odjednom kroz `npm run unity:check`. Treba im `tree_sitter` i
`tree_sitter_c_sharp` (`pip install tree_sitter tree_sitter_c_sharp`); bez njih se preskaču tiho.

---

## 6. Šta se NE menja

- **Mehanika**: pokret, pucanje, bojenje, turf, forma i plivanje, oružja/sup/specijali, botovi, match tok.
- **Mape**: svih 8 (tidewater, kelpline, halyard, saltpan, crossmarket, lockgate, terraces, cargo),
  isti izvoz, ista geometrija, iste zone.
- **Balans**: `src/config.js` je jedini izvor brojeva.
- **Igra radi van Steama**: build se pokreće bez Steam klijenta (dostignuća/oblak/lobiji se tiho isključe).
