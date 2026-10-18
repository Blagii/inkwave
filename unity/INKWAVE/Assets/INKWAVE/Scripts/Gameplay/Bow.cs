// The Tideline Bow: hold to draw, release to loose THREE arrows at once — a flat fan on the ground, an upright
// one in the air. The charged arrows stick where they land and burst.
//
// A port of the web build's src/game/kits/bow.js (the gameplay core: the draw, the three tiers, the fan, the
// lodging arrows and the burst). The bow's mesh and the arrow models are not ported.
using System.Collections.Generic;
using INKWAVE.Core;
using INKWAVE.Data;
using INKWAVE.World;
using InkPhysics = INKWAVE.World.Physics;   // UnityEngine.Physics would be ambiguous
using UnityEngine;

namespace INKWAVE.Gameplay
{
    /// One arrow: flying (st 0) or lodged (st 1).
    public class BowArrow
    {
        public Actor Owner;
        public int Team;
        public Vector3 Pos, Prev, Vel, Dir, HitN;
        public float Age, Dist, Range, Speed, Fuse, Dmg, Br, Bd, Be, Bp;
        public int Tier;
        public bool Lodge, Center, NoHit;
        public bool Lodged;
        public float LodgedT;
        public readonly HashSet<Actor> Hits = new HashSet<Actor>();
    }

    public partial class WeaponRunner
    {
        public int BowRing;

        // ------------------------------------------------------------------------------- the bow
        public void BowUpdate(float dt, bool fire, bool firePressed, JObj w)
        {
            var a = A;
            float maxC = Mathf.Max(0f, (a.Ink - w.Float("inkMin", 0.15f)) / Mathf.Max(0.01f, w.Float("inkFull", 16f)));
            if (maxC > 1f) maxC = 1f;

            if (fire && Cooldown <= 0f)
            {
                if (!Charging)
                {
                    Charging = true; Charge = 0f; ChargeT = 0f; BowRing = 0;
                }
                ChargeT = Mathf.Min(1f, ChargeT + dt / Mathf.Max(0.05f, w.Float("chargeTime", 1f)));
                Charge = Mathf.Min(maxC, ChargeT);
                a.FireFacing = 0.4f;
                if (BowRing < 1 && Charge >= w.Float("ring1", 0.45f)) BowRing = 1;
                if (BowRing < 2 && Charge >= 0.999f) BowRing = 2;
            }
            else if (Charging)
            {
                Charging = false;
                Loose(a, w, Charge);
                Charge = 0f; ChargeT = 0f; BowRing = 0;
                Cooldown = w.Float("cooldown", 0.22f);
            }
        }

        /// The tier of a draw: a tap, a ring, or the full draw.
        public static void BowShot(JObj w, float c, out int tier, out float range, out float speed, out float dmg,
                                   out float side, out float fan, out bool lodge, out float fuse, out float br, out float bd, out float be, out float bp)
        {
            float ring1 = w.Float("ring1", 0.45f);
            var flightTap = w.Floats("flightTap");
            var flightRing = w.Floats("flightRing");
            var speedRing = w.Floats("speedRing");
            var damageTap = w.Floats("damageTap");
            var damageRing = w.Floats("damageRing");
            var burstRadius = w.Floats("burstRadius");
            var burstDamage = w.Floats("burstDamage");
            var burstEdge = w.Floats("burstEdge");
            var burstPaint = w.Floats("burstPaint");
            float sideMul = w.Float("sideMul", 0.8f);
            if (c >= 0.999f)
            {
                tier = 2; range = w.Float("flightFull", 21f); speed = w.Float("speedFull", 58f);
                dmg = w.Float("damageFull", 55f); side = w.Float("sideFull", 45f); fan = w.Float("fanFull", 4.5f);
                lodge = true; fuse = w.Float("fuseFull", 0.55f);
                br = burstRadius.Length > 1 ? burstRadius[1] : 1.8f;
                bd = burstDamage.Length > 1 ? burstDamage[1] : 45f;
                be = burstEdge.Length > 1 ? burstEdge[1] : 15f;
                bp = burstPaint.Length > 1 ? burstPaint[1] : 1.55f;
            }
            else if (c >= ring1)
            {
                float u = Mathf.Clamp01((c - ring1) / (1f - ring1));
                tier = 1;
                range = Mathf.Lerp(flightRing.Length > 0 ? flightRing[0] : 12.5f, flightRing.Length > 1 ? flightRing[1] : 16.5f, u);
                speed = Mathf.Lerp(speedRing.Length > 0 ? speedRing[0] : 42f, speedRing.Length > 1 ? speedRing[1] : 48f, u);
                dmg = Mathf.Lerp(damageRing.Length > 0 ? damageRing[0] : 30f, damageRing.Length > 1 ? damageRing[1] : 38f, u);
                side = dmg * sideMul; fan = w.Float("fanRing", 6f);
                lodge = true; fuse = w.Float("fuseRing", 0.7f);
                br = burstRadius.Length > 0 ? burstRadius[0] : 1.35f;
                bd = burstDamage.Length > 0 ? burstDamage[0] : 30f;
                be = burstEdge.Length > 0 ? burstEdge[0] : 12f;
                bp = burstPaint.Length > 0 ? burstPaint[0] : 1.2f;
            }
            else
            {
                float u = Mathf.Clamp01(c / Mathf.Max(0.01f, ring1));
                tier = 0;
                range = Mathf.Lerp(flightTap.Length > 0 ? flightTap[0] : 5f, flightTap.Length > 1 ? flightTap[1] : 7.5f, u);
                speed = w.Float("speedTap", 32f);
                dmg = Mathf.Lerp(damageTap.Length > 0 ? damageTap[0] : 14f, damageTap.Length > 1 ? damageTap[1] : 20f, u);
                side = dmg * sideMul; fan = w.Float("fanTap", 8f);
                lodge = false; fuse = 0f; br = 0f; bd = 0f; be = 0f; bp = 0f;
            }
        }

        void Loose(Actor a, JObj w, float c)
        {
            BowShot(w, c, out int tier, out float range, out float speed, out float dmg, out float side, out float fan,
                    out bool lodge, out float fuse, out float br, out float bd, out float be, out float bp);

            Vector3 m = Projectiles.Muzzle(a);
            Vector3 dir = Projectiles.AimFrom(a, m, out _);
            dir = Projectiles.Ballistic(m, dir, a.AimPoint, speed, w.Float("straight", 0.05f), w.Float("grav", 6f), 0f, range);

            // the fan axis: on the ground the arrows spread about the vertical, in the air about the aim's right
            bool air = !a.Grounded;
            var axis = air ? Vector3.Cross(dir, Vector3.up) : Vector3.up;
            if (axis.sqrMagnitude < 1e-4f) axis = Vector3.right;
            axis.Normalize();

            for (int i = -1; i <= 1; i++)
            {
                var d = i == 0 ? dir : Quaternion.AngleAxis(i * fan, axis) * dir;
                var p = new BowArrow
                {
                    Owner = a, Team = a.Team, Pos = m, Prev = m,
                    Vel = d * speed, Dir = d,
                    Range = range, Speed = speed, Tier = tier,
                    Dmg = i != 0 ? side : dmg,
                    Lodge = lodge, Fuse = fuse, Br = br, Bd = bd, Be = be, Bp = bp,
                    Center = i == 0,
                };
                BowArrows.List.Add(p);
            }
            GameContext.Emit("weapon:fire", new Dictionary<string, object>
            {
                { "actor", a }, { "weapon", w.Str("id") }, { "muzzle", m }, { "dir", dir }, { "charge", c },
            });
        }
    }

    /// The live arrows.
    public static class BowArrows
    {
        public static readonly List<BowArrow> List = new List<BowArrow>();
        public static void Clear() => List.Clear();

        const float ArrowR = 0.16f;

        public static void Tick(float dt)
        {
            var actors = GameContext.Match != null ? GameContext.Match.Actors : null;
            for (int i = List.Count - 1; i >= 0; i--)
            {
                var p = List[i];
                var w = p.Owner != null ? p.Owner.Weapon : null;
                if (w == null) { List.RemoveAt(i); continue; }

                // ---- lodged: the fuse ticks down, then it bursts
                if (p.Lodged)
                {
                    p.LodgedT += dt;
                    if (p.LodgedT >= p.Fuse) { Burst(p, w); List.RemoveAt(i); }
                    continue;
                }

                p.Age += dt;
                p.Prev = p.Pos;
                if (p.Age > w.Float("straight", 0.05f)) p.Vel.y -= w.Float("grav", 6f) * dt;
                if (p.Dist > p.Range) { p.Vel.y -= w.Float("diveGrav", 140f) * dt; p.Vel *= 1f - w.Float("diveDrag", 6f) * dt; }
                p.Pos += p.Vel * dt;
                float sp = p.Vel.magnitude;
                p.Dist += sp * dt;
                if (sp > 0.01f) p.Dir = p.Vel / sp;

                // ---- the actors
                bool done = false;
                if (!p.NoHit && actors != null)
                {
                    float hr = GameData.P.Float("radius", 0.38f);
                    for (int e = 0; e < actors.Count; e++)
                    {
                        var a = actors[e];
                        if (a == null || a.Team == p.Team || !a.Alive) continue;
                        if (p.Hits.Contains(a)) continue;
                        float h = a.Form == ActorForm.Squid ? GameData.P.Float("squidHeight", 0.55f) : GameData.P.Float("height", 1.45f);
                        if (Mathf.Abs(a.Pos.x - p.Pos.x) > 3f + hr || Mathf.Abs(a.Pos.z - p.Pos.z) > 3f + hr) continue;
                        var res = new SegmentResult();
                        InkPhysics.SegmentCapsuleDist(p.Prev, p.Pos, a.Pos, hr, h, res);
                        if (res.Dist < hr * 0.95f + ArrowR)
                        {
                            Vector3 at = Vector3.Lerp(p.Prev, p.Pos, res.T);
                            p.Hits.Add(a);
                            Projectiles.ApplyHit(p.Owner, a, p.Dmg);
                            GameContext.Emit("weapon:impact", new Dictionary<string, object>
                            {
                                { "pos", at }, { "normal", -p.Dir }, { "team", p.Team }, { "kind", "shot" }, { "radius", 0.3f },
                            });
                            if (!p.Lodge) { done = true; break; }
                            // a lodging arrow drops and sticks at their feet
                            p.NoHit = true;
                            p.Pos = at; p.Prev = at;
                            p.Vel = new Vector3(p.Dir.x * 1.5f, -9f, p.Dir.z * 1.5f);
                            p.Dist = Mathf.Max(p.Dist, p.Range);
                        }
                    }
                }
                if (done) { List.RemoveAt(i); continue; }

                // ---- the world
                var hit = GameContext.Physics.Segment(p.Prev, p.Pos, null, true);
                if (hit.Hit_)
                {
                    if (p.Lodge)
                    {
                        p.Lodged = true; p.LodgedT = 0f;
                        p.Pos = hit.Point; p.HitN = hit.Normal;
                        // it paints a little where it sticks
                        float area = GameContext.Paint.Splat(hit.Point + hit.Normal * 0.1f, w.Float("paintStick", 0.42f),
                                                            p.Team, UnityEngine.Random.value, PaintSystem.Kind.Shot, p.Dir, 0.7f);
                        if (p.Owner != null && area > 0f) p.Owner.AddTurf(area);
                        GameContext.Emit("weapon:impact", new Dictionary<string, object>
                        {
                            { "pos", hit.Point }, { "normal", hit.Normal }, { "team", p.Team }, { "kind", "shot" },
                        });
                    }
                    else
                    {
                        float area = GameContext.Paint.Splat(hit.Point + hit.Normal * 0.12f,
                                                            w.Float("paintTap", 0.55f) * (0.85f + UnityEngine.Random.value * 0.3f),
                                                            p.Team, UnityEngine.Random.value, PaintSystem.Kind.Shot, p.Dir, 0.7f);
                        if (p.Owner != null && area > 0f) p.Owner.AddTurf(area);
                        GameContext.Emit("weapon:impact", new Dictionary<string, object>
                        {
                            { "pos", hit.Point }, { "normal", hit.Normal }, { "team", p.Team }, { "kind", "shot" },
                        });
                        List.RemoveAt(i);
                    }
                    continue;
                }

                if (p.Age > 6f) List.RemoveAt(i);
            }
        }

        /// The burst of a lodged arrow.
        static void Burst(BowArrow p, JObj w)
        {
            var actors = GameContext.Match != null ? GameContext.Match.Actors : null;
            float inner = w.Float("burstInner", 0.6f);
            if (actors != null)
            {
                for (int i = 0; i < actors.Count; i++)
                {
                    var a = actors[i];
                    if (a == null || a.Team == p.Team || !a.Alive) continue;
                    float d = (a.Pos - p.Pos).magnitude;
                    if (d > p.Br) continue;
                    // the full damage in the middle, the edge damage at the rim
                    float k = Mathf.Clamp01((d - inner) / Mathf.Max(0.01f, p.Br - inner));
                    Projectiles.ApplyHit(p.Owner, a, Mathf.Lerp(p.Bd, p.Be, k));
                }
            }
            float area = GameContext.Paint.Splat(p.Pos, p.Bp, p.Team, UnityEngine.Random.value, PaintSystem.Kind.Blast);
            if (p.Owner != null && area > 0f) p.Owner.AddTurf(area);
            GameContext.Emit("weapon:impact", new Dictionary<string, object>
            {
                { "pos", p.Pos }, { "normal", Vector3.up }, { "team", p.Team }, { "kind", "blast" },
            });
        }
    }
}
