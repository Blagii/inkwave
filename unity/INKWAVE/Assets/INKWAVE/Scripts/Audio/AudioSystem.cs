// The sound: a procedural synth that renders the effects at runtime, the Unity counterpart of the web build's
// src/audio/audio.js (the same voices: a pitch-swept tone, a filtered noise burst, a bubble).
//
// The clips are generated once (a small bank), then played through a pool of AudioSources with the pitch and the
// volume variations the web build uses. Everything is optional: if there is no audio device, the game still runs.
using System;
using System.Collections.Generic;
using INKWAVE.Core;
using UnityEngine;

namespace INKWAVE.Audio
{
    /// A tiny synth: renders the voices into a float buffer.
    public class Synth
    {
        public const int Rate = 44100;
        public float[] Buf;
        public float T;                       // the write head (seconds)
        readonly Random _rng = new Random(12345);

        public Synth(float seconds)
        {
            Buf = new float[Mathf.Max(1, Mathf.CeilToInt(seconds * Rate))];
        }

        public float R(float a, float b) => a + (float)_rng.NextDouble() * (b - a);

        float Noise()
        {
            // white noise
            return (float)_rng.NextDouble() * 2f - 1f;
        }

        float Pink()
        {
            // a cheap pink-ish noise (a one-pole on the white noise)
            _pink = _pink * 0.92f + Noise() * 0.08f;
            return _pink * 3.5f;
        }
        float _pink;

        void Write(int start, int len, float v)
        {
            int end = Mathf.Min(Buf.Length, start + len);
            for (int i = Mathf.Max(0, start); i < end; i++) Buf[i] += v;
        }

        /// A tone: a sine (or a triangle / square) with a pitch sweep and an attack/decay envelope.
        public void Tone(float t, float f, float f1, float sw, float a, float d, float peak, string type = "sine", float h = 0f)
        {
            int start = Mathf.FloorToInt(t * Rate);
            int n = Mathf.CeilToInt((a + h + d) * Rate) + 4;
            double phase = 0.0;
            for (int i = 0; i < n; i++)
            {
                int idx = start + i;
                if (idx < 0 || idx >= Buf.Length) break;
                double time = i / (double)Rate;
                double fr = f1 > 0f && sw > 0f ? f * Mathf.Pow(f1 / Mathf.Max(1e-4f, f), Mathf.Min(1f, time / sw)) : f;
                if (fr < 1e-3f) fr = 1e-3f;
                phase += 2.0 * Math.PI * fr / Rate;
                float s;
                if (type == "square") s = ((phase / (2.0 * Math.PI)) % 1.0) < 0.5 ? 1f : -1f;
                else if (type == "triangle") s = 1f - 4f * Mathf.Abs((float)((phase / (2.0 * Math.PI)) % 1.0) - 0.5f);
                else s = Mathf.Sin((float)phase);
                // the envelope: attack, hold, decay
                float env;
                if (time < a) env = a > 0f ? time / a : 1f;
                else if (h > 0f && time < a + h) env = 1f;
                else env = d > 0f ? Mathf.Max(0f, 1f - (time - a - h) / d) : 0f;
                Buf[idx] += s * env * peak;
            }
        }

        /// A noise burst through a state-variable filter (the filter's centre sweeps if f1 is given).
        public void Nz(float t, float f, float f1, float sw, float q, float a, float d, float peak,
                       string kind = "white", string ft = "bandpass")
        {
            int start = Mathf.FloorToInt(t * Rate);
            int n = Mathf.CeilToInt((a + d) * Rate) + 4;
            float low = 0f, band = 0f;
            for (int i = 0; i < n; i++)
            {
                int idx = start + i;
                if (idx < 0 || idx >= Buf.Length) break;
                double time = i / (double)Rate;
                float fr = f1 > 0f && sw > 0f ? f * Mathf.Pow(f1 / Mathf.Max(1e-4f, f), Mathf.Min(1f, time / sw)) : f;
                fr = Mathf.Clamp(fr, 20f, 18000f);
                float src = kind == "pink" ? Pink() : Noise();
                // a state-variable filter (a bandpass / a lowpass / a highpass)
                float F = 2f * Mathf.Sin(Mathf.PI * Mathf.Clamp01(fr / Rate) * 2f);
                float Q = 1f / Mathf.Max(0.5f, q);
                float high = src - Q * band - low;
                band += F * high;
                low += F * band;
                float outv;
                if (ft == "lowpass") outv = low;
                else if (ft == "highpass") outv = high;
                else outv = band;
                float env = time < a ? (a > 0f ? (float)(time / a) : 1f) : (d > 0f ? Mathf.Max(0f, 1f - (float)((time - a) / d)) : 0f);
                Buf[idx] += outv * env * peak * 0.6f;
            }
        }

        /// A bubble: a sine with a fast upward sweep and a soft attack.
        public void Bub(float t, float f, float peak, float d, float sweep = 1.8f)
        {
            int start = Mathf.FloorToInt(t * Rate);
            int n = Mathf.CeilToInt((d + 0.02f) * Rate) + 4;
            double phase = 0.0;
            for (int i = 0; i < n; i++)
            {
                int idx = start + i;
                if (idx < 0 || idx >= Buf.Length) break;
                double time = i / (double)Rate;
                double fr = f * (1f + (sweep - 1f) * Mathf.Clamp01((float)time / Mathf.Max(0.001f, d)));
                phase += 2.0 * Math.PI * fr / Rate;
                float env = (float)(time / 0.004f);
                if (time > 0.004f) env = Mathf.Max(0f, 1f - (float)((time - 0.004f) / d));
                Buf[idx] += Mathf.Sin((float)phase) * env * peak;
            }
        }

        /// A few random bloops (the ink blobs).
        public void Bloops(float t, int count, float spread, float fLo, float fHi, float peak, float p = 1f)
        {
            for (int i = 0; i < count; i++)
            {
                float f = R(fLo, fHi) * p;
                Tone(t + R(0f, spread), f, f * R(0.3f, 0.45f), R(0.04f, 0.07f), 0.0015f, R(0.05f, 0.09f), peak * R(0.6f, 1f));
            }
        }

        /// A few random plips.
        public void Plips(float t, int count, float spread, float fLo, float fHi, float peak, float p = 1f)
        {
            for (int i = 0; i < count; i++) Bub(t + R(0f, spread), R(fLo, fHi) * p, peak * R(0.5f, 1f), R(0.02f, 0.04f), R(1.4f, 2f));
        }

        /// The big splat (the ink landing).
        public void BigSplat(float t, float p, float s = 1f)
        {
            Tone(t, 120 * p, 40 * p, 0.15f, 0.002f, 0.28f, 1f * s);
            Nz(t, 2500f, 300f, 0.25f, 1.5f, 0.002f, 0.28f, 0.9f * s, "pink", "lowpass");
            Nz(t + 0.01f, 1600f * p, 320f * p, 0.3f, R(5f, 8f), 0.003f, 0.32f, 0.9f * s);
            Bloops(t + 0.02f, 3, 0.12f, 400f, 650f, 0.35f * s, p);
            Nz(t, 0f, 0f, 0f, 1f, 0.005f, 0.35f, 0.3f * s, "white", "highpass");
        }
    }

    /// The sound bank: the clips are rendered on demand and cached.
    public class AudioSystem : MonoBehaviour
    {
        static AudioSystem _inst;
        readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>();
        AudioSource[] _pool;
        int _next;
        public float Master = 1f;
        public bool Enabled = true;

        public static AudioSystem Inst
        {
            get
            {
                if (_inst == null)
                {
                    var go = new GameObject("INKWAVE.Audio");
                    DontDestroyOnLoad(go);
                    _inst = go.AddComponent<AudioSystem>();
                    _inst.Setup();
                }
                return _inst;
            }
        }

        void Setup()
        {
            _pool = new AudioSource[16];
            for (int i = 0; i < _pool.Length; i++)
            {
                var src = gameObject.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.spatialBlend = 1f;
                src.rolloffMode = AudioRolloffMode.Linear;
                src.minDistance = 6f;
                src.maxDistance = 45f;
                src.dopplerLevel = 0f;
                _pool[i] = src;
            }
        }

        void Awake()
        {
            if (_inst == null) { _inst = this; Setup(); }
            GameContext.On("weapon:fire", OnWeaponFire);
            GameContext.On("splat", OnSplat);
            GameContext.On("special:start", OnSpecial);
        }

        // ------------------------------------------------------------------------------ the events
        void OnWeaponFire(object data)
        {
            var d = data as Dictionary<string, object>;
            if (d == null) return;
            var actor = d["actor"] as Actor;
            string id = actor != null ? actor.Weapon.Str("kind", "shooter") : "shooter";
            Vector3 pos = actor != null ? actor.Pos : Vector3.zero;
            switch (id)
            {
                case "roller": Play("roller_flick", 0.7f, 0.8f, pos); break;
                case "twins": Play("shoot_dualies", 0.7f, 1f, pos); break;
                case "dualies": Play("shoot_dualies", 0.7f, 1f, pos); break;
                case "spinner": Play("shoot_splatling", 0.6f, 1f, pos); break;
                case "splatling": Play("shoot_splatling", 0.7f, 1f, pos); break;
                case "slosher": Play("slosh_throw", 0.7f, 1f, pos); break;
                case "bucket": Play("slosh_throw", 0.7f, 0.8f, pos); break;
                case "brush": Play("roller_flick", 0.5f, 1.7f, pos); break;
                case "brolly": Play("shoot_blaster", 0.7f, 1f, pos); break;
                case "blade": Play("blade_swish", 0.6f, 1f, pos); break;
                case "bow": Play("bow_loose", 0.7f, 1f, pos); break;
                case "mitts": Play("shoot_splatling", 0.5f, 1.4f, pos); break;
                case "charger": Play("shoot_charger", 0.6f, 1f, pos); break;
                case "blaster": Play("shoot_blaster", 0.6f, 1f, pos); break;
                default: Play("shoot_shooter", 0.6f, 1f, pos); break;
            }
        }

        void OnSplat(object data)
        {
            var d = data as Dictionary<string, object>;
            if (d == null) return;
            var victim = d["victim"] as Actor;
            var attacker = d["attacker"] as Actor;
            Vector3 pos = victim != null ? victim.Pos : Vector3.zero;
            if (attacker != null && victim != null && attacker.IsLocal) Play("splat_enemy", 0.5f, 1f, pos);
            else if (victim != null && victim.IsLocal) Play("splatted_self", 0.6f, 1f, pos);
        }

        void OnSpecial(object data)
        {
            var d = data as Dictionary<string, object>;
            if (d == null) return;
            var actor = d["actor"] as Actor;
            Play("special_activate", 0.6f, 1f, actor != null ? actor.Pos : Vector3.zero);
        }

        // ------------------------------------------------------------------------------ the playback
        public void Play(string id, float volume = 1f, float pitch = 1f, Vector3? pos = null)
        {
            if (!Enabled || Master <= 0f) return;
            var clip = Clip(id);
            if (clip == null) return;
            var src = _pool[_next];
            _next = (_next + 1) % _pool.Length;
            src.clip = clip;
            src.volume = Mathf.Clamp01(volume * Master);
            src.pitch = Mathf.Clamp(pitch, 0.25f, 4f);
            if (pos.HasValue)
            {
                src.transform.position = pos.Value;
                src.spatialBlend = 1f;
            }
            else src.spatialBlend = 0f;
            src.Play();
        }

        public void PlayUi(string id, float volume = 1f, float pitch = 1f) => Play(id, volume, pitch, null);

        /// The clip for a sound id (rendered on demand, then cached).
        public AudioClip Clip(string id)
        {
            if (_clips.TryGetValue(id, out var clip)) return clip;
            float len;
            var build = Build(id, out len);
            if (build == null) return null;
            var s = build;
            int n = Mathf.CeilToInt(len * Synth.Rate);
            if (n <= 0) return null;
            var data = new float[n];
            for (int i = 0; i < n; i++) data[i] = Mathf.Clamp(s.Buf[i], -1f, 1f);
            clip = AudioClip.Create("sfx_" + id, n, 1, Synth.Rate, false);
            clip.SetData(data, 0);
            _clips[id] = clip;
            return clip;
        }

        /// The definitions (a port of the web build's SFX table).
        System.Func<Synth> Build(string id, out float len)
        {
            len = 0.5f;
            switch (id)
            {
                case "shoot_shooter":
                    len = 0.4f;
                    return () =>
                    {
                        var v = new Synth(len);
                        v.Nz(0f, 4200f, 0f, 0f, 1.6f, 0.0004f, 0.018f, 0.66f);
                        v.Tone(0f, 2600f, 820f, 0.014f, 0.0005f, 0.02f, 0.26f);
                        v.Nz(0f, 2800f, 1740f, 0.05f, 1.4f, 0.0008f, 0.05f, 0.8f);
                        v.Tone(0f, 175f, 62f, 0.05f, 0.001f, 0.055f, 0.52f);
                        v.Nz(0.01f, 1700f, 600f, 0.09f, 5f, 0.004f, 0.09f, 0.72f);
                        v.Bub(0.025f, 1100f, 0.12f, 0.022f, 1.8f);
                        v.Nz(0f, 0f, 0f, 0f, 1f, 0.001f, 0.06f, 0.2f, "white", "highpass");
                        return v;
                    };
                case "shoot_blaster":
                    len = 0.4f;
                    return () =>
                    {
                        var v = new Synth(len);
                        v.Nz(0f, 0f, 0f, 0f, 1f, 0.0003f, 0.008f, 0.5f, "white", "highpass");
                        v.Nz(0f, 1500f, 900f, 0.02f, 2.2f, 0.0006f, 0.022f, 0.75f);
                        v.Tone(0f, 150f, 40f, 0.16f, 0.002f, 0.3f, 1f);
                        v.Tone(0f, 300f, 88f, 0.08f, 0.002f, 0.1f, 0.35f, "triangle");
                        v.Nz(0f, 1600f, 280f, 0.12f, 2f, 0.002f, 0.14f, 0.9f, "pink", "lowpass");
                        v.Nz(0.008f, 320f, 950f, 0.06f, 6f, 0.004f, 0.08f, 0.55f);
                        v.Nz(0.05f, 0f, 0f, 0f, 1f, 0.02f, 0.16f, 0.08f, "white", "highpass");
                        return v;
                    };
                case "shoot_dualies":
                    len = 0.3f;
                    return () =>
                    {
                        var v = new Synth(len);
                        v.Nz(0f, 4800f, 0f, 0f, 1.8f, 0.0003f, 0.01f, 0.6f);
                        v.Nz(0.002f, 3200f, 0f, 0f, 3f, 0.0003f, 0.006f, 0.35f);
                        v.Nz(0f, 3000f, 1800f, 0.04f, 1.5f, 0.0006f, 0.04f, 0.72f);
                        v.Tone(0f, 210f, 80f, 0.04f, 0.001f, 0.042f, 0.45f);
                        v.Nz(0.008f, 1900f, 680f, 0.07f, 6f, 0.003f, 0.07f, 0.55f);
                        return v;
                    };
                case "shoot_splatling":
                    len = 0.3f;
                    return () =>
                    {
                        var v = new Synth(len);
                        v.Nz(0f, 3600f, 0f, 0f, 1.6f, 0.0003f, 0.012f, 0.75f);
                        v.Nz(0f, 2400f, 1300f, 0.03f, 1.6f, 0.0005f, 0.04f, 0.85f);
                        v.Tone(0f, 160f, 70f, 0.03f, 0.001f, 0.04f, 0.42f);
                        v.Nz(0.006f, 1700f, 600f, 0.06f, 5f, 0.002f, 0.075f, 0.6f);
                        v.Nz(0.01f, 2100f, 1500f, 0.08f, 1.2f, 0.004f, 0.08f, 0.28f);
                        return v;
                    };
                case "shoot_charger":
                    len = 0.7f;
                    return () =>
                    {
                        var v = new Synth(len);
                        v.Nz(0f, 0f, 0f, 0f, 1f, 0.0002f, 0.03f, 1f, "white", "highpass");
                        v.Tone(0f, 5200f, 0f, 0f, 0.0002f, 0.006f, 0.22f);
                        v.Nz(0f, 6000f, 900f, 0.14f, 3f, 0.001f, 0.16f, 0.5f);
                        v.Tone(0f, 2600f, 260f, 0.13f, 0.0008f, 0.15f, 0.16f, "sawtooth");
                        v.Tone(0f, 140f, 46f, 0.12f, 0.001f, 0.2f, 0.85f);
                        v.Nz(0.03f, 1700f, 420f, 0.5f, 2.5f, 0.01f, 0.55f, 0.5f);
                        return v;
                    };
                case "roller_flick":
                    len = 0.6f;
                    return () =>
                    {
                        var v = new Synth(len);
                        v.Nz(0f, 320f, 1700f, 0.15f, 1.8f, 0.01f, 0.3f, 0.6f, "pink");
                        v.Tone(0.12f, 120f, 45f, 0.18f, 0.002f, 0.25f, 0.9f);
                        v.Nz(0.14f, 1100f, 220f, 0.2f, 1.5f, 0.002f, 0.2f, 0.6f, "pink", "lowpass");
                        v.Bloops(0.16f, 4, 0.2f, 350f, 700f, 0.3f);
                        return v;
                    };
                case "slosh_throw":
                    len = 0.6f;
                    return () =>
                    {
                        var v = new Synth(len);
                        v.Nz(0f, 260f, 1400f, 0.13f, 1.6f, 0.01f, 0.3f, 0.6f, "pink");
                        v.Tone(0.12f, 180f, 420f, 0.09f, 0.004f, 0.12f, 0.55f);
                        v.Nz(0.12f, 380f, 1100f, 0.1f, 6f, 0.006f, 0.12f, 0.5f);
                        v.Tone(0.125f, 110f, 48f, 0.14f, 0.003f, 0.2f, 0.7f);
                        v.Nz(0.14f, 1500f, 500f, 0.35f, 2.5f, 0.01f, 0.4f, 0.45f);
                        return v;
                    };
                case "blade_swish":
                    len = 0.4f;
                    return () =>
                    {
                        var v = new Synth(len);
                        v.Nz(0f, 900f, 2600f, 0.1f, 1.2f, 0.01f, 0.16f, 0.5f, "pink");
                        v.Tone(0.02f, 420f, 160f, 0.1f, 0.004f, 0.14f, 0.3f);
                        return v;
                    };
                case "bow_loose":
                    len = 0.4f;
                    return () =>
                    {
                        var v = new Synth(len);
                        v.Nz(0f, 2200f, 700f, 0.1f, 2f, 0.002f, 0.12f, 0.7f);
                        v.Tone(0f, 700f, 240f, 0.08f, 0.001f, 0.1f, 0.35f);
                        v.Nz(0.01f, 0f, 0f, 0f, 1f, 0.002f, 0.08f, 0.2f, "white", "highpass");
                        return v;
                    };
                case "splat_small":
                    len = 0.3f;
                    return () =>
                    {
                        var v = new Synth(len);
                        v.Tone(0f, 170f, 65f, 0.06f, 0.001f, 0.08f, 0.8f);
                        v.Nz(0f, 2600f, 950f, 0.09f, 3f, 0.001f, 0.1f, 0.9f);
                        v.Tone(0.006f, 650f, 210f, 0.05f, 0.0015f, 0.06f, 0.45f);
                        v.Plips(0.03f, 1, 0.05f, 700f, 1300f, 0.15f);
                        return v;
                    };
                case "splat_big":
                    len = 0.6f;
                    return () => { var v = new Synth(len); v.BigSplat(0f, 1f); return v; };
                case "bomb_throw":
                    len = 0.3f;
                    return () =>
                    {
                        var v = new Synth(len);
                        v.Tone(0f, 330f, 210f, 0.03f, 0.001f, 0.05f, 0.4f, "triangle");
                        v.Nz(0f, 500f, 2200f, 0.22f, 1.5f, 0.08f, 0.14f, 0.5f);
                        return v;
                    };
                case "bomb_beep":
                    len = 0.2f;
                    return () =>
                    {
                        var v = new Synth(len);
                        v.Tone(0f, 1480f, 0f, 0f, 0.002f, 0.05f, 0.22f, "square", 0.05f);
                        v.Tone(0f, 1480f, 0f, 0f, 0.002f, 0.04f, 0.35f);
                        return v;
                    };
                case "bomb_explode":
                    len = 1.1f;
                    return () =>
                    {
                        var v = new Synth(len);
                        v.Tone(0f, 90f, 30f, 0.5f, 0.002f, 0.7f, 1f);
                        v.Nz(0f, 0f, 0f, 0f, 1f, 0.0003f, 0.04f, 0.9f, "white", "highpass");
                        v.Nz(0f, 5000f, 180f, 0.6f, 0.9f, 0.002f, 0.7f, 1f, "pink", "lowpass");
                        v.Nz(0.04f, 1500f, 350f, 0.5f, 3f, 0.004f, 0.6f, 0.7f);
                        v.Bloops(0.05f, 5, 0.25f, 350f, 700f, 0.3f);
                        return v;
                    };
                case "squid_in":
                    len = 0.3f;
                    return () =>
                    {
                        var v = new Synth(len);
                        v.Tone(0f, 950f, 240f, 0.09f, 0.002f, 0.12f, 0.7f);
                        v.Nz(0f, 900f, 380f, 0.1f, 6f, 0.004f, 0.1f, 0.55f);
                        v.Plips(0.05f, 2, 0.05f, 900f, 1400f, 0.18f);
                        return v;
                    };
                case "squid_out":
                    len = 0.3f;
                    return () =>
                    {
                        var v = new Synth(len);
                        v.Tone(0f, 260f, 1050f, 0.07f, 0.002f, 0.09f, 0.6f);
                        v.Nz(0f, 400f, 1200f, 0.08f, 5f, 0.003f, 0.08f, 0.4f);
                        return v;
                    };
                case "jump":
                    len = 0.2f;
                    return () =>
                    {
                        var v = new Synth(len);
                        v.Tone(0f, 380f, 620f, 0.06f, 0.003f, 0.08f, 0.35f);
                        return v;
                    };
                case "land":
                    len = 0.3f;
                    return () =>
                    {
                        var v = new Synth(len);
                        v.Tone(0f, 150f, 70f, 0.05f, 0.002f, 0.09f, 0.5f);
                        v.Nz(0f, 700f, 260f, 0.07f, 3f, 0.002f, 0.08f, 0.5f);
                        return v;
                    };
                case "swim_splash":
                    len = 0.35f;
                    return () =>
                    {
                        var v = new Synth(len);
                        v.Nz(0f, 1600f, 700f, 0.12f, 2f, 0.002f, 0.16f, 0.8f);
                        v.Tone(0f, 500f, 200f, 0.06f, 0.002f, 0.07f, 0.3f);
                        v.Plips(0.02f, 3, 0.1f, 700f, 1500f, 0.16f);
                        return v;
                    };
                case "step_ink":
                    len = 0.2f;
                    return () =>
                    {
                        var v = new Synth(len);
                        v.Nz(0f, 1300f, 520f, 0.05f, 3.5f, 0.002f, 0.07f, 0.6f);
                        v.Tone(0f, 190f, 110f, 0.04f, 0.001f, 0.05f, 0.35f);
                        v.Plips(0.01f, 2, 0.05f, 900f, 1900f, 0.12f);
                        return v;
                    };
                case "ink_hit_body":
                    len = 0.3f;
                    return () =>
                    {
                        var v = new Synth(len);
                        v.Tone(0f, 190f, 70f, 0.05f, 0.001f, 0.06f, 0.85f);
                        v.Nz(0f, 1300f, 520f, 0.07f, 3f, 0.001f, 0.08f, 0.8f);
                        v.Bub(0.02f, 900f, 0.14f, 0.025f, 1.7f);
                        return v;
                    };
                case "hurt":
                    len = 0.3f;
                    return () =>
                    {
                        var v = new Synth(len);
                        v.Tone(0f, 130f, 50f, 0.12f, 0.002f, 0.18f, 0.9f);
                        v.Nz(0f, 700f, 260f, 0.14f, 5f, 0.003f, 0.16f, 0.8f);
                        return v;
                    };
                case "splat_enemy":
                    len = 0.6f;
                    return () =>
                    {
                        var v = new Synth(len);
                        v.Nz(0f, 0f, 0f, 0f, 1f, 0.0003f, 0.02f, 0.45f, "white", "highpass");
                        v.Tone(0f, 780f, 170f, 0.06f, 0.001f, 0.09f, 0.9f);
                        v.Nz(0f, 2000f, 700f, 0.1f, 2f, 0.001f, 0.1f, 0.6f);
                        v.Tone(0f, 110f, 45f, 0.1f, 0.001f, 0.15f, 0.6f);
                        v.Nz(0.005f, 2400f, 300f, 0.2f, 1.2f, 0.002f, 0.22f, 0.5f, "pink", "lowpass");
                        // a little sparkle
                        float[] notes = { 88f, 92f, 95f, 100f };
                        for (int i = 0; i < notes.Length; i++)
                            v.Tone(0.03f + i * 0.045f, 440f * Mathf.Pow(2f, (notes[i] - 69f) / 12f), 0f, 0f, 0.001f, 0.35f, 0.26f);
                        return v;
                    };
                case "splatted_self":
                    len = 1.4f;
                    return () =>
                    {
                        var v = new Synth(len);
                        v.BigSplat(0f, 1f, 1.1f);
                        v.Tone(0.12f, 620f, 150f, 0.95f, 0.02f, 0.3f, 0.4f, "square");
                        return v;
                    };
                case "low_ink":
                    len = 0.4f;
                    return () =>
                    {
                        var v = new Synth(len);
                        for (int k = 0; k < 2; k++)
                        {
                            float t = k * 0.15f;
                            v.Tone(t, 520f, 300f, 0.07f, 0.002f, 0.1f, 0.5f);
                            v.Tone(t, 260f, 150f, 0.07f, 0.002f, 0.08f, 0.2f, "triangle");
                        }
                        return v;
                    };
                case "empty_click":
                    len = 0.2f;
                    return () =>
                    {
                        var v = new Synth(len);
                        v.Nz(0f, 2400f, 0f, 0f, 2f, 0.0003f, 0.006f, 0.8f);
                        v.Tone(0f, 190f, 0f, 0f, 0.0005f, 0.018f, 0.3f, "square");
                        v.Bub(0.05f, 1300f, 0.06f, 0.02f, 1.6f);
                        return v;
                    };
                case "refill_full":
                    len = 0.8f;
                    return () =>
                    {
                        var v = new Synth(len);
                        v.Tone(0f, 400f, 1200f, 0.06f, 0.002f, 0.07f, 0.4f);
                        v.Tone(0.05f, 1046f, 0f, 0f, 0.002f, 0.45f, 0.3f);
                        v.Tone(0.11f, 1568f, 0f, 0f, 0.002f, 0.55f, 0.25f);
                        return v;
                    };
                case "special_ready":
                    len = 1.1f;
                    return () =>
                    {
                        var v = new Synth(len);
                        float[] notes = { 60f, 64f, 67f, 72f, 76f, 79f, 84f };
                        for (int i = 0; i < notes.Length; i++)
                            v.Tone(i * 0.045f, 440f * Mathf.Pow(2f, (notes[i] - 69f) / 12f), 0f, 0f, 0.002f, 0.18f, 0.22f, "square");
                        v.Tone(0.32f, 2093f, 0f, 0f, 0.002f, 0.7f, 0.3f);
                        v.Nz(0f, 800f, 6000f, 0.32f, 2f, 0.01f, 0.36f, 0.35f, "white");
                        return v;
                    };
                case "special_activate":
                    len = 0.9f;
                    return () =>
                    {
                        var v = new Synth(len);
                        v.Nz(0f, 300f, 3000f, 0.35f, 2f, 0.25f, 0.15f, 0.6f);
                        v.Tone(0f, 110f, 440f, 0.35f, 0.05f, 0.25f, 0.4f, "sawtooth");
                        v.Tone(0.35f, 120f, 40f, 0.25f, 0.002f, 0.3f, 0.9f);
                        v.Nz(0.35f, 3000f, 200f, 0.3f, 1f, 0.002f, 0.3f, 0.6f, "pink", "lowpass");
                        return v;
                    };
                case "respawn":
                    len = 1.2f;
                    return () =>
                    {
                        var v = new Synth(len);
                        v.Tone(0f, 300f, 1300f, 0.35f, 0.03f, 0.2f, 0.3f);
                        for (int i = 0; i < 8; i++) v.Bub(i * 0.05f, 500f + i * 140f, 0.14f, 0.03f, 1.8f);
                        v.Nz(0f, 600f, 3000f, 0.4f, 1.5f, 0.3f, 0.15f, 0.3f);
                        return v;
                    };
                case "ui_click":
                    len = 0.2f;
                    return () =>
                    {
                        var v = new Synth(len);
                        v.Tone(0f, 380f, 1150f, 0.035f, 0.002f, 0.075f, 0.6f);
                        v.Tone(0f, 760f, 2300f, 0.03f, 0.001f, 0.04f, 0.16f, "triangle");
                        v.Nz(0f, 3000f, 0f, 0f, 1.5f, 0.0005f, 0.01f, 0.18f);
                        return v;
                    };
                case "ui_hover":
                    len = 0.15f;
                    return () =>
                    {
                        var v = new Synth(len);
                        v.Tone(0f, 1900f, 1450f, 0.03f, 0.002f, 0.05f, 0.6f);
                        return v;
                    };
                case "countdown_tick":
                    len = 0.25f;
                    return () =>
                    {
                        var v = new Synth(len);
                        v.Tone(0f, 880f, 0f, 0f, 0.001f, 0.06f, 0.55f);
                        v.Tone(0f, 880f, 0f, 0f, 0.001f, 0.04f, 0.1f, "square");
                        return v;
                    };
                case "times_up":
                    len = 1.6f;
                    return () =>
                    {
                        var v = new Synth(len);
                        v.Tone(0f, 2600f, 2600f, 0f, 0.02f, 0.55f, 0.12f);
                        v.Nz(0f, 2600f, 0f, 0f, 3f, 0.02f, 0.55f, 0.12f);
                        float[] notes = { 58f, 62f, 65f, 70f };
                        for (int i = 0; i < notes.Length; i++)
                            v.Tone(0.72f, 440f * Mathf.Pow(2f, (notes[i] - 69f) / 12f), 0f, 0f, 0.01f, 0.38f, 0.14f, "sawtooth");
                        v.Nz(0.72f, 5000f, 500f, 0.5f, 0.8f, 0.005f, 0.6f, 0.5f, "pink", "lowpass");
                        return v;
                    };
                default:
                    return null;
            }
        }
    }
}
