// Actor: one squidkid (local player or bot). Owns the movement physics, the ink/health/special state.
//
// A 1:1 port of the web build's src/game/actor.js — the same kinematic character controller, the same numbers:
//  · feet: flat-footprint ground probe — exact on slopes, ledges hold until the whole footprint is off, curbs ≤ stepUp
//    are walked onto, the ground is stuck to within stepDown
//  · body: a capsule lifted above the step height, pushed out horizontally while grounded
//  · grounded velocity follows the ground plane
//  · horizontal: S-curve accel, eased braking, heading slew, plant-and-reverse
//  · jump buffer + coyote time, apex hang, hard-landing recovery
using System;
using System.Collections.Generic;
using INKWAVE.Core;
using INKWAVE.Data;
using INKWAVE.World;
using UnityEngine;

namespace INKWAVE.Gameplay
{
    public struct ActorIntent
    {
        public Vector3 Move;
        public bool Jump;
        public bool Squid;
        public bool Fire;
        public bool Sub;
        public bool Special;
    }

    public enum ActorForm { Kid, Squid }

    public class Actor
    {
        public int Team;
        public string Name;
        public bool IsLocal;
        public bool IsBot;
        public int Slot;

        public string WeaponId = "shooter";
        public JObj Weapon;
        public string SubId = "bomb";
        public JObj Sub;
        public string SpecialId = "slam";
        public JObj Special;

        // status effects (seconds)
        public float Track, TrackTeam = -1, Poison, Reveal, RevealTeam = -1, Shield;

        public Vector3 Pos;
        public Vector3 Vel;
        public float Yaw, AimYaw, AimPitch;
        public Vector3 AimDir = new Vector3(0, 0, 1);
        public Vector3 AimPoint;

        public ActorIntent Intent;
        bool _prevFire, _prevJump, _prevSub, _prevSpecial, _prevSquid;
        float _squidPressT = -1f, _firePressT = -1f;

        public bool Alive = true;
        public float Hp = 100f;
        public float Ink = 100f;
        public float Special = 0f;
        public bool SpecialActive;        // a special is running
        public bool Kraken;               // the kraken form
        public float KrakenCd;
        public bool Jet;                  // the ink jet
        public ActorForm Form = ActorForm.Kid;
        public bool Submerged, Climbing, Grounded;
        public int GroundTeam;         // 0 none, 1 own, 2 enemy
        public float RespawnTimer;
        public float Invuln;
        public float LastDamage = 99f, LastFire = 99f, InkIdle;
        public float DamageFromInk;
        public float HurtFlash;
        public Actor LastAttacker;
        public float FireFacing;
        public float AirTime, LandSpeed, LandT = 99f, HardLand;
        public float Coyote, JumpBuffer, FireBuffer, KidT = 99f;
        public float ClimbExit, ClimbV, InkWarnCd;
        public float YawVel;
        float? _faceTarget;
        public float SmoothY, SmoothYV;
        public bool OnEnemy;

        // visuals (the Unity view reads these)
        public float AnimTime, AnimSpeed, AnimVy, AnimCharge, AnimSpecial;
        public bool AnimFiring, AnimRolling, AnimInvuln, AnimLowInk, AnimInEnemyInk;
        public int AnimSurface;
        public float AnimTurnRate;
        public Vector3 AnimLocalMove;

        public readonly Gameplay.WeaponRunner Weapons;
        public Gameplay.BotBrain Bot;

        readonly Contacts _contacts = new Contacts();
        readonly GroundHit _ground = new GroundHit();
        readonly GroundHit _ground2 = new GroundHit();
        Vector3 _groundN = Vector3.up;
        readonly List<int> _railIds = new List<int>();
        Vector3 _roofDir; Vector3 _roofP; float _roofT, _roofStall;

        static JObj P => GameData.P;

        public Actor(int team, string name, bool isLocal = false, bool isBot = false, int slot = 0)
        {
            Team = team; Name = name; IsLocal = isLocal; IsBot = isBot; Slot = slot;
            SetWeapon("shooter");
            SetSub("bomb");
            SetSpecial("slam");
            Weapons = new WeaponRunner(this);
            Reset();
        }

        // ------------------------------------------------------------------------------------------------ state
        public void SetWeapon(string id)
        {
            WeaponId = id;
            Weapon = GameData.Weapon(id) ?? GameData.Weapon("shooter");
            if (Weapons != null) Weapons.Reset();
        }

        public void SetSub(string id)
        {
            var s = GameData.Sub(id);
            if (s == null) return;
            SubId = id; Sub = s;
        }

        public void SetSpecial(string id)
        {
            var s = GameData.Special(id);
            if (s != null) SpecialId = id;
        }

        public float SpecialCost() => Weapon.Float("specialCost", 100f);
        public float SpecialFrac() => MathUtil.Clamp(Special / Mathf.Max(1f, SpecialCost()), 0f, 1f);
        public bool SpecialReady() => Special >= SpecialCost();

        public void Reset()
        {
            Alive = true;
            Hp = P.Float("hp", 100f);
            Ink = P.Float("inkMax", 100f);
            Special = 0f;
            Form = ActorForm.Kid;
            Submerged = false; Climbing = false; Grounded = false;
            GroundTeam = 0;
            RespawnTimer = 0f;
            Invuln = 0f;
            LastDamage = 99f; LastFire = 99f; InkIdle = 0f;
            DamageFromInk = 0f;
            HurtFlash = 0f;
            LastAttacker = null;
            FireFacing = 0f;
            AirTime = 0f; LandSpeed = 0f; LandT = 99f; HardLand = 0f;
            Coyote = 0f; JumpBuffer = 0f; FireBuffer = 0f; KidT = 99f;
            ClimbExit = 0f; ClimbV = 0f; InkWarnCd = 0f;
            YawVel = 0f; _faceTarget = null;
            SmoothY = 0f; SmoothYV = 0f;
            OnEnemy = false;
            Track = 0f; Poison = 0f; Reveal = 0f; Shield = 0f;
            _roofT = 0f;
            Weapons?.Reset();
        }

        public void SpawnAt(Vector3 p, float yaw)
        {
            Reset();
            Pos = p;
            Vel = Vector3.zero;
            Yaw = AimYaw = yaw;
            AimPitch = 0f;
            Invuln = P.Float("spawnInvuln", 1.6f);
        }

        public void Respawn()
        {
            var pads = GameContext.Level.SpawnPads;
            int idx = Mathf.Clamp(Team, 0, Mathf.Max(0, pads.Count - 1));
            Vector3 p = pads.Count > 0 ? pads[idx] : Vector3.zero;
            float yaw = Team == 0 ? 0f : Mathf.PI;
            SpawnAt(p, yaw);
        }

        public void AddTurf(float area)
        {
            if (area <= 0f) return;
            GameContext.Emit("turf", new Dictionary<string, object> { { "actor", this }, { "area", area } });
            Special = Mathf.Min(SpecialCost(), Special + area * P.Float("specialChargeRate", 0.8f));
        }

        // ------------------------------------------------------------------------------------------------ damage
        public void Damage(float amount, Actor attacker, string source = "weapon")
        {
            if (!Alive || Invuln > 0f) return;
            // the sponge mitts: the gloves up soak part of a hit that comes from the front
            if (Weapons != null) amount *= Weapons.GuardScale(attacker);
            Hp -= amount;
            LastDamage = 0f;
            LastAttacker = attacker;
            HurtFlash = Mathf.Min(1f, HurtFlash + 0.5f);
            if (Hp <= 0f) Splat(attacker, source);
        }

        public void Splat(Actor attacker, string cause = "weapon")
        {
            if (!Alive) return;
            Alive = false;
            Hp = 0f;
            RespawnTimer = P.Float("respawnTime", 5.5f);
            float keep = Special * P.Float("specialKeepOnSplat", 0.5f);
            Special = 0f;
            Ink = 0f;
            GameContext.Emit("splat", new Dictionary<string, object>
            {
                { "actor", this }, { "attacker", attacker }, { "cause", cause }, { "keep", keep },
            });
        }

        // ------------------------------------------------------------------------------------------------ update
        public void Update(float dt)
        {
            AnimTime = GameContext.Time;
            if (!Alive)
            {
                RespawnTimer -= dt;
                if (RespawnTimer <= 0f && GameContext.Match != null && GameContext.Match.CanRespawn()) Respawn();
                return;
            }

            bool firePressed = Intent.Fire && !_prevFire;
            bool jumpPressed = Intent.Jump && !_prevJump;
            bool subReleased = !Intent.Sub && _prevSub;
            bool specialPressed = Intent.Special && !_prevSpecial;
            if (specialPressed && SpecialReady() && !SpecialActive) SpecialSystem.Use(this);
            if (Intent.Squid && !_prevSquid) _squidPressT = GameContext.Time;
            if (firePressed) _firePressT = GameContext.Time;
            _prevFire = Intent.Fire; _prevJump = Intent.Jump; _prevSub = Intent.Sub;
            _prevSpecial = Intent.Special; _prevSquid = Intent.Squid;

            Invuln = Mathf.Max(0f, Invuln - dt);
            LastDamage += dt; LastFire += dt; LandT += dt; KidT += dt;
            HurtFlash = Mathf.Max(0f, HurtFlash - dt * 0.6f);
            InkWarnCd -= dt;
            JumpBuffer = jumpPressed ? P.Float("jumpBuffer", 0.13f) : Mathf.Max(0f, JumpBuffer - dt);
            FireBuffer = firePressed ? P.Float("fireBuffer", 0.16f) : Mathf.Max(0f, FireBuffer - dt);
            HardLand = Mathf.Max(0f, HardLand - dt / Mathf.Max(0.01f, P.Float("hardLandTime", 0.16f)));

            float cp = Mathf.Cos(AimPitch);
            AimDir = new Vector3(Mathf.Sin(AimYaw) * cp, Mathf.Sin(AimPitch), Mathf.Cos(AimYaw) * cp);

            // ---- form: squid while the swim button is held. Swim + fire both held → the most recent press wins.
            bool fireWins = (Intent.Fire || FireBuffer > 0f) && _firePressT >= _squidPressT;
            bool wantSquid = Intent.Squid && !fireWins && !Weapons.Busy();
            if (wantSquid != (Form == ActorForm.Squid))
            {
                Form = wantSquid ? ActorForm.Squid : ActorForm.Kid;
                if (!wantSquid) KidT = 0f;
                GameContext.Emit("actor:form", new Dictionary<string, object>
                {
                    { "actor", this }, { "form", wantSquid ? "squid" : "kid" }, { "surface", GroundTeam },
                });
            }
            bool isSquid = Form == ActorForm.Squid;

            // ---- surface under the feet (from the last frame's ground probe; the position has not moved since)
            UpdateSurface();
            Submerged = isSquid && Grounded && GroundTeam == 1;
            OnEnemy = Grounded && GroundTeam == 2 && !Submerged;

            // ---- horizontal movement
            if (!Climbing) Horizontal(dt, isSquid, OnEnemy);

            // a dodge roll (the twins / the dualies) owns the velocity while it lasts
            Weapons.TwinDodgeVelocity(ref Vel);
            // a jump press while firing and moving rolls instead of jumping
            if (JumpBuffer > 0f && !isSquid && Weapons.TryDodge(Intent.Move)) JumpBuffer = 0f;

            // ---- jump (buffered, with coyote time)
            Coyote = Grounded ? P.Float("coyoteTime", 0.12f) : Coyote - dt;
            bool jumped = false;
            if (JumpBuffer > 0f && (Grounded || Coyote > 0f) && !Climbing)
            {
                float jv = Submerged ? P.Float("swimJumpVel", 9.4f) : P.Float("jumpVel", 8.4f);
                if (OnEnemy) jv *= 0.72f;
                Vel.y = jv;
                Grounded = false; Coyote = 0f; JumpBuffer = 0f;
                jumped = true;
                GameContext.Emit("actor:jump", new Dictionary<string, object>
                {
                    { "actor", this }, { "surface", GroundTeam }, { "swim", Submerged },
                });
            }

            // ---- integrate + collide (feet + body)
            Integrate(dt, isSquid, jumped);
            SpawnBarrier();

            // ---- ink / hp
            if (OnEnemy)
            {
                if (DamageFromInk < P.Float("enemyInkDamageCap", 40f) && Invuln <= 0f && Shield <= 0f)
                {
                    float d = Mathf.Min(P.Float("enemyInkDps", 20f) * dt, P.Float("enemyInkDamageCap", 40f) - DamageFromInk);
                    DamageFromInk += d;
                    Hp = Mathf.Max(1f, Hp - d);
                    HurtFlash = Mathf.Min(1f, HurtFlash + dt * 0.5f);
                }
                LastDamage = Mathf.Min(LastDamage, 0.4f);
            }
            else
            {
                DamageFromInk = Mathf.Max(0f, DamageFromInk - dt * 30f);
            }
            if (LastDamage > P.Float("regenDelay", 1.3f) && Hp < P.Float("hp", 100f))
                Hp = Mathf.Min(P.Float("hp", 100f), Hp + (Submerged ? P.Float("regenRateSwim", 60f) : P.Float("regenRate", 22f)) * dt);

            bool wasFull = Ink >= P.Float("inkMax", 100f);
            if (Track > 0f) Track = Mathf.Max(0f, Track - dt);
            if (Reveal > 0f) Reveal = Mathf.Max(0f, Reveal - dt);
            if (Poison > 0f)
            {
                Poison = Mathf.Max(0f, Poison - dt);
                Ink = Mathf.Max(0f, Ink - GameData.Sub("mist").Float("inkDrain", 8f) * dt);
            }
            // poisoned: no idle refill at all and half-speed refill while swimming, so the drain is felt
            if (Submerged || Climbing) Ink = Mathf.Min(P.Float("inkMax", 100f), Ink + P.Float("inkRefillSwim", 42f) * (Poison > 0f ? 0.5f : 1f) * dt);
            else if (Poison > 0f) { /* drain only */ }
            else if (!isSquid && LastFire > P.Float("inkRefillDelay", 0.9f) && !Weapons.Busy())
                Ink = Mathf.Min(P.Float("inkMax", 100f), Ink + P.Float("inkRefillKid", 9f) * dt);
            else if (isSquid) Ink = Mathf.Min(P.Float("inkMax", 100f), Ink + P.Float("inkRefillKid", 9f) * 0.5f * dt);

            // ---- weapons (a squid → kid pop-out holds the first shot for emergeDelay; a tap during it is buffered)
            bool fire = false, pressed = false;
            if (!isSquid && KidT >= P.Float("emergeDelay", 0.07f))
            {
                bool buffered = FireBuffer > 0f;
                fire = Intent.Fire || buffered;
                pressed = firePressed || buffered;
                FireBuffer = 0f;
            }
            Weapons.Update(dt, fire, pressed, Intent.Sub && !isSquid, subReleased && !isSquid);
            if (!Alive) return;

            // ---- fall into the sea
            float fallDeathY = P.Float("fallDeathY", -1.45f);
            if (Pos.y < fallDeathY && float.IsNegativeInfinity(GameContext.Level.GroundHeight(Pos.x, Pos.z, Pos.y + 0.6f)))
            {
                Splat(LastDamage < 4f ? LastAttacker : null, "water");
                return;
            }

            FinishFrame(dt, isSquid);
        }

        // ------------------------------------------------------------------------------------------------ surface
        void UpdateSurface()
        {
            var g = _ground;
            if (Grounded && g.Hit_ && g.Face >= 0)
            {
                byte t = GameContext.Paint.Sample(g.Face, g.U, g.V);
                GroundTeam = t == 0 ? 0 : (t - 1 == Team ? 1 : 2);
            }
            else GroundTeam = 0;
        }

        // ------------------------------------------------------------------ horizontal movement model
        void Horizontal(float dt, bool isSquid, bool onEnemy)
        {
            // while a dualies dodge roll runs, the weapon runner owns the horizontal velocity
            if (Weapons.DualDodgeVelocity(ref Vel)) return;
            // in the post-roll lock you are a planted turret
            Vector3 mv = Grounded && Weapons.LockT > 0f ? Vector3.zero : Intent.Move;
            float mh = Mathf.Sqrt(mv.x * mv.x + mv.z * mv.z);
            float mag = Mathf.Min(1f, mh);
            float vx = Vel.x, vz = Vel.z;
            float sp = Mathf.Sqrt(vx * vx + vz * vz);

            // ---- airborne: vector steering with light air control (momentum is kept)
            if (!Grounded)
            {
                float target, accel, decel;
                if (isSquid) { target = Mathf.Max(P.Float("squidDrySpeed", 2.9f), sp); accel = P.Float("squidAirAccel", 14f); decel = P.Float("squidAirDecel", 3f); }
                else { target = Mathf.Max(Weapons.MoveSpeed(), P.Float("airMinSpeed", 4.6f)); accel = P.Float("airAccel", 20f); decel = P.Float("airDecel", 4f); }
                float tvx = mh > 0.01f ? (mv.x / mh) * target * mag : 0f;
                float tvz = mh > 0.01f ? (mv.z / mh) * target * mag : 0f;
                float dvx = tvx - vx, dvz = tvz - vz, dl = Mathf.Sqrt(dvx * dvx + dvz * dvz);
                float rate = (mh > 0.01f ? accel : decel) * dt;
                if (dl <= rate) { Vel.x = tvx; Vel.z = tvz; }
                else { Vel.x += (dvx / dl) * rate; Vel.z += (dvz / dl) * rate; }
                return;
            }

            // ---- grounded: speed + heading model
            float vt, A, aIn, inKnee, outKnee, D, dMin, dKnee, W;
            if (isSquid && Submerged)
            {
                vt = P.Float("swimSpeed", 11.8f); A = P.Float("swimAccel", 64f); aIn = P.Float("swimAccelIn", 0.75f);
                inKnee = 3f; outKnee = P.Float("swimOutKnee", 0.22f); D = P.Float("swimDecel", 42f);
                dMin = 0.5f; dKnee = 4f; W = P.Float("swimTurn", 11f);
            }
            else if (isSquid)
            {
                vt = P.Float("squidDrySpeed", 2.9f); A = P.Float("squidAccel", 34f); aIn = 0.6f;
                inKnee = 1f; outKnee = 0.3f; D = P.Float("squidDecel", 26f);
                dMin = 0.5f; dKnee = 2f; W = P.Float("squidTurn", 13f);
            }
            else
            {
                vt = Weapons.MoveSpeed(); A = P.Float("runAccel", 70f); aIn = P.Float("runAccelIn", 0.5f);
                inKnee = P.Float("runInKnee", 1.6f); outKnee = P.Float("runOutKnee", 0.28f);
                D = P.Float("runDecel", 58f); dMin = P.Float("runDecelMin", 0.4f); dKnee = P.Float("runDecelKnee", 2.2f);
                W = P.Float("turnRate", 15f);
                if (HardLand > 0f) vt *= 1f - (1f - P.Float("hardLandSlow", 0.72f)) * HardLand;
            }
            if (onEnemy)
            {
                vt = Mathf.Min(vt, P.Float("enemyInkSpeed", 1.9f));
                A = Mathf.Min(A, P.Float("enemyInkAccel", 30f));
                D = Mathf.Max(P.Float("enemyInkDecel", 30f), 0f);
            }
            if (Poison > 0f) vt *= GameData.Sub("mist").Float("slow", 0.7f);
            if (Reveal > 0f) vt *= GameData.Special("sonar").Float("slow", 0.85f);

            if (mh < 0.01f)
            {
                // brake: strong at speed, easing into the stop
                if (sp < 1e-4f) { Vel.x = 0f; Vel.z = 0f; return; }
                float d = D * (dMin + (1f - dMin) * MathUtil.SmoothStep(0f, dKnee, sp)) * dt;
                float k = Mathf.Max(0f, sp - d) / sp;
                Vel.x *= k; Vel.z *= k;
                return;
            }

            float tx = mv.x / mh, tz = mv.z / mh, vts = vt * mag;
            float dx = tx, dz = tz;
            if (sp > 0.05f) { dx = vx / sp; dz = vz / sp; }
            float cosA = MathUtil.Clamp(dx * tx + dz * tz, -1f, 1f);
            float ang = Mathf.Acos(cosA);
            if (sp > 0.5f && ang > P.Float("reverseAngle", 2.2f))
            {
                // plant-and-reverse: brake through zero toward the new direction
                float tvx = tx * vts, tvz = tz * vts, ex = tvx - vx, ez = tvz - vz;
                float el = Mathf.Sqrt(ex * ex + ez * ez);
                float r = Mathf.Max(P.Float("reverseDecel", 78f), D) * dt * (onEnemy ? 0.5f : 1f);
                if (el <= r) { Vel.x = tvx; Vel.z = tvz; }
                else { Vel.x += (ex / el) * r; Vel.z += (ez / el) * r; }
                return;
            }

            // heading slews toward the input (faster when slow) — turns carve at full speed instead of dipping
            float wmax = W * (1f + P.Float("turnRateSlow", 1.5f) * (1f - MathUtil.SmoothStep(0f, vt, sp)));
            float rot = Mathf.Min(ang, wmax * dt);
            if (rot > 1e-6f)
            {
                float s = (dz * tx - dx * tz) >= 0f ? 1f : -1f;
                float c = Mathf.Cos(rot * s), sn = Mathf.Sin(rot * s);
                float nx = dx * c + dz * sn, nz = -dx * sn + dz * c;
                dx = nx; dz = nz;
            }

            float ns;
            if (sp < vts)
            {
                float a = A * (aIn + (1f - aIn) * MathUtil.SmoothStep(0f, inKnee, sp))
                        * MathUtil.Clamp((vts - sp) / (outKnee * vt), P.Float("runOutMin", 0.22f), 1f);
                ns = Mathf.Min(vts, sp + a * dt);
            }
            else
            {
                // over speed (swim exit glide, entering enemy ink, starting to fire): shed it at the brake rate
                ns = Mathf.Max(vts, sp - D * (dMin + (1f - dMin) * MathUtil.SmoothStep(0f, dKnee, sp - vts)) * dt);
            }
            Vel.x = dx * ns; Vel.z = dz * ns;
        }

        // ------------------------------------------------------------------ character controller
        void Integrate(float dt, bool isSquid, bool jumped)
        {
            if (Climbing)
            {
                Pos += Vel * dt;
                GameContext.Physics.CollideBody(ref Pos, P.Float("radius", 0.38f), P.Float("squidBodyLift", 0.16f),
                                                P.Float("squidHeight", 0.55f), _contacts, false, true);
                if (_contacts.Ceiling && Vel.y > 0f) Vel.y = 0f;
                Grounded = false;
                AirTime = 0f;
                return;
            }

            bool wasGrounded = Grounded && !jumped;

            // off-limits tops (roofs, crane tops …): nobody can stand there — you slide off
            var rb = wasGrounded && _ground.Hit_ && _ground.Block >= 0 ? GameContext.Level.Blocks[_ground.Block] : null;
            if (rb != null && rb.Roof) RoofSlide(rb, dt);
            else if (_roofT > 0f) { _roofT = 0f; }

            if (wasGrounded)
            {
                // follow the ground plane: the vertical component keeps the feet on the surface at the current speed
                Vector3 n = _groundN;
                Vel.y = -(Vel.x * n.x + Vel.z * n.z) / Mathf.Max(0.35f, n.y);
            }
            else
            {
                float g = P.Float("gravity", 25f);
                if (Vel.y < 0f) g *= P.Float("fallGravityMul", 1.2f);
                if (Mathf.Abs(Vel.y) < P.Float("apexBand", 1.6f)) g *= P.Float("apexGravityMul", 0.82f);
                Vel.y = Mathf.Max(-P.Float("maxFall", 40f), Vel.y - g * dt);
            }
            float prevY = Pos.y;
            Pos += Vel * dt;
            Resolve(isSquid, prevY, wasGrounded);
        }

        /// Standing on a roof block: pushed toward its nearest edge, picking up speed until you drop off.
        void RoofSlide(Block b, float dt)
        {
            Vector3 n = b.Axes[1];
            if (_roofT <= 0f)
            {
                _roofStall = 0f; _roofP = Pos;
                if (n.y < 0.995f) _roofDir = new Vector3(n.x, 0f, n.z).normalized;
                else
                {
                    Vector3 ax = b.Axes[0], az = b.Axes[2];
                    float dx = Pos.x - b.Center.x, dz = Pos.z - b.Center.z;
                    float lx = dx * ax.x + dz * ax.z, lz = dx * az.x + dz * az.z;
                    bool alongX = b.Half.x - Mathf.Abs(lx) < b.Half.z - Mathf.Abs(lz);
                    Vector3 a = alongX ? ax : az;
                    float l = alongX ? lx : lz;
                    float sign = l >= 0f ? 1f : -1f;
                    _roofDir = new Vector3(a.x * sign, 0f, a.z * sign);
                }
            }
            _roofT += dt;
            float moved = (Pos.x - _roofP.x) * _roofDir.x + (Pos.z - _roofP.z) * _roofDir.z;
            _roofP = Pos;
            _roofStall = _roofT > 0.1f && moved < 0.3f * dt ? _roofStall + dt : 0f;
            if (_roofStall > 0.25f) { _roofDir = new Vector3(-_roofDir.z, 0f, _roofDir.x); _roofStall = 0f; }
            float want = Mathf.Min(9f, 3f + 14f * _roofT);
            float along = Vel.x * _roofDir.x + Vel.z * _roofDir.z;
            if (along < want) { Vel.x += _roofDir.x * (want - along); Vel.z += _roofDir.z * (want - along); }
        }

        /// Body (walls/ceilings) then feet (ground stick / landing). `stick` = we were grounded and did not jump.
        void Resolve(bool isSquid, float prevY, bool stick)
        {
            float lift = isSquid ? P.Float("squidBodyLift", 0.16f) : P.Float("stepUp", 0.35f);
            float height = isSquid ? P.Float("squidHeight", 0.55f) : P.Float("height", 1.45f);
            var c = GameContext.Physics.CollideBody(ref Pos, P.Float("radius", 0.38f), lift, height, _contacts, stick, isSquid);
            if (c.Ceiling && Vel.y > 0f) Vel.y = 0f;
            if (c.Wall)
            {
                Vector3 n = c.WallNormal;
                float vn = Vel.x * n.x + Vel.z * n.z;
                if (vn < 0f) { Vel.x -= n.x * vn; Vel.z -= n.z * vn; }
            }

            var gh = _ground;
            float up = isSquid ? P.Float("squidStepUp", 0.24f) : P.Float("stepUp", 0.35f);
            bool wasGrounded = Grounded;
            bool grounded = false;
            if (stick)
            {
                GameContext.Physics.GroundProbe(Pos.x, Pos.y, Pos.z, up, P.Float("stepDown", 0.45f),
                                               P.Float("footRadius", 0.24f), gh, isSquid);
                if (!isSquid) RailFeet(gh, Pos.y - P.Float("stepDown", 0.45f), Pos.y + up);
                if (gh.Hit_)
                {
                    float dy = gh.Y - Pos.y;
                    Pos.y = gh.Y;
                    grounded = true;
                    // discontinuities (curbs, steps up / down) are eased visually
                    if (Mathf.Abs(dy) > 0.06f) SmoothY -= dy;
                }
            }
            else if (Vel.y <= 0.5f)
            {
                // landing: probe from the highest point this frame passed through (never tunnels), with ledge assist
                float top = Mathf.Max(prevY, Pos.y);
                float assist = isSquid ? P.Float("squidStepUp", 0.24f) : P.Float("ledgeAssist", 0.35f);
                GameContext.Physics.GroundProbe(Pos.x, Pos.y, Pos.z, (top - Pos.y) + assist, 0.02f,
                                               P.Float("footRadius", 0.24f), gh, isSquid);
                if (!isSquid) RailFeet(gh, Pos.y - 0.02f, top + assist);
                if (gh.Hit_ && gh.Y >= Pos.y - 0.02f && (Vel.y <= 0f || gh.Y - Pos.y < 0.02f))
                {
                    float pop = gh.Y - prevY;
                    Pos.y = gh.Y;
                    grounded = true;
                    if (pop > 0.035f) SmoothY -= pop;
                }
            }
            if (grounded)
            {
                _groundN = gh.Normal;
                if (!wasGrounded) OnLand(isSquid);
                Vel.y = 0f;
            }
            Grounded = grounded;
            AirTime = grounded ? 0f : AirTime + 1f / 60f;
        }

        /// Railings are only a few cm thick, so the ground probe's discrete footprint ring can straddle one and miss
        /// it. A kid's feet are also held by any level rail top the flat footprint overlaps.
        void RailFeet(GroundHit gh, float lo, float hi)
        {
            var lvl = GameContext.Level;
            if (!lvl.HasRails) return;
            float x = Pos.x, z = Pos.z, fr = P.Float("footRadius", 0.24f);
            lvl.QueryBlocks(x - fr - 0.05f, z - fr - 0.05f, x + fr + 0.05f, z + fr + 0.05f, _railIds);
            Block best = null;
            float bestY = gh.Hit_ ? gh.Y + 1e-3f : float.NegativeInfinity;
            for (int i = 0; i < _railIds.Count; i++)
            {
                var b = lvl.Blocks[_railIds[i]];
                if (!b.Rail || !b.Solid || b.Axes[1].y < 0.999f) continue;
                float topY = b.Center.y + b.Half.y;
                if (topY < lo || topY > hi || topY <= bestY) continue;
                float dx = x - b.Center.x, dz = z - b.Center.z;
                float ex = Mathf.Max(0f, Mathf.Abs(dx * b.Axes[0].x + dz * b.Axes[0].z) - b.Half.x);
                float ez = Mathf.Max(0f, Mathf.Abs(dx * b.Axes[2].x + dz * b.Axes[2].z) - b.Half.z);
                if (ex * ex + ez * ez > fr * fr) continue;   // the footprint misses its top
                best = b; bestY = topY;
            }
            if (best == null) return;
            gh.Hit_ = true; gh.Y = bestY; gh.Normal = Vector3.up; gh.Block = best.Id; gh.Face = -1;
            gh.U = 0f; gh.V = 0f; gh.Center = false; gh.Grate = true;
        }

        void OnLand(bool isSquid)
        {
            float speed = Mathf.Max(0f, -Vel.y);
            LandSpeed = speed;
            LandT = 0f;
            if (speed > P.Float("hardLandSpeed", 11.5f))
                HardLand = MathUtil.Clamp((speed - P.Float("hardLandSpeed", 11.5f)) / 6f + 0.5f, 0f, 1f);
            if (speed > 3f)
            {
                Grounded = true;
                UpdateSurface();
                GameContext.Emit("actor:land", new Dictionary<string, object>
                {
                    { "actor", this }, { "speed", speed }, { "surface", GroundTeam },
                });
            }
        }

        void SpawnBarrier()
        {
            var pads = GameContext.Level.SpawnPads;
            if (pads.Count < 2) return;
            var pad = pads[1 - Team];
            float R = GameContext.Level.SpawnBarrier;
            if (R <= 0f) return;
            float dx = Pos.x - pad.x, dz = Pos.z - pad.z;
            float d = Mathf.Sqrt(dx * dx + dz * dz);
            if (d < R && Pos.y > pad.y - 1.0f)
            {
                float k = (R - d) / Mathf.Max(d, 0.01f);
                Pos.x += dx * k; Pos.z += dz * k;
                float vn = (Vel.x * dx + Vel.z * dz) / Mathf.Max(d, 0.01f);
                if (vn < 0f) { Vel.x -= (dx / d) * vn * 1.6f; Vel.z -= (dz / d) * vn * 1.6f; }
            }
        }

        // ------------------------------------------------------------------------------------------------ facing
        void Face(float dt, bool isSquid)
        {
            Vector3 mv = Intent.Move;
            float mh = Mathf.Sqrt(mv.x * mv.x + mv.z * mv.z);
            float hs = Mathf.Sqrt(Vel.x * Vel.x + Vel.z * Vel.z);
            FireFacing = Mathf.Max(0f, FireFacing - dt);
            float? target = null;
            float omega = P.Float("faceOmega", 20f), maxRate = P.Float("faceMaxRate", 12.5f), maxAcc = P.Float("faceMaxAcc", 170f);
            if (Weapons.FiringPose() || FireFacing > 0f || Intent.Sub)
            {
                target = AimYaw; omega = P.Float("aimFaceOmega", 36f);
                maxRate = P.Float("aimFaceMaxRate", 24f); maxAcc = P.Float("aimFaceMaxAcc", 380f);
            }
            else if (Climbing) { target = Mathf.Atan2(-_groundN.x, -_groundN.z); omega = 26f; maxRate = 18f; }
            else
            {
                if (mh > 0.2f) target = Mathf.Atan2(mv.x, mv.z);
                else if (hs > 0.6f) target = Mathf.Atan2(Vel.x, Vel.z);
                if (isSquid)
                {
                    omega = P.Float("squidFaceOmega", 26f);
                    maxRate = Submerged ? P.Float("swimFaceMaxRate", 14f) : P.Float("squidFaceMaxRate", 17f);
                    maxAcc = P.Float("squidFaceMaxAcc", 260f);
                }
            }

            // feed-forward the target's own angular velocity, so the body tracks a smoothly moving target
            float targetRate = 0f;
            if (target.HasValue && _faceTarget.HasValue)
            {
                float d = MathUtil.AngleDiff(_faceTarget.Value, target.Value);
                if (Mathf.Abs(d) < 0.12f) targetRate = MathUtil.Clamp(d / Mathf.Max(dt, 1e-4f), -maxRate, maxRate);
            }
            _faceTarget = target;

            float acc = target.HasValue
                ? omega * omega * MathUtil.AngleDiff(Yaw, target.Value) + 2f * omega * (targetRate - YawVel)
                : -2f * omega * YawVel;
            YawVel = MathUtil.Clamp(YawVel + MathUtil.Clamp(acc, -maxAcc, maxAcc) * dt, -maxRate, maxRate);
            if (Mathf.Abs(YawVel) < 1e-5f) YawVel = 0f;
            Yaw += YawVel * dt;
            if (Yaw > Mathf.PI) Yaw -= Mathf.PI * 2f; else if (Yaw < -Mathf.PI) Yaw += Mathf.PI * 2f;
            AnimTurnRate = YawVel;
        }

        // ------------------------------------------------------------------------------------------------ frame
        void FinishFrame(float dt, bool isSquid)
        {
            Face(dt, isSquid);
            float hs = Mathf.Sqrt(Vel.x * Vel.x + Vel.z * Vel.z);
            float cy = Mathf.Cos(Yaw), sy = Mathf.Sin(Yaw);
            AnimSpeed = hs;
            float inv = 1f / Mathf.Max(hs, 0.001f);
            float lmx = hs > 0.1f ? (Vel.x * cy - Vel.z * sy) * inv : 0f;
            float lmz = hs > 0.1f ? (Vel.x * sy + Vel.z * cy) * inv : 0f;
            AnimLocalMove = new Vector3(-lmx, 0f, lmz);
            AnimVy = Vel.y;
            AnimFiring = Weapons.FiringPose();
            AnimCharge = Weapons.Charge;
            AnimRolling = Weapons.Rolling;
            AnimInvuln = Invuln > 0f;
            AnimLowInk = Ink < 18f;
            AnimSpecial = SpecialFrac();
            AnimInEnemyInk = OnEnemy;
            AnimSurface = Grounded ? GroundTeam : 0;

            // visual step smoothing (critically damped, ~0.12 s)
            const float w = 24f;
            float acc = -w * w * SmoothY - 2f * w * SmoothYV;
            SmoothYV += acc * dt; SmoothY += SmoothYV * dt;
            if (Mathf.Abs(SmoothY) > 0.7f) SmoothY = Mathf.Sign(SmoothY) * 0.7f;
            if (Mathf.Abs(SmoothY) < 1e-4f && Mathf.Abs(SmoothYV) < 1e-3f) { SmoothY = 0f; SmoothYV = 0f; }
        }

        /// The visual (smoothed) world position of the feet — cameras and name tags follow this.
        public Vector3 VisualPos => new Vector3(Pos.x, Pos.y + SmoothY, Pos.z);
    }
}
