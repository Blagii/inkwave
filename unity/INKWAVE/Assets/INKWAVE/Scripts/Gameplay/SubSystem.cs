// Sub weapons (the devices you throw or place) and the specials (the super meter).
//
// A port of the web build's src/game/subs.js and the core of src/game/specials.js. Ported so far:
//   subs     — the splat bomb, the cling charge, the pop pellet, the skitter bomb and the scan cloud
//   specials — the Tidal Slam, the Ink Tempest, the Bomb Barrage (all its variants), the Deep Sonar,
//              the Bubble Guard, the Vortex Strike, the Kraken and the Ink Jet
// The rest (the ink curtain, the sprinkler, the mines, the beacons, the murk bomb and the kit subs) and the
// specials that need their own world objects (the Twister Zooka, the Howl Box, the Bubble Blower, the Mega
// Stamp, the Cheer Orb, the Zipline, the Crab Rig) are not ported yet.
using System.Collections.Generic;
using INKWAVE.Core;
using INKWAVE.Data;
using INKWAVE.World;
using InkPhysics = INKWAVE.World.Physics;   // UnityEngine.Physics would be ambiguous
using UnityEngine;

namespace INKWAVE.Gameplay
{
    // =============================================================================================== the subs
    public class SubItem
    {
        public Actor Owner;
        public int Team;
        public string Kind;
        public JObj Def;
        public Vector3 Pos, Vel, Normal;
        public string State = "fly";     // fly | stuck | run | cloud | dead
        public float Age, T, Fuse, BeepT, Trail, StuckT, Dash, Head;
        public Actor Target;
    }

    public static class SubSystem
    {
        const float Grav = 24f;
        public static readonly List<SubItem> Items = new List<SubItem>();

        public static void Clear() => Items.Clear();

        public static bool Supported(string kind)
        {
            return kind == "bomb" || kind == "sticky" || kind == "burst" || kind == "seeker" || kind == "scan";
        }

        /// Throw (or place) a sub.
        public static void Use(Actor a, JObj sub)
        {
            if (sub == null) return;
            string kind = sub.Str("kind");
            if (!Supported(kind)) return;
            var pos = a.Pos + new Vector3(0f, 1.35f, 0f);
            var vel = Projectiles.ThrowVelocity(a, sub.Float("throwSpeed", 13.5f));
            var it = new SubItem
            {
                Owner = a, Team = a.Team, Kind = kind, Def = sub,
                Pos = pos, Vel = vel, State = "fly",
            };
            Items.Add(it);
            GameContext.Emit("sub:use", new Dictionary<string, object> { { "actor", a }, { "kind", kind } });
        }

        public static void Tick(float dt)
        {
            for (int i = Items.Count - 1; i >= 0; i--)
            {
                var it = Items[i];
                it.Age += dt;
                switch (it.State)
                {
                    case "fly": Fly(it, dt); break;
                    case "stuck": Stuck(it, dt); break;
                    case "run": Run(it, dt); break;
                    case "cloud": Cloud(it, dt); break;
                }
                if (it.State == "dead") Items.RemoveAt(i);
            }
        }

        static void Fly(SubItem it, float dt)
        {
            var s = it.Def;
            it.Vel.y -= Grav * dt;
            var from = it.Pos;
            it.Pos += it.Vel * dt;

            // the pop pellet bursts on a direct hit
            if (it.Kind == "burst")
            {
                var actors = GameContext.Match != null ? GameContext.Match.Actors : null;
                if (actors != null)
                {
                    float hr = GameData.P.Float("radius", 0.38f);
                    for (int i = 0; i < actors.Count; i++)
                    {
                        var a = actors[i];
                        if (a == null || a.Team == it.Team || !a.Alive) continue;
                        float h = a.Form == ActorForm.Squid ? GameData.P.Float("squidHeight", 0.55f) : GameData.P.Float("height", 1.45f);
                        var res = new SegmentResult();
                        InkPhysics.SegmentCapsuleDist(from, it.Pos, a.Pos, hr + 0.1f, h, res);
                        if (res.Dist < hr + 0.12f)
                        {
                            it.Pos = Vector3.Lerp(from, it.Pos, res.T);
                            PelletBlast(it, a);
                            it.State = "dead";
                            return;
                        }
                    }
                }
            }

            // the scan cloud bursts in mid-air when its fuse runs out
            if (it.Kind == "scan" && it.Age > s.Float("fuse", 0.7f)) { StartCloud(it); return; }

            var hit = GameContext.Physics.Segment(from, it.Pos, null, true);
            if (hit.Hit_)
            {
                bool floor = hit.Normal.y > 0.6f;
                if (it.Kind == "sticky") { Stick(it, hit, s.Float("fuse", 2.4f)); return; }
                if (it.Kind == "burst") { PelletBlast(it, null); it.Pos = hit.Point; it.State = "dead"; return; }
                if (it.Kind == "scan") { it.Pos = hit.Point + hit.Normal * 0.3f; StartCloud(it); return; }
                if (it.Kind == "seeker" && floor) { StartRun(it, hit); return; }
                // otherwise it bounces
                it.Pos = hit.Point + hit.Normal * 0.12f;
                float vn = Vector3.Dot(it.Vel, hit.Normal);
                it.Vel = (it.Vel - hit.Normal * vn * 1.4f) * 0.55f;
            }
            if (it.Pos.y < GameData.P.Float("waterY", -1.6f) - 1.8f) it.State = "dead";
        }

        static void Stick(SubItem it, Hit hit, float fuse)
        {
            it.State = "stuck"; it.T = 0f; it.Fuse = fuse; it.BeepT = 0f;
            it.Normal = hit.Normal;
            it.Pos = hit.Point + hit.Normal * 0.005f;
            GameContext.Emit("sub:land", new Dictionary<string, object>
            {
                { "kind", it.Kind }, { "pos", it.Pos }, { "team", it.Team }, { "radius", it.Def.Float("radius", 3f) },
            });
        }

        static void Stuck(SubItem it, float dt)
        {
            it.T += dt;
            if (it.T >= it.Fuse)
            {
                var s = it.Def;
                Blast(it, it.Pos, s.Float("radius", 4f), s.Float("damageMax", 180f), s.Float("damageMin", 35f), s.Float("paintRadius", 3.3f), it.Normal);
                it.State = "dead";
            }
        }

        static void StartRun(SubItem it, Hit hit)
        {
            it.State = "run"; it.T = 0f;
            it.Pos = hit.Point;
            it.Head = Mathf.Atan2(it.Vel.x, it.Vel.z);
            it.Trail = 0f; it.StuckT = 0f; it.Target = null; it.Dash = false;
        }

        static void Run(SubItem it, float dt)
        {
            var s = it.Def;
            it.T += dt;
            // the nearest foe
            if (it.Target == null || !it.Target.Alive)
            {
                it.Target = NearestFoe(it, s.Float("seekRange", 15f));
            }
            float speed = s.Float("speed", 6.3f);
            if (it.Target != null)
            {
                var d = it.Target.Pos - it.Pos;
                d.y = 0f;
                if (d.magnitude > 0.01f)
                {
                    float want = Mathf.Atan2(d.x, d.z);
                    float diff = Mathf.DeltaAngle(it.Head * Mathf.Rad2Deg, want * Mathf.Rad2Deg) * Mathf.Deg2Rad;
                    it.Head += Mathf.Clamp(diff, -s.Float("turnRate", 1.75f) * dt, s.Float("turnRate", 1.75f) * dt);
                }
                float dist = new Vector2(it.Target.Pos.x - it.Pos.x, it.Target.Pos.z - it.Pos.z).magnitude;
                if (dist < s.Float("triggerDist", 1.2f))
                {
                    Blast(it, it.Pos + Vector3.up * 0.2f, s.Float("radius", 2.8f), s.Float("damageMax", 180f), s.Float("damageMin", 35f), s.Float("paintRadius", 2.4f), Vector3.up);
                    it.State = "dead";
                    return;
                }
            }
            // it creeps along the ground, leaving a swimmable trail
            var dir = new Vector3(Mathf.Sin(it.Head), 0f, Mathf.Cos(it.Head));
            var next = it.Pos + dir * speed * dt;
            var g = GameContext.Physics.Raycast(new Vector3(next.x, it.Pos.y + 0.5f, next.z), Vector3.down, 2f, null, true);
            if (g.Hit_)
            {
                it.Pos = g.Point;
                it.Trail += speed * dt;
                if (it.Trail > 0.25f)
                {
                    it.Trail = 0f;
                    float area = GameContext.Paint.Splat(g.Point + g.Normal * 0.05f, s.Float("trailRadius", 0.6f), it.Team, UnityEngine.Random.value);
                    if (it.Owner != null && area > 0f) it.Owner.AddTurf(area);
                }
            }
            else it.Pos = next;
            if (it.T > s.Float("life", 4.5f)) it.State = "dead";
        }

        static void StartCloud(SubItem it)
        {
            it.State = "cloud"; it.T = 0f;
            it.Fuse = it.Def.Float("fuse", 0.7f);
            GameContext.Emit("sub:cloud", new Dictionary<string, object>
            {
                { "kind", it.Kind }, { "pos", it.Pos }, { "team", it.Team }, { "radius", it.Def.Float("radius", 3f) },
            });
        }

        static void Cloud(SubItem it, float dt)
        {
            var s = it.Def;
            it.T += dt;
            float radius = s.Float("radius", 3f);
            // the cloud marks the enemies it can see
            var actors = GameContext.Match != null ? GameContext.Match.Actors : null;
            if (actors != null)
            {
                for (int i = 0; i < actors.Count; i++)
                {
                    var a = actors[i];
                    if (a == null || a.Team == it.Team || !a.Alive) continue;
                    if ((a.Pos - it.Pos).magnitude > radius) continue;
                    a.Reveal = Mathf.Max(a.Reveal, 0.2f);
                }
            }
            if (it.T >= it.Fuse) it.State = "dead";
        }

        /// The blast of a bomb: the damage falls off with the distance, the ink lands in a ring.
        static void Blast(SubItem it, Vector3 c, float radius, float dmgMax, float dmgMin, float paintRadius, Vector3 n)
        {
            var actors = GameContext.Match != null ? GameContext.Match.Actors : null;
            if (actors != null)
            {
                for (int i = 0; i < actors.Count; i++)
                {
                    var a = actors[i];
                    if (a == null || a.Team == it.Team || !a.Alive) continue;
                    var v = a.Pos + new Vector3(0f, 0.7f, 0f);
                    float d = (v - c).magnitude;
                    if (d > radius) continue;
                    if (!GameContext.Physics.Los(c, v)) continue;
                    Projectiles.ApplyHit(it.Owner, a, Mathf.Lerp(dmgMax, dmgMin, Mathf.Clamp01(d / radius)));
                }
            }
            var at = c + n * 0.12f;
            float area = GameContext.Paint.Splat(at, paintRadius, it.Team, UnityEngine.Random.value, PaintSystem.Kind.Blast);
            if (it.Owner != null && area > 0f) it.Owner.AddTurf(area);
            GameContext.Emit("weapon:impact", new Dictionary<string, object>
            {
                { "pos", at }, { "normal", n }, { "team", it.Team }, { "kind", "blast" },
            });
        }

        /// The pop pellet: a direct hit splats, near misses hurt.
        static void PelletBlast(SubItem it, Actor direct)
        {
            var s = it.Def;
            var actors = GameContext.Match != null ? GameContext.Match.Actors : null;
            if (direct != null) Projectiles.ApplyHit(it.Owner, direct, s.Float("directDamage", 60f));
            if (actors != null)
            {
                float radius = s.Float("radius", 2.1f);
                for (int i = 0; i < actors.Count; i++)
                {
                    var a = actors[i];
                    if (a == null || a.Team == it.Team || !a.Alive || a == direct) continue;
                    var v = a.Pos + new Vector3(0f, 0.6f, 0f);
                    if ((v - it.Pos).magnitude > radius) continue;
                    Projectiles.ApplyHit(it.Owner, a, s.Float("splashDamage", 35f));
                }
            }
            float area = GameContext.Paint.Splat(it.Pos, s.Float("paintRadius", 1.8f), it.Team, UnityEngine.Random.value, PaintSystem.Kind.Blast);
            if (it.Owner != null && area > 0f) it.Owner.AddTurf(area);
        }

        static Actor NearestFoe(SubItem it, float range)
        {
            var actors = GameContext.Match != null ? GameContext.Match.Actors : null;
            Actor best = null; float bd = range;
            if (actors == null) return null;
            for (int i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || a.Team == it.Team || !a.Alive) continue;
                float d = (a.Pos - it.Pos).magnitude;
                if (d < bd) { bd = d; best = a; }
            }
            return best;
        }
    }

    // ============================================================================================ the specials
    public class SpecialState
    {
        public Actor Owner;
        public string Id, Kind;
        public JObj Def;
        public float T, Dur, Next;
        public bool Ended;
        public Vector3 From, At;
        public float Phase;
    }

    public static class SpecialSystem
    {
        public static readonly List<SpecialState> Active = new List<SpecialState>();

        public static void Clear() => Active.Clear();

        public static bool Supported(string id)
        {
            return id == "slam" || id == "storm" || id == "barrage" || id == "barrage_sticky" || id == "barrage_burst"
                || id == "barrage_seeker" || id == "sonar" || id == "bubbler" || id == "strike" || id == "kraken"
                || id == "jetpack";
        }

        /// Fire the special (the meter is full).
        public static void Use(Actor a)
        {
            if (a == null || !a.Alive) return;
            var def = GameData.Special(a.SpecialId);
            if (def == null) return;
            string kind = def.Str("kind", a.SpecialId);
            if (!Supported(a.SpecialId)) return;
            var s = new SpecialState
            {
                Owner = a, Id = a.SpecialId, Kind = kind, Def = def,
                Dur = def.Float("duration", 0f), At = a.Pos, From = a.Pos,
            };
            Active.Add(s);
            a.Special = 0f;
            a.SpecialActive = true;
            GameContext.Emit("special:start", new Dictionary<string, object> { { "actor", a }, { "id", a.SpecialId } });

            switch (kind)
            {
                case "slam": Slam(a, def); break;
                case "storm": Storm(a, def); break;
                case "barrage": Barrage(a, def); break;
                case "sonar": Sonar(a, def); break;
                case "bubbler": Bubbler(a, def); break;
                case "strike": Strike(a, def); break;
                case "kraken": Kraken(a, def); break;
                case "jetpack": Jetpack(a, def); break;
            }
        }

        public static void Tick(float dt)
        {
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                var s = Active[i];
                s.T += dt;
                var a = s.Owner;
                switch (s.Kind)
                {
                    case "slam": SlamTick(s, dt); break;
                    case "storm": StormTick(s, dt); break;
                    case "barrage": BarrageTick(s, dt); break;
                    case "sonar": SonarTick(s, dt); break;
                    case "bubbler": BubblerTick(s, dt); break;
                    case "strike": StrikeTick(s, dt); break;
                    case "kraken": KrakenTick(s, dt); break;
                    case "jetpack": JetpackTick(s, dt); break;
                }
                bool over = s.T >= s.Dur && s.Dur > 0f;
                if (over || a == null || !a.Alive)
                {
                    if (a != null) a.SpecialActive = false;
                    Active.RemoveAt(i);
                }
            }
        }

        // --------------------------------------------------------------------------------- the Tidal Slam
        static void Slam(Actor a, JObj def)
        {
            // leap up, hang, then come down with a shockwave
            a.Vel.y = 13.5f;
            a.Grounded = false;
            GameContext.Emit("special:slam", new Dictionary<string, object>
            {
                { "actor", a }, { "phase", "rise" },
            });
        }

        static void SlamTick(SpecialState s, float dt)
        {
            var a = s.Owner;
            if (a == null) return;
            if (s.Phase < 1f)
            {
                if (s.T >= s.Def.Float("rise", 0.55f)) { s.Phase = 1f; s.T = 0f; a.Vel.y = 0f; }
                return;
            }
            if (s.Phase < 2f)
            {
                if (s.T >= s.Def.Float("hang", 0.25f))
                {
                    s.Phase = 2f; s.T = 0f;
                    a.Vel.y = -34f;
                }
                return;
            }
            // the landing
            if (a.Grounded || s.T > 2.5f)
            {
                s.Phase = 3f;
                float radius = s.Def.Float("radius", 5.2f);
                float kill = s.Def.Float("killRadius", 3.2f);
                var actors = GameContext.Match != null ? GameContext.Match.Actors : null;
                if (actors != null)
                {
                    for (int i = 0; i < actors.Count; i++)
                    {
                        var e = actors[i];
                        if (e == null || e.Team == a.Team || !e.Alive) continue;
                        float d = (e.Pos - a.Pos).magnitude;
                        if (d > radius) continue;
                        float dmg = Mathf.Lerp(s.Def.Float("damageMax", 180f), s.Def.Float("damageMin", 55f), Mathf.Clamp01(d / radius));
                        if (d <= kill) dmg = s.Def.Float("damageMax", 180f);
                        Projectiles.ApplyHit(a, e, dmg);
                    }
                }
                float area = GameContext.Paint.Splat(a.Pos, radius * 0.8f, a.Team, UnityEngine.Random.value, PaintSystem.Kind.Blast);
                if (area > 0f) a.AddTurf(area);
                a.Invuln = Mathf.Max(a.Invuln, 0.4f);
                GameContext.Emit("special:slam", new Dictionary<string, object>
                {
                    { "actor", a }, { "pos", a.Pos }, { "radius", radius },
                });
            }
        }

        // ------------------------------------------------------------------------------- the Ink Tempest
        static void Storm(Actor a, JObj def)
        {
            var cloud = new StormCloud
            {
                Owner = a, Team = a.Team,
                Pos = a.Pos + new Vector3(Mathf.Sin(a.AimYaw) * 2f, 2.2f, Mathf.Cos(a.AimYaw) * 2f),
                Radius = def.Float("radius", 3.4f),
                Dps = def.Float("dps", 34f),
                Drift = def.Float("driftSpeed", 1.1f),
                Life = def.Float("duration", 6.5f),
            };
            StormClouds.Add(cloud);
        }

        public class StormCloud
        {
            public Actor Owner; public int Team;
            public Vector3 Pos;
            public float Radius, Dps, Drift, Life, T;
        }
        public static readonly List<StormCloud> StormClouds = new List<StormCloud>();

        static void StormTick(SpecialState s, float dt)
        {
            var cloud = StormClouds.Count > 0 ? StormClouds[StormClouds.Count - 1] : null;
            if (cloud == null) return;
            cloud.T += dt;
            cloud.Pos += new Vector3(Mathf.Sin(s.Owner != null ? s.Owner.AimYaw : 0f), 0f, Mathf.Cos(s.Owner != null ? s.Owner.AimYaw : 0f)) * cloud.Drift * dt;
            // it soaks the turf below
            var g = GameContext.Physics.Raycast(cloud.Pos + Vector3.up * 0.5f, Vector3.down, 12f, null, true);
            if (g.Hit_)
            {
                float area = GameContext.Paint.Splat(g.Point + g.Normal * 0.05f, cloud.Radius, cloud.Team, UnityEngine.Random.value);
                if (cloud.Owner != null && area > 0f) cloud.Owner.AddTurf(area);
            }
            // and the foes under it burn
            var actors = GameContext.Match != null ? GameContext.Match.Actors : null;
            if (actors != null)
            {
                for (int i = 0; i < actors.Count; i++)
                {
                    var e = actors[i];
                    if (e == null || e.Team == cloud.Team || !e.Alive) continue;
                    if ((e.Pos - cloud.Pos).magnitude > cloud.Radius) continue;
                    Projectiles.ApplyHit(cloud.Owner, e, cloud.Dps * dt);
                }
            }
            if (cloud.T >= cloud.Life) StormClouds.Remove(cloud);
        }

        // ------------------------------------------------------------------------------ the Bomb Barrage
        static void Barrage(Actor a, JObj def)
        {
            string bomb = def.Str("bomb", "bomb");
            var sub = GameData.Sub(bomb);
            if (sub == null) sub = GameData.Sub("bomb");
            if (sub == null) return;
            s_bomb = sub;
            s_next = 0f;
        }
        static JObj s_bomb;
        static float s_next;

        static void BarrageTick(SpecialState s, float dt)
        {
            var a = s.Owner;
            if (a == null || s_bomb == null) return;
            s.Next -= dt;
            if (s.Next > 0f) return;
            s.Next = s.Def.Float("gap", 0.3f);
            var sub = s_bomb;
            // the bomb is thrown toward the aim
            var pos = a.Pos + new Vector3(0f, 1.35f, 0f);
            var vel = Projectiles.ThrowVelocity(a, sub.Float("throwSpeed", 13.5f));
            var it = new SubItem
            {
                Owner = a, Team = a.Team, Kind = sub.Str("kind", "bomb"), Def = sub,
                Pos = pos, Vel = vel, State = "fly",
            };
            SubSystem.Items.Add(it);
        }

        // ---------------------------------------------------------------------------------- the Deep Sonar
        static void Sonar(Actor a, JObj def)
        {
            // every enemy is revealed to the team for a while
            var actors = GameContext.Match != null ? GameContext.Match.Actors : null;
            if (actors == null) return;
            for (int i = 0; i < actors.Count; i++)
            {
                var e = actors[i];
                if (e == null || e.Team == a.Team || !e.Alive) continue;
                e.Reveal = Mathf.Max(e.Reveal, def.Float("duration", 8f));
            }
            GameContext.Emit("special:sonar", new Dictionary<string, object> { { "actor", a } });
        }

        static void SonarTick(SpecialState s, float dt) { }

        // -------------------------------------------------------------------------------- the Bubble Guard
        static void Bubbler(Actor a, JObj def)
        {
            Bubbles.Add(new Bubble { Owner = a, Team = a.Team, Pos = a.Pos, Radius = def.Float("radius", 1.3f) });
        }

        public class Bubble
        {
            public Actor Owner; public int Team;
            public Vector3 Pos;
            public float Radius;
            public float T;
        }
        public static readonly List<Bubble> Bubbles = new List<Bubble>();

        static void BubblerTick(SpecialState s, float dt)
        {
            var b = Bubbles.Count > 0 ? Bubbles[Bubbles.Count - 1] : null;
            if (b == null) return;
            b.T += dt;
            b.Pos = b.Owner != null ? b.Owner.Pos : b.Pos;
        }

        // -------------------------------------------------------------------------------- the Vortex Strike
        static void Strike(Actor a, JObj def)
        {
            // the mark lands where the player aims
            var target = a.AimPoint;
            var g = GameContext.Physics.Raycast(target + Vector3.up * 0.5f, Vector3.down, 20f, null, true);
            Vector3 at = g.Hit_ ? g.Point : a.Pos;
            Vortices.Add(new Vortex
            {
                Owner = a, Team = a.Team, Pos = at,
                Radius = def.Float("radius", 5.5f), Dps = def.Float("dps", 62f),
                Life = def.Float("duration", 4.5f),
            });
        }

        public class Vortex
        {
            public Actor Owner; public int Team;
            public Vector3 Pos;
            public float Radius, Dps, Life, T;
        }
        public static readonly List<Vortex> Vortices = new List<Vortex>();

        static void StrikeTick(SpecialState s, float dt)
        {
            var v = Vortices.Count > 0 ? Vortices[Vortices.Count - 1] : null;
            if (v == null) return;
            v.T += dt;
            var actors = GameContext.Match != null ? GameContext.Match.Actors : null;
            if (actors != null)
            {
                for (int i = 0; i < actors.Count; i++)
                {
                    var e = actors[i];
                    if (e == null || e.Team == v.Team || !e.Alive) continue;
                    float d = (e.Pos - v.Pos).magnitude;
                    if (d > v.Radius) continue;
                    Projectiles.ApplyHit(v.Owner, e, v.Dps * dt);
                    // the vortex pulls the foes in
                    var pull = (v.Pos - e.Pos).normalized * v.Radius * 0.35f * dt;
                    e.Pos += pull;
                }
            }
            if (v.T % 0.3f < dt)
            {
                float area = GameContext.Paint.Splat(v.Pos, v.Radius * 0.6f, v.Team, UnityEngine.Random.value);
                if (v.Owner != null && area > 0f) v.Owner.AddTurf(area);
            }
            if (v.T >= v.Life) Vortices.Remove(v);
        }

        // -------------------------------------------------------------------------------------- the Kraken
        static void Kraken(Actor a, JObj def)
        {
            a.Kraken = true;
            GameContext.Emit("special:kraken", new Dictionary<string, object> { { "actor", a } });
        }

        static void KrakenTick(SpecialState s, float dt)
        {
            var a = s.Owner;
            if (a == null) return;
            // the kraken swims through any ink and splats what it touches
            float radius = s.Def.Float("radius", 2.3f);
            float damage = s.Def.Float("damage", 200f);
            var actors = GameContext.Match != null ? GameContext.Match.Actors : null;
            if (actors == null) return;
            for (int i = 0; i < actors.Count; i++)
            {
                var e = actors[i];
                if (e == null || e.Team == a.Team || !e.Alive) continue;
                if ((e.Pos - a.Pos).magnitude > radius) continue;
                if (a.KrakenCd > 0f) continue;
                a.KrakenCd = s.Def.Float("cooldown", 0.65f);
                Projectiles.ApplyHit(a, e, damage);
                float area = GameContext.Paint.Splat(e.Pos, s.Def.Float("paintRadius", 1.15f), a.Team, UnityEngine.Random.value);
                if (area > 0f) a.AddTurf(area);
            }
            if (a.KrakenCd > 0f) a.KrakenCd -= dt;
        }

        // ------------------------------------------------------------------------------------- the Ink Jet
        static void Jetpack(Actor a, JObj def)
        {
            a.Jet = true;
            GameContext.Emit("special:jetpack", new Dictionary<string, object> { { "actor", a } });
        }

        static void JetpackTick(SpecialState s, float dt)
        {
            var a = s.Owner;
            if (a == null) return;
            // the jet lifts the player and fires at the aim
            a.Vel.y = Mathf.Max(a.Vel.y, s.Def.Float("height", 3.8f) * 0.6f);
            s.Next -= dt;
            if (s.Next > 0f) return;
            s.Next = s.Def.Float("interval", 0.55f);
            var m = Projectiles.Muzzle(a);
            var dir = Projectiles.AimFrom(a, m, out _);
            var p = new Shot
            {
                Owner = a, Team = a.Team, Kind = "shot", Age = 0f, Life = 1.4f,
                Straight = 0.05f, Radius = s.Def.Float("splashRadius", 2.4f),
                Damage = s.Def.Float("directDamage", 125f),
                DmgFar = s.Def.Float("splashMin", 30f),
                Size = 0.18f, Trail = 0f, TrailEvery = 2f, TrailRadius = 0.4f,
                Grav = 0f, Drag = 0.2f, Seed = UnityEngine.Random.value,
            };
            p.Pos = m; p.Prev = m; p.Start = m;
            p.Vel = dir * s.Def.Float("projSpeed", 30f);
            Projectiles.List.Add(p);
        }
    }
}
