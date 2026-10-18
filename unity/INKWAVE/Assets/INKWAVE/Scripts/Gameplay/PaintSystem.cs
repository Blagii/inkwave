// The ink paint system.
//
//  * CPU: a coarse grid (0.25 m cells) per paintable face answers the gameplay questions — "is this spot my ink?"
//    — and tracks turf coverage for scoring. Both sides evaluate the same body edge (blobWobble), so they agree.
//  * GPU: every paintable face owns a rectangle in one big atlas render target. Splats are drawn as quads straight
//    into the atlas in face space (texture-space painting), so ink wraps across floors/walls/corners exactly like a
//    spherical splash would.
//
// A 1:1 port of the web build's src/world/paint.js (the gameplay grid and the atlas layout are identical; the splat
// shader is ported to URP in Assets/INKWAVE/Shaders/InkSplat.shader).
using System;
using System.Collections.Generic;
using INKWAVE.Core;
using INKWAVE.World;
using UnityEngine;

namespace INKWAVE.Gameplay
{
    public class PaintSystem
    {
        /// Main-blob outline: organic lobes + two narrow "fingers" thrown out by the impact. The GPU splat shader
        /// evaluates the identical function, so the gameplay grid and the rendered ink agree on the edge.
        public static float BlobWobble(float ang, float seed)
        {
            return 1f
                + 0.12f * Mathf.Sin(3f * ang + seed * 6.2831f)
                + 0.08f * Mathf.Sin(5f * ang + seed * 17f)
                + 0.05f * Mathf.Sin(7f * ang + seed * 41f)
                + 0.03f * Mathf.Sin(11f * ang + seed * 73f)
                + 0.018f * Mathf.Sin(17f * ang + seed * 29f)
                + 0.17f * Mathf.Pow(Mathf.Max(Mathf.Cos(ang - seed * 37.7f), 0f), 28f)
                + 0.12f * Mathf.Pow(Mathf.Max(Mathf.Cos(ang - seed * 53.3f - 2.1f), 0f), 36f);
        }

        public const float WobMax = 1.5f;   // upper bound of blobWobble (the reach of the CPU cell loop)
        // roller band segment (face space): half length / half width / corner rounding, × radius
        const float BandL = 0.55f, BandW = 0.62f, BandR = 0.1f;

        public enum Kind { Shot, Line, Blast, Bomb, Trail, Drop, Roll, Speck }

        // quad half-extent in footprint radii (satellites / spatter reach) and the extra reach below wall splats
        static readonly float[] Reach = { 2.45f, 2.1f, 2.7f, 2.75f, 2.3f, 1.9f, 1.25f, 1.35f };
        const float DripReach = 3.9f;

        readonly Level _level;
        readonly Physics _physics;
        public float Cell = 0.25f;
        public int AtlasSize = 4096;
        public float Ppm;

        public byte[] Grid;              // 0 none, 1 team0, 2 team1
        byte[] _dead;                   // cells buried inside other geometry
        int _turfTotal;
        public int[] Counts = new int[2];  // live turf cells per team
        public int Version;              // bumps whenever the CPU grid changes

        readonly List<int> _ids = new List<int>();
        readonly List<Face> _paintFaces = new List<Face>();

        // splats still spreading on screen (the gameplay grid is already updated)
        class Growing
        {
            public Face Face; public float Lu, Lv, Dn, Sdu, Sdv, Sa;
            public float R, Seed, Age, Dur, Cx, Cy, Cz;
            public int Team, Kind;
        }
        readonly List<Growing> _growing = new List<Growing>();
        public int Quads;

        public PaintSystem(Level level, Physics physics, int atlasSize = 4096)
        {
            _level = level;
            _physics = physics;
            AtlasSize = atlasSize;
            Layout();
            InitGrid();
        }

        // ------------------------------------------------------------ atlas layout (shelf packing)
        void Layout()
        {
            _paintFaces.Clear();
            foreach (var f in _level.Faces) if (f.Paintable) _paintFaces.Add(f);
            float ppm = 30f;
            for (int attempt = 0; attempt < 30; attempt++)
            {
                if (TryPack(ppm)) break;
                ppm *= 0.92f;
            }
            Ppm = ppm;
        }

        bool TryPack(float ppm)
        {
            const int pad = 8;   // ≥ 2^maxInkLod texels, so mip levels never bleed between faces
            int s = AtlasSize;
            var rects = new List<(Face f, int w, int h)>();
            foreach (var f in _paintFaces)
                rects.Add((f, Mathf.CeilToInt(f.Su * ppm) + pad * 2, Mathf.CeilToInt(f.Sv * ppm) + pad * 2));
            rects.Sort((a, b) => b.h.CompareTo(a.h));
            int x = 0, y = 0, rowH = 0;
            foreach (var r in rects)
            {
                if (r.w > s) return false;
                if (x + r.w > s) { x = 0; y += rowH; rowH = 0; }
                if (y + r.h > s) return false;
                r.f.Ax = x; r.f.Ay = y; r.f.Aw = r.w; r.f.Ah = r.h; r.f.Ppm = ppm;
                x += r.w; rowH = Mathf.Max(rowH, r.h);
            }
            UsedHeight = y + rowH;
            return true;
        }

        public int UsedHeight { get; private set; }

        // ------------------------------------------------------------ CPU grid
        void InitGrid()
        {
            int total = 0;
            foreach (var f in _paintFaces)
            {
                f.Nu = Mathf.Max(1, Mathf.RoundToInt(f.Su / Cell));
                f.Nv = Mathf.Max(1, Mathf.RoundToInt(f.Sv / Cell));
                f.Cu = f.Su / f.Nu; f.Cv = f.Sv / f.Nv;
                f.Grid = total;
                total += f.Nu * f.Nv;
            }
            Grid = new byte[total];
            _dead = new byte[total];
            _turfTotal = 0;
            Counts[0] = Counts[1] = 0;
            foreach (var f in _paintFaces)
            {
                for (int j = 0; j < f.Nv; j++)
                {
                    for (int i = 0; i < f.Nu; i++)
                    {
                        Vector3 p = f.Origin + f.U * ((i + 0.5f) * f.Cu) + f.V * ((j + 0.5f) * f.Cv) + f.N * 0.06f;
                        int k = f.Grid + j * f.Nu + i;
                        if (_level.PointInside(p, 0f, f.BlockId)) _dead[k] = 1;
                        else if (f.Turf) _turfTotal++;
                    }
                }
            }
        }

        public int TurfTotal => _turfTotal;

        // --------------------------------------------------------------------------------- region floods
        // Zone Control: taking a zone floods it with the taker's ink (a front running out from the zone's centre),
        // and a newly operational zone is wiped back to bare floor. The gameplay grid flips as the front passes.
        public class Flood
        {
            public int[] Cells;
            public float[] Dist;
            public int Team;        // -1 = wipe
            public float T, Dur, Reach;
            public int Index;
            public bool Linear;
        }
        readonly List<Flood> _floods = new List<Flood>();

        /// Ink (or wipe) a whole region. `cells` are the paint-grid indices of the region.
        public void FloodRegion(List<int> cells, int team, Vector3 center, float reach, float dur, bool linear)
        {
            if (cells == null || cells.Count == 0) return;
            var f = new Flood
            {
                Team = team,
                Dur = Mathf.Max(0f, dur),
                Reach = Mathf.Max(0.1f, reach),
                Linear = linear,
            };
            var live = new List<int>(cells.Count);
            var dist = new List<float>(cells.Count);
            for (int i = 0; i < cells.Count; i++)
            {
                int k = cells[i];
                if (k < 0 || k >= Grid.Length) continue;
                if (_dead[k] != 0) continue;                       // the buried cells do not count
                Vector3 p;
                if (!CellWorld(k, out p)) continue;
                live.Add(k);
                dist.Add(new Vector2(p.x - center.x, p.z - center.z).magnitude);
            }
            f.Cells = live.ToArray();
            f.Dist = dist.ToArray();
            // the front starts at the centre: the cells in the order they are reached
            var order = new int[f.Cells.Length];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, (a, b) => f.Dist[a].CompareTo(f.Dist[b]));
            var cellsOut = new int[order.Length];
            var distOut = new float[order.Length];
            for (int i = 0; i < order.Length; i++) { cellsOut[i] = f.Cells[order[i]]; distOut[i] = f.Dist[order[i]]; }
            f.Cells = cellsOut; f.Dist = distOut;
            _floods.Add(f);
        }

        /// The index (in the paintable list) of the face a grid cell belongs to (-1 when there is none).
        int CellFaceIndex(int k)
        {
            int lo = 0, hi = _paintFaces.Count - 1, face = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (_paintFaces[mid].Grid <= k) { face = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            if (face < 0) return -1;
            var f = _paintFaces[face];
            int l = k - f.Grid;
            return (l < 0 || l >= f.Nu * f.Nv) ? -1 : face;
        }

        /// The face a paint-grid cell belongs to (-1 when there is none).
        public int CellFaceId(int k)
        {
            int face = CellFaceIndex(k);
            return face < 0 ? -1 : _paintFaces[face].Id;
        }

        /// The world position of a paint-grid cell.
        public bool CellWorld(int k, out Vector3 p)
        {
            p = Vector3.zero;
            int face = CellFaceIndex(k);
            if (face < 0) return false;
            var f = _paintFaces[face];
            int l = k - f.Grid;
            int i = l % f.Nu, j = l / f.Nu;
            p = f.Origin + f.U * ((i + 0.5f) * f.Cu) + f.V * ((j + 0.5f) * f.Cv);
            return true;
        }

        /// Advance the running floods (the grid cells the front passed get the team's ink).
        public void StepFloods(float dt)
        {
            for (int k = _floods.Count - 1; k >= 0; k--)
            {
                var J = _floods[k];
                J.T += dt;
                bool done = J.T >= J.Dur;
                float x = done ? 1f : J.T / Mathf.Max(0.0001f, J.Dur);
                float rad = done ? 1e9f : J.Reach * (J.Linear ? x : 1f - (1f - x) * (1f - x));
                byte val = (byte)(J.Team + 1);
                bool changed = false;
                while (J.Index < J.Cells.Length && J.Dist[J.Index] <= rad)
                {
                    int c = J.Cells[J.Index++];
                    byte was = Grid[c];
                    if (was == val) continue;
                    Grid[c] = val;
                    if (was > 0) Counts[was - 1]--;
                    if (val > 0) Counts[val - 1]++;
                    changed = true;
                }
                if (changed) Version++;
                if (done) _floods.RemoveAt(k);
            }
        }

        public int FloodCount => _floods.Count;

        // ------------------------------------------------------------ splats
        /// Lay ink down. Returns the claimed area in m² (the gameplay edge).
        public float Splat(Vector3 center, float radius, int team, float seed = -1f, Kind kind = Kind.Shot,
                           Vector3 stretch = default, float stretchAmt = 0f, bool cosmetic = false)
        {
            if (seed < 0f) seed = UnityEngine.Random.value;
            float sAmt = stretch.sqrMagnitude > 0f ? stretchAmt : 0f;
            if (kind == Kind.Roll) sAmt = 0f;
            float reachK = Reach[(int)kind];
            float reach = radius * Mathf.Max(3.2f, reachK + 1.4f * sAmt + 0.3f);
            _level.QueryBlocks(center.x - reach, center.z - reach, center.x + reach, center.z + reach, _ids);

            float claimed = 0f;
            bool wall = false;
            for (int i = 0; i < _ids.Count; i++)
            {
                var b = _level.Blocks[_ids[i]];
                // quick reject by AABB distance
                if (center.x < b.AabbMin.x - reach || center.x > b.AabbMax.x + reach ||
                    center.y < b.AabbMin.y - reach || center.y > b.AabbMax.y + reach ||
                    center.z < b.AabbMin.z - reach || center.z > b.AabbMax.z + reach) continue;

                for (int fi = 0; fi < 6; fi++)
                {
                    int fid = b.Faces[fi];
                    if (fid < 0) continue;
                    var f = _level.Faces[fid];
                    if (!f.Paintable) continue;
                    Vector3 rel = center - f.Origin;
                    float dn = Vector3.Dot(rel, f.N);
                    if (dn > radius || dn < -0.12f) continue;
                    float lu = Vector3.Dot(rel, f.U), lv = Vector3.Dot(rel, f.V);
                    float rr = Mathf.Sqrt(Mathf.Max(0f, radius * radius - dn * dn));
                    float ext = rr * (reachK + 1.4f * sAmt);
                    if (lu < -ext || lu > f.Su + ext || lv < -ext - (f.Wall ? rr * DripReach : 0f) || lv > f.Sv + ext) continue;

                    // stretch / band direction projected into face space
                    float sdu = 0f, sdv = 0f, sa = 0f;
                    if (stretch.sqrMagnitude > 0f)
                    {
                        sdu = Vector3.Dot(stretch, f.U); sdv = Vector3.Dot(stretch, f.V);
                        float l = Mathf.Sqrt(sdu * sdu + sdv * sdv);
                        if (l > 0.2f) { sdu /= l; sdv /= l; sa = sAmt * l; }
                        else if (kind == Kind.Roll) { sdu = 1f; sdv = 0f; }
                        else { sdu = 0f; sdv = 0f; }
                    }
                    if (!cosmetic) claimed += CpuSplat(f, lu, lv, rr, team, seed, sdu, sdv, sa, kind);
                    if (f.Wall && rr > radius * 0.3f) wall = true;
                }
            }
            if (wall && kind != Kind.Speck && kind != Kind.Roll)
            {
                // the wall drips run on the GPU atlas; the gameplay grid already holds the body
            }
            return claimed;
        }

        float CpuSplat(Face f, float lu, float lv, float r, int team, float seed, float sdu, float sdv, float sa, Kind kind)
        {
            if (r <= 0.02f) return 0f;
            byte val = (byte)(team + 1);
            bool roll = kind == Kind.Roll;
            float ext = roll ? r * (Mathf.Sqrt(BandL * BandL + BandW * BandW) + BandR + 0.05f) : r * (1f + sa) * WobMax;
            int i0 = Mathf.Max(0, Mathf.FloorToInt((lu - ext) / f.Cu));
            int i1 = Mathf.Min(f.Nu - 1, Mathf.FloorToInt((lu + ext) / f.Cu));
            int j0 = Mathf.Max(0, Mathf.FloorToInt((lv - ext) / f.Cv));
            int j1 = Mathf.Min(f.Nv - 1, Mathf.FloorToInt((lv + ext) / f.Cv));
            if (i1 < i0 || j1 < j0) return 0f;

            float claimed = 0f;
            float cellA = f.Cu * f.Cv;
            for (int j = j0; j <= j1; j++)
            {
                for (int i = i0; i <= i1; i++)
                {
                    float px = (i + 0.5f) * f.Cu - lu, py = (j + 0.5f) * f.Cv - lv;
                    if (roll)
                    {
                        float qa = Mathf.Abs(px * sdu + py * sdv) - r * BandL;
                        float qb = Mathf.Abs(-px * sdv + py * sdu) - r * BandW;
                        float sd = Mathf.Sqrt(Mathf.Max(qa, 0f) * Mathf.Max(qa, 0f) + Mathf.Max(qb, 0f) * Mathf.Max(qb, 0f))
                                   + Mathf.Min(Mathf.Max(qa, qb), 0f) - r * BandR;
                        if (sd > -0.03f * r) continue;
                    }
                    else
                    {
                        if (sa > 0f)
                        {
                            float a = px * sdu + py * sdv;
                            float qx = px - a * sdu, qy = py - a * sdv;
                            float s = a > 0f ? 1f + sa : 1f + 0.25f * sa;
                            px = qx + sdu * (a / s); py = qy + sdv * (a / s);
                        }
                        float d = Mathf.Sqrt(px * px + py * py);
                        if (d > r * WobMax) continue;
                        if (d / (r * BlobWobble(Mathf.Atan2(py, px), seed)) > 0.97f) continue;
                    }
                    int k = f.Grid + j * f.Nu + i;
                    byte prev = Grid[k];
                    if (prev == val) continue;
                    Grid[k] = val;
                    claimed += cellA;
                    if (f.Turf && _dead[k] == 0)
                    {
                        if (prev != 0) Counts[prev - 1]--;
                        Counts[team]++;
                    }
                }
            }
            if (claimed > 0f) Version++;
            return claimed;
        }

        // ------------------------------------------------------------ queries
        /// The team that owns the ink at a face coordinate: 0 none, 1 team0, 2 team1.
        public byte Sample(int faceId, float u, float v)
        {
            if (faceId < 0 || faceId >= _level.Faces.Count) return 0;
            var f = _level.Faces[faceId];
            if (!f.Paintable) return 0;
            int i = Mathf.Clamp(Mathf.FloorToInt(u / f.Cu), 0, f.Nu - 1);
            int j = Mathf.Clamp(Mathf.FloorToInt(v / f.Cv), 0, f.Nv - 1);
            return Grid[f.Grid + j * f.Nu + i];
        }

        /// The team that owns the ink at a world point lying on a face.
        public byte SampleWorld(int faceId, Vector3 p)
        {
            if (faceId < 0) return 0;
            var f = _level.Faces[faceId];
            Vector3 rel = p - f.Origin;
            return Sample(faceId, Vector3.Dot(rel, f.U), Vector3.Dot(rel, f.V));
        }

        /// Turf coverage fractions [team0, team1] of all live turf cells.
        public float[] Coverage()
        {
            float t = Mathf.Max(1, _turfTotal);
            return new[] { Counts[0] / t, Counts[1] / t };
        }

        /// Turf cells per team (the raw counters the scoreboard shows).
        public int TurfCount(int team) => Counts[team];
    }
}
