// Collision queries against the level's oriented boxes: raycasts, capsule resolution, the character-controller
// queries (flat-footprint ground probe, lifted body capsule, camera probe).
//
// A 1:1 port of the web build's src/game/physics.js. The Unity build deliberately does NOT use PhysX: the feel of
// the game (stepping onto curbs, sliding along walls, ground snapping) comes from these exact numbers.
using System.Collections.Generic;
using UnityEngine;

namespace INKWAVE.World
{
    public class Hit
    {
        public bool Hit_;
        public float Dist;
        public Vector3 Point;
        public Vector3 Normal;
        public int Block = -1;
        public int Face = -1;
        public float U, V;

        public void Reset(float maxDist)
        {
            Hit_ = false; Dist = maxDist; Block = -1; Face = -1;
        }
    }

    /// Result of the ground probe (the support under a character's feet).
    public class GroundHit
    {
        public bool Hit_;
        public float Y;
        public Vector3 Normal = Vector3.up;
        public int Block = -1;
        public int Face = -1;
        public float U, V;
        public bool Center;
        public bool Grate;
    }

    public class Contacts
    {
        public bool Ground, Wall, Ceiling;
        public Vector3 GroundNormal = Vector3.up;
        public Vector3 WallNormal = Vector3.zero;
        public int GroundBlock = -1;
        public int WallBlock = -1;

        public void Clear()
        {
            Ground = false; Wall = false; Ceiling = false;
            GroundNormal = Vector3.up; WallNormal = Vector3.zero;
            GroundBlock = -1; WallBlock = -1;
        }
    }

    public class CameraProbe
    {
        public float Hard, Soft;
        public bool Floor;
    }

    public class Physics
    {
        /// Minimum ground normal.y a character can stand on (≈47°) — the web build's WALKABLE.
        public const float Walkable = 0.68f;

        // Footprint ring for the ground probe: 8 samples, so a ledge edge is caught at any heading.
        static readonly Vector2[] Ring = BuildRing(8);

        // Camera probe rays: [side, up, weight] in units of the probe radius (the centre is the hard line of sight).
        static readonly Vector3[] CamRays = BuildCamRays();

        readonly Level _level;
        readonly List<int> _ids = new List<int>();
        readonly Hit _hit = new Hit();
        readonly Hit _hit2 = new Hit();
        readonly Hit _losHit = new Hit();
        readonly Hit _camHit = new Hit();

        public Physics(Level level) { _level = level; }

        static Vector2[] BuildRing(int n)
        {
            var r = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                float a = (i / (float)n) * Mathf.PI * 2f;
                r[i] = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            }
            return r;
        }

        static Vector3[] BuildCamRays()
        {
            var rays = new System.Collections.Generic.List<Vector3>();
            rays.Add(new Vector3(0, 0, 1));
            for (int i = 0; i < 6; i++)
            {
                float a = (i / 6f) * Mathf.PI * 2f;
                rays.Add(new Vector3(Mathf.Cos(a) * 0.5f, Mathf.Sin(a) * 0.5f, 0.75f));
            }
            for (int i = 0; i < 8; i++)
            {
                float a = (i / 8f) * Mathf.PI * 2f + 0.39f;
                rays.Add(new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0.4f));
            }
            return rays.ToArray();
        }

        // ----------------------------------------------------------------------------------------- raycast
        /// Ray against all blocks. `dir` must be normalized. Blocks containing the origin are ignored.
        public Hit Raycast(Vector3 origin, Vector3 dir, float maxDist, Hit outHit = null, bool skipGrates = false)
        {
            Hit h = outHit ?? _hit;
            h.Reset(maxDist);
            float ex = origin.x + dir.x * maxDist, ez = origin.z + dir.z * maxDist;
            _level.QueryBlocks(Mathf.Min(origin.x, ex), Mathf.Min(origin.z, ez),
                              Mathf.Max(origin.x, ex), Mathf.Max(origin.z, ez), _ids);
            float best = maxDist;
            int bestK = -1, bestSign = 0, bestB = -1;

            for (int i = 0; i < _ids.Count; i++)
            {
                var b = _level.Blocks[_ids[i]];
                if (!b.Solid || (skipGrates && b.Grate)) continue;

                Vector3 o = origin - b.Center;
                float tmin = float.NegativeInfinity, tmax = float.PositiveInfinity;
                int kmin = -1, smin = 0;
                bool miss = false;
                for (int k = 0; k < 3; k++)
                {
                    Vector3 ax = b.Axes[k];
                    float oo = Vector3.Dot(o, ax), d = Vector3.Dot(dir, ax);
                    float hh = k == 0 ? b.Half.x : k == 1 ? b.Half.y : b.Half.z;
                    if (Mathf.Abs(d) < 1e-9f) { if (oo < -hh || oo > hh) { miss = true; break; } continue; }
                    float t1 = (-hh - oo) / d, t2 = (hh - oo) / d;
                    int s1 = -1;
                    if (t1 > t2) { float t = t1; t1 = t2; t2 = t; s1 = 1; }
                    if (t1 > tmin) { tmin = t1; kmin = k; smin = s1; }
                    if (t2 < tmax) tmax = t2;
                    if (tmin > tmax) { miss = true; break; }
                }
                if (miss || tmax < 0 || tmin < 0 || tmin > best) continue;
                best = tmin; bestK = kmin; bestSign = smin; bestB = b.Id;
            }

            if (bestB < 0) return h;

            var block = _level.Blocks[bestB];
            h.Hit_ = true; h.Dist = best; h.Block = bestB;
            h.Point = origin + dir * best;
            h.Normal = block.Axes[bestK] * bestSign;
            h.Face = block.Faces[bestK * 2 + (bestSign > 0 ? 0 : 1)];
            if (h.Face >= 0)
            {
                var f = _level.Faces[h.Face];
                Vector3 p = h.Point - f.Origin;
                h.U = Vector3.Dot(p, f.U);
                h.V = Vector3.Dot(p, f.V);
            }
            return h;
        }

        public Hit Segment(Vector3 a, Vector3 b, Hit outHit = null, bool skipGrates = false)
        {
            Vector3 d = b - a;
            float len = d.magnitude;
            if (len < 1e-6f) { if (outHit != null) outHit.Hit_ = false; return outHit ?? _hit; }
            return Raycast(a, d / len, len, outHit, skipGrates);
        }

        /// Line of sight between two points (true = clear).
        public bool Los(Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float len = ab.magnitude;
            if (len < 1e-4f) return true;
            return !Raycast(a, ab / len, len - 0.05f, _losHit, true).Hit_;
        }

        public Vector3 ClosestOnBlock(Block b, Vector3 p)
        {
            Vector3 o = p - b.Center;
            Vector3 res = b.Center;
            for (int k = 0; k < 3; k++)
            {
                Vector3 ax = b.Axes[k];
                float h = k == 0 ? b.Half.x : k == 1 ? b.Half.y : b.Half.z;
                float d = Mathf.Clamp(Vector3.Dot(o, ax), -h, h);
                res += ax * d;
            }
            return res;
        }

        // ---------------------------------------------------------------------------------- capsule body
        /// Resolve a vertical capsule (feet at pos) against the level. Mutates pos.
        /// squid = true: grates don't collide (squids slip through the mesh).
        public Contacts CollideCapsule(ref Vector3 pos, float radius, float height, Contacts c, int iterations = 3, bool squid = false)
        {
            c.Clear();
            var blocks = _level.Blocks;
            float top = Mathf.Max(radius, height - radius);
            for (int it = 0; it < iterations; it++)
            {
                _level.QueryBlocks(pos.x - radius - 0.2f, pos.z - radius - 0.2f,
                                  pos.x + radius + 0.2f, pos.z + radius + 0.2f, _ids);
                bool moved = false;
                for (int i = 0; i < _ids.Count; i++)
                {
                    var b = blocks[_ids[i]];
                    if (!b.Solid || (squid && b.Grate)) continue;
                    if (pos.y + height < b.AabbMin.y - 0.05f || pos.y > b.AabbMax.y + 0.05f) continue;

                    Vector3 a = new Vector3(pos.x, pos.y + radius, pos.z);
                    Vector3 bTop = new Vector3(pos.x, pos.y + top, pos.z);
                    Vector3 ab = bTop - a;
                    float abLen2 = Mathf.Max(1e-6f, ab.sqrMagnitude);
                    float t = 0.5f;
                    Vector3 q = Vector3.zero;
                    for (int k = 0; k < 3; k++)
                    {
                        Vector3 s = a + ab * t;
                        q = ClosestOnBlock(b, s);
                        t = Mathf.Clamp01((Vector3.Dot(q - a, ab)) / abLen2);
                    }
                    Vector3 s = a + ab * t;
                    q = ClosestOnBlock(b, s);
                    Vector3 n = s - q;
                    float dist = n.magnitude, pen;
                    if (dist > 1e-5f)
                    {
                        if (dist >= radius) continue;
                        n /= dist;
                        pen = radius - dist;
                    }
                    else
                    {
                        // the segment point is inside the box: push out along the axis of least penetration
                        Vector3 o = s - b.Center;
                        float bestPen = float.PositiveInfinity;
                        for (int k = 0; k < 3; k++)
                        {
                            Vector3 ax = b.Axes[k];
                            float h = k == 0 ? b.Half.x : k == 1 ? b.Half.y : b.Half.z;
                            float d = Vector3.Dot(o, ax);
                            float pk = h - Mathf.Abs(d);
                            if (pk < bestPen) { bestPen = pk; n = ax * (d >= 0 ? 1f : -1f); }
                        }
                        pen = bestPen + radius;
                    }
                    // prefer resolving as ground when standing on top edges (avoid being shoved sideways off ledges)
                    pos += n * (pen + 1e-4f);
                    moved = true;
                    if (n.y > 0.6f) { c.Ground = true; c.GroundNormal = n; c.GroundBlock = b.Id; }
                    else if (n.y < -0.6f) c.Ceiling = true;
                    else if (Mathf.Abs(n.y) < 0.55f) { c.Wall = true; c.WallNormal = n; c.WallBlock = b.Id; }
                }
                if (!moved) break;
            }
            return c;
        }

        /// Flat-footprint ground probe — the character's feet. Vertical rays at the centre and on a ring of radius
        /// `foot` are cast from `up` above the feet to `down` below them. The centre surface wins when it is walkable
        /// and in range (so slopes are exact: no hovering, no bounce); ring samples take over when the centre is over
        /// a gap (the feet stay planted on a ledge until the whole footprint is off) or when one of them sits on a
        /// clear step (> stepMin above the centre: curbs are stepped onto as soon as the foot reaches them).
        public GroundHit GroundProbe(float x, float y, float z, float up, float down, float foot,
                                     GroundHit outGround = null, bool skipGrates = false, float stepMin = 0.12f)
        {
            GroundHit g = outGround ?? new GroundHit();
            g.Hit_ = false; g.Center = false;
            float len = up + down;
            bool have = false;
            float cy = float.NegativeInfinity;

            // centre
            var go = new Vector3(x, y + up, z);
            var h = Raycast(go, Vector3.down, len, _hit2, skipGrates);
            if (h.Hit_ && h.Normal.y >= Walkable)
            {
                have = true; cy = h.Point.y;
                FillGround(g, h, true);
            }

            // ring: the best clear step above the centre, or (centre missing) the highest walkable sample
            float by = have ? cy + stepMin : float.NegativeInfinity;
            int bi = -1;
            for (int i = 0; i < Ring.Length; i++)
            {
                var ring = new Vector3(x + Ring[i].x * foot, y + up, z + Ring[i].y * foot);
                var hr = Raycast(ring, Vector3.down, len, _hit2, skipGrates);
                if (!hr.Hit_ || hr.Normal.y < Walkable) continue;
                if (hr.Point.y > by) { by = hr.Point.y; bi = i; FillGround(g, hr, false); }
            }

            g.Hit_ = have || bi >= 0;
            return g;
        }

        void FillGround(GroundHit g, Hit h, bool center)
        {
            g.Y = h.Point.y; g.Normal = h.Normal; g.Block = h.Block; g.Face = h.Face; g.U = h.U; g.V = h.V;
            g.Center = center;
            g.Grate = h.Block >= 0 && _level.Blocks[h.Block].Grate;
        }

        /// Resolve the character BODY: a vertical capsule whose bottom is lifted `lift` above the feet (everything
        /// below the lift is the feet's job — step-ups, curbs). `horizontal` (grounded) turns push-outs into
        /// horizontal pushes so walls never shove a grounded character up or down.
        public Contacts CollideBody(ref Vector3 pos, float radius, float lift, float height, Contacts c,
                                    bool horizontal = false, bool skipGrates = false, int iterations = 3)
        {
            c.Clear();
            var blocks = _level.Blocks;
            float bot = lift + radius, top = Mathf.Max(bot, height - radius);
            for (int it = 0; it < iterations; it++)
            {
                _level.QueryBlocks(pos.x - radius - 0.2f, pos.z - radius - 0.2f,
                                  pos.x + radius + 0.2f, pos.z + radius + 0.2f, _ids);
                bool moved = false;
                for (int i = 0; i < _ids.Count; i++)
                {
                    var b = blocks[_ids[i]];
                    if (!b.Solid || (skipGrates && b.Grate)) continue;
                    if (pos.y + height < b.AabbMin.y - 0.05f || pos.y + lift > b.AabbMax.y + 0.05f) continue;

                    Vector3 a = new Vector3(pos.x, pos.y + bot, pos.z);
                    Vector3 bTop = new Vector3(pos.x, pos.y + top, pos.z);
                    Vector3 ab = bTop - a;
                    float abLen2 = Mathf.Max(1e-6f, ab.sqrMagnitude);
                    float t = 0.5f;
                    Vector3 q = Vector3.zero;
                    for (int k = 0; k < 3; k++)
                    {
                        Vector3 s = a + ab * t;
                        q = ClosestOnBlock(b, s);
                        t = Mathf.Clamp01((Vector3.Dot(q - a, ab)) / abLen2);
                    }
                    Vector3 s = a + ab * t;
                    q = ClosestOnBlock(b, s);
                    Vector3 n = s - q;
                    float dist = n.magnitude, pen;
                    if (dist > 1e-5f)
                    {
                        if (dist >= radius) continue;
                        n /= dist;
                        pen = radius - dist;
                    }
                    else
                    {
                        Vector3 o = s - b.Center;
                        float bestPen = float.PositiveInfinity;
                        for (int k = 0; k < 3; k++)
                        {
                            Vector3 ax = b.Axes[k];
                            float h = k == 0 ? b.Half.x : k == 1 ? b.Half.y : b.Half.z;
                            float d = Vector3.Dot(o, ax);
                            float pk = h - Mathf.Abs(d);
                            if (pk < bestPen) { bestPen = pk; n = ax * (d >= 0 ? 1f : -1f); }
                        }
                        pen = bestPen + radius;
                    }

                    float hl = new Vector2(n.x, n.z).magnitude;
                    if (horizontal && hl > 0.3f && n.y > -0.6f)
                    {
                        // grounded: push straight out sideways by the horizontal distance that clears the penetration
                        float push = Mathf.Min(0.45f, pen / hl) + 1e-4f;
                        pos.x += (n.x / hl) * push;
                        pos.z += (n.z / hl) * push;
                        c.Wall = true; c.WallNormal = new Vector3(n.x / hl, 0, n.z / hl); c.WallBlock = b.Id;
                    }
                    else
                    {
                        pos += n * (pen + 1e-4f);
                        if (n.y > 0.6f) { c.Ground = true; c.GroundNormal = n; c.GroundBlock = b.Id; }
                        else if (n.y < -0.6f) c.Ceiling = true;
                        else if (Mathf.Abs(n.y) < 0.6f) { c.Wall = true; c.WallNormal = n; c.WallBlock = b.Id; }
                    }
                    moved = true;
                }
                if (!moved) break;
            }
            return c;
        }

        /// Would a body capsule (same shape as CollideBody) at `pos` overlap solid geometry?
        public bool BodyFits(Vector3 pos, float radius, float lift, float height, bool skipGrates = false, float margin = 0.01f)
        {
            var blocks = _level.Blocks;
            float bot = lift + radius, top = Mathf.Max(bot, height - radius);
            _level.QueryBlocks(pos.x - radius - 0.1f, pos.z - radius - 0.1f,
                              pos.x + radius + 0.1f, pos.z + radius + 0.1f, _ids);
            for (int i = 0; i < _ids.Count; i++)
            {
                var b = blocks[_ids[i]];
                if (!b.Solid || (skipGrates && b.Grate)) continue;
                if (pos.y + height < b.AabbMin.y || pos.y + lift > b.AabbMax.y) continue;
                Vector3 a = new Vector3(pos.x, pos.y + bot, pos.z);
                Vector3 bTop = new Vector3(pos.x, pos.y + top, pos.z);
                Vector3 ab = bTop - a;
                float abLen2 = Mathf.Max(1e-6f, ab.sqrMagnitude);
                float t = 0.5f;
                Vector3 q = Vector3.zero;
                for (int k = 0; k < 3; k++)
                {
                    Vector3 s = a + ab * t;
                    q = ClosestOnBlock(b, s);
                    t = Mathf.Clamp01((Vector3.Dot(q - a, ab)) / abLen2);
                }
                Vector3 s = a + ab * t;
                q = ClosestOnBlock(b, s);
                if ((s - q).sqrMagnitude < (radius - margin) * (radius - margin)) return false;
            }
            return true;
        }

        /// Soft camera probe ("sphere-cast feel"): a cylinder of rays parallel to the boom — the centre plus two
        /// rings (radius 0.5·R and R). Each ray's hit limits the boom length; off-axis rays only partially
        /// (weight w), so an obstacle sweeping toward the line of sight pulls the camera in before it crosses it.
        public CameraProbe CameraProbe(Vector3 pivot, Vector3 back, float want, float radius, CameraProbe outProbe = null, float pad = 0.3f)
        {
            CameraProbe o = outProbe ?? new CameraProbe();
            Vector3 up = Mathf.Abs(back.y) > 0.95f ? Vector3.right : Vector3.up;
            Vector3 side = Vector3.Cross(back, up).normalized;
            Vector3 upish = Vector3.Cross(side, back).normalized;
            float hard = want, soft = want;
            o.Floor = false;
            for (int i = 0; i < CamRays.Length; i++)
            {
                float ox = CamRays[i].x, oy = CamRays[i].y, w = CamRays[i].z;
                Vector3 s = pivot + side * (ox * radius) + upish * (oy * radius);
                var h = Raycast(s, back, want + pad, _camHit, true);
                if (!h.Hit_) continue;
                float lim = Mathf.Max(0, h.Dist - pad);
                if (w >= 1f) { if (lim < hard) { hard = lim; o.Floor = h.Normal.y > 0.6f; } }
                float blended = lim + (want - lim) * (1f - w);
                if (blended < soft) soft = blended;
            }
            o.Hard = hard;
            o.Soft = Mathf.Min(soft, hard);
            return o;
        }

        /// Distance from point p to the vertical capsule (feet at base).
        public static float PointCapsuleDist(Vector3 p, Vector3 baseP, float radius, float height)
        {
            float y = Mathf.Clamp(p.y, baseP.y + radius, baseP.y + Mathf.Max(radius, height - radius));
            float dx = p.x - baseP.x, dy = p.y - y, dz = p.z - baseP.z;
            return Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// Closest approach of segment a→b to the vertical capsule; sets res.T (0..1 along the segment).
        public static float SegmentCapsuleDist(Vector3 a, Vector3 b, Vector3 baseP, float radius, float height, SegmentResult res = null)
        {
            SegmentResult r = res ?? new SegmentResult();
            float best = float.PositiveInfinity, bt = 0;
            const int steps = 6;
            for (int i = 0; i <= steps; i++)
            {
                float t = i / (float)steps;
                float d = PointCapsuleDist(Vector3.Lerp(a, b, t), baseP, radius, height);
                if (d < best) { best = d; bt = t; }
            }
            // refine
            float lo = Mathf.Max(0, bt - 1f / steps), hi = Mathf.Min(1, bt + 1f / steps);
            for (int k = 0; k < 8; k++)
            {
                float m1 = lo + (hi - lo) / 3f, m2 = hi - (hi - lo) / 3f;
                float d1 = PointCapsuleDist(Vector3.Lerp(a, b, m1), baseP, radius, height);
                float d2 = PointCapsuleDist(Vector3.Lerp(a, b, m2), baseP, radius, height);
                if (d1 < d2) hi = m2; else lo = m1;
            }
            r.T = (lo + hi) * 0.5f;
            r.Dist = PointCapsuleDist(Vector3.Lerp(a, b, r.T), baseP, radius, height);
            return r.Dist;
        }
    }

    public class SegmentResult
    {
        public float T;
        public float Dist;
    }
}
