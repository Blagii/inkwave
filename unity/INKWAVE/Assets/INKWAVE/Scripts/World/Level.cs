// The level: the arena as oriented boxes, plus the 2D spatial hash used for broadphase queries.
// A 1:1 port of the web build's src/world/level.js — same block model, same flags, same math.
using System;
using System.Collections.Generic;
using INKWAVE.Data;
using UnityEngine;

namespace INKWAVE.World
{
    /// One oriented box of the arena.
    public class Block
    {
        public int Id;
        public Vector3 Center;
        public Vector3 Half;
        public Vector3[] Axes = { Vector3.right, Vector3.up, Vector3.forward };
        public Vector3 AabbMin;
        public Vector3 AabbMax;

        public bool Paint = true;   // inkable
        public bool Solid = true;   // collides
        public bool Grate;          // walkable for kids, but ink/shots/squids pass through
        public bool Roof;           // off-limits top
        public bool Perch;          // you can stand on it, but it is never inked
        public bool NoNav;          // bots never route along its top
        public bool Rail;           // railing/fence
        public bool Hidden;         // collision only, never drawn

        public Color Color = UnityEngine.Color.white;
        public int Pattern;
        public string Tag;
        public List<Data.MuralDef> Mural;
        public List<float[]> NoPaint;
        public float[] Bevel;

        public int[] Faces = new int[6];   // paintable face ids (0..5 = -X,+X,-Y,+Y,-Z,+Z), -1 = not paintable
        public bool Aligned = true;

        public float HalfX => Half.x;
        public float HalfY => Half.y;
        public float HalfZ => Half.z;
    }

    /// A paintable face of a block: the plane the ink lands on.
    public class Face
    {
        public int Id;
        public Vector3 Origin;
        public Vector3 U;
        public Vector3 V;
        public Vector3 N;
        public float Su, Sv;
        public int BlockId;
        public bool Wall;      // near-vertical face
        public bool Turf;      // counts towards turf scoring
        public bool Ceiling;
        public bool Paintable;
        public int Pattern;
        public Color Color = UnityEngine.Color.white;
        public bool GroundedBottom;
        public int MuralId = -1;

        // CPU gameplay grid (0.25 m cells) — filled in by the paint system
        public int Nu, Nv;
        public float Cu, Cv;
        public int Grid;

        // atlas rectangle (the GPU ink atlas)
        public int Ax, Ay, Aw, Ah;
        public float Ppm;
    }

    public class Level
    {
        public readonly List<Block> Blocks = new List<Block>();
        public readonly List<Face> Faces = new List<Face>();
        public BoundsDef Bounds;
        public List<Vector3> SpawnPads = new List<Vector3>();
        public float SpawnBarrier;
        public bool HasRails;

        // 2D spatial hash over XZ
        const float CellSize = 4f;
        int _hw, _hd;
        float _hx0, _hz0;
        List<int>[] _hash;
        uint[] _stamp;
        uint _queryStamp = 1;
        readonly List<int> _qtmp = new List<int>();

        public void Build(StageDef stage, ModeDef mode)
        {
            Bounds = stage.bounds;
            SpawnPads = new List<Vector3>();
            if (stage.spawnPads != null)
                foreach (var p in stage.spawnPads) SpawnPads.Add(new Vector3(p[0], p[1], p[2]));
            SpawnBarrier = stage.spawnBarrier;

            Blocks.Clear();
            Faces.Clear();
            foreach (var d in mode.blocks) AddBlock(d);

            HasRails = false;
            foreach (var b in Blocks) if (b.Rail) { HasRails = true; break; }

            BuildHash();
            for (int i = 0; i < Blocks.Count; i++) BuildFaces(Blocks[i]);
        }

        // ------------------------------------------------------------------------------ block construction
        void AddBlock(BlockDef d)
        {
            var b = new Block
            {
                Id = Blocks.Count,
                Paint = d.paint && !d.grate && !d.rail,
                Solid = d.solid,
                Grate = d.grate || d.rail,
                Roof = d.roof,
                Perch = d.perch,
                NoNav = d.noNav,
                Rail = d.rail,
                Hidden = d.hidden || d.rail,
                Color = ParseColor(d.color, Color.white),
                Pattern = d.pattern,
                Tag = d.tag,
                Mural = d.mural,
                NoPaint = d.noPaint,
                Bevel = d.bevel,
            };

            switch (d.kind)
            {
                case "obox":
                {
                    float a = d.rotY * Mathf.Deg2Rad, c = Mathf.Cos(a), s = Mathf.Sin(a);
                    b.Center = new Vector3(d.center[0], d.center[1], d.center[2]);
                    b.Half = new Vector3(d.size[0] * 0.5f, d.size[1] * 0.5f, d.size[2] * 0.5f);
                    b.Axes = new[] { new Vector3(c, 0, -s), Vector3.up, new Vector3(s, 0, c) };
                    b.Aligned = false;
                    break;
                }
                case "ramp":
                {
                    // A thick tilted slab: its top runs low → high, its underside reaches the floor.
                    var l0 = new Vector3(d.low[0], d.low[1], d.low[2]);
                    var h0 = new Vector3(d.high[0], d.high[1], d.high[2]);
                    Vector3 sv = h0 - l0;
                    float len = sv.magnitude;
                    sv /= len;
                    Vector3 flat = new Vector3(sv.x, 0, sv.z).normalized;
                    Vector3 side = Vector3.Cross(Vector3.up, flat).normalized;
                    Vector3 n = Vector3.Cross(sv, side).normalized;
                    if (n.y < 0) { n = -n; side = -side; }
                    float cosT = n.y;
                    float rise = h0.y - l0.y;
                    // thin: a free-spanning plank that keeps its own thickness instead of reaching the floor
                    float thick = d.thin ? d.thickness : Mathf.Max(d.thickness, rise * cosT + 0.35f);
                    const float ext = 0.6f;      // extend under the floor at the low end
                    Vector3 a = l0 + sv * (-ext);
                    Vector3 topMid = (a + h0) * 0.5f;
                    b.Center = topMid + n * (-thick * 0.5f);
                    b.Half = new Vector3(d.width * 0.5f, thick * 0.5f, (len + ext) * 0.5f);
                    b.Axes = new[] { side, n, sv };
                    b.Aligned = false;
                    // keep the basis right-handed: side × n should equal sv
                    if (Vector3.Cross(side, n).Dot(sv) < 0) b.Axes[0] = -b.Axes[0];
                    break;
                }
                default: // 'box'
                {
                    b.Center = new Vector3((d.min[0] + d.max[0]) * 0.5f, (d.min[1] + d.max[1]) * 0.5f,
                                           (d.min[2] + d.max[2]) * 0.5f);
                    b.Half = new Vector3((d.max[0] - d.min[0]) * 0.5f, (d.max[1] - d.min[1]) * 0.5f,
                                         (d.max[2] - d.min[2]) * 0.5f);
                    break;
                }
            }

            b.AabbMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            b.AabbMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < 8; i++)
            {
                Vector3 v = b.Center
                    + b.Axes[0] * ((i & 1) != 0 ? 1f : -1f) * b.Half.x
                    + b.Axes[1] * ((i & 2) != 0 ? 1f : -1f) * b.Half.y
                    + b.Axes[2] * ((i & 4) != 0 ? 1f : -1f) * b.Half.z;
                b.AabbMin = Vector3.Min(b.AabbMin, v);
                b.AabbMax = Vector3.Max(b.AabbMax, v);
            }

            Blocks.Add(b);
        }

        static Color ParseColor(string hex, Color fallback)
        {
            if (string.IsNullOrEmpty(hex)) return fallback;
            if (!hex.StartsWith("#")) return fallback;
            try
            {
                if (hex.Length == 7)
                {
                    return new Color(
                        System.Convert.ToInt32(hex.Substring(1, 2), 16) / 255f,
                        System.Convert.ToInt32(hex.Substring(3, 2), 16) / 255f,
                        System.Convert.ToInt32(hex.Substring(5, 2), 16) / 255f, 1f);
                }
            }
            catch { }
            return fallback;
        }

        // ------------------------------------------------------------------------------------ spatial hash
        void BuildHash()
        {
            _hx0 = Bounds.minX - 8f;
            _hz0 = Bounds.minZ - 8f;
            _hw = Mathf.CeilToInt((Bounds.maxX - Bounds.minX + 16f) / CellSize);
            _hd = Mathf.CeilToInt((Bounds.maxZ - Bounds.minZ + 16f) / CellSize);
            _hash = new List<int>[_hw * _hd];
            for (int i = 0; i < _hash.Length; i++) _hash[i] = new List<int>();
            _stamp = new uint[Blocks.Count];
            foreach (var b in Blocks)
            {
                int x0 = Hxi(b.AabbMin.x), x1 = Hxi(b.AabbMax.x);
                int z0 = Hzi(b.AabbMin.z), z1 = Hzi(b.AabbMax.z);
                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++)
                        _hash[z * _hw + x].Add(b.Id);
            }
        }

        int Hxi(float x) => Mathf.Clamp(Mathf.FloorToInt((x - _hx0) / CellSize), 0, _hw - 1);
        int Hzi(float z) => Mathf.Clamp(Mathf.FloorToInt((z - _hz0) / CellSize), 0, _hd - 1);

        /// Unique block ids whose hash cells overlap the XZ rectangle.
        public void QueryBlocks(float minX, float minZ, float maxX, float maxZ, List<int> output)
        {
            output.Clear();
            uint st = ++_queryStamp;
            int x0 = Hxi(minX), x1 = Hxi(maxX), z0 = Hzi(minZ), z1 = Hzi(maxZ);
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                {
                    var cell = _hash[z * _hw + x];
                    for (int i = 0; i < cell.Count; i++)
                    {
                        int id = cell[i];
                        if (_stamp[id] != st) { _stamp[id] = st; output.Add(id); }
                    }
                }
        }

        // ------------------------------------------------------------------------------------- faces
        void BuildFaces(Block b)
        {
            if (b.Hidden) return;
            var ax = b.Axes;
            var h = new[] { b.Half.x, b.Half.y, b.Half.z };
            for (int k = 0; k < 3; k++)
            {
                for (int si = 0; si < 2; si++)
                {
                    int sign = si == 0 ? 1 : -1;
                    Vector3 n = ax[k] * sign;
                    if (n.y < -0.5f)
                    {
                        // underside: only keep it if it floats well above the floor (overhangs)
                        float lowest = b.Center.y - h[1];
                        if (lowest < 0.5f) continue;
                    }
                    int o0 = k == 0 ? 1 : 0, o1 = k == 2 ? 1 : 2;
                    int ui, vi;
                    if (Mathf.Abs(n.y) < 0.5f)
                    {
                        // wall: v = the in-plane axis most aligned with world up
                        vi = Mathf.Abs(ax[o0].y) > Mathf.Abs(ax[o1].y) ? o0 : o1;
                        ui = o0 == vi ? o1 : o0;
                    }
                    else
                    {
                        ui = Mathf.Abs(ax[o0].x) >= Mathf.Abs(ax[o1].x) ? o0 : o1;
                        vi = o0 == ui ? o1 : o0;
                    }
                    Vector3 v = ax[vi];
                    if (Mathf.Abs(n.y) < 0.5f) { if (v.y < 0) v = -v; }
                    else if (v.z < 0 && Mathf.Abs(v.z) > 0.3f) v = -v;
                    Vector3 u = Vector3.Cross(v, n);   // u × v = n
                    float su = 2f * h[ui], sv = 2f * h[vi];
                    Vector3 origin = b.Center + n * h[k] + u * (-su * 0.5f) + v * (-sv * 0.5f);

                    var face = new Face
                    {
                        Id = Faces.Count,
                        BlockId = b.Id,
                        N = n, U = u, V = v, Origin = origin, Su = su, Sv = sv,
                        Wall = Mathf.Abs(n.y) < 0.3f,
                        Turf = n.y > 0.7f,
                        Ceiling = n.y < -0.5f,
                        Paintable = b.Paint && n.y > -0.5f,
                        Pattern = b.Pattern,
                        Color = b.Color,
                    };
                    if (FaceHidden(face)) continue;
                    if (b.Mural != null)
                        foreach (var m in b.Mural)
                            if (m.n != null && n.x * m.n[0] + n.y * m.n[1] + n.z * m.n[2] > 0.9f) face.MuralId = m.id;
                    if (b.NoPaint != null)
                        foreach (var m in b.NoPaint)
                            if (m != null && m.Length >= 3 && n.x * m[0] + n.y * m[1] + n.z * m[2] > 0.9f) face.Paintable = false;
                    if ((b.Roof || b.Perch) && n.y > 0.5f) face.Paintable = false;
                    if (face.Wall)
                    {
                        Vector3 v2 = face.Origin + face.U * (su * 0.5f) + face.V * (-0.06f) + face.N * 0.06f;
                        face.GroundedBottom = PointInside(v2, 0f, b.Id) || v2.y < 0.02f;
                    }
                    b.Faces[k * 2 + (sign > 0 ? 0 : 1)] = face.Id;
                    Faces.Add(face);
                }
            }
        }

        /// A face is dropped when it is completely buried inside other geometry.
        bool FaceHidden(Face f)
        {
            int nu = Mathf.Max(2, Mathf.CeilToInt(f.Su / 1.25f));
            int nv = Mathf.Max(2, Mathf.CeilToInt(f.Sv / 1.25f));
            for (int j = 0; j <= nv; j++)
            {
                for (int i = 0; i <= nu; i++)
                {
                    float uu = Mathf.Clamp((i / (float)nu) * f.Su, 0.05f, f.Su - 0.05f);
                    float vv = Mathf.Clamp((j / (float)nv) * f.Sv, 0.05f, f.Sv - 0.05f);
                    Vector3 v = f.Origin + f.U * uu + f.V * vv + f.N * 0.03f;
                    if (!PointInside(v, 0f, f.BlockId)) return false;
                }
            }
            return true;
        }

        /// Is the point inside any solid block (except `exclude`)?
        public bool PointInside(Vector3 p, float pad = 0f, int exclude = -1)
        {
            QueryBlocks(p.x - 0.01f, p.z - 0.01f, p.x + 0.01f, p.z + 0.01f, _qtmp);
            for (int i = 0; i < _qtmp.Count; i++)
            {
                int id = _qtmp[i];
                if (id == exclude) continue;
                var b = Blocks[id];
                if (!b.Solid) continue;
                if (PointInBlock(b, p, pad)) return true;
            }
            return false;
        }

        /// The highest walkable surface under (x, z) below yMax. Returns the height, or -Infinity when there is none.
        /// skipGrates: what a squid would land on (squids drop straight through grates).
        public float GroundHeight(float x, float z, float yMax = 50f, bool skipGrates = false)
        {
            float best = float.NegativeInfinity;
            QueryBlocks(x - 0.01f, z - 0.01f, x + 0.01f, z + 0.01f, _qtmp);
            for (int i = 0; i < _qtmp.Count; i++)
            {
                var b = Blocks[_qtmp[i]];
                if (!b.Solid || (skipGrates && b.Grate)) continue;
                // intersect the vertical line with the block's top face plane
                Vector3 n = b.Axes[1];
                if (n.y < 0.5f) continue;
                Vector3 top = b.Center + n * b.Half.y;
                // plane: n·(p - top) = 0 → y = top.y - (n.x (x - top.x) + n.z (z - top.z)) / n.y
                float y = top.y - (n.x * (x - top.x) + n.z * (z - top.z)) / n.y;
                var probe = new Vector3(x, y - 0.01f, z);
                if (y <= yMax && y > best && PointInBlock(b, probe, 0.001f)) best = y;
            }
            return best;
        }

        public bool PointInBlock(Block b, Vector3 p, float pad = 0f)
        {
            Vector3 o = p - b.Center;
            for (int k = 0; k < 3; k++)
            {
                float h = k == 0 ? b.Half.x : k == 1 ? b.Half.y : b.Half.z;
                if (Mathf.Abs(Vector3.Dot(o, b.Axes[k])) > h + pad) return false;
            }
            return true;
        }
    }
}
