# INKWAVE — analiza projekta i plan izlaska na Steam

Datum: 2026-10-18 · repo: `Blagii/inkwave` (fork od `jaydendavisnc/inkwave`, MIT, © 2026 Jayden Davis)

> **Status: odluka je promenjena (2026-10-18) — prelazimo na Unity.** Faza 0/1/2 (Electron + Steamworks) su
> implementirane i ostaju u repou kao fallback, ali se finalni desktop build pravi u Unity-ju, a three.js i
> Electron se izbacuju iz proizvoda. Mehanika, mape i balans ostaju isti.
> Detalji: [`docs/UNITY-PORT.md`](UNITY-PORT.md) · (stari plan: [`docs/STEAM.md`](STEAM.md)).
>
> | | |
> |---|---|
> | ✅ Faza 0 | `npm run package:mac` / `package:win` / `package:linux` — tri platforme, kros-platformske ikone (`build/make-icon.cjs`) |
> | ✅ Faza 1 | Steamworks binding (`steamworks-ffi-node`), 13 dostignuća, Rich Presence, Steam Cloud, store stats, IPC most |
> | ✅ | `npm run steam:verify` (šta nedostaje) · `npm run steam-test` (logika dostignuća) · `npm run prepare:steam` (pravi app id) |
> | ✅ Faza 2 | Steam Deck: low preset na detekciju + Steam Input layout (trackpad + gyro → miš) — **treba test na uređaju** |
> | ✅ | CI workflow (check + win/mac/linux packaging) + kros-platformski lookup browsera za sve headless alate |
> | ⬜ Faza 3 | Steam lobbies / pozivi (CF relay danas radi i ostaje) |
> | ⬜ Faza 4 | Store page, demo, wishlist kampanja, $100 fee |

---

## 0. Rezime (TL;DR)

**Ne prelazite na Unity.** Za ovaj projekat Unity nije "migracija" nego **ponovno pisanje ~100.000 linija
JavaScript-a u C#**, i to baš onih delova koji čine igru posebnom (proceduralna geometrija, proceduralan
zvuк, proceduralan UI). Realan trošak: **9–14 meseci full-time solo** (2–3 godine part-time), uz veliki
rizik da rezultat bude *gori* od sadašnjeg, a ne bolji.

**Steam ne zahteva Unity.** Igru već pakiraš u Electron. Dodavanje Steamworks-a na postojeći Electron build
je put koji je **luda 6–10 nedelja** do pravog Steam build-a, i to uz **0% gubitka postojećeg koda**.

Preporuka: **zadrži three.js + Electron, dodaj Steamworks integraciju.** Unity razmotri samo ako budući plan
zaista zahteva nešto što web/Electron ne može (npr. AAA budžet, konzolne ekskluzive, moderan AAA pipeline) —
i u tom slučaju to je novi projekat, ne port ovog.

---

## 1. Šta je INKWAVE zapravo

| | |
|---|---|
| Žanr | 4v4 turf-war shooter u trećem licu (Splatoon-like) |
| Grafika | three.js r186, **vendor-ovan** (bez build koraka, ES moduli + import map) |
| Umesto | 100.018 linija JS u `src/` (139 modula) + 106.513 ukupno sa toolingom |
| Sadržaj | 7 stage-ova, 12 oružja, 15 sub-ova, 19 special-a, 3 moda (Turf War, Zone Control, Boss) |
| Online | WebSocket relay na Cloudflare Durable Object (`server/`), 5-znakovni kod sobe, 8 igrača |
| Desktop | Electron (`electron/main.cjs`) — **trenutno se pakuje samo za macOS** |
| Web | statički build, hostovan na Cloudflare Pages |
| Jezici | EN + Simplified Chinese (`src/i18n/`) |
| Asseti | **sve proceduralno** — geometrija, teksture, 60-bone rig, animacije, SFX i muzika generišu se u kodu |
| Licenca | MIT |

### 1.1 Raspodela koda (linije)

```
src/game     36.954  (37%)  actor, oružje, kitovi, subovi, speciali, botovi, character rig + animacija
src/world    34.740  (35%)  nivo/level, ink painting, teksture, environment, props, 7 stage foldera
src/ui        9.907  (10%)  menus, HUD, diorama, ikone   (+ 4.126 linija CSS-a)
src/fx        4.671  ( 5%)  čestice, screen efekti
src/boss      4.474  ( 4%)  HULLBREAKER boss fight (model, AI, hazardi)
src/audio     4.172  ( 4%)  WebAudio sinteza: SFX + sekvencer za muziku
src/net       1.778  ( 2%)  session, netmatch, transport
src/i18n        860  ( 1%)
```

### 1.2 Arhitektonske osobine koje bitno utiču na odluku

1. **Nema build koraka.** Sve je ES moduli + import mapa. Izmena je "refresh browsera". To je *brzina* razvoja,
   ali i *loša vest* za Unity: svaki od 139 modula postaje C# klasa + assembly + asset.
2. **Sve je proceduralno.** Nema .fbx/.png/.wav fajlova u repou (osim 2 fonta). U Unity-u to znači da proceduralni
   generatori moraju da se **prepišu na C#** (nova disciplina, drugi jezik, ista matematika) — ili da se eksportuju
   u mesh fajlove i izgubi "sve se menja iz koda" filozofija koja je ovu igru napravilo jeftinom za napraviti.
3. **Mrežni protokol je čist tekst preko WebSocket-a.** Server (`server/src/index.js`) je ~200 linija JS-a i
   **ne dira se ni u jednom scenariju** — ni za Unity ni za Electron. To je jedini veliki sistem koji je "porting free".
4. **Fizika, navigacija i gameplay su čist JS** bez three.js zavisnosti (fizika = sopstveni OBB raycast,
   nav = sopstveni A*). Ta logika se prenosi 1:1 u bilo koji engine.
5. **Post-shading je težak**: GTAO + bloom + custom grade pass + HDR half-float MSAA target, sa posebnim
   putanjama za Apple GPU. Na Steam Deck (AMD Van Gogh) to zahteva Low/Medium preset (4K ink atlas → 2K).
6. **UI je DOM/CSS** (9.9k JS + 4.1k CSS), ne canvas. U Unity-u → UI Toolkit ili UGUI = potpuno prepisivanje.

---

## 2. Opcije i realni troškovi

### Opcija A — Unity (C# rewrite)

Ovo **nije port**. Jedini način da se prebaciš na Unity je da isti sistem ponovno napišeš u C#.

| Podsistem | LOC | Unity trošak | Napomena |
|---|---|---|---|
| Proceduralni character (geo/mats/outfit/hair/face/style/weapons/character.js) | ~13.000 | 400–600 h | najskuplji pojedinačni blok; 60-bone rig + spring pozovi |
| Props / environment / 7 stage foldera | ~20.000 | 300–450 h | alternativno: eksport u glTF (three.js ima GLTFExporter), ali shadere/materijali se i dalje pišu ispočetka |
| Gameplay logika (actor, weapons, kits, subs, specials, bots) | ~20.000 | 250–350 h | prenosi se gotovo 1:1, ali Unity idiomi + prefabi + ScriptableObjecti |
| UI (menus + HUD + CSS) | ~14.000 | 250–400 h | UI Toolkit (UXML/USS) ili UGUI — oba su drugi jezik |
| Proceduralni audio (SFX + muzika) | ~4.200 | 150–250 h | WebAudio ne postoji u Unity-ju → FMOD (licenciran) ili custom DSP |
| Ink sistem (GPU atlas + CPU grid + GLSL) | ~1.000 | 120–160 h | GLSL → HLSL, RenderTexture + CommandBuffer; ovo je *srce* igre |
| Boss mode | ~4.500 | 100–150 h | model, AI, faze, hazardi |
| Fizika (custom OBB → PhysX) | ~1.000 | 60–100 h | + ponovno balansiranje feel-a |
| Navigacija (custom A* → NavMesh) | ~500 | 40–60 h | bake, jump/drop edge case-ovi |
| Netcode (WebSocket, isti protokol) | ~1.800 | 80–120 h | protokol ostaje isti; C# WebSocket klijent |
| Config/i18n/tooling/CI/Steam SDK | — | 150–250 h | |
| QA, balans, polish, Deck test | — | 200–400 h | |
| **UKUPNO** | **100k** | **2.100–3.300 h** | **≈ 9–14 meseci full-time solo** |

**Dodatni rizici Unity puta:**
- Proceduralna generacija u C# daje *vizuelno drugačije* rezultate → ponovno balansiranje svega (osvetljenje,
  scale, hitbox-ovi, osećaj oružja). Projekat već ima `tools/measure-handling.mjs` i `tools/film.py` za
  deterministička merenja — taj alat ne prelazi u Unity automatski.
- Unity build (~150–250 MB) + CI + multi-platform export pipeline, umesto jednog `electron-packager` komande.
- Gubiš deterministički headless test harness (`npm run smoke`, `botlab`, `net-test`) — mora se preraditi u
  Unity Test Framework + batch mode.
- Dok traje 9–14 meseci, **igra ne postoji za igrače** — nema wishlist-a, nema feedback-a, nema early access-a.
  Steam Direct fee ti tik-a i 30-dnevni čekaju u tom periodu.

**Kada bi Unity imao smisla:** ako bi isti tim pravio *novu* igru istog žanra, sa iskustvom iz ovog projekta
(gamedev dizajn, ink mehanika, netcode dizajn) — tada bi Unity bio legitimna platforma za novi projekat, i
ovi 100k linija bi bili "iskustvo", ne "sirovina". Ali to je drugi projekat, sa svojim budžetom i vremenom.

### Opcija B — Electron + three.js + Steamworks (preporuka)

**Steam ne traži Unity.** Steamworks SDK je C++, ali postoje binding-i za Node/Electron:

| Biblioteka | Pristup | Overlay | Stanje | Preporuka |
|---|---|---|---|---|
| `steamworks-ffi-node` | Koffi FFI (bez kompajliranja), SDK 1.63/1.64 | **native** (Metal / Win32 GL / GLX) — radi i na macOS | aktivno, TS tipovi, širok API (lobbies, achievements, stats, cloud, workshop, rich presence, P2P) | ✅ **izbor** |
| `steamworks.js` (ceifa) | Rust/NAPI, bez kompajliranja | samo Electron helper, problematičan na macOS | "poor maintenance" | fallback |
| `greenworks` | C++ addon, treba build za svaki electron/node par | NW.js prvenstvo | best-effort, staro | ❌ ne |

Dobri prekret: `steam-electron-build` (github.com/alexanderthurn) — gotov pipeline: overlay, achievements,
saves + **Steam Deck fix-ovi**.

**Trošak: ~150–250 h (6–10 nedelja).**

| Faza | Sadržaj | H |
|---|---|---|
| 0 | Win/Linux build (jedna izmena u `package.json` skripti: `--platform=win32,linux,darwin`), provera boot-a na Win | 15–25 |
| 1 | Steamworks SDK u main procesu + IPC most ka rendereru; achievements (iz `PROGRESSION`/rank sistema), Rich Presence, Cloud Saves (settings + profil + unlocks) | 50–80 |
| 2 | Steam Input + **Deck podrška**: trackpad→miš, **gyro aim** na desnom sticku (Steam Input podržava gyro), default config koji se šalje uz igru | 25–40 |
| 3 | Online: zadržati CF relay (već radi, 0 rizika) **ili** prebaciti na Steam lobbies (invite, P2P, bez troška servera) — preporuka: CF relay za v1, Steam lobbies za 1.1 | 15–40 |
| 4 | Deck/low-end performanse (4K→2K ink atlas, isključen GTAO, dinamička rezolucija), crash handler, prvi launch flow, updater | 30–45 |
| 5 | Steamworks API: 4 achievements minimum, Steam Cloud, Rich Presence, inviti, build config | (u Fazi 1) |
| 6 | Store page (capsule, 5+ screenshotova, trailer), demo, wishlist kampanja | 2–4 nedelje |

**Bonus:** web build ostaje besplatan (itch.io / Cloudflare Pages) kao "funnel" za wishlist.

### Opcija C — prelazak na drugi JS/3D framework (Babylon.js, Godot, Bevy…)

- **Babylon.js**: API je blizak three.js-u, ali **ne rešava ništa vezano za Steam**. Prebacivanje bi donelo
  WebGPU, bolji editor, ali bi zahtevalo da ponovo napišeš 100k linija render-era (paint atlas, post chain,
  custom materijali) radi nule dobitka. **Ne.**
- **Godot 4**: ponovno pisanje u GDScript/C#, ista cena kao Unity (~9–14 meseci), a dobiješ manji build i
  open-source srž. Radi samo ako ti je bitan *asset pipeline*, a ne Steam.
- **Unity WebGL wrapper** (Unity školjka koja učitava web build): daje Steam achievements "na papiru", ali
  WebGL na Deck-u je loš, build je ogroman, i izgleda kao prečica. **Ne.**
- **Tauri**: manji binary, ali gubi Node runtime → Steam pozivi moraju u Rust stranu; više posla, manje koristi.

---

## 3. Uporedni pregled

| | Unity | Electron + Steam | CF relay → Steam lobbies | "Drugi framework" |
|---|---|---|---|---|
| Nedovršen kod koji se gubi | ~100% | ~0% | ~0% | 30–100% |
| Vreme do Steam build-a | 9–14 meseci | 6–10 nedelja | +2–3 nedelje | 6–18 meseci |
| Rizik pada kvaliteta | visok | nizak | nizak | srednje-visok |
| Steam Deck | da (ali skupo) | da (Steam Input + gyro) | da | zavisi |
| Veličina build-a | ~150–250 MB | ~150–250 MB (Chromium) | isto | zavisi |
| Steam overlay | odličan | radi (proveriti na Win/macOS) | isto | — |
| Server trošak | Steam lobbies = 0 | CF relay ≈ 0 | 0 | 0 |

---

## 4. Preporučeni plan (Opcija B)

### Faza 0 — 3–5 dana: „luka na Windows"
```bash
# package.json: --platform=darwin,win32,linux
npm run package
```
Proveriti: boot preko `app://` protokola na Windows 10/11, punscreen/windowed, gamepad, `npm run smoke` bez grešaka.

### Faza 1 — 2 nedelje: Steamworks
- `npm i steamworks-ffi-node` (+ redistributable binari iz Steamworks SDK-a u `steamworks_sdk/redistributable_bin`)
- `electron/main.cjs`: `steam.init({ appId })` u `whenReady`, `restartAppIfNecessary` guard, `shutdown` na quit.
- IPC: `ipcMain.handle('steam:achievement', ...)` → renderer (`src/ui/hud.js` / `src/game/match.js` već emituju
  tipizovane evente preko `src/core/ctx.js` bus-a → idealna tačka za kačenje).
- Achievements mapirane na postojeći sistem: rank tier (`RANK_TIERS`), prvi splat, 10/50/100 splatova,
  boje mode-i (turf/zones/boss), win streak, level 10/25/50, zone capture, boss kill.
- Rich Presence: trenutni mod + mapa + rank.
- Steam Cloud: `settings` (localStorage) + `profile` (unlocks/XP/style) u jedan JSON fajl.

### Faza 2 — 1 nedelja: Steam Deck
- Default Steam Input config: trackpad = miš, desni stick = **gyro aim** (Steam Input gyro podržava od 2023),
  gumbi po standardnim imenima.
- Low preset kao podrazumevani na Deck-u; 2K ink atlas; isključen GTAO.
- Proveriti 60 fps u worst-case scenariju (8 igrača + boss + 4K ink + bloom).

### Faza 3 — 1–2 nedelje: Online
- **v1**: zadržati Cloudflare relay — protokol je odličan, već testiran (`npm run net-test`), 0 promena.
- **1.1**: Steam lobbies + `ISteamNetworkingSockets` za invite/P2P; zadržati CF relay kao fallback.
- Proveriti: `ERR_*` kodovi i i18n poruke (`src/net/errors.js`, `src/i18n/`) da se ne pokvare.

### Faza 4 — 2–4 nedelje: Store page + release
1. Steamworks nalog + porezna/bankarska verifikacija (2–7 radnih dana) → **plati $100 odmah** (30-dnevni čekaj
   tada teče paralelno sa razvojem).
2. Store page: capsule 616×353, 5+ screenshotova (1920×1080), trailer (60–90 s), opis, tagovi
   (≥5: *Shooter, Third-Person, Colorful, Competitive, Team-Based, Arena, Multiplayer, Indie*).
3. „Coming Soon" stranica javno vidljiva **≥ 2 nedelje** pre release-a.
4. Build review (3–5 radnih dana) + minimum 21–30 dana od plaćene fee.
5. Demo (besplatan) za wishlist growth; opciono **Next Fest**.

### Faza 5 — kontinuirano
- Ostavi web build besplatan (itch.io / Pages) — funnel za wishlist i community feedback.
- Fazi 1.1: prevod na nemački/španacski/korejski (i18n je već centralizovan → mala cena).

---

## 5. Rizici i "šta ne raditi"

### Pravno / IP
- **MIT dozvoljava komercijalnu upotrebu**, ali traži zadržavanje copyright notice-a (`© 2026 Jayden Davis`).
  Repo je tvoj fork; **uzmi pismenu saglasnost od Jaydena pre komercijalnog release-a** i dogovorite ko stoji
  kao publisher na Steam-u. Nema CLA-a u projektu, pa je "ko drži prava" trenutno **nerazrešen** — to reši pre
  nego što platiš $100.
- **Nintendo / Splatoon**: igra je originalna po dizajnu (README eksplicitno kaže da nije u vezi sa Nintendo),
  ali na Steam store page-u **ne koristi reč "Splatoon"** u naslovu, opisu ni tagovima. Koristi
  "ink shooter", "turf war", "color-splashing 4v4". Nintendo je aktivan u trademark parnicama protiv
  klonova — uradi brzi pravni pregled pre lansiranja (cena: par stotina evra).
- **Steam AI disclosure**: upitnik o sadržaju traži otkrivanje AI-generisanog sadržaja. Tvoja igra je
  *kod-generisana* (to nije AI), ali ako koristiš AI za store art/trailer/marketing — prijavi to.

### Tehničko
- **Steam overlay u Electron-u** je najčešći problem. `steamworks-ffi-node` ima native most
  (Metal/Win32/GLX) — testiraj Shift+Tab na **svim** platformama pre release-a; ako overlay ne radi na
  macOS, koristi programski `activateOverlay()` i sopstvenu "toast" notifikaciju za dostignuća.
- **electron `contextIsolation`**: steamworks binding-i traže `nodeIntegration: true`. To je security smell
  (README to već radi sa `contextIsolation: true, sandbox: true`). Ostavi `sandbox: false` samo za main
  proces; u renderer učitaj **samo lokalne fajlove** (već radiš `app://` protokol sa CSP-om — zadržati to).
- **Chromium u Steam build-u** = ~150–250 MB download. Za "$9.99 igra" je to osetno, ali Steam toleriše
  (FNF i slični HTML5 naslovi). Ako smanjiš: `--prune` ili `asar` + uklanjanje devDependencies iz bundle-a.
- **Steam Deck performanse** su najveći tehnički rizik: GTAO + bloom + HDR + proceduralan atlas na Van Gogh
  APU. Mora biti "Deck-verified" ili bar "playable" — meri na pravom hardveru, ne na emulaciji.
- **CI/CD**: trenutni workflow ne postoji u ovom forku (`.github/workflows` je prazan). Dodaj Windows build
  u CI, inače Windows build pada kad god se promeni nešto.

### Strateški
- **Ne plati $100 dok ne prođeš "smoke test tržišta"**: objavi besplatnu web verziju + itch.io demo, pogledaj
  da li ljudi igraju. 30 dana čekanja + 2 nedelje Coming Soon su "free" ali prozor za povrat u igru nije.
- **Online bez matchmaking-a je problem za zadržavanje igrača**. CF relay radi za "igraj sa prijateljima";
  za launch ti treba quick-match (bots popunjavaju — već imaš `bots.js`, 3 nivoa difficulty) ili Steam lobbies
  sa matchmaking-om.
- **Boss mode (HULLBREAKER) je najjači marketing asset** — co-op protiv giganta. To je ono što se prodaje na
  traileru. Ubaci ga u prvi trailer.

---

## 6. Zaključak

> Projekat je **već gotova igra** sa ozbiljnim sadržajem (7 stage-ova, 3 moda, 12+ oružja, 19 special-a,
> boss fight, online, 2 jezika, kompletan proceduralni pipeline). Umanjenje ili "prebaci na drugi engine"
> **briše 100.000 linija verifikovanog, izbalansiranog koda da bi se dobila platforma koja Steam ne traži.**

Korak koji te zapravo deli od Steam release-a nije *engine* — nego:
1. pisanje u vlasništvo (dogovor sa Jaydenom),
2. Windows/Linux build,
3. Steamworks binding + achievements/cloud/overlay,
4. Deck podrška (Steam Input + gyro),
5. store page + trailer + wishlist kampanja.

Sve to je **radno vreme, ne arhitektonska promena**. Preporučeni redosled: Faza 0 → 1 → 2 → 4 (store page),
pa Faza 3 (online) kad je core na Steamu stabilan.

**Ako ipak odlučiš za Unity**, uradi to kao *novi projekat* sa jasnim ciljem (PC-only, procišćen scope,
bez boss moda, 3–4 stage-a, 4 oružja) i sa 6 meseci "learning + vertical slice" pre bilo kakve odluke o
Steam Direct fee-u. Ali tada planiraj 2–3 godine, ne 9 meseci, i ne očekuj da ćeš prebaciti ovaj kod —
očekuj da ćeš ga *zaboraviti*.
