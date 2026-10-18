// Zone Control: the rules engine (no UI) — a port of the web build's src/game/zones.js.
//
// A match in the "zones" mode owns one of these. The stage layout gives a centre objective (one zone, or two that
// have to be held together) and one side zone per team half (Bravo's is the 180° mirror of Alpha's). Only one
// objective is live at a time: the centre to start with, then a rotation every 30–60 s.
//
//   · coverage  — the share of the zone's inkable floor each team has inked. Taking it needs ≥ 80 %; a held zone is
//                 neutralised when the other team inks ≥ 40 % of it.
//   · countdown — each team starts at 100 and counts down while the opponent holds the live objective (1 pt/s at the
//                 centre, 1 pt/2 s at a side zone on your own half, 1 pt/0.5 s on the enemy's half). At 0 you lose.
//   · penalty   — losing the objective costs ROUND(0.75 × (start − end)) + (start = 100 ? 1 : 0) points of lock.
//   · overtime  — the last 30 s and the overtime are played on the centre only.
using System.Collections.Generic;
using INKWAVE.Core;
using INKWAVE.Data;
using UnityEngine;

namespace INKWAVE.Gameplay
{
    public class ZoneControl
    {
        public class Zone
        {
            public int Id;
            public string Kind;            // "center" | "side"
            public int Home = -1;          // -1 = the centre, 0 / 1 = the side zone of that team
            public int Owner = -1;
            public readonly float[] Share = new float[2];
            public float Hold;             // the zone cannot be taken until the wipe is over
            public bool WarnArmed = true;
            public float WarnT = -99f;
            public readonly List<int> Cells = new List<int>();
            public Vector3 Center;
            public float Reach;
            public List<List<float[]>> Polys = null;   // the outline(s) (x, z pairs) for the point-in-polygon test
        }

        public class Objective
        {
            public string Id;
            public readonly List<Zone> Zones = new List<Zone>();
            public int Home = -1;
        }

        // ---- the config (src/config.js → ZONES)
        const float FinalCentre = 30f, Warn = 0.3f, WarnGap = 4f, MinStint = 10f, FillT = 0.6f, WipeT = 0.7f;
        const float Count0 = 100f, RotateMin = 30f, RotateMax = 60f, Control = 0.8f, Contest = 0.4f;
        const float RateCenter = 1f, RateHome = 0.5f, RateAway = 2f, PenaltyK = 0.75f;
        const float GaugeHeld = 4.5f, GaugeNeutral = 1.5f, OvertimeGrace = 10f, OvertimeMax = 300f;
        const float SampleHz = 5f;

        readonly Match _match;
        readonly List<Zone> _zones = new List<Zone>();
        readonly List<Objective> _objectives = new List<Objective>();

        public Objective Active;
        public int Owner = -1;             // the holder of the live objective (-1 = nobody)
        public int LastOwner = -1;
        public float NeutralT;
        public readonly float[] Count = new float[2];
        public readonly float[] Penalty = new float[2];
        public readonly float[] Start = new float[2];
        public readonly float[] TieEnd = { -1f, -1f };
        public readonly float[] HoldT = new float[2];
        public float Clock;
        public bool Final;
        public float NextSwap = float.PositiveInfinity;
        public bool Overtime;
        public float OvertimeT;
        public int OvertimeLosing = -1;
        public int Winner = -1;
        public string Reason;
        public bool Placeholder;

        float _sampleT;

        public ZoneControl(Match match)
        {
            _match = match;
            var layout = GameContext.StageZones;
            if (layout == null || layout.center == null || layout.center.Count == 0) { Placeholder = true; layout = PlaceholderLayout(); }
            else Placeholder = false;

            // the zones: the centre ones, then the side zone of team 0 and its mirror for team 1
            var defs = new List<Zone>();
            for (int i = 0; i < layout.center.Count; i++)
            {
                var z = MakeZone(defs.Count, "center", -1, layout.center[i]);
                defs.Add(z);
            }
            if (layout.side != null)
            {
                defs.Add(MakeZone(defs.Count, "side", 0, layout.side));
                defs.Add(MakeZone(defs.Count, "side", 1, Mirror(layout.side)));
            }
            _zones.AddRange(defs);

            int nc = layout.center.Count;
            var centre = new Objective { Id = "center", Home = -1 };
            for (int i = 0; i < nc; i++) centre.Zones.Add(_zones[i]);
            _objectives.Add(centre);
            if (_zones.Count > nc)
            {
                var sideA = new Objective { Id = "sideA", Home = 0 };
                sideA.Zones.Add(_zones[nc]);
                var sideB = new Objective { Id = "sideB", Home = 1 };
                sideB.Zones.Add(_zones[nc + 1]);
                _objectives.Add(sideA);
                _objectives.Add(sideB);
            }
            Active = _objectives[0];
            Count[0] = Count[1] = Count0;
            Start[0] = Start[1] = Count0;
            Final = _match != null && _match.Time <= FinalCentre;
            Schedule();
        }

        // ------------------------------------------------------------------------------ the zone setup
        Zone MakeZone(int id, string kind, int home, ZonePoly def)
        {
            var z = new Zone { Id = id, Kind = kind, Home = home };
            var parts = new List<List<float[]>>();
            if (def.polys != null && def.polys.Count > 0) parts.AddRange(def.polys);
            else if (def.poly != null && def.poly.Count > 0) parts.Add(def.poly);
            z.Polys = parts;

            float y0 = def.y0, y1 = def.y1;
            var level = GameContext.Level;
            var paint = GameContext.Paint;
            if (level == null || paint == null) return z;

            // the paint-grid cells inside the outline
            float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
            foreach (var p in parts) foreach (var pt in p)
            {
                x0 = Mathf.Min(x0, pt[0]); x1 = Mathf.Max(x1, pt[0]);
                z0 = Mathf.Min(z0, pt[1]); z1 = Mathf.Max(z1, pt[1]);
            }
            float cx = 0f, cy = 0f, cz = 0f;
            int n = 0;
            foreach (var f in level.Faces)
            {
                if (!f.Turf || !f.Paintable) continue;
                for (int j = 0; j < f.Nv; j++)
                {
                    for (int i = 0; i < f.Nu; i++)
                    {
                        float s = (i + 0.5f) * f.Cu, t = (j + 0.5f) * f.Cv;
                        var w = f.Origin + f.U * s + f.V * t;
                        if (w.x < x0 || w.x > x1 || w.z < z0 || w.z > z1) continue;
                        if (w.y < y0 || w.y > y1) continue;
                        if (!InsideParts(parts, w.x, w.z)) continue;
                        z.Cells.Add(f.Grid + j * f.Nu + i);
                        cx += w.x; cy += w.y; cz += w.z; n++;
                    }
                }
            }
            if (n > 0) z.Center = new Vector3(cx / n, cy / n, cz / n);
            float reach = 0f;
            foreach (var p in parts) foreach (var pt in p)
                reach = Mathf.Max(reach, new Vector2(pt[0] - z.Center.x, pt[1] - z.Center.z).magnitude);
            z.Reach = reach;
            return z;
        }

        static bool InsideParts(List<List<float[]>> parts, float x, float z)
        {
            foreach (var poly in parts) if (InsidePoly(poly, x, z)) return true;
            return false;
        }

        static bool InsidePoly(List<float[]> poly, float x, float z)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                float xi = poly[i][0], zi = poly[i][1], xj = poly[j][0], zj = poly[j][1];
                if ((zi > z) != (zj > z) && x < ((xj - xi) * (z - zi)) / (zj - zi) + xi) inside = !inside;
            }
            return inside;
        }

        /// Bravo's side zone is Alpha's mirrored through the centre.
        static ZonePoly Mirror(ZonePoly src)
        {
            var m = new ZonePoly { y0 = src.y0, y1 = src.y1 };
            if (src.poly != null)
            {
                m.poly = new List<float[]>();
                foreach (var p in src.poly) m.poly.Add(new[] { -p[0], -p[1] });
            }
            if (src.polys != null)
            {
                m.polys = new List<List<float[]>>();
                foreach (var poly in src.polys)
                {
                    var q = new List<float[]>();
                    foreach (var p in poly) q.Add(new[] { -p[0], -p[1] });
                    m.polys.Add(q);
                }
            }
            return m;
        }

        /// A stage without zones gets a placeholder so the mode always runs.
        ZoneLayout PlaceholderLayout()
        {
            var layout = new ZoneLayout();
            var sq = new List<float[]>
            {
                new[] { -5f, -5f }, new[] { 5f, -5f }, new[] { 5f, 5f }, new[] { -5f, 5f },
            };
            layout.center.Add(new ZonePoly { poly = sq, y0 = -2f, y1 = 6f });
            var level = GameContext.Level;
            float sx = 0f, sz = 0f;
            if (level != null && level.SpawnPads.Count > 0)
            {
                sx = level.SpawnPads[0].x * 0.4f;
                sz = level.SpawnPads[0].z * 0.45f;
            }
            var side = new List<float[]>
            {
                new[] { sx - 4f, sz - 4f }, new[] { sx + 4f, sz - 4f }, new[] { sx + 4f, sz + 4f }, new[] { sx - 4f, sz + 4f },
            };
            layout.side = new ZonePoly { poly = side, y0 = -2f, y1 = 6f };
            return layout;
        }

        // ---------------------------------------------------------------------------------- the rules
        /// The count + the penalty: how much the team still has to count off to reach 0.
        public float Total(int t) => Count[t] + Penalty[t];

        /// The team currently behind (the penalty does not count for the score), -1 when level.
        public int Losing()
        {
            if (Count[0] == Count[1]) return -1;
            return Count[0] > Count[1] ? 0 : 1;
        }

        void Schedule()
        {
            float d = RotateMin + UnityEngine.Random.value * (RotateMax - RotateMin);
            float left = (_match != null ? _match.Time : 0f) - FinalCentre;
            NextSwap = left - d >= MinStint ? d : (Active == _objectives[0] ? float.PositiveInfinity : Mathf.Max(0.5f, left + 0.5f));
        }

        /// Ink a zone (the capture) or wipe it (a new objective).
        void Flood(Zone z, int team)
        {
            bool fill = team == 0 || team == 1;
            float dur = fill ? FillT : WipeT;
            float reach = fill ? z.Reach + 0.4f : z.Reach + 3.5f;
            if (!fill) z.Hold = Clock + dur + 0.05f;
            var paint = GameContext.Paint;
            if (paint != null) paint.FloodRegion(z.Cells, fill ? team : -1, z.Center, reach, dur, !fill);
        }

        void Sample()
        {
            var paint = GameContext.Paint;
            if (paint == null) return;
            foreach (var z in Active.Zones)
            {
                int a = 0, b = 0;
                foreach (int c in z.Cells)
                {
                    byte g = paint.Grid[c];
                    if (g == 1) a++; else if (g == 2) b++;
                }
                int n = Mathf.Max(1, z.Cells.Count);
                z.Share[0] = a / (float)n; z.Share[1] = b / (float)n;
                if (Clock < z.Hold) continue;                 // still being wiped for its turn as the objective
                if (z.Owner == -1)
                {
                    int t = z.Share[0] >= Control ? 0 : z.Share[1] >= Control ? 1 : -1;
                    if (t >= 0) ZoneOwner(z, t);
                }
                else if (z.Share[1 - z.Owner] >= Contest) ZoneOwner(z, -1);
            }
            int o = Active.Zones.TrueForAll(z => z.Owner == 0) ? 0 : Active.Zones.TrueForAll(z => z.Owner == 1) ? 1 : -1;
            if (o != Owner) SetOwner(o);
        }

        void ZoneOwner(Zone z, int t)
        {
            z.Owner = t;
            if (t >= 0) { z.WarnArmed = true; z.WarnT = -99f; Flood(z, t); }
            GameContext.Emit("zones:zone", new Dictionary<string, object> { { "zone", z }, { "owner", t } });
        }

        void SetOwner(int o)
        {
            int prev = Owner;
            Owner = o;
            if (o == -1) NeutralT = 0f;
            else
            {
                int other = 1 - o;
                if (LastOwner == other) Penalise(other);
                if (LastOwner != o) { Start[o] = Total(o); TieEnd[o] = -1f; }
                LastOwner = o;
            }
            HoldT[0] = HoldT[1] = 0f;
            GameContext.Emit("zones:control", new Dictionary<string, object>
            {
                { "owner", o }, { "prev", prev }, { "objective", Active.Id },
            });
        }

        void Penalise(int t)
        {
            float start = Start[t];
            float end = TieEnd[t] >= 0f ? Mathf.Max(TieEnd[t], Total(t)) : Total(t);
            float p = Mathf.Max(0f, Mathf.Round(PenaltyK * (start - end))) + (Mathf.Approximately(start, Count0) ? 1f : 0f);
            Penalty[t] += p;
            GameContext.Emit("zones:penalty", new Dictionary<string, object>
            {
                { "team", t }, { "penalty", p }, { "total", Total(t) }, { "start", start }, { "end", end },
            });
        }

        void Tick(int t, float dt)
        {
            float rate = Active.Home < 0 ? RateCenter : Active.Home == t ? RateHome : RateAway;
            float d = rate * dt;
            float before = Count[t], opp = Count[1 - t];
            float fromPen = Mathf.Min(Penalty[t], d);
            Penalty[t] -= fromPen; d -= fromPen;
            Count[t] = Mathf.Max(0f, Count[t] - d);
            if (TieEnd[t] < 0f && before > opp && Count[t] <= opp) TieEnd[t] = opp;
        }

        /// The special gauges: the team that does not hold the objective fills faster.
        void FillSpecials(float dt)
        {
            int team = -1; float rate = 0f;
            if (Owner >= 0) { team = 1 - Owner; rate = GaugeHeld; }
            else { team = Losing(); rate = GaugeNeutral; }
            if (team < 0) return;
            var actors = _match != null ? _match.Actors : null;
            if (actors == null) return;
            foreach (var a in actors)
            {
                if (a == null || a.Team != team || !a.Alive) continue;
                bool was = a.SpecialReady();
                a.Special = Mathf.Min(a.SpecialCost(), a.Special + rate * dt);
                if (!was && a.SpecialReady())
                    GameContext.Emit("special:ready", new Dictionary<string, object> { { "actor", a } });
            }
        }

        /// The per-frame update (the match calls it while playing).
        public void Update(float dt)
        {
            if (Winner >= 0) return;
            Clock += dt;
            _sampleT -= dt;
            if (_sampleT <= 0f) { _sampleT = 1f / SampleHz; Sample(); }
            if (Owner >= 0)
            {
                Tick(Owner, dt);
                HoldT[Owner] += dt;
                if (Count[Owner] <= 0f && Penalty[Owner] <= 0f) { End(Owner, "knockout"); return; }
            }
            else NeutralT += dt;
            FillSpecials(dt);
            if (Overtime) { OvertimeUpdate(dt); return; }
            if (Final) return;
            if (_match != null && _match.Time - dt <= FinalCentre) FinalLock();
            else
            {
                NextSwap -= dt;
                if (NextSwap <= 0f) Swap();
            }
        }

        void Swap(Objective to = null, bool final = false)
        {
            var next = to ?? (Active == _objectives[0]
                ? (_objectives.Count > 1 ? (_objectives[UnityEngine.Random.value < 0.5f ? 1 : 2]) : _objectives[0])
                : _objectives[0]);
            foreach (var z in Active.Zones) z.Owner = -1;
            Active = next;
            foreach (var z in next.Zones) { z.Owner = -1; z.WarnArmed = true; Flood(z, -1); }
            if (final) NextSwap = 0f; else Schedule();
            if (Owner != -1) { Owner = -1; NeutralT = 0f; }
            _sampleT = 0f;
            GameContext.Emit("zones:active", new Dictionary<string, object>
            {
                { "objective", next.Id }, { "final", final },
            });
        }

        void FinalLock()
        {
            Final = true;
            NextSwap = 0f;
            var c = _objectives[0];
            if (Active != c) Swap(c, true);
        }

        /// Time is up: true when the match should end, false when the overtime has begun.
        public bool TimeUp()
        {
            int l = Losing();
            bool grace = Owner == -1 && LastOwner == l && NeutralT < OvertimeGrace;
            if (l >= 0 && (Owner == l || grace))
            {
                Overtime = true; OvertimeT = 0f; OvertimeLosing = l;
                GameContext.Emit("zones:overtime", new Dictionary<string, object> { { "losing", l } });
                return false;
            }
            if (l < 0)
            {
                Overtime = true; OvertimeT = 0f; OvertimeLosing = -1;
                GameContext.Emit("zones:overtime", new Dictionary<string, object> { { "losing", -1 } });
                return false;
            }
            End(1 - l, "time");
            return true;
        }

        void OvertimeUpdate(float dt)
        {
            OvertimeT += dt;
            int l = OvertimeLosing;
            if (l < 0)
            {
                l = Losing();
                if (l < 0) { if (OvertimeT >= OvertimeMax) End(Tiebreak(), "overtime-cap"); return; }
            }
            int w = 1 - l;
            if (Count[l] < Count[w]) { End(l, "comeback"); return; }
            if (Owner == w) { End(w, "retake"); return; }
            if (Owner != l && NeutralT >= OvertimeGrace) { End(w, "neutralised"); return; }
            if (OvertimeT >= OvertimeMax) End(w, "overtime-cap");
        }

        int Tiebreak()
        {
            float a = 0f, b = 0f;
            foreach (var z in Active.Zones) { a += z.Share[0]; b += z.Share[1]; }
            if (Mathf.Approximately(a, b)) return UnityEngine.Random.value < 0.5f ? 0 : 1;
            return a > b ? 0 : 1;
        }

        void End(int winner, string reason)
        {
            if (Winner >= 0) return;
            Winner = winner; Reason = reason;
            GameContext.Emit("zones:end", new Dictionary<string, object>
            {
                { "winner", winner }, { "reason", reason },
                { "counts", new[] { Count[0], Count[1] } }, { "penalty", new[] { Penalty[0], Penalty[1] } },
            });
        }
    }
}
