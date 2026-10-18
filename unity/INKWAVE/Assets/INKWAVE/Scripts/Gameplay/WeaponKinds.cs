// The weapon kinds, ported from the web build's src/game/weapons.js.
//
// Each kind keeps its own behaviour: the roller's band roll, the twins' alternating pistols, the dualies' dodge
// roll, the spinner's charged burst, the slosher's wave and the splatling's stream. The numbers come from the
// weapon's own entry in the exported config, so the feel is the web build's.
using System.Collections.Generic;
using INKWAVE.Core;
using INKWAVE.Data;
using UnityEngine;

namespace INKWAVE.Gameplay
{
    public partial class WeaponRunner
    {
        // ---- the roller's band roll
        public float RollT, RollDist, Flick = -1f, FlickRecover;
        public int SwipeSide = 1;
        public Vector3 LastRollPos;
        public readonly Dictionary<Actor, float> RollHits = new Dictionary<Actor, float>();

        // ---- the twins / the dualies
        public float RollLock, SinceRoll = 9f, DodgeT, LockT, RollPaint, TurretT;
        public bool Dodge, Turret, RollQueued;
        public int RollsUsed, TwinSide = 1, Hand, DualRolls = 2;
        public Vector3 DodgeDir;

        // ---- the spinner / the splatling
        public float ChargeT, BurstT, BurstPower, BurstDur, BurstFrac, StreamCd;

        // ---- the slosher
        public float Slosh = -1f;

        /// The per-kind state (the roll, the dodge, the charge) resets with the weapon.
        public void ResetKinds()
        {
            RollT = 0f; RollDist = 0f; Flick = -1f; FlickRecover = 0f; RollHits.Clear();
            RollLock = 0f; SinceRoll = 9f; DodgeT = 0f; LockT = 0f; RollPaint = 0f; TurretT = 0f;
            Dodge = false; Turret = false; RollQueued = false; RollsUsed = 0; TwinSide = 1; Hand = 0; DualRolls = 2;
            ChargeT = 0f; BurstT = 0f; BurstPower = 0f; BurstDur = 0f; BurstFrac = 0f; StreamCd = 0f;
            Slosh = -1f; Streaming = false; SwipeSide = 1;
        }

        // ======================================================================================== the bucket
        /// The bucket: a short wind-up, then a volley of ink hurled in an arc (holding fire keeps hurling).
        public void Bucket(float dt, bool fire, bool firePressed, JObj w)
        {
            var a = A;
            if (Flick >= 0f)
            {
                Flick += dt; a.FireFacing = 0.45f;
                if (Flick >= w.Float("windup", 0.12f))
                {
                    Flick = -1f;
                    Projectiles.FireSloshBucket(a, w);
                    Cooldown = w.Float("fireInterval", 0.6f) - w.Float("windup", 0.12f);
                    FiringT = 0.35f;
                }
                return;
            }
            if (!(fire || firePressed) || Cooldown > 0f) return;
            float inkPerShot = w.Float("inkPerShot", 6f);
            if (a.Ink < inkPerShot) { Empty(); return; }
            a.Ink -= inkPerShot; a.LastFire = 0f;
            Flick = 0f;
        }

        // ======================================================================================== the brush
        /// The brush: dash along the ground leaving a thin trail, or swipe side to side to flick a spray.
        public void Brush(float dt, bool fire, bool firePressed, JObj w)
        {
            var a = A;
            if (Flick >= 0f)
            {
                Flick += dt; a.FireFacing = 0.4f;
                if (Flick >= 0.04f)
                {
                    Flick = -1f;
                    Projectiles.FireSwipe(a, w, SwipeSide);
                    Cooldown = w.Float("swipeInterval", 0.16f);
                    FiringT = 0.25f;
                }
                return;
            }
            float hs = new Vector2(a.Vel.x, a.Vel.z).magnitude;
            bool moving = a.Grounded && hs > 2f && new Vector2(a.Intent.Move.x, a.Intent.Move.z).magnitude > 0.3f;
            if ((firePressed || (fire && !moving)) && Cooldown <= 0f)
            {
                float swipeInk = w.Float("swipeInk", 2.2f);
                if (a.Ink < swipeInk) Empty();
                else
                {
                    a.Ink -= swipeInk; a.LastFire = 0f; Flick = 0f;
                    SwipeSide = -SwipeSide;
                    Rolling = false;   // the swipe stops the dash
                    return;
                }
            }

            bool brushing = fire && moving && a.Ink > 0.3f;
            RollT = brushing ? RollT + dt : 0f;
            if (brushing != Rolling)
            {
                Rolling = brushing;
                if (brushing) { LastRollPos = a.Pos; RollDist = 0f; RollHits.Clear(); }
            }
            if (!Rolling) return;
            a.LastFire = 0f;

            float fx = Mathf.Sin(a.Yaw), fz = Mathf.Cos(a.Yaw);
            float brushWidth = w.Float("brushWidth", 0.95f);
            var actors = GameContext.Match != null ? GameContext.Match.Actors : null;
            if (actors != null)
            {
                float brushHitCd = w.Float("brushHitCd", 0.35f);
                for (int i = 0; i < actors.Count; i++)
                {
                    var e = actors[i];
                    if (e == null || e.Team == a.Team || !e.Alive) continue;
                    float dx = e.Pos.x - a.Pos.x, dz = e.Pos.z - a.Pos.z, dy = e.Pos.y - a.Pos.y;
                    float fwd = dx * fx + dz * fz, lat = Mathf.Abs(dx * fz - dz * fx);
                    if (fwd > -0.2f && fwd < 1.2f && lat < brushWidth * 0.5f + 0.3f && Mathf.Abs(dy) < 1.2f)
                    {
                        float last;
                        if (!RollHits.TryGetValue(e, out last)) last = -9f;
                        if (GameContext.Time - last > brushHitCd)
                        {
                            RollHits[e] = GameContext.Time;
                            Projectiles.ApplyHit(a, e, w.Float("brushDamage", 30f));
                        }
                    }
                }
            }

            float moved = (a.Pos - LastRollPos).magnitude;
            if (moved < 0.22f) return;
            LastRollPos = a.Pos;
            a.Ink = Mathf.Max(0f, a.Ink - w.Float("brushInkPerMeter", 0.32f) * moved);
            var v = new Vector3(a.Pos.x + fx * 0.65f, a.Pos.y + 0.35f, a.Pos.z + fz * 0.65f);
            a.AddTurf(GameContext.Paint.Splat(v, brushWidth * 0.45f, a.Team, UnityEngine.Random.value,
                                              PaintSystem.Kind.Roll, new Vector3(fx, 0f, fz), 0.5f));
            GameContext.Emit("weapon:impact", new Dictionary<string, object>
            {
                { "pos", new Vector3(a.Pos.x + fx * 0.65f, a.Pos.y + 0.02f, a.Pos.z + fz * 0.65f) },
                { "team", a.Team }, { "kind", "roll" }, { "radius", brushWidth * 0.5f },
            });
        }

        // ======================================================================================== the roller
        /// The roller: a flick wind-up (a sheet of ink thrown forward) or a roll (the drum paints a band).
        public void Roller(float dt, bool fire, bool firePressed, JObj w)
        {
            var a = A;
            if (Flick >= 0f)
            {
                Flick += dt;
                a.FireFacing = 0.4f;
                if (Flick >= w.Float("flickWindup", 0.3f))
                {
                    Flick = -1f;
                    Projectiles.FireFlick(a, w);
                    Cooldown = w.Float("flickInterval", 1f) - w.Float("flickWindup", 0.3f);
                    FiringT = 0.25f;
                    FlickRecover = 0.18f;
                }
                return;
            }
            if (firePressed && Cooldown <= 0f)
            {
                float flickInk = w.Float("flickInk", 10f);
                if (a.Ink < flickInk) Empty();
                else { a.Ink -= flickInk; a.LastFire = 0f; Flick = 0f; return; }
            }

            bool canRoll = fire && a.Grounded && a.Ink > 0.5f && Cooldown <= 0.25f;
            RollT = canRoll ? RollT + dt : 0f;
            if (canRoll != Rolling)
            {
                Rolling = canRoll;
                if (canRoll) { LastRollPos = a.Pos; RollDist = 0f; }
            }
            if (fire && a.Ink <= 0.5f) Empty();
            if (!Rolling) return;
            a.LastFire = 0f;

            float hs = new Vector2(a.Vel.x, a.Vel.z).magnitude;
            float fx = Mathf.Sin(a.Yaw), fz = Mathf.Cos(a.Yaw);

            // roll damage in front of the drum
            var actors = GameContext.Match != null ? GameContext.Match.Actors : null;
            if (actors != null)
            {
                float rollWidth = w.Float("rollWidth", 1.6f);
                for (int i = 0; i < actors.Count; i++)
                {
                    var e = actors[i];
                    if (e.Team == a.Team || !e.Alive) continue;
                    float dx = e.Pos.x - a.Pos.x, dz = e.Pos.z - a.Pos.z, dy = e.Pos.y - a.Pos.y;
                    float fwd = dx * fx + dz * fz, lat = Mathf.Abs(dx * fz - dz * fx);
                    if (fwd > -0.2f && fwd < 1.35f && lat < rollWidth * 0.5f + 0.35f && Mathf.Abs(dy) < 1.2f && hs > 1f)
                    {
                        float last;
                        if (!RollHits.TryGetValue(e, out last)) last = -9f;
                        if (GameContext.Time - last > 0.5f)
                        {
                            RollHits[e] = GameContext.Time;
                            Projectiles.ApplyHit(a, e, w.Float("rollDamage", 30f));
                        }
                    }
                }
            }

            float moved = (a.Pos - LastRollPos).magnitude;
            if (moved < 0.28f) return;
            LastRollPos = a.Pos;
            a.Ink = Mathf.Max(0f, a.Ink - w.Float("rollInkPerMeter", 2f) * moved);

            // paint a stripe across the drum: kind 'roll' + the roll direction → a straight-edged band segment
            float area = 0f;
            float rx = fz, rz = -fx;
            var fwdDir = new Vector3(fx, 0f, fz);
            float rollWidth = w.Float("rollWidth", 1.6f);
            for (int i = -1; i <= 1; i++)
            {
                float off = i * rollWidth * 0.33f;
                var v = new Vector3(a.Pos.x + fx * 0.75f + rx * off, a.Pos.y + 0.35f, a.Pos.z + fz * 0.75f + rz * off);
                area += GameContext.Paint.Splat(v, 0.62f, a.Team, UnityEngine.Random.value, PaintSystem.Kind.Roll, fwdDir, 0f);
            }
            a.AddTurf(area);
            GameContext.Emit("weapon:impact", new Dictionary<string, object>
            {
                { "pos", new Vector3(a.Pos.x + fx * 0.75f, a.Pos.y + 0.02f, a.Pos.z + fz * 0.75f) },
                { "team", a.Team }, { "kind", "roll" }, { "radius", rollWidth * 0.5f },
            });
        }

        // ========================================================================================= the twins
        /// The twins: rapid alternating fire; after a dodge roll, holding fire plants you with both guns together.
        public void Twins(float dt, bool fire, JObj w)
        {
            var a = A;
            RollLock = Mathf.Max(0f, RollLock - dt);
            SinceRoll += dt;
            if (RollLock <= 0f && SinceRoll > w.Float("rollReset", 3f) && DodgeT <= 0f) RollsUsed = 0;
            if (Turret && (!fire || new Vector2(a.Intent.Move.x, a.Intent.Move.z).magnitude > 0.3f)) Turret = false;

            if (DodgeT > 0f)
            {
                DodgeT -= dt;
                if (DodgeT <= 0f)
                {
                    DodgeT = 0f;
                    Turret = fire;
                    // a second roll tapped during this one chains straight on
                    if (RollQueued) { RollQueued = false; if (TwinDodge(a.Intent.Move)) return; }
                }
                return;
            }
            if (!fire) { if (Cooldown < 0f) Cooldown = 0f; return; }
            FiringT = 0.35f; a.FireFacing = 0.5f;
            int guard = 0;
            while (Cooldown <= 0f && guard++ < 3)
            {
                float inkPerShot = w.Float("inkPerShot", 4f);
                if (a.Ink < inkPerShot) { Empty(); Cooldown += w.Float("fireInterval", 0.2f); break; }
                a.Ink -= inkPerShot; a.LastFire = 0f;
                Spread = SpreadDeg();
                TwinSide = -TwinSide;
                Projectiles.FireTwin(a, w, Spread, Turret ? 0 : TwinSide);
                Bloom = Mathf.Min(1f, Bloom + 0.25f);
                Cooldown += Turret ? w.Float("turretInterval", 0.12f) : w.Float("fireInterval", 0.2f);
            }
        }

        /// The twins' dodge roll: a jump press while firing with a direction rolls instead of jumping.
        public bool TwinDodge(Vector3 move)
        {
            var a = A;
            var w = a.Weapon;
            if (w.Str("kind") != "twins" || DodgeT > 0f || !a.Alive || !a.Grounded || a.Form == ActorForm.Squid) return false;
            if (!(FiringT > 0f || a.Intent.Fire)) return false;
            float ml = new Vector2(move.x, move.z).magnitude;
            if (ml < 0.3f || RollsUsed >= w.Int("rollCharges", 2)) return false;
            float rollInk = w.Float("rollInk", 12f);
            if (a.Ink < rollInk) { Empty(); return false; }
            a.Ink -= rollInk; a.LastFire = 0f;
            RollsUsed++;
            LockT = 0f; RollPaint = 0f;
            DodgeDir = new Vector3(move.x / ml, 0f, move.z / ml);
            DodgeT = w.Float("rollTime", 0.3f);
            GameContext.Emit("weapon:dodge", new Dictionary<string, object>
            {
                { "actor", a }, { "pos", a.Pos }, { "dir", DodgeDir },
            });
            return true;
        }

        // ======================================================================================= the dualies
        /// The dualies: the hands alternate; a jump press while firing rolls instead (then a locked turret).
        public void Dualies(float dt, bool fire, JObj w)
        {
            var a = A;
            if (Dodge)
            {
                DodgeT += dt;
                a.FireFacing = 0.5f; FiringT = 0.35f;
                RollPaint -= dt;
                if (RollPaint <= 0f && a.Grounded)
                {
                    // the roll smears a trail of ink behind it
                    RollPaint = 0.045f;
                    var v = new Vector3(a.Pos.x, a.Pos.y + 0.3f, a.Pos.z);
                    a.AddTurf(GameContext.Paint.Splat(v, 0.62f, a.Team, UnityEngine.Random.value, PaintSystem.Kind.Trail));
                }
                if (DodgeT >= w.Float("rollTime", 0.3f)) { Dodge = false; LockT = w.Float("lockTime", 0.5f); }
                return;   // no shots mid-roll
            }
            if (LockT > 0f) { LockT = Mathf.Max(0f, LockT - dt); a.FireFacing = 0.5f; FiringT = Mathf.Max(FiringT, 0.3f); }
            if (!fire && LockT <= 0f) DualRolls = w.Int("rolls", 2);
            if (!fire) { if (Cooldown < 0f) Cooldown = 0f; return; }
            FiringT = 0.35f; a.FireFacing = 0.5f;
            int guard = 0;
            while (Cooldown <= 0f && guard++ < 3)
            {
                float inkPerShot = w.Float("inkPerShot", 4f);
                if (a.Ink < inkPerShot) { Empty(); Cooldown += w.Float("fireInterval", 0.25f); break; }
                a.Ink -= inkPerShot; a.LastFire = 0f;
                Spread = SpreadDeg();
                Hand ^= 1;
                Projectiles.FireTwin(a, w, Spread, Hand == 1 ? 1 : -1);
                Bloom = Mathf.Min(1f, Bloom + 0.25f);
                Cooldown += LockT > 0f ? w.Float("lockInterval", 0.12f) : w.Float("fireInterval", 0.25f);
            }
        }

        /// The dualies' dodge roll.
        public bool DualDodge(Vector3 move)
        {
            var a = A;
            var w = a.Weapon;
            if (w.Str("kind") != "dualies" || Dodge || !a.Alive || !a.Grounded || a.Form == ActorForm.Squid) return false;
            if (AimingSub) return false;
            if (!(FiringT > 0f || a.Intent.Fire)) return false;
            float ml = new Vector2(move.x, move.z).magnitude;
            if (ml < 0.3f || DualRolls <= 0) return false;
            float rollInk = w.Float("rollInk", 12f);
            if (a.Ink < rollInk) { Empty(); return false; }
            a.Ink -= rollInk; a.LastFire = 0f;
            DualRolls--; LockT = 0f; RollPaint = 0f;
            DodgeDir = new Vector3(move.x / ml, 0f, move.z / ml);
            Dodge = true; DodgeT = 0f;
            GameContext.Emit("weapon:dodge", new Dictionary<string, object>
            {
                { "actor", a }, { "pos", a.Pos }, { "dir", DodgeDir },
            });
            return true;
        }

        /// The twins' roll owns the horizontal velocity (a fast-out ease: 1.5·dist/time → 0).
        public bool TwinDodgeVelocity(ref Vector3 vel)
        {
            if (!Dodge) return false;
            var w = A.Weapon;
            float u = Mathf.Min(1f, DodgeT / Mathf.Max(0.01f, w.Float("rollTime", 0.3f)));
            float sp = (1.5f * w.Float("rollDist", 3.2f) / w.Float("rollTime", 0.3f)) * (1f - u * u);
            vel.x = DodgeDir.x * sp; vel.z = DodgeDir.z * sp;
            return true;
        }

        /// The dualies' roll owns the horizontal velocity (the same fast-out ease).
        public bool DualDodgeVelocity(ref Vector3 vel)
        {
            if (!Dodge) return false;
            var w = A.Weapon;
            float u = Mathf.Min(1f, DodgeT / Mathf.Max(0.01f, w.Float("rollTime", 0.3f)));
            float sp = (1.5f * w.Float("rollDist", 2.8f) / w.Float("rollTime", 0.3f)) * (1f - u * u);
            vel.x = DodgeDir.x * sp; vel.z = DodgeDir.z * sp;
            return true;
        }

        /// The jump press that rolls instead of jumping.
        public bool TryDodge(Vector3 move)
        {
            string kind = A.Weapon.Str("kind");
            if (kind == "twins") return TwinDodge(move);
            if (kind == "dualies") return DualDodge(move);
            if (kind == "mitts") return MittsJump(move);   // the mitts take the jump: it charges the leap
            return false;
        }

        // ======================================================================================== the spinner
        /// The spinner: hold to charge, release for a burst of shots.
        public void Spinner(float dt, bool fire, JObj w)
        {
            var a = A;
            if (BurstT > 0f)
            {
                BurstT -= dt; StreamCd -= dt; FiringT = 0.2f; a.FireFacing = 0.5f; a.LastFire = 0f;
                int guard = 0;
                while (StreamCd <= 0f && guard++ < 4)
                {
                    Projectiles.FireStream(a, w, BurstPower, SpreadDeg());
                    StreamCd += w.Float("fireInterval", 0.07f);
                }
                if (BurstT <= 0f)
                {
                    BurstT = 0f; Cooldown = 0.2f;
                }
                return;
            }
            if (fire && Cooldown <= 0f)
            {
                if (!Charging)
                {
                    float inkFull = w.Float("inkFull", 100f);
                    if (a.Ink < inkFull * 0.15f) { Empty(); return; }
                    Charging = true; Charge = 0f; ChargeT = 0f;
                }
                ChargeT = Mathf.Min(1f, ChargeT + dt / Mathf.Max(0.05f, w.Float("chargeTime", 1f)));
                Charge = Mathf.Min(Mathf.Clamp01(a.Ink / Mathf.Max(1f, w.Float("inkFull", 100f))), ChargeT);
                a.FireFacing = 0.4f;
            }
            else if (Charging)
            {
                Charging = false;
                float c = Mathf.Max(0.15f, Charge);
                a.Ink = Mathf.Max(0f, a.Ink - w.Float("inkFull", 100f) * c);
                BurstPower = c;
                BurstT = Mathf.Lerp(w.Float("burstMin", 0.4f), w.Float("burstMax", 1.2f), c);
                BurstDur = BurstT; StreamCd = 0f;
                Charge = 0f; ChargeT = 0f;
            }
        }

        // ======================================================================================= the slosher
        /// The slosher: a short heave, then the wave leaves.
        public void Slosher(float dt, bool fire, JObj w)
        {
            var a = A;
            if (Slosh >= 0f)
            {
                Slosh += dt; a.FireFacing = 0.5f; FiringT = 0.35f;
                if (Slosh >= w.Float("windup", 0.13f))
                {
                    Slosh = -1f;
                    Projectiles.FireSlosher(a, w);
                    Cooldown = w.Float("fireInterval", 0.6f) - w.Float("windup", 0.13f);
                }
                return;
            }
            if (fire && Cooldown <= 0f)
            {
                float inkPerShot = w.Float("inkPerShot", 30f);
                if (a.Ink < inkPerShot) { Empty(); Cooldown = 0.2f; return; }
                a.Ink -= inkPerShot; a.LastFire = 0f;
                Slosh = 0f; FiringT = 0.35f; a.FireFacing = 0.5f;
            }
        }

        // ===================================================================================== the splatling
        /// The splatling: hold to spin up, release for a stream of shots.
        public void Splatling(float dt, bool fire, JObj w)
        {
            var a = A;
            if (Streaming)
            {
                BurstT -= dt;
                BurstFrac = Mathf.Max(0f, BurstT / Mathf.Max(0.01f, BurstDur));
                Charge = BurstFrac;
                FiringT = 0.3f; a.FireFacing = 0.5f;
                int guard = 0;
                while (Cooldown <= 0f && guard++ < 3 && BurstT > 0f)
                {
                    float inkPerShot = w.Float("inkPerShot", 2f);
                    if (a.Ink < inkPerShot) { Empty(); BurstT = 0f; break; }
                    a.Ink -= inkPerShot; a.LastFire = 0f;
                    Spread = SpreadDeg();
                    Projectiles.FireShooter(a, w, Spread);
                    Bloom = Mathf.Min(1f, Bloom + w.Float("bloomPerShot", 0.05f));
                    Cooldown += w.Float("fireInterval", 0.07f);
                }
                if (BurstT <= 0f)
                {
                    BurstT = 0f; Streaming = false; Charge = 0f; BurstFrac = 0f;
                    Cooldown = Mathf.Max(Cooldown, 0.22f);
                }
                return;
            }
            if (fire && Cooldown <= 0f)
            {
                if (!Charging)
                {
                    float inkPerShot = w.Float("inkPerShot", 2f);
                    if (a.Ink < inkPerShot * 5f) { Empty(); return; }
                    Charging = true; Charge = 0f; ChargeT = 0f;
                }
                ChargeT += dt;
                Charge = Mathf.Min(1f, ChargeT / Mathf.Max(0.05f, w.Float("chargeTime", 0.6f)));
                a.FireFacing = 0.45f;
            }
            else if (Charging)
            {
                Charging = false;
                BurstDur = Mathf.Lerp(w.Float("burstMin", 0.4f), w.Float("burstMax", 1.2f), Charge);
                BurstT = BurstDur; BurstFrac = 1f;
                Streaming = true; Cooldown = 0f; Bloom = 0f;
            }
        }
    }
}
