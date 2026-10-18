// The Sponge Mitts: punch out ink fists that burst a few metres ahead; with the gloves up the sponge soaks part of
// any hit from the front; hold fire and press jump to charge a long leap that splashes where you land.
//
// A port of the web build's src/game/kits/mitts.js (the gameplay core: the punches, the guard, the leap and the
// landing splash). The glove meshes, the wall cling and the arc preview are not ported.
using System.Collections.Generic;
using INKWAVE.Core;
using INKWAVE.Data;
using INKWAVE.World;
using InkPhysics = INKWAVE.World.Physics;   // UnityEngine.Physics would be ambiguous
using UnityEngine;

namespace INKWAVE.Gameplay
{
    public class MittsKit
    {
        public Actor Owner;
        public float Cd, GuardT;
        public bool Charging, Full, Leaping, Guard;
        public float Charge, ChargeT, LeapT;
        public Vector3 LeapV;
    }

    /// The mitts' fists: a flying blob that bursts where it lands.
    public class MittFist
    {
        public Actor Owner;
        public int Team;
        public Vector3 Pos, Prev, Vel;
        public float Age, Life, Radius, Damage;
    }

    public partial class WeaponRunner
    {
        public MittsKit Mitts;

        // ------------------------------------------------------------------------------- the mitts
        public void MittsUpdate(float dt, bool fire, bool firePressed, JObj w)
        {
            var a = A;
            if (Mitts == null) Mitts = new MittsKit { Owner = a };
            var k = Mitts;
            k.Cd -= dt;
            k.GuardT = Mathf.Max(0f, k.GuardT - dt);

            // the guard: the gloves are up while the trigger is held (it soaks the hits from the front)
            k.Guard = fire && a.Form == ActorForm.Kid && !k.Leaping;
            if (k.Guard) k.GuardT = w.Float("guardAfter", 0.25f);

            // the punches
            if (fire && k.Cd <= 0f && a.Form == ActorForm.Kid && !k.Leaping)
            {
                Punch(a, w);
                k.Cd = w.Float("punchInterval", 0.12f);
            }

            // the leap charge (started by the jump press while firing)
            if (k.Charging)
            {
                k.ChargeT += dt;
                float maxC = Mathf.Clamp01((a.Ink - w.Float("leapInkMin", 9f)) / Mathf.Max(0.01f, w.Float("leapInkMax", 15f) - w.Float("leapInkMin", 9f)));
                k.Charge = Mathf.Min(maxC, k.ChargeT / Mathf.Max(0.05f, w.Float("leapChargeTime", 0.45f)));
                Charge = k.Charge;
                a.LastFire = 0f; a.FireFacing = 0.5f;
                if (k.Charge >= 0.999f) k.Full = true;
                if (!a.Intent.Jump) ReleaseLeap(k, w);
            }
            else if (k.Leaping)
            {
                k.LeapT += dt;
                if (a.Grounded)
                {
                    k.Leaping = false;
                    LandSplash(a, a.Pos, Vector3.up, w);
                }
                else if (a.Form != ActorForm.Kid || k.LeapT > 4f) k.Leaping = false;
                else
                {
                    // the launch keeps its horizontal speed
                    a.Vel.x = k.LeapV.x; a.Vel.z = k.LeapV.z;
                    a.FireFacing = 0.3f; a.LastFire = 0f;
                }
            }
        }

        /// The jump press starts the leap charge (the mitts take the jump instead).
        public bool MittsJump(Vector3 move)
        {
            var a = A;
            var k = Mitts;
            if (k == null) return false;
            if (k.Leaping || k.Charging) return false;
            if (a.Form != ActorForm.Kid || !a.Grounded || !a.Alive) return false;
            if (!a.Intent.Fire) return false;
            k.Charging = true; k.Charge = 0f; k.ChargeT = 0f; k.Full = false;
            return true;
        }

        void Punch(Actor a, JObj w)
        {
            float inkPerPunch = w.Float("inkPerPunch", 1.6f);
            if (a.Ink < inkPerPunch) { Empty(); return; }
            a.Ink -= inkPerPunch; a.LastFire = 0f;
            FiringT = 0.25f; a.FireFacing = 0.4f;

            Vector3 m = Projectiles.Muzzle(a);
            Vector3 dir = Projectiles.AimFrom(a, m, out _);
            float spread = w.Float("punchSpread", 2.5f) * Mathf.Deg2Rad;
            float speed = w.Float("fistSpeed", 28f);
            for (int hand = 0; hand < 2; hand++)
            {
                float s = (UnityEngine.Random.value - 0.5f) * 2f * spread;
                var v = Quaternion.AngleAxis(s * Mathf.Rad2Deg, Vector3.up) * dir;
                var p = new MittFist
                {
                    Owner = a, Team = a.Team, Pos = m + (hand == 0 ? Vector3.left : Vector3.right) * 0.16f,
                    Age = 0f, Life = w.Float("fistRange", 4.6f) / speed,
                    Radius = w.Float("fistSize", 0.2f),
                    Damage = w.Float("punchDamage", 38f),
                };
                p.Prev = p.Pos;
                p.Vel = v * speed;
                MittFists.List.Add(p);
            }
            GameContext.Emit("weapon:fire", new Dictionary<string, object>
            {
                { "actor", a }, { "weapon", w.Str("id") }, { "muzzle", m }, { "dir", dir },
            });
        }

        void ReleaseLeap(MittsKit k, JObj w)
        {
            var a = A;
            float c = k.Charge;
            k.Charging = false; Charge = 0f;
            a.Ink = Mathf.Max(0f, a.Ink - Mathf.Lerp(w.Float("leapInkMin", 9f), w.Float("leapInkMax", 15f), c));
            a.LastFire = 0f;

            // the launch: the angle follows the aim, the speed the charge
            float ang = Mathf.Clamp(w.Float("leapAngle", 40f) * Mathf.Deg2Rad + a.AimPitch * w.Float("leapPitchK", 0.55f),
                                    w.Float("leapAngleMin", 18f) * Mathf.Deg2Rad, w.Float("leapAngleMax", 60f) * Mathf.Deg2Rad);
            float v = Mathf.Sqrt(Mathf.Lerp(w.Float("leapSpeedMin", 10.9f) * w.Float("leapSpeedMin", 10.9f),
                                           w.Float("leapSpeedMax", 18.6f) * w.Float("leapSpeedMax", 18.6f), Mathf.Clamp01(c)));
            float vy = Mathf.Min(w.Float("leapVyMax", 13f), v * Mathf.Sin(ang));
            float vh = v * Mathf.Cos(ang);
            var vel = new Vector3(Mathf.Sin(a.AimYaw) * vh, vy, Mathf.Cos(a.AimYaw) * vh);
            a.Vel = vel; a.Grounded = false; a.Coyote = 0f; a.JumpBuffer = 0f;
            k.Leaping = true; k.LeapT = 0f; k.LeapV = new Vector3(vel.x, 0f, vel.z);

            // the push-off splat
            float area = GameContext.Paint.Splat(a.Pos + new Vector3(0f, 0.25f, 0f), 0.9f, a.Team, UnityEngine.Random.value);
            if (area > 0f) a.AddTurf(area);
            GameContext.Emit("mitts:leap", new Dictionary<string, object>
            {
                { "actor", a }, { "from", a.Pos }, { "charge", c },
            });
        }

        /// The landing: a splash of ink, the damage in the middle, and a moment of invulnerability.
        void LandSplash(Actor a, Vector3 at, Vector3 n, JObj w)
        {
            a.Invuln = Mathf.Max(a.Invuln, w.Float("landInvuln", 0.5f));
            float gloveDamage = w.Float("gloveDamage", 40f);
            var actors = GameContext.Match != null ? GameContext.Match.Actors : null;
            if (actors != null)
            {
                float reach = w.Float("gloveRadius", 1.1f) + GameData.P.Float("radius", 0.38f);
                for (int i = 0; i < actors.Count; i++)
                {
                    var e = actors[i];
                    if (e == null || e.Team == a.Team || !e.Alive) continue;
                    if (new Vector2(e.Pos.x - at.x, e.Pos.z - at.z).magnitude > reach) continue;
                    if (Mathf.Abs(e.Pos.y + 0.7f - at.y) > 1.6f) continue;
                    Projectiles.ApplyHit(a, e, gloveDamage);
                }
            }
            var c = at + n * 0.35f;
            float area = GameContext.Paint.Splat(c, w.Float("landPaint", 2.5f), a.Team, UnityEngine.Random.value);
            if (area > 0f) a.AddTurf(area);

            float landRadius = w.Float("landRadius", 2.8f);
            float maxD = w.Float("landDamageMax", 100f), minD = w.Float("landDamageMin", 35f);
            if (actors != null)
            {
                for (int i = 0; i < actors.Count; i++)
                {
                    var e = actors[i];
                    if (e == null || e.Team == a.Team || !e.Alive) continue;
                    var v = e.Pos + new Vector3(0f, e.Form == ActorForm.Squid ? 0.3f : 0.7f, 0f);
                    float d = (v - c).magnitude;
                    if (d > landRadius) continue;
                    if (!GameContext.Physics.Los(c, v)) continue;
                    float k = Mathf.Clamp01(d / landRadius);
                    Projectiles.ApplyHit(a, e, Mathf.Lerp(maxD, minD, k));
                }
            }
            GameContext.Emit("weapon:impact", new Dictionary<string, object>
            {
                { "pos", at }, { "normal", n }, { "team", a.Team }, { "kind", "blast" },
            });
        }

        /// The guard soaks part of a hit that comes from the front.
        public float GuardScale(Actor attacker)
        {
            var k = Mitts;
            if (k == null || !k.Guard) return 1f;
            var w = A.Weapon;
            float armor = w.Float("guardArmor", 0.5f);
            float arc = w.Float("guardArc", 70f) * Mathf.Deg2Rad * 0.5f;
            if (attacker == null) return 1f;
            var d = attacker.Pos - A.Pos;
            d.y = 0f;
            if (d.sqrMagnitude < 1e-4f) return 1f;
            d.Normalize();
            var fwd = new Vector3(Mathf.Sin(A.Yaw), 0f, Mathf.Cos(A.Yaw));
            float ang = Mathf.Acos(Mathf.Clamp(Vector3.Dot(d, fwd), -1f, 1f));
            if (ang > arc) return 1f;
            return 1f - armor;
        }
    }

    /// The mitts' fists in flight.
    public static class MittFists
    {
        public static readonly List<MittFist> List = new List<MittFist>();
        public static void Clear() => List.Clear();

        public static void Tick(float dt)
        {
            var actors = GameContext.Match != null ? GameContext.Match.Actors : null;
            for (int i = List.Count - 1; i >= 0; i--)
            {
                var p = List[i];
                var w = p.Owner != null ? p.Owner.Weapon : null;
                if (w == null) { List.RemoveAt(i); continue; }
                p.Age += dt;
                p.Prev = p.Pos;
                p.Pos += p.Vel * dt;

                bool done = false;
                if (actors != null)
                {
                    float hr = GameData.P.Float("radius", 0.38f);
                    for (int e = 0; e < actors.Count; e++)
                    {
                        var a = actors[e];
                        if (a == null || a.Team == p.Team || !a.Alive) continue;
                        float h = a.Form == ActorForm.Squid ? GameData.P.Float("squidHeight", 0.55f) : GameData.P.Float("height", 1.45f);
                        if (Mathf.Abs(a.Pos.x - p.Pos.x) > 2f + hr || Mathf.Abs(a.Pos.z - p.Pos.z) > 2f + hr) continue;
                        var res = new SegmentResult();
                        InkPhysics.SegmentCapsuleDist(p.Prev, p.Pos, a.Pos, hr, h, res);
                        if (res.Dist < hr * 0.9f + p.Radius)
                        {
                            Projectiles.ApplyHit(p.Owner, a, p.Damage);
                            done = true;
                            break;
                        }
                    }
                }
                if (done) { List.RemoveAt(i); continue; }

                var hit = GameContext.Physics.Segment(p.Prev, p.Pos, null, true);
                if (hit.Hit_)
                {
                    // the fist bursts where it lands
                    float splash = w.Float("splashRadius", 1.4f);
                    float maxD = w.Float("splashMax", 22f), minD = w.Float("splashMin", 10f);
                    var c = hit.Point + hit.Normal * 0.1f;
                    if (actors != null)
                    {
                        for (int e = 0; e < actors.Count; e++)
                        {
                            var a = actors[e];
                            if (a == null || a.Team == p.Team || !a.Alive) continue;
                            var v = a.Pos + new Vector3(0f, 0.6f, 0f);
                            float d = (v - c).magnitude;
                            if (d > splash) continue;
                            if (!GameContext.Physics.Los(c, v)) continue;
                            Projectiles.ApplyHit(p.Owner, a, Mathf.Lerp(maxD, minD, Mathf.Clamp01(d / splash)));
                        }
                    }
                    float area = GameContext.Paint.Splat(c, w.Float("fistPaint", 0.95f), p.Team, UnityEngine.Random.value);
                    if (p.Owner != null && area > 0f) p.Owner.AddTurf(area);
                    GameContext.Emit("weapon:impact", new Dictionary<string, object>
                    {
                        { "pos", hit.Point }, { "normal", hit.Normal }, { "team", p.Team }, { "kind", "shot" },
                    });
                    List.RemoveAt(i);
                    continue;
                }

                if (p.Age > p.Life) List.RemoveAt(i);
            }
        }
    }
}
