// The Brine Cutlass: a quick cut that flings a crescent of ink and cuts what is in front of you, and a charged
// overhead cut that sends a long ink wave.
//
// A port of the web build's src/game/kits/blade.js (the gameplay core: the tap, the charge, the melee arc, the
// ground stroke, the wave). The swing's visuals (the blade mesh, the slash trail) are not ported.
using System.Collections.Generic;
using INKWAVE.Core;
using INKWAVE.Data;
using UnityEngine;

namespace INKWAVE.Gameplay
{
    /// The blade's runner state.
    public class BladeKit
    {
        public class Swing
        {
            public bool Heavy;
            public float T;
            public int Side;
            public float Yaw, Pitch;
            public bool Fired, Stroke;
            public readonly HashSet<Actor> Hits = new HashSet<Actor>();
        }

        public Actor Owner;
        public float Cd, Since, Charge, HoldT, Lunge, Lx, Lz, Lv, Queued, Store;
        public bool Charging, Full, Held, LowWarned;
        public int Side = 1;
        public Swing Swing;
    }

    /// The ink wave: a wall of ink that rolls forward, painting and cutting.
    public class BladeWave
    {
        public Actor Owner;
        public int Team;
        public Vector3 Pos, Dir;
        public float Dist, T, PaintAcc;
        public readonly HashSet<Actor> Hits = new HashSet<Actor>();
    }

    public partial class WeaponRunner
    {
        public BladeKit Blade;

        // ------------------------------------------------------------------------------- the blade
        public void BladeUpdate(float dt, bool fire, bool firePressed, JObj w)
        {
            var a = A;
            if (Blade == null) Blade = new BladeKit { Owner = a };
            var k = Blade;
            k.Cd -= dt; k.Since += dt; k.Queued = Mathf.Max(0f, k.Queued - dt);
            if (k.Swing != null) RunSwing(k, dt, w);

            bool canAct = a.Form != ActorForm.Squid && a.KidT >= 0.1f && a.Alive;
            if (!canAct)
            {
                if (k.Charging) { k.Store += dt; a.LastFire = 0f; if (!fire || k.Store > w.Float("chargeStore", 1f)) CancelCharge(k); }
                k.Held = k.Held && fire;
                Charge = k.Charge;
                return;
            }
            k.Store = 0f;

            if (firePressed)
            {
                if (k.Cd <= 0f) Tap(k, w); else k.Queued = 0.16f;
                k.Held = true; k.HoldT = 0f;
            }
            else if (k.Queued > 0f && k.Cd <= 0f && !k.Charging) { k.Queued = 0f; Tap(k, w); }

            if (fire && k.Held)
            {
                k.HoldT += dt;
                // held past the cut: the blade rises and charges
                if (!k.Charging && k.HoldT >= w.Float("chargeDelay", 0.14f))
                {
                    k.Charging = true; k.Charge = 0f; k.Full = false;
                }
                if (k.Charging)
                {
                    // not enough ink for the heavy cut: it never completes
                    float cap = a.Ink >= w.Float("heavyInk", 9f) ? 1f : 0.94f;
                    k.Charge = Mathf.Min(cap, k.Charge + dt / Mathf.Max(0.05f, w.Float("chargeTime", 0.62f)));
                    if (k.Charge >= 1f && !k.Full) k.Full = true;
                    a.FireFacing = Mathf.Max(a.FireFacing, 0.35f);
                    FiringT = Mathf.Max(FiringT, 0.3f);
                    a.LastFire = 0f;      // no idle refill mid-charge
                }
            }
            else if (k.Held && !fire)
            {
                k.Held = false; k.LowWarned = false;
                if (k.Charging)
                {
                    float c = k.Charge;
                    CancelCharge(k);
                    if (c >= 1f) Heavy(k, w);
                    else if (c >= 0.3f) { if (k.Cd <= 0.04f) { k.Cd = 0f; Tap(k, w); } else k.Queued = 0.16f; }
                }
            }
            Charge = k.Charge;
        }

        static void CancelCharge(BladeKit k)
        {
            k.Charging = false; k.Charge = 0f; k.Full = false; k.Store = 0f;
        }

        /// The heading of a cut: toward the crosshair's world point, else the aim yaw.
        static float CutYaw(Actor a)
        {
            var p = a.AimPoint;
            float dx = p.x - a.Pos.x, dz = p.z - a.Pos.z;
            if (dx * dx + dz * dz < 4f) return a.AimYaw;
            float y = Mathf.Atan2(dx, dz);
            float d = Mathf.DeltaAngle(a.AimYaw, y);
            return Mathf.Abs(d) < 0.35f ? y : a.AimYaw;
        }

        void Tap(BladeKit k, JObj w)
        {
            var a = A;
            float tapInk = w.Float("tapInk", 3.2f);
            if (a.Ink < tapInk) { Empty(); k.Cd = 0.18f; return; }
            a.Ink -= tapInk; a.LastFire = 0f;
            k.Side = -k.Side;
            k.Cd = w.Float("tapInterval", 0.27f); k.Since = 0f;
            k.Swing = new BladeKit.Swing { Heavy = false, T = 0f, Side = k.Side, Yaw = CutYaw(a), Pitch = a.AimPitch };
            FiringT = 0.4f; a.FireFacing = 0.45f;
        }

        void Heavy(BladeKit k, JObj w)
        {
            var a = A;
            a.Ink = Mathf.Max(0f, a.Ink - w.Float("heavyInk", 9f)); a.LastFire = 0f;
            k.Cd = w.Float("heavyInterval", 0.46f); k.Since = 0f;
            k.Swing = new BladeKit.Swing { Heavy = true, T = 0f, Side = 1, Yaw = CutYaw(a), Pitch = a.AimPitch };
            FiringT = 0.5f; a.FireFacing = 0.6f;
            // a short forward lunge with the cut
            float fx = Mathf.Sin(k.Swing.Yaw), fz = Mathf.Cos(k.Swing.Yaw);
            k.Lunge = 0f;
            if (a.Grounded && LungeSafe(a, fx, fz, w.Float("lungeDist", 1.05f)))
            {
                k.Lunge = w.Float("lungeTime", 0.13f);
                k.Lx = fx; k.Lz = fz;
                k.Lv = w.Float("lungeDist", 1.05f) / (w.Float("lungeTime", 0.13f) * 0.75f);
            }
        }

        static bool LungeSafe(Actor a, float fx, float fz, float d)
        {
            var level = GameContext.Level;
            if (level == null) return false;
            float[] steps = { 0.5f, 1f, 1.4f };
            foreach (var k in steps)
            {
                float gy = level.GroundHeight(a.Pos.x + fx * d * k, a.Pos.z + fz * d * k, a.Pos.y + 0.6f);
                if (!(gy > GameData.P.Float("waterY", -1.6f) + 0.25f)) return false;
            }
            return true;
        }

        void RunSwing(BladeKit k, float dt, JObj w)
        {
            var S = k.Swing;
            var a = A;
            S.T += dt;
            if (!S.Fired && S.T >= (S.Heavy ? 0.05f : 0.028f))
            {
                S.Fired = true;
                if (S.Heavy) SpawnWave(a, w, S); else FireDrops(a, w, S);
            }
            if (S.Heavy && !S.Stroke && S.T >= 0.075f) { S.Stroke = true; GroundStroke(a, w, S); }
            float t0 = 0.01f, t1 = S.Heavy ? 0.16f : 0.12f;
            if (S.T >= t0 && S.T - dt <= t1) Melee(a, w, S);
            if (k.Lunge > 0f)
            {
                k.Lunge -= dt;
                if (a.Alive)
                {
                    float v = k.Lv * (0.55f + 0.45f * Mathf.Min(1f, k.Lunge / (w.Float("lungeTime", 0.13f) * 0.5f)));
                    a.Vel.x = k.Lx * v; a.Vel.z = k.Lz * v;
                }
            }
            if (S.T > 0.4f) k.Swing = null;
        }

        /// The quick cut: a crescent of droplets flung across the front along the sweep.
        void FireDrops(Actor a, JObj w, BladeKit.Swing S)
        {
            float yaw = S.Yaw;
            int side = S.Side;
            float pitch = a.AimPitch;
            bool steep = pitch < -0.55f;
            float up = steep ? pitch * 0.85f : Mathf.Clamp(pitch, -0.4f, 0.45f) * 0.75f + 0.06f;
            int n = Mathf.Max(1, w.Int("tapDrops", 7));
            float spread = w.Float("tapSpreadDeg", 66f) * Mathf.Deg2Rad;
            var volley = Projectiles.NewVolley();
            for (int i = 0; i < n; i++)
            {
                float t = n > 1 ? i / (float)(n - 1) : 0.5f;
                float off = side * (t - 0.5f) * spread;
                float ang = yaw + off, ox = Mathf.Sin(ang), oz = Mathf.Cos(ang);
                float mid = 1f - Mathf.Abs(t - 0.5f) * 2f;
                float sp = w.Float("tapSpeed", 17.5f) * (0.8f + 0.24f * mid) * (0.95f + UnityEngine.Random.value * 0.1f) * (steep ? 0.45f : 1f);
                var p = new Shot
                {
                    Owner = a, Team = a.Team, Kind = "drop", Age = 0f, Life = 1.2f,
                    Straight = steep ? 0f : w.Float("tapStraight", 0.07f),
                    Radius = w.Float("tapDropRadius", 0.64f) * (0.85f + UnityEngine.Random.value * 0.3f),
                    Damage = w.Float("tapDamageNear", 40f),
                    DmgFar = w.Float("tapDamageFar", 20f),
                    Size = 0.13f,
                    Trail = -0.2f, TrailEvery = 1.25f, TrailRadius = 0.3f,
                    Grav = 24f, Drag = 0.5f,
                    Seed = UnityEngine.Random.value,
                    Volley = volley,
                    Delay = t * 0.018f,
                };
                float r = steep ? 0.55f : 0.8f;
                p.Pos = new Vector3(a.Pos.x + ox * r, a.Pos.y + (steep ? 0.7f : 0.98f) + (0.5f - t) * 0.12f, a.Pos.z + oz * r);
                var from = new Vector3(a.Pos.x, p.Pos.y, a.Pos.z);
                if (!GameContext.Physics.Los(from, p.Pos))
                    p.Pos = from + new Vector3(ox, 0f, oz) * 0.12f;
                p.Prev = p.Pos; p.Start = p.Pos;
                float cu = Mathf.Cos(up + (UnityEngine.Random.value - 0.5f) * 0.05f);
                float tx = -oz * side, tz = ox * side;
                float hx = ox + tx * 0.12f, hz = oz + tz * 0.12f;
                float hl = Mathf.Sqrt(hx * hx + hz * hz);
                if (hl < 1e-4f) hl = 1f;
                p.Vel = new Vector3((hx / hl) * cu * sp, Mathf.Sin(up) * sp, (hz / hl) * cu * sp);
                Projectiles.List.Add(p);
            }
            GameContext.Emit("weapon:fire", new Dictionary<string, object>
            {
                { "actor", a }, { "weapon", w.Str("id") }, { "cut", "quick" },
                { "muzzle", new Vector3(a.Pos.x + Mathf.Sin(yaw) * 0.8f, a.Pos.y + 0.98f, a.Pos.z + Mathf.Cos(yaw) * 0.8f) },
                { "dir", new Vector3(Mathf.Sin(yaw), Mathf.Sin(up), Mathf.Cos(yaw)).normalized },
            });
        }

        /// The blade is a hitbox: every enemy inside the arc in front takes the cut once per swing.
        void Melee(Actor a, JObj w, BladeKit.Swing S)
        {
            float reach = w.Float("reach", 1.5f);
            float half = (S.Heavy ? w.Float("heavyArcDeg", 110f) : w.Float("meleeArcDeg", 150f)) * Mathf.Deg2Rad * 0.5f;
            float fx = Mathf.Sin(S.Yaw), fz = Mathf.Cos(S.Yaw);
            float dmg = S.Heavy ? w.Float("heavyMelee", 130f) : w.Float("tapMelee", 35f);
            var actors = GameContext.Match != null ? GameContext.Match.Actors : null;
            if (actors == null) return;
            var from = new Vector3(a.Pos.x, a.Pos.y + 1f, a.Pos.z);
            float hr = GameData.P.Float("radius", 0.38f);
            for (int i = 0; i < actors.Count; i++)
            {
                var e = actors[i];
                if (e == null || e == a || e.Team == a.Team || !e.Alive) continue;
                if (S.Hits.Contains(e)) continue;
                float dx = e.Pos.x - a.Pos.x, dz = e.Pos.z - a.Pos.z, d = Mathf.Sqrt(dx * dx + dz * dz);
                if (d > reach + hr) continue;
                float h = e.Form == ActorForm.Squid ? GameData.P.Float("squidHeight", 0.55f) : GameData.P.Float("height", 1.45f);
                float ey = e.Pos.y - a.Pos.y;
                if (ey > (S.Heavy ? 1.9f : 1.55f) || ey + h < (S.Heavy ? -0.9f : -0.35f)) continue;
                float fwd = dx * fx + dz * fz, lat = Mathf.Abs(dx * fz - dz * fx);
                float ang = Mathf.Atan2(lat, fwd);
                if (ang > half && !(d < hr + 0.4f && fwd > -0.15f)) continue;
                var to = new Vector3(e.Pos.x, e.Pos.y + Mathf.Min(h * 0.6f, 0.85f), e.Pos.z);
                if (!GameContext.Physics.Los(from, to)) continue;
                S.Hits.Add(e);
                Projectiles.ApplyHit(a, e, dmg);
                GameContext.Emit("blade:hit", new Dictionary<string, object>
                {
                    { "actor", a }, { "victim", e }, { "damage", dmg }, { "heavy", S.Heavy },
                });
            }
        }

        /// The blade meets the ground in front at the bottom of the overhead cut.
        void GroundStroke(Actor a, JObj w, BladeKit.Swing S)
        {
            float fx = Mathf.Sin(S.Yaw), fz = Mathf.Cos(S.Yaw);
            var n = new Vector3(fx, 0f, fz);
            float area = 0f;
            float[] ds = { 0.75f, 1.35f };
            foreach (float d in ds)
            {
                var v = new Vector3(a.Pos.x + fx * d, a.Pos.y + 0.8f, a.Pos.z + fz * d);
                var g = GameContext.Physics.Raycast(v, Vector3.down, 2.2f, null, true);
                if (!g.Hit_) continue;
                var at = g.Point + g.Normal * 0.08f;
                area += GameContext.Paint.Splat(at, w.Float("strokeRadius", 0.6f), a.Team, UnityEngine.Random.value,
                                                PaintSystem.Kind.Shot, n, 0.9f);
            }
            if (area > 0f) a.AddTurf(area);
        }

        /// The charged cut sends a wave of ink forward.
        void SpawnWave(Actor a, JObj w, BladeKit.Swing S)
        {
            float pitch = Mathf.Abs(S.Pitch) < 0.3f ? S.Pitch * 0.4f : Mathf.Clamp(S.Pitch, -0.45f, 0.35f);
            float cp = Mathf.Cos(pitch);
            var dir = new Vector3(Mathf.Sin(S.Yaw) * cp, Mathf.Sin(pitch), Mathf.Cos(S.Yaw) * cp);
            var pos = new Vector3(a.Pos.x + Mathf.Sin(S.Yaw) * 0.55f, a.Pos.y + w.Float("waveHeight", 1.7f) * 0.52f, a.Pos.z + Mathf.Cos(S.Yaw) * 0.55f);
            var from = new Vector3(a.Pos.x, pos.y, a.Pos.z);
            if (!GameContext.Physics.Los(from, pos)) pos = from;
            BladeWaves.Add(new BladeWave { Owner = a, Team = a.Team, Pos = pos, Dir = dir.normalized });
            GameContext.Emit("weapon:fire", new Dictionary<string, object>
            {
                { "actor", a }, { "weapon", w.Str("id") }, { "cut", "charged" }, { "muzzle", pos }, { "dir", dir },
            });
        }
    }

    /// The live ink waves (the blade's heavy cut).
    public static class BladeWaves
    {
        public static readonly List<BladeWave> List = new List<BladeWave>();
        public static void Clear() => List.Clear();

        public static void Tick(float dt)
        {
            var actors = GameContext.Match != null ? GameContext.Match.Actors : null;
            for (int i = List.Count - 1; i >= 0; i--)
            {
                var W = List[i];
                var w = W.Owner != null ? W.Owner.Weapon : null;
                if (w == null) { List.RemoveAt(i); continue; }
                float speed = w.Float("waveSpeed", 26f), range = w.Float("waveRange", 12f);
                W.T += dt;
                float step = speed * dt;
                W.Pos += W.Dir * step;
                W.Dist += step;
                if (W.Dist > range) { List.RemoveAt(i); continue; }

                // the wall paints as it goes
                W.PaintAcc += dt;
                if (W.PaintAcc >= w.Float("wavePaintEvery", 0.7f))
                {
                    W.PaintAcc = 0f;
                    var at = W.Pos + Vector3.down * (w.Float("waveHeight", 1.7f) * 0.5f);
                    var g = GameContext.Physics.Raycast(at, Vector3.down, 3f, null, true);
                    if (g.Hit_)
                    {
                        var p = g.Point + g.Normal * 0.06f;
                        float area = GameContext.Paint.Splat(p, w.Float("wavePaintRadius", 0.62f), W.Team, UnityEngine.Random.value,
                                                            PaintSystem.Kind.Roll, new Vector3(W.Dir.x, 0f, W.Dir.z), 0.8f);
                        if (W.Owner != null && area > 0f) W.Owner.AddTurf(area);
                    }
                }

                // the wall cuts what it passes
                if (actors != null)
                {
                    float endR = w.Float("waveEndRadius", 1.05f);
                    float dmg = w.Float("waveDamage", 70f);
                    for (int e = 0; e < actors.Count; e++)
                    {
                        var a = actors[e];
                        if (a == null || a.Team == W.Team || !a.Alive) continue;
                        if (W.Hits.Contains(a)) continue;
                        if ((a.Pos - W.Pos).magnitude > endR + GameData.P.Float("radius", 0.38f)) continue;
                        W.Hits.Add(a);
                        Projectiles.ApplyHit(W.Owner, a, dmg);
                    }
                }
            }
        }
    }
}
