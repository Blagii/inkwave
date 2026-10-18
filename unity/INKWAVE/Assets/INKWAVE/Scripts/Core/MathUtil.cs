// Small math helpers shared by the core modules — a direct port of the web build's src/core/ctx.js so the
// simulation behaves identically in both builds.
using UnityEngine;

namespace INKWAVE.Core
{
    public static class MathUtil
    {
        public const float Epsilon = 1e-6f;

        public static float Clamp(float v, float a, float b) => v < a ? a : (v > b ? b : v);
        public static int Clamp(int v, int a, int b) => v < a ? a : (v > b ? b : v);
        public static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        public static float Lerp(float a, float b, float t) => a + (b - a) * t;

        /// Frame-rate independent exponential approach (the web build's damp()).
        public static float Damp(float a, float b, float lambda, float dt)
            => Lerp(a, b, 1f - Mathf.Exp(-lambda * dt));

        public static float SmoothStep(float a, float b, float x)
        {
            float t = Clamp((x - a) / (b - a), 0f, 1f);
            return t * t * (3f - 2f * t);
        }

        /// Signed shortest difference between two angles, in (-PI, PI].
        public static float AngleDiff(float a, float b)
        {
            float d = b - a;
            while (d > Mathf.PI) d -= Mathf.PI * 2f;
            while (d < -Mathf.PI) d += Mathf.PI * 2f;
            return d;
        }

        public static float DampAngle(float a, float b, float lambda, float dt)
            => a + AngleDiff(a, b) * (1f - Mathf.Exp(-lambda * dt));

        /// Deterministic hash → [0,1) — the same function the web build uses for procedural variation.
        public static float Hash1(float n)
        {
            float s = Mathf.Sin(n * 127.1f + 311.7f) * 43758.5453123f;
            return s - Mathf.Floor(s);
        }

        /// Seeded RNG (mulberry32) — the same sequence as the web build's rng(), so a seed gives the same
        /// procedural result in both builds.
        public sealed class Rng
        {
            private uint _a;
            public Rng(uint seed) { _a = seed; }
            /// Uniform [0,1).
            public float Next()
            {
                unchecked
                {
                    _a |= 0u;
                    _a = (_a + 0x6D2B79F5u) | 0u;
                    uint t = _a ^ (_a >> 15);
                    t = (t | (t << 12)) ^ t;
                    t = (t + ((t >> 7) * (61u | t))) ^ t;
                    return ((t ^ (t >> 14)) & 0xFFFFFFFFu) / 4294967296f;
                }
            }
            public float Range(float min, float max) => min + (max - min) * Next();
        }
    }
}
