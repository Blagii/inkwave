// The weapons: the per-actor runner (cooldown, charge, bloom, the trigger) and the shared projectile list.
//
// A port of the web build's src/game/weapons.js. The shooter family (shooter / blaster / twins / splatling / brush)
// is ported shot-for-shot: the same ballistic correction, the same spread cone, the same straight-then-gravity
// integrator, the same trail drips and impact splats. The kinds with their own bespoke behaviour (the roller's
// band, the twins' dodge, the brush's swipe, the splatling's stream) fall back to their own tuning numbers
// (fireInterval, inkPerShot, projSpeed, damage, range, spread) — see docs/UNITY-PORT.md for the gap list.
using System;
using System.Collections.Generic;
using INKWAVE.Core;
using INKWAVE.Data;
using INKWAVE.World;
using UnityEngine;

namespace INKWAVE.Gameplay
{
    public class WeaponRunner
    {
        public readonly Actor A;
        public float Cooldown, Charge, FiringT, Bloom, Spread, EmptyCd;
        public bool Charging, Rolling, AimingSub;

        const float Deg = Mathf.PI / 180f;
        const float SimDt = 1f / 120f;

        public WeaponRunner(Actor actor)
        {
            A = actor;
            Reset();
        }

        public void Reset()
        {
            Cooldown = 0f; Charge = 0f; Charging = false; Rolling = false;
            FiringT = 0f; EmptyCd = 0f; AimingSub = false; Bloom = 0f; Spread = 0f;
        }

        public bool Busy() => Charging || Rolling || AimingSub;

        public bool FiringPose() => FiringT > 0f || Charging || Rolling;

        /// How fast the actor may run while holding this weapon.
        public float MoveSpeed()
        {
            var w = A.Weapon;
            float run = GameData.P.Float("runSpeed", 6f);
            switch (w.Str("kind"))
            {
                case "brush":
                    if (Rolling) return Mathf.Lerp(w.Float("moveSpeedFiring", 5f), w.Float("brushSpeed", 6f),
                                                   MathUtil.SmoothStep(0f, 0.35f, 0f));
                    return FiringT > 0f ? w.Float("moveSpeedFiring", 5f) : run;
                case "bucket":
                    return FiringT > 0f ? w.Float("moveSpeedFiring", 5f) : run;
                case "spinner":
                    if (Charging) return Mathf.Lerp(run * 0.75f, w.Float("moveSpeedCharging", 4f), Mathf.Min(1f, Charge * 2f));
                    return run;
                case "twins":
                    return FiringT > 0f ? w.Float("moveSpeedFiring", 5f) : run;
            }
            if (Charging && w.Str("kind") == "splatling")
                return Mathf.Lerp(run * 0.75f, w.Float("moveSpeedCharging", 4f), Mathf.Min(1f, Charge * 2.5f));
            if (Rolling) return Mathf.Lerp(w.Float("rollSpeed", 9f) * 0.5f, w.Float("rollSpeed", 9f), 0.5f);
            if (Charging) return Mathf.Lerp(run * 0.7f, w.Float("moveSpeedFiring", 5f), Mathf.Min(1f, Charge * 3f));
            if (FiringT > 0f) return w.Float("moveSpeedFiring", 5f);
            return run;
        }

        /// The current shot cone half-angle in degrees (the HUD crosshair uses this).
        public float SpreadDeg()
        {
            var w = A.Weapon;
            string kind = w.Str("kind");
            switch (kind)
            {
                case "shooter":
                case "splatling":
                case "dualies":
                {
                    float baseDeg = A.Grounded ? w.Float("spreadGround", 1.6f) : w.Float("spreadAir", 4f);
                    return baseDeg * Mathf.Lerp(w.Float("spreadFirst", 0.45f), 1f, Bloom);
                }
                case "blaster":
                    return A.Grounded ? w.Float("spread", 1.2f) : w.Float("spreadAir", 4f);
                case "twins":
                    return (A.Grounded ? w.Float("spreadGround", 1.6f) : w.Float("spreadAir", 4f)) * Mathf.Lerp(0.5f, 1f, Bloom);
                case "spinner":
                    return A.Grounded ? w.Float("spreadGround", 1.6f) : w.Float("spreadAir", 4f);
            }
            return 0f;
        }

        public void Update(float dt, bool fire, bool firePressed, bool sub, bool subReleased)
        {
            var w = A.Weapon;
            Cooldown -= dt; EmptyCd -= dt;
            FiringT = Mathf.Max(0f, FiringT - dt);
            if (!fire) Bloom = Mathf.Max(0f, Bloom - dt / w.Float("bloomRecover", 0.28f));
            Spread = SpreadDeg();

            string kind = w.Str("kind");
            if (kind == "charger") Charger(dt, fire, w);
            else Auto(dt, fire, w);

            // ---- sub weapon (the Splat Bomb by default)
            if (sub && !AimingSub)
            {
                AimingSub = true;
                float cost = A.Sub != null ? A.Sub.Float("inkCost", 70f) : 70f;
                if (A.Ink < cost) GameContext.Emit("lowink", new Dictionary<string, object> { { "actor", A }, { "need", cost } });
            }
            if (AimingSub) A.FireFacing = 0.3f;
            if (subReleased && AimingSub)
            {
                AimingSub = false;
                float cost = A.Sub != null ? A.Sub.Float("inkCost", 70f) : 70f;
                if (A.Ink >= cost)
                {
                    A.Ink -= cost;
                    A.LastFire = 0f;
                    Projectiles.ThrowBomb(A);
                }
            }
            if (!sub && !subReleased) AimingSub = false;
        }

        /// The automatic fire mode (shooter, blaster, and the fallback for the remaining kinds).
        void Auto(float dt, bool fire, JObj w)
        {
            if (!fire)
            {
                if (Cooldown < 0f) Cooldown = 0f;
                return;
            }
            FiringT = 0.35f;
            A.FireFacing = 0.5f;
            int guard = 0;
            while (Cooldown <= 0f && guard++ < 3)
            {
                float inkPerShot = w.Float("inkPerShot", 4f);
                if (A.Ink < inkPerShot) { Empty(); Cooldown += w.Float("fireInterval", 0.28f); break; }
                A.Ink -= inkPerShot;
                A.LastFire = 0f;
                Spread = SpreadDeg();
                Projectiles.FireShooter(A, w, Spread);
                Bloom = Mathf.Min(1f, Bloom + w.Float("bloomPerShot", 0.3f));
                Cooldown += w.Float("fireInterval", 0.28f);
            }
        }

        /// The charger (hold to charge, release to fire).
        void Charger(float dt, bool fire, JObj w)
        {
            if (fire && Cooldown <= 0f)
            {
                if (!Charging)
                {
                    float inkFull = w.Float("inkFull", 100f);
                    if (A.Ink < inkFull * 0.2f) { Empty(); return; }
                    Charging = true; Charge = 0f;
                }
                Charge = Mathf.Min(1f, Charge + dt / Mathf.Max(0.05f, w.Float("chargeTime", 1f)));
            }
            else if (Charging)
            {
                float inkFull = w.Float("inkFull", 100f);
                float cost = inkFull * Mathf.Max(0.2f, Charge);
                if (A.Ink >= cost)
                {
                    A.Ink -= cost;
                    A.LastFire = 0f;
                    Projectiles.FireCharger(A, w, Charge);
                }
                Charging = false; Charge = 0f;
                Cooldown = w.Float("fireInterval", 0.6f);
            }
        }

        void Empty()
        {
            if (EmptyCd > 0f) return;
            EmptyCd = 0.6f;
            GameContext.Emit("lowink", new Dictionary<string, object> { { "actor", A } });
        }
    }

    // ----------------------------------------------------------------------------------------- projectiles
    public class Shot
    {
        public Vector3 Pos, Prev, Start, Vel;
        public float Age, Life, Straight, Radius, Damage, Size, Trail, TrailEvery, TrailRadius, Grav, Drag, Seed;
        public int Team;
        public Actor Owner;
        public bool Dead;
    }

    public static class Projectiles
    {
        public static readonly List<Shot> List = new List<Shot>();
        static readonly SegmentResult _res = new SegmentResult();
        static readonly Hit _hit = new Hit();
        static readonly Hit _hit2 = new Hit();
        static readonly Vector3 _v = new Vector3();
        static readonly Vector3 _v2 = new Vector3();

        const float Deg = Mathf.PI / 180f;
        const float SimDt = 1f / 120f;

        public static void Clear() => List.Clear();

        // --------------------------------------------------------------------------------- the muzzle
        /// Where the shot leaves the actor (the hand / barrel), with a line-of-sight check.
        public static Vector3 Muzzle(Actor a)
        {
            Vector3 eye = a.Pos + new Vector3(0f, a.Form == ActorForm.Squid ? 0.4f : 1.05f, 0f);
            Vector3 m = eye + a.AimDir * 0.35f;
            if (!GameContext.Physics.Los(eye, m)) m = eye + a.AimDir * 0.3f;
            return m;
        }

        /// The direction from the muzzle toward the actor's aim point.
        static Vector3 AimFrom(Actor a, Vector3 from, out Vector3 out)
        {
            out = a.AimPoint - from;
            float d = out.magnitude;
            if (d < 2f || Vector3.Dot(out, a.AimDir) < 0f) out = a.AimDir;
            else out /= d;
            return out;
        }

        /// A random spread inside the cone.
        static void Spread(Vector3 dir, float deg)
        {
            if (deg <= 0f) return;
            float r = deg * Deg * Mathf.Sqrt(UnityEngine.Random.value);
            float t = UnityEngine.Random.value * Mathf.PI * 2f;
            Vector3 side = new Vector3(-dir.z, 0f, dir.x);
            if (side.sqrMagnitude < 1e-4f) side = Vector3.right;
            side.Normalize();
            Vector3 up = Vector3.Cross(dir, side);
            dir += side * (Mathf.Cos(t) * Mathf.Tan(r)) + up * (Mathf.Sin(t) * Mathf.Tan(r) * 0.55f);
            dir.Normalize();
        }

        /// Raise the launch direction so a gravity shot passes through `target` (the web build's _ballistic).
        static Vector3 Ballistic(Vector3 from, Vector3 dir, Vector3 target, float speed, float straight, float grav, float drag, float maxDist)
        {
            float hx = target.x - from.x, hz = target.z - from.z;
            float hd = Mathf.Sqrt(hx * hx + hz * hz);
            if (hd < 1.5f || hd > maxDist || grav == 0f) return dir;
            float dy = target.y - from.y;
            float hdir = Mathf.Sqrt(dir.x * dir.x + dir.z * dir.z);
            if (hdir < 1e-4f) return dir;

            Func<float, float> sim = pitch =>
            {
                float vh = Mathf.Cos(pitch) * speed, vy = Mathf.Sin(pitch) * speed, x = 0f, y = 0f, age = 0f;
                for (int i = 0; i < 90; i++)
                {
                    age += SimDt;
                    float px = x, py = y;
                    if (age > straight) { vy -= grav * SimDt; float k = 1f - drag * SimDt; vh *= k; vy *= k; }
                    x += vh * SimDt; y += vy * SimDt;
                    if (x >= hd) { float f = (hd - px) / Mathf.Max(1e-6f, x - px); return py + (y - py) * f; }
                    if (vh < 0.5f) break;
                }
                return -1e3f;
            };

            float p0 = Mathf.Atan2(dir.y, hdir), e0 = sim(p0) - dy;
            if (Mathf.Abs(e0) < 0.01f) return dir;
            float p1 = p0 - Mathf.Atan2(e0, hd), e1 = sim(p1) - dy;
            for (int it = 0; it < 4 && Mathf.Abs(e1) > 0.005f; it++)
            {
                float d = e1 - e0;
                if (Mathf.Abs(d) < 1e-6f) break;
                float p2 = p1 - e1 * (p1 - p0) / d;
                p0 = p1; e0 = e1; p1 = MathUtil.Clamp(p2, -1.2f, 1.2f); e1 = sim(p1) - dy;
            }
            if (Mathf.Abs(e1) > 0.25f || Mathf.Abs(p1 - Mathf.Atan2(dir.y, hdir)) > 0.35f) return dir;
            float cp = Mathf.Cos(p1);
            dir = new Vector3((dir.x / hdir) * cp, Mathf.Sin(p1), (dir.z / hdir) * cp);
            return dir;
        }

        // --------------------------------------------------------------------------------- firing
        public static void FireShooter(Actor a, JObj w, float spreadDeg)
        {
            Vector3 m = Muzzle(a);
            Vector3 dir = AimFrom(a, m, out _);
            dir = Ballistic(m, dir, a.AimPoint, w.Float("projSpeed", 24f), w.Float("straightTime", 0.22f), 28f, 0.8f, w.Float("range", 22f));
            Spread(dir, spreadDeg);
            var p = new Shot
            {
                Owner = a, Team = a.Team, Age = 0f, Life = 1.2f,
                Straight = w.Float("straightTime", 0.22f),
                Radius = w.Float("impactRadius", 0.42f),
                Damage = w.Float("damage", 28f),
                Size = 0.15f,
                Trail = -(2.5f - w.Float("trailEvery", 0.6f)),
                TrailEvery = w.Float("trailEvery", 0.6f),
                TrailRadius = w.Float("trailRadius", 0.22f),
                Grav = 28f, Drag = 0.8f,
                Seed = UnityEngine.Random.value,
            };
            p.Pos = m; p.Prev = m; p.Start = m;
            p.Vel = dir * w.Float("projSpeed", 24f);
            List.Add(p);
            GameContext.Emit("weapon:fire", new Dictionary<string, object>
            {
                { "actor", a }, { "weapon", w.Str("id") }, { "muzzle", m }, { "dir", dir },
            });
        }

        public static void FireCharger(Actor a, JObj w, float charge)
        {
            Vector3 m = Muzzle(a);
            Vector3 dir = AimFrom(a, m, out _);
            float speed = w.Float("projSpeed", 26f) * Mathf.Lerp(0.6f, 1.2f, charge);
            dir = Ballistic(m, dir, a.AimPoint, speed, w.Float("straightTime", 0.3f), 28f, 0.8f, w.Float("range", 26f));
            Spread(dir, 0.4f);
            var p = new Shot
            {
                Owner = a, Team = a.Team, Age = 0f, Life = 1.4f,
                Straight = w.Float("straightTime", 0.3f),
                Radius = w.Float("impactRadius", 0.7f) * Mathf.Lerp(0.6f, 1.4f, charge),
                Damage = Mathf.Lerp(w.Float("damageMin", 20f), w.Float("damageMax", 60f), charge),
                Size = 0.2f,
                Trail = 0f, TrailEvery = 0f, TrailRadius = 0.3f,
                Grav = 28f, Drag = 0.8f,
                Seed = UnityEngine.Random.value,
            };
            p.Pos = m; p.Prev = m; p.Start = m;
            p.Vel = dir * speed;
            List.Add(p);
            GameContext.Emit("weapon:fire", new Dictionary<string, object>
            {
                { "actor", a }, { "weapon", w.Str("id") }, { "muzzle", m }, { "dir", dir },
            });
        }

        public static void ThrowBomb(Actor a)
        {
            var sub = a.Sub ?? GameData.Sub("bomb");
            float speed = sub.Float("throwSpeed", 13.5f);
            float pitch = MathUtil.Clamp(a.AimPitch + 0.28f, -0.3f, 1.1f);
            float cp = Mathf.Cos(pitch);
            Vector3 pos = a.Pos + new Vector3(0f, 1.35f, 0f);
            Vector3 vel = new Vector3(
                Mathf.Sin(a.AimYaw) * cp * speed + a.Vel.x * 0.4f,
                Mathf.Sin(pitch) * speed + 1.5f,
                Mathf.Cos(a.AimYaw) * cp * speed + a.Vel.z * 0.4f);
            GameContext.Emit("bomb:throw", new Dictionary<string, object>
            {
                { "actor", a }, { "pos", pos }, { "team", a.Team }, { "radius", sub.Float("radius", 3.1f) },
            });
            // the bomb is simulated as a shot that explodes on impact
            var p = new Shot
            {
                Owner = a, Team = a.Team, Age = 0f, Life = sub.Float("fuse", 0.95f) + 2f,
                Straight = 0f,
                Radius = sub.Float("paintRadius", 2.7f),
                Damage = sub.Float("damageMax", 180f),
                Size = 0.3f,
                Trail = 0f, TrailEvery = 0f, TrailRadius = 0f,
                Grav = 26f, Drag = 0.6f,
                Seed = UnityEngine.Random.value,
            };
            p.Pos = pos; p.Prev = pos; p.Start = pos;
            p.Vel = vel;
            List.Add(p);
        }

        // --------------------------------------------------------------------------------- per-frame
        public static void Update(float dt)
        {
            for (int i = List.Count - 1; i >= 0; i--)
            {
                var p = List[i];
                if (Step(p, dt))
                {
                    List[i] = List[List.Count - 1];
                    List.RemoveAt(List.Count - 1);
                }
            }
        }

        static bool Step(Shot p, float dt)
        {
            p.Age += dt;
            p.Prev = p.Pos;
            if (p.Age > p.Straight) p.Vel.y -= p.Grav * dt;
            if (p.Drag > 0f) p.Vel *= 1f - p.Drag * dt * (p.Age > p.Straight ? 1f : 0f);
            p.Pos += p.Vel * dt;

            bool dead = false;

            // actors
            var actors = GameContext.Match != null ? GameContext.Match.Actors : null;
            if (actors != null)
            for (int ei = 0; ei < actors.Count; ei++)
            {
                var e = actors[ei];
                if (e == null || e.Team == p.Team || !e.Alive) continue;
                float h = e.Form == ActorForm.Squid ? GameData.P.Float("squidHeight", 0.55f) : GameData.P.Float("height", 1.45f);
                float hr = GameData.P.Float("radius", 0.38f);
                if (Mathf.Abs(e.Pos.x - p.Pos.x) > 3f + hr || Mathf.Abs(e.Pos.z - p.Pos.z) > 3f + hr) continue;
                Physics.SegmentCapsuleDist(p.Prev, p.Pos, e.Pos, hr, h, _res);
                if (_res.Dist < hr * 0.95f + p.Size)
                {
                    Vector3 at = Vector3.Lerp(p.Prev, p.Pos, _res.T);
                    ApplyHit(p.Owner, e, p.Damage);
                    GameContext.Emit("weapon:impact", new Dictionary<string, object>
                    {
                        { "pos", at }, { "normal", -p.Vel.normalized }, { "team", p.Team }, { "kind", "shot" },
                    });
                    dead = true;
                    break;
                }
            }

            // world
            if (!dead)
            {
                var hit = GameContext.Physics.Segment(p.Prev, p.Pos, _hit, true);
                if (hit.Hit_)
                {
                    Impact(p, hit);
                    dead = true;
                }
            }

            // trail drips
            if (!dead && p.TrailEvery > 0f)
            {
                p.Trail += p.Vel.magnitude * dt;
                if (p.Trail > p.TrailEvery)
                {
                    p.Trail = 0f;
                    var g = GameContext.Physics.Raycast(p.Pos, Vector3.down, 4f, _hit2, true);
                    if (g.Hit_)
                    {
                        Vector3 at = g.Point + g.Normal * 0.1f;
                        float area = GameContext.Paint.Splat(at, p.TrailRadius * (0.8f + UnityEngine.Random.value * 0.4f),
                                                            p.Team, UnityEngine.Random.value, PaintSystem.Kind.Trail);
                        Credit(p, area);
                    }
                }
            }

            if (!dead && p.Age > p.Life) dead = true;
            if (!dead && p.Pos.y < GameData.P.Float("waterY", -1.6f) - 1.8f) dead = true;
            return dead;
        }

        static void Impact(Shot p, Hit hit)
        {
            Vector3 at = hit.Point + hit.Normal * 0.14f;
            Vector3 dir = p.Vel.normalized;
            float rad = p.Radius * (0.85f + UnityEngine.Random.value * 0.3f);
            float area = GameContext.Paint.Splat(at, rad, p.Team, p.Seed, PaintSystem.Kind.Shot, dir, 0.7f);
            Credit(p, area);
            GameContext.Emit("weapon:impact", new Dictionary<string, object>
            {
                { "pos", hit.Point }, { "normal", hit.Normal }, { "team", p.Team }, { "kind", "shot" }, { "radius", rad },
            });
        }

        static void Credit(Shot p, float area)
        {
            if (p.Owner != null && area > 0f) p.Owner.AddTurf(area);
        }

        public static void ApplyHit(Actor attacker, Actor victim, float dmg)
        {
            if (victim == null || !victim.Alive || attacker == null) return;
            if (victim.Team == attacker.Team) return;
            victim.Damage(dmg, attacker, "weapon");
            GameContext.Emit("hit", new Dictionary<string, object>
            {
                { "attacker", attacker }, { "victim", victim }, { "damage", dmg },
            });
        }
    }
}
