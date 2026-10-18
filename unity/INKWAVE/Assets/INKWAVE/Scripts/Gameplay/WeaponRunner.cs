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
    public partial class WeaponRunner
    {
        public readonly Actor A;
        public float Cooldown, Charge, FiringT, Bloom, Spread, EmptyCd;
        public bool Charging, Rolling, AimingSub, Streaming;

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
            Streaming = false; ResetKinds();
        }

        public bool Busy() => Charging || Flick >= 0f || BurstT > 0f || DodgeT > 0f || Slosh >= 0f || Streaming
                              || Dodge || LockT > 0f || AimingSub;

        public bool FiringPose() => FiringT > 0f || Charging || Flick >= 0f || Rolling || BurstT > 0f || Turret
                                    || Slosh >= 0f || Streaming || Dodge || LockT > 0f;

        /// How fast the actor may run while holding this weapon.
        public float MoveSpeed()
        {
            var w = A.Weapon;
            string kind = w.Str("kind");
            float run = GameData.P.Float("runSpeed", 6f);
            switch (kind)
            {
                case "brush":
                    // brushing is faster than running; the swipes barely slow you
                    if (Rolling) return Mathf.Lerp(w.Float("moveSpeedFiring", 5.8f), w.Float("brushSpeed", 7.4f),
                                                   MathUtil.SmoothStep(0f, 0.35f, RollT));
                    return FiringT > 0f ? w.Float("moveSpeedFiring", 5.8f) : run;
                case "bucket":
                    if (Flick >= 0f) return w.Float("moveSpeedFiring", 4.2f) * 0.7f;
                    return FiringT > 0f ? w.Float("moveSpeedFiring", 4.2f) : run;
                case "spinner":
                    if (BurstT > 0f) return w.Float("moveSpeedFiring", 3f);
                    if (Charging) return Mathf.Lerp(run * 0.75f, w.Float("moveSpeedCharging", 3.3f), Mathf.Min(1f, Charge * 2f));
                    return run;
                case "twins":
                    return FiringT > 0f ? w.Float("moveSpeedFiring", 5.2f) : run;
                case "brolly":
                    if (Brolly != null && Brolly.Open > 0.2f) return w.Float("moveSpeedShield", 3.1f);
                    return FiringT > 0f ? w.Float("moveSpeedFiring", 4.6f) : run;
            }
            if (LockT > 0f) return 0f;                                    // the dualies: planted after a roll
            if (Streaming) return w.Float("moveSpeedFiring", 3.4f);      // the splatling stream
            if (Charging && kind == "splatling")
                return Mathf.Lerp(run * 0.75f, w.Float("moveSpeedCharging", 2.4f), Mathf.Min(1f, Charge * 2.5f));
            if (Slosh >= 0f) return w.Float("moveSpeedFiring", 4.2f) * 0.7f;   // the slosher's heave plants you a little
            // the roller: the drum has weight — the rolling speed builds up over ~0.45 s; the flick plants you
            if (Rolling) return Mathf.Lerp(w.Float("rollSpeed", 4.4f) * 0.5f, w.Float("rollSpeed", 4.4f),
                                          MathUtil.SmoothStep(0f, 0.45f, RollT));
            if (Flick >= 0f) return Mathf.Lerp(w.Float("moveSpeedFiring", 4.4f), w.Float("moveSpeedFiring", 4.4f) * 0.45f,
                                                Mathf.Clamp01(Flick / Mathf.Max(0.01f, w.Float("flickWindup", 0.22f))));
            if (FlickRecover > 0f) return Mathf.Lerp(run, w.Float("moveSpeedFiring", 4.4f) * 0.6f, FlickRecover / 0.18f);
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
                    if (kind == "dualies" && LockT > 0f) return w.Float("spreadLock", 2.2f);   // the locked turret: tight
                    float baseDeg = A.Grounded ? w.Float("spreadGround", 1.6f) : w.Float("spreadAir", 4f);
                    return baseDeg * Mathf.Lerp(w.Float("spreadFirst", 0.45f), 1f, Bloom);
                }
                case "blaster":
                    return A.Grounded ? w.Float("spread", 1.2f) : w.Float("spreadAir", 4f);
                case "twins":
                    if (Turret) return w.Float("turretSpread", 1.6f);
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
            FlickRecover = Mathf.Max(0f, FlickRecover - dt);
            if (!fire) Bloom = Mathf.Max(0f, Bloom - dt / w.Float("bloomRecover", 0.28f));
            Spread = SpreadDeg();

            switch (w.Str("kind"))
            {
                case "roller": Roller(dt, fire, firePressed, w); break;
                case "twins": Twins(dt, fire, w); break;
                case "dualies": Dualies(dt, fire, w); break;
                case "spinner": Spinner(dt, fire, w); break;
                case "slosher": Slosher(dt, fire, w); break;
                case "splatling": Splatling(dt, fire, w); break;
                case "bucket": Bucket(dt, fire, firePressed, w); break;
                case "brush": Brush(dt, fire, firePressed, w); break;
                case "brolly": BrollyUpdate(dt, fire, w); break;
                case "blade": BladeUpdate(dt, fire, firePressed, w); break;
                case "charger": Charger(dt, fire, w); break;
                default: Auto(dt, fire, w); break;
            }

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
        // the per-kind extras: 'shot' | 'drop' | 'slosh'
        public string Kind = "shot";
        public float DmgFar;             // the drop's far damage (the near damage fades into it with the distance)
        public float BelowFalloff, BelowFalloffMax;   // the slosher's falloff when it lands below the victim
        public Volley Volley;            // a volley: one direct hit per victim (the slosher's wave, the bucket's throw)
        public bool Wave;                // the slosher's wave (it lands as a thick stripe and splashes)
        public bool Head;                // the lead glob of a volley
        public float Delay;              // the glob leaves a little later (the wave pours out over ~0.1 s)
        public Blast Blast;              // the brolly's blast (the pellets of one pull land as one hit)
    }

    /// A volley of globs: only the first one to reach a victim hurts them (the rest still paint).
    public class Volley
    {
        public readonly HashSet<Actor> Hit = new HashSet<Actor>();
        public void Clear() => Hit.Clear();
    }

    public static class Projectiles
    {
        public static readonly List<Shot> List = new List<Shot>();
        public static readonly List<Blast> Blasts = new List<Blast>();
        static readonly SegmentResult _res = new SegmentResult();
        static readonly Hit _hit = new Hit();
        static readonly Hit _hit2 = new Hit();
        static readonly Vector3 _v = new Vector3();
        static readonly Vector3 _v2 = new Vector3();

        const float Deg = Mathf.PI / 180f;
        const float SimDt = 1f / 120f;

        public static void Clear() { List.Clear(); Blasts.Clear(); }

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
        public static Vector3 AimFrom(Actor a, Vector3 from, out Vector3 out)
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
        public static Vector3 Ballistic(Vector3 from, Vector3 dir, Vector3 target, float speed, float straight, float grav, float drag, float maxDist)
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

        // ---- the roller's flick: a sheet of heavy drops thrown forward
        public static void FireFlick(Actor a, JObj w)
        {
            Vector3 m = a.Pos + new Vector3(0f, 1.0f, 0f);
            float fx = Mathf.Sin(a.Yaw), fz = Mathf.Cos(a.Yaw);
            float up = Mathf.Clamp(a.AimPitch, -0.2f, 0.5f) + 0.32f;
            int drops = Mathf.Max(1, w.Int("flickDrops", 9));
            float spreadDeg = w.Float("flickSpreadDeg", 34f);
            float speed = w.Float("flickSpeed", 17f);
            for (int i = 0; i < drops; i++)
            {
                float t = drops > 1 ? (i / (float)(drops - 1)) * 2f - 1f : 0f;
                float ang = a.Yaw + t * spreadDeg * Deg * 0.5f + (UnityEngine.Random.value - 0.5f) * 0.05f;
                float sp = speed * (0.82f + 0.28f * (1f - Mathf.Abs(t)) + UnityEngine.Random.value * 0.08f);
                var p = new Shot
                {
                    Owner = a, Team = a.Team, Kind = "drop", Age = 0f, Life = 1.4f, Straight = 0f,
                    Radius = 0.85f + UnityEngine.Random.value * 0.3f,
                    Damage = w.Float("flickDamageNear", 125f),
                    DmgFar = w.Float("flickDamageFar", 30f),
                    Size = 0.15f,
                    Trail = 0f, TrailEvery = 1.8f, TrailRadius = 0.45f,
                    Grav = 26f, Drag = 0.4f,
                    Seed = UnityEngine.Random.value,
                };
                p.Pos = m + new Vector3(fx * 0.6f, 0.3f, fz * 0.6f);
                p.Prev = p.Pos; p.Start = p.Pos;
                float cu = Mathf.Cos(up + (UnityEngine.Random.value - 0.5f) * 0.12f);
                p.Vel = new Vector3(Mathf.Sin(ang) * cu * sp, Mathf.Sin(up) * sp, Mathf.Cos(ang) * cu * sp);
                List.Add(p);
            }
            GameContext.Emit("weapon:fire", new Dictionary<string, object>
            {
                { "actor", a }, { "weapon", w.Str("id") },
                { "muzzle", m + new Vector3(fx * 0.6f, 0.3f, fz * 0.6f) },
                { "dir", new Vector3(fx, Mathf.Sin(up), fz).normalized },
            });
        }

        // ---- the twins / the dualies: a shot from the left or the right gun (side 0 = both, planted)
        public static void FireTwin(Actor a, JObj w, float spreadDeg, int side)
        {
            Vector3 m = Muzzle(a);
            if (side != 0)
            {
                // the left pistol sits mirrored across the kid's midline
                var perp = new Vector3(a.AimDir.z, 0f, -a.AimDir.x);
                if (perp.sqrMagnitude < 1e-4f) perp = Vector3.right;
                perp.Normalize();
                float cy = Mathf.Cos(a.Yaw), sy = Mathf.Sin(a.Yaw);
                float dx = m.x - a.Pos.x, dz = m.z - a.Pos.z;
                float lx = dx * cy - dz * sy, lz = dx * sy + dz * cy;
                var mirrored = new Vector3(a.Pos.x - lx * cy + lz * sy, m.y, a.Pos.z + lx * sy + lz * cy);
                if (side > 0) m = (m + perp * 0.17f + mirrored) * 0.5f;
                else m += perp * -0.085f;
            }
            Vector3 dir = AimFrom(a, m, out _);
            // side 0 = the planted rapid mode: faster, straighter, longer
            float speed = side != 0 ? w.Float("projSpeed", 31f) : w.Float("turretProjSpeed", 36f);
            float straight = side != 0 ? w.Float("straightTime", 0.11f) : w.Float("turretStraight", 0.16f);
            float range = side != 0 ? w.Float("range", 11f) : w.Float("turretRange", 13.5f);
            dir = Ballistic(m, dir, a.AimPoint, speed, straight, 28f, 0.8f, range);
            Spread(dir, spreadDeg);
            var p = new Shot
            {
                Owner = a, Team = a.Team, Kind = "shot", Age = 0f, Life = 1.2f, Straight = straight,
                Radius = w.Float("impactRadius", 0.78f),
                Damage = w.Float("damage", 30f),
                Size = 0.13f,
                Trail = -(2.4f - w.Float("trailEvery", 1.1f)),
                TrailEvery = w.Float("trailEvery", 1.1f),
                TrailRadius = w.Float("trailRadius", 0.4f),
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

        // ---- the spinner: the speed / the straight time / the reach follow the charge
        public static void FireStream(Actor a, JObj w, float power, float spreadDeg)
        {
            Vector3 m = Muzzle(a);
            Vector3 dir = AimFrom(a, m, out _);
            float speed = Mathf.Lerp(w.Float("speedMin", 25f), w.Float("speedMax", 40f), power);
            float straight = Mathf.Lerp(w.Float("straightMin", 0.09f), w.Float("straightMax", 0.24f), power);
            float range = Mathf.Lerp(w.Float("rangeMin", 10f), w.Float("rangeMax", 21f), power);
            dir = Ballistic(m, dir, a.AimPoint, speed, straight, 28f, 0.8f, range);
            Spread(dir, spreadDeg);
            var p = new Shot
            {
                Owner = a, Team = a.Team, Kind = "shot", Age = 0f, Life = 1.3f, Straight = straight,
                Radius = w.Float("impactRadius", 0.72f),
                Damage = w.Float("damage", 26f),
                Size = 0.12f,
                Trail = -(2.2f - w.Float("trailEvery", 1.25f)),
                TrailEvery = w.Float("trailEvery", 1.25f),
                TrailRadius = w.Float("trailRadius", 0.38f),
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

        // ---- the slosher: a heavy wave lobbed over the cover, poured out along one arc
        public static void FireSlosher(Actor a, JObj w)
        {
            Vector3 m = Muzzle(a);
            Vector3 to = a.AimPoint - m;
            float hd = new Vector2(to.x, to.z).magnitude;
            float yaw = hd > 0.3f ? Mathf.Atan2(to.x, to.z) : a.AimYaw;
            hd = MathUtil.Clamp(hd, 1.2f, w.Float("range", 9.5f));
            float dy = MathUtil.Clamp(to.y, -4f, 5f);
            float g = w.Float("grav", 22f);
            const float T0 = 0.32f;

            // the same ~18° heave at every range: the speed does the work
            float v = w.Float("projSpeed", 15f), pitch;
            float den = 2f * Mathf.Cos(T0) * Mathf.Cos(T0) * (hd * Mathf.Tan(T0) - dy);
            float vT = den > 1e-3f ? Mathf.Sqrt((g * hd * hd) / den) : float.PositiveInfinity;
            if (vT <= v) { v = Mathf.Max(5.5f, vT); pitch = T0; }
            else
            {
                float disc = v * v * v * v - g * (g * hd * hd + 2f * dy * v * v);
                pitch = disc >= 0f ? Mathf.Atan((v * v - Mathf.Sqrt(disc)) / (g * hd)) : Mathf.PI / 4f;
                pitch = MathUtil.Clamp(pitch, T0, 1.2f);
            }

            int n = Mathf.Max(1, w.Int("drops", 8));
            var vol = NextVolley();
            for (int i = 0; i < n; i++)
            {
                float k = n > 1 ? i / (float)(n - 1) : 0f;
                float sp = v * (1f - 0.18f * k), pt = pitch - 0.04f * k;
                float yw = yaw + (i == 0 ? 0f : (i % 2 != 0 ? 1f : -1f) * 0.028f * Mathf.Min(1f, i / 3f));
                var p = new Shot
                {
                    Owner = a, Team = a.Team, Kind = "slosh", Age = 0f, Life = 2.4f, Straight = 0f,
                    Delay = i * 0.012f,
                    Radius = w.Float("impactRadius", 1.05f) * (i == 0 ? 1f : 0.78f - 0.22f * k),
                    Damage = i == 0 ? w.Float("damageHead", 70f) : w.Float("damageTail", 34f),
                    Head = i == 0,
                    Wave = true,
                    Size = i == 0 ? 0.2f : 0.14f,
                    Trail = -0.8f, TrailEvery = i < 3 ? w.Float("trailEvery", 1.4f) : 0f,
                    TrailRadius = w.Float("trailRadius", 0.5f),
                    Grav = g, Drag = 0f,
                    Seed = UnityEngine.Random.value,
                    Volley = vol,
                };
                p.Pos = m; p.Prev = m; p.Start = m;
                float cp = Mathf.Cos(pt);
                // + g·dt/2 cancels the integrator's half-step drop, so the head glob lands on the crosshair
                p.Vel = new Vector3(Mathf.Sin(yw) * cp * sp, Mathf.Sin(pt) * sp + g * SimDt * 0.5f, Mathf.Cos(yw) * cp * sp);
                List.Add(p);
            }
            GameContext.Emit("weapon:fire", new Dictionary<string, object>
            {
                { "actor", a }, { "weapon", w.Str("id") }, { "muzzle", m },
                { "dir", new Vector3(Mathf.Sin(yaw) * Mathf.Cos(pitch), Mathf.Sin(pitch), Mathf.Cos(yaw) * Mathf.Cos(pitch)) },
            });
        }

        // ---- the bucket: a volley of blobs thrown along the aim, lobbed upward so it clears the cover
        public static void FireSloshBucket(Actor a, JObj w)
        {
            Vector3 m = Muzzle(a);
            Vector3 dir = AimFrom(a, m, out _);
            float hd0 = new Vector2(dir.x, dir.z).magnitude;
            if (hd0 < 1e-4f) hd0 = 1f;
            float p0 = Mathf.Atan2(dir.y, hd0) + w.Float("lob", 0.3f);
            dir = new Vector3((dir.x / hd0) * Mathf.Cos(p0), Mathf.Sin(p0), (dir.z / hd0) * Mathf.Cos(p0));
            // bend the arc so the lead blob lands on the crosshair
            LobAim(m, ref dir, a.AimPoint, w.Float("throwSpeed", 15.5f), w.Float("gravity", 20f), w.Float("drag", 0.25f), w.Float("reach", 9.5f));

            float hd = new Vector2(dir.x, dir.z).magnitude;
            if (hd < 1e-4f) hd = 1f;
            float pitch = Mathf.Atan2(dir.y, hd);
            var volley = NextVolley();
            int blobs = Mathf.Max(1, w.Int("blobs", 5));
            for (int i = 0; i < blobs; i++)
            {
                float sp = w.Float("throwSpeed", 15.5f) * (1f - i * 0.075f);
                float side = (UnityEngine.Random.value - 0.5f) * 0.05f;
                float pi = pitch + (UnityEngine.Random.value - 0.5f) * 0.04f;
                float yaw = Mathf.Atan2(dir.x, dir.z) + side;
                float cp = Mathf.Cos(pi);
                float size = 0.22f - i * 0.02f;
                var p = new Shot
                {
                    Owner = a, Team = a.Team, Kind = "slosh", Age = 0f, Life = 2.2f, Straight = 0f,
                    Radius = w.Float("impactRadius", 1.05f) * (1f - i * 0.08f),
                    Damage = w.Float("damage", 55f),
                    BelowFalloff = w.Float("belowFalloff", 0.12f),
                    BelowFalloffMax = w.Float("belowFalloffMax", 0.35f),
                    Size = size,
                    Trail = 0f, TrailEvery = 1.6f, TrailRadius = 0.42f,
                    Grav = w.Float("gravity", 20f), Drag = w.Float("drag", 0.25f),
                    Seed = UnityEngine.Random.value,
                    Volley = volley,
                };
                p.Pos = m - dir * (i * 0.12f); p.Prev = p.Pos; p.Start = p.Pos;
                p.Vel = new Vector3(Mathf.Sin(yaw) * cp * sp, Mathf.Sin(pi) * sp, Mathf.Cos(yaw) * cp * sp);
                List.Add(p);
            }
            GameContext.Emit("weapon:fire", new Dictionary<string, object>
            {
                { "actor", a }, { "weapon", w.Str("id") }, { "muzzle", m }, { "dir", dir },
            });
        }

        // ---- the brush: globs flung off the head as it sweeps sideways low across the front
        public static void FireSwipe(Actor a, JObj w, int side)
        {
            float fx = Mathf.Sin(a.Yaw), fz = Mathf.Cos(a.Yaw);
            float up = Mathf.Clamp(a.AimPitch, -0.3f, 0.3f) * 0.5f + 0.08f;
            float arc = w.Float("swipeSpreadDeg", 40f) * Deg * 1.6f;
            int drops = Mathf.Max(1, w.Int("swipeDrops", 5));
            for (int i = 0; i < drops; i++)
            {
                float t = i / (float)Mathf.Max(1, drops - 1);
                float ang = a.Yaw - side * (t - 0.5f) * arc;
                float ox = Mathf.Sin(ang), oz = Mathf.Cos(ang);
                float tx = -oz * side, tz = ox * side;
                float sp = w.Float("swipeSpeed", 12.5f) * (0.8f + UnityEngine.Random.value * 0.3f);
                var p = new Shot
                {
                    Owner = a, Team = a.Team, Kind = "drop", Age = 0f, Life = 1f, Straight = 0f,
                    Radius = 0.55f + UnityEngine.Random.value * 0.2f,
                    Damage = w.Float("swipeDamageNear", 26f),
                    DmgFar = w.Float("swipeDamageFar", 12f),
                    Size = 0.1f,
                    Trail = 0f, TrailEvery = 2.2f, TrailRadius = 0.32f,
                    Grav = 26f, Drag = 0.6f,
                    Seed = UnityEngine.Random.value,
                };
                p.Pos = new Vector3(a.Pos.x + ox * 0.85f, a.Pos.y + 0.45f, a.Pos.z + oz * 0.85f);
                p.Prev = p.Pos; p.Start = p.Pos;
                float hx = ox * 0.8f + tx * 0.45f + fx * 0.25f, hz = oz * 0.8f + tz * 0.45f + fz * 0.25f;
                float hl = Mathf.Sqrt(hx * hx + hz * hz);
                if (hl < 1e-4f) hl = 1f;
                float cu = Mathf.Cos(up + (UnityEngine.Random.value - 0.5f) * 0.08f);
                p.Vel = new Vector3((hx / hl) * cu * sp, Mathf.Sin(up) * sp, (hz / hl) * cu * sp);
                List.Add(p);
            }
            GameContext.Emit("weapon:fire", new Dictionary<string, object>
            {
                { "actor", a }, { "weapon", w.Str("id") },
                { "muzzle", new Vector3(a.Pos.x + fx * 0.85f, a.Pos.y + 0.45f, a.Pos.z + fz * 0.85f) },
                { "dir", new Vector3(fx, Mathf.Sin(up), fz).normalized },
            });
        }

        /// Bend a lobbed throw so the lead blob lands on the crosshair (the web build's _lobAim).
        static bool LobAim(Vector3 from, ref Vector3 dir, Vector3 target, float speed, float grav, float drag, float maxDist)
        {
            float hd = new Vector2(target.x - from.x, target.z - from.z).magnitude;
            if (hd < 1f || hd > maxDist) return false;
            float dy = target.y - from.y;
            Func<float, float> f = pitch =>
            {
                float vh = Mathf.Cos(pitch) * speed, vy = Mathf.Sin(pitch) * speed, x = 0f, y = 0f;
                for (int i = 0; i < 180; i++)
                {
                    float px = x, py = y;
                    vy -= grav * SimDt;
                    float k = 1f - drag * SimDt;
                    vh *= k; vy *= k;
                    x += vh * SimDt; y += vy * SimDt;
                    if (x >= hd) return py + (y - py) * ((hd - px) / Mathf.Max(1e-6f, x - px)) - dy;
                    if (y < dy - 8f) break;
                }
                return -1e3f;
            };
            float prev = -0.6f, fp = f(prev);
            for (float p = -0.58f; p <= 1.2f; p += 0.02f)
            {
                float fv = f(p);
                if (fp < 0f && fv >= 0f)
                {
                    float lo = p - 0.02f, hi = p;
                    for (int k = 0; k < 10; k++)
                    {
                        float mid = (lo + hi) * 0.5f;
                        if (f(mid) >= 0f) hi = mid; else lo = mid;
                    }
                    float hdir = new Vector2(dir.x, dir.z).magnitude;
                    if (hdir < 1e-4f) hdir = 1f;
                    float cp = Mathf.Cos(hi);
                    dir = new Vector3((dir.x / hdir) * cp, Mathf.Sin(hi), (dir.z / hdir) * cp);
                    return true;
                }
                fp = fv;
            }
            return false;
        }

        static readonly Volley[] _vols = { new Volley(), new Volley(), new Volley(), new Volley() };
        static int _volI;
        public static Volley NewVolley() => NextVolley();

        static Volley NextVolley()
        {
            var v = _vols[_volI = (_volI + 1) % _vols.Length];
            v.Clear();
            return v;
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
            FlushBlasts();
        }

        /// The brolly's blasts: the pellets that landed this frame hit as one.
        static void FlushBlasts()
        {
            for (int i = 0; i < Blasts.Count; i++)
            {
                var b = Blasts[i];
                if (b.Pending.Count == 0) continue;
                foreach (var kv in b.Pending) ApplyHit(b.Owner, kv.Key, kv.Value);
                b.Pending.Clear();
            }
        }

        static bool Step(Shot p, float dt)
        {
            if (p.Delay > 0f) { p.Delay -= dt; return false; }
            p.Age += dt;
            p.Prev = p.Pos;
            if (p.Age > p.Straight) p.Vel.y -= p.Grav * dt;
            if (p.Drag > 0f) p.Vel *= 1f - p.Drag * dt * (p.Age > p.Straight ? 1f : 0f);
            p.Pos += p.Vel * dt;

            bool dead = false;

            // the canopies (the brolly) block the enemy shots
            Vector3 capAt, capN; BrollyKit capKit;
            if (Canopies.BlockSegment(p.Prev, p.Pos, p.Team, out capAt, out capN, out capKit))
            {
                float mul = p.Kind == "drop" ? 0.25f : p.Kind == "slosh" ? 0.5f : 1f;
                capKit.Hp -= p.Damage * mul;
                if (capKit.Hp <= 0f)
                {
                    capKit.Hp = 0f;
                    capKit.State = "broken";
                    capKit.RegrowT = 0f;
                    capKit.Open = 0f;
                }
                GameContext.Emit("weapon:impact", new Dictionary<string, object>
                {
                    { "pos", capAt }, { "normal", capN }, { "team", p.Team }, { "kind", "shot" },
                });
                return true;
            }

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
                    // a volley lands one direct hit per victim (the rest of the wave still paints)
                    if (p.Volley != null)
                    {
                        if (p.Volley.Hit.Contains(e)) continue;
                        p.Volley.Hit.Add(e);
                    }
                    float dmg = p.Damage;
                    if (p.Kind == "drop") dmg = Mathf.Lerp(p.Damage, p.DmgFar, Mathf.Clamp01((p.Start - at).magnitude / 7f));
                    if (p.BelowFalloff > 0f)
                        dmg *= 1f - Mathf.Clamp((p.Start.y - (e.Pos.y + 0.8f) - 0.5f) * p.BelowFalloff, 0f, p.BelowFalloffMax);
                    if (p.Blast != null)
                    {
                        // the brolly: the pellets of one pull land as one hit per victim
                        float pd = WeaponRunner.PelletDamage(p.Owner != null ? p.Owner.Weapon : null, (p.Start - at).magnitude);
                        float acc;
                        p.Blast.Pending.TryGetValue(e, out acc);
                        p.Blast.Pending[e] = acc + pd;
                    }
                    else ApplyHit(p.Owner, e, dmg);
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
            bool sloshr = p.Kind == "slosh" && p.Wave;
            float area;
            if (sloshr)
            {
                // the wave lands as a thick stripe along its travel
                dir.y = 0f;
                if (dir.sqrMagnitude < 1e-4f) dir = Vector3.forward;
                dir.Normalize();
                area = GameContext.Paint.Splat(at, rad * 1.12f, p.Team, p.Seed, PaintSystem.Kind.Shot, dir, 1.25f);
            }
            else area = GameContext.Paint.Splat(at, rad, p.Team, p.Seed, PaintSystem.Kind.Shot, dir, 0.7f);
            Credit(p, area);
            if (sloshr && p.Head) SloshSplash(p, hit.Point);
            GameContext.Emit("weapon:impact", new Dictionary<string, object>
            {
                { "pos", hit.Point }, { "normal", hit.Normal }, { "team", p.Team },
                { "kind", p.Kind == "drop" || sloshr ? "drop" : "shot" }, { "radius", rad },
            });
        }

        /// The slosher's wave splashes the enemies around the landing point.
        static void SloshSplash(Shot p, Vector3 at)
        {
            var w = p.Owner != null ? p.Owner.Weapon : null;
            if (w == null) return;
            float splashRadius = w.Float("splashRadius", 1.1f);
            float splashDamage = w.Float("splashDamage", 26f);
            var actors = GameContext.Match != null ? GameContext.Match.Actors : null;
            if (actors == null) return;
            var from = new Vector3(at.x, at.y + 0.25f, at.z);
            for (int i = 0; i < actors.Count; i++)
            {
                var e = actors[i];
                if (e == null || e.Team == p.Team || !e.Alive) continue;
                if (p.Volley != null && p.Volley.Hit.Contains(e)) continue;
                var to = e.Pos + new Vector3(0f, 0.6f, 0f);
                if ((to - at).magnitude > splashRadius + 0.3f) continue;
                if (!GameContext.Physics.Los(from, to)) continue;
                if (p.Volley != null) p.Volley.Hit.Add(e);
                ApplyHit(p.Owner, e, splashDamage);
            }
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
