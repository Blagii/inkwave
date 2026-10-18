// The Canopy Brolly: a shotgun spray of ink pellets, and the canopy that unfolds into a shield.
//
// A port of the web build's src/game/kits/brolly.js (the gameplay core: the blast, the pellet scoring, the held
// canopy that blocks enemy shots, the regrowth). The launched rolling wall is not ported yet.
using System.Collections.Generic;
using INKWAVE.Core;
using INKWAVE.Data;
using UnityEngine;

namespace INKWAVE.Gameplay
{
    /// The brolly's runner state (the JS keeps it in runner.kit).
    public class BrollyKit
    {
        public Actor Owner;
        public float Hp, HpMax;
        public string State = "ready";      // "ready" | "broken"
        public float RegrowT, RegrowK, Open, PressT, OpenT;
        public bool Shot, PrevFire, Want, LockRelease;

        // the held canopy: a disc in front of the chest, facing the aim
        public Vector3 C;                   // the disc centre
        public Vector3 N;                   // the disc normal (the aim)
        public float R = 0.8f;
        public bool Live;

        public BrollyKit(Actor owner, float hp)
        {
            Owner = owner; Hp = hp; HpMax = hp;
        }
    }

    /// The live canopies: they block the enemy shots.
    public static class Canopies
    {
        public static readonly List<BrollyKit> Held = new List<BrollyKit>();
        public static readonly List<BrollyKit> Launched = new List<BrollyKit>();

        public static void Clear()
        {
            Held.Clear();
            Launched.Clear();
        }

        /// Does a shot segment cross a canopy of another team? Returns the hit point.
        public static bool BlockSegment(Vector3 a, Vector3 b, int shotTeam, out Vector3 at, out Vector3 normal, out BrollyKit kit)
        {
            at = Vector3.zero; normal = Vector3.zero; kit = null;
            for (int i = 0; i < Held.Count; i++)
            {
                var k = Held[i];
                if (k == null || k.Owner == null || k.Owner.Team == shotTeam) continue;
                if (k.Hp <= 0f || k.Open < 0.2f) continue;
                float d0 = Vector3.Dot(a - k.C, k.N), d1 = Vector3.Dot(b - k.C, k.N);
                if (d0 > 0f && d1 > 0f) continue;       // both in front of the canopy
                if (d0 < 0f && d1 < 0f) continue;       // both behind it
                float t = Mathf.Abs(d1 - d0) < 1e-6f ? 0f : d0 / (d0 - d1);
                Vector3 p = Vector3.Lerp(a, b, t);
                if ((p - k.C).magnitude <= k.R)
                {
                    at = p; normal = k.N; kit = k;
                    return true;
                }
            }
            return false;
        }
    }

    /// One blast: the pellets that land together sum into a single hit per victim.
    public class Blast
    {
        public Actor Owner;
        public int Team;
        public readonly Dictionary<Actor, float> Pending = new Dictionary<Actor, float>();
    }

    public partial class WeaponRunner
    {
        public BrollyKit Brolly;

        // ------------------------------------------------------------------------------- the brolly
        public void BrollyUpdate(float dt, bool fire, JObj w)
        {
            var a = A;
            if (Brolly == null) Brolly = new BrollyKit(a, w.Float("canopyHp", 450f));
            var k = Brolly;

            bool press = fire && !k.PrevFire;
            k.PrevFire = fire;
            if (press) { k.PressT = 0f; k.Shot = false; }
            if (fire) k.PressT += dt; else k.LockRelease = false;

            bool canopy = k.State == "ready" && k.RegrowK > 0.6f && !k.LockRelease;
            bool want = false;
            if (fire && !k.LockRelease)
            {
                if (canopy)
                {
                    // a tap = one blast; holding it = the blast, then the canopy unfolds
                    if (!k.Shot && Cooldown <= 0f && k.Open < 0.05f && k.PressT <= w.Float("openDelay", 0.12f) + 0.06f)
                    {
                        FireBrollyBlast(w);
                        k.Shot = true;
                    }
                    want = k.PressT >= w.Float("openDelay", 0.12f);
                }
                else if (Cooldown <= 0f) FireBrollyBlast(w);
            }
            if (!fire && Cooldown < 0f) Cooldown = 0f;

            // unfold / fold
            float was = k.Open;
            if (want) k.Open = Mathf.Min(1f, k.Open + dt / Mathf.Max(0.01f, w.Float("openTime", 0.14f)));
            else k.Open = Mathf.Max(0f, k.Open - dt / Mathf.Max(0.01f, w.Float("closeTime", 0.1f)));
            k.Want = want;
            if (k.Open > 0f) { a.FireFacing = 0.5f; a.LastFire = 0f; FiringT = Mathf.Max(FiringT, 0.2f); }

            // the canopy sits in front of the chest, facing the aim
            k.Live = k.Open > 0.2f;
            if (k.Live)
            {
                k.N = a.AimDir.sqrMagnitude > 1e-4f ? a.AimDir.normalized : new Vector3(Mathf.Sin(a.Yaw), 0f, Mathf.Cos(a.Yaw));
                k.C = a.Pos + new Vector3(0f, 0.9f, 0f) + k.N * 0.62f;
                if (!Canopies.Held.Contains(k)) Canopies.Held.Add(k);
            }
            else Canopies.Held.Remove(k);

            // the regrowth after a break
            if (k.State == "broken")
            {
                k.RegrowT += dt;
                float regrow = Mathf.Max(0.01f, w.Float("regrowTime", 5f));
                k.RegrowK = Mathf.Min(1f, k.RegrowT / regrow);
                if (k.RegrowK >= 1f)
                {
                    k.State = "ready";
                    k.Hp = k.HpMax;
                    k.RegrowK = 1f;
                }
            }
        }

        /// The blast: a narrow cone of ink pellets (the pattern: the centre, an inner ring of 4, an outer ring of 6).
        void FireBrollyBlast(JObj w)
        {
            var a = A;
            float inkPerShot = w.Float("inkPerShot", 5.5f);
            if (a.Ink < inkPerShot) { Empty(); Cooldown = Mathf.Max(Cooldown, 0.2f); return; }
            a.Ink -= inkPerShot; a.LastFire = 0f;
            FiringT = 0.35f; a.FireFacing = 0.5f;

            Vector3 m = Projectiles.Muzzle(a);
            Vector3 dir = Projectiles.AimFrom(a, m, out _);
            dir = Projectiles.Ballistic(m, dir, a.AimPoint, w.Float("projSpeed", 25f), w.Float("straightTime", 0.1f),
                                        w.Float("pelletGrav", 30f), w.Float("pelletDrag", 2f), w.Float("range", 7f));
            var right = Vector3.Cross(dir, Vector3.up);
            if (right.sqrMagnitude < 1e-6f) right = Vector3.right;
            right.Normalize();
            var up = Vector3.Cross(right, dir).normalized;

            float cone = Mathf.Tan((a.Grounded ? w.Float("spreadDeg", 8f) : w.Float("spreadAir", 10f)) * Mathf.Deg2Rad);
            float turn = UnityEngine.Random.value * Mathf.PI * 2f;
            int n = Mathf.Min(w.Int("pellets", 11), Pattern.Length);
            var blast = new Blast { Owner = a, Team = a.Team };
            Projectiles.Blasts.Add(blast);

            for (int i = 0; i < n; i++)
            {
                var pr = Pattern[i];
                float ang = pr.Item2 + turn;
                float ox = (pr.Item1 * Mathf.Cos(ang) + (UnityEngine.Random.value - 0.5f) * 0.18f) * cone;
                float oy = (pr.Item1 * Mathf.Sin(ang) + (UnityEngine.Random.value - 0.5f) * 0.18f) * cone * 0.72f;
                var v = (dir + right * ox + up * oy).normalized;
                var p = new Shot
                {
                    Owner = a, Team = a.Team, Kind = "shot", Age = 0f,
                    Life = w.Float("pelletLife", 0.6f),
                    Straight = w.Float("straightTime", 0.1f),
                    Radius = w.Float("pelletPaint", 0.5f) * (0.85f + UnityEngine.Random.value * 0.3f),
                    Damage = 0f,                       // the kit scores the hits itself
                    Size = w.Float("pelletSize", 0.1f),
                    Trail = 0f, TrailEvery = 0f, TrailRadius = 0.3f,
                    Grav = w.Float("pelletGrav", 30f), Drag = w.Float("pelletDrag", 2f),
                    Seed = UnityEngine.Random.value,
                    Blast = blast,
                };
                p.Pos = m; p.Prev = m; p.Start = m;
                p.Vel = v * (w.Float("projSpeed", 25f) * (0.985f + UnityEngine.Random.value * 0.03f));
                Projectiles.List.Add(p);
            }
            Cooldown = w.Float("fireInterval", 0.45f);
            GameContext.Emit("weapon:fire", new Dictionary<string, object>
            {
                { "actor", a }, { "weapon", w.Str("id") }, { "muzzle", m }, { "dir", dir },
            });
        }

        /// The pellet pattern: the centre, an inner ring of 4, an outer ring of 6.
        static readonly (float, float)[] Pattern =
        {
            (0f, 0f),
            (0.42f, 0.4f), (0.42f, 0.4f + 1.5708f), (0.42f, 0.4f + 3.1416f), (0.42f, 0.4f + 4.7124f),
            (0.9f, 0f), (0.9f, 1.0472f), (0.9f, 2.0944f), (0.9f, 3.1416f), (0.9f, 4.1888f), (0.9f, 5.236f),
        };

        /// The pellet damage by the distance it travelled.
        public static float PelletDamage(JObj w, float distance)
        {
            float start = w.Float("falloffStart", 2.8f), end = w.Float("falloffEnd", 6.5f);
            if (distance <= start) return w.Float("pelletDamage", 8f);
            float k = Mathf.Clamp01((distance - start) / Mathf.Max(0.01f, end - start));
            return Mathf.Lerp(w.Float("pelletDamage", 8f), w.Float("pelletDamageFar", 2.5f), k);
        }
    }
}
