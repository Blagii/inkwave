// Match: the turf-war rules and the match lifecycle (intro → countdown → play → time's up → judge → results).
//
// A port of the web build's src/game/match.js. The scoring is the same: the ink grid decides the winner.
using System;
using System.Collections.Generic;
using INKWAVE.Core;
using INKWAVE.Data;
using UnityEngine;

namespace INKWAVE.Gameplay
{
    public enum MatchState { Init, Intro, Playing, Finish, Judge }

    public class MatchResult
    {
        public float[] Coverage;
        public int Winner;
        public string Mode = "turf";
    }

    public class Match
    {
        public List<Actor> Actors = new List<Actor>();
        public Actor Local;
        public MatchState State = MatchState.Init;
        public float StateT;
        public float Duration = 180f;
        public float Time = 180f;
        public bool Paused;
        public bool Attract, Practice;
        public string Mode = "turf";
        public MatchResult Result;
        public readonly List<(float t, Actor victim, Actor attacker, string cause)> Events =
            new List<(float, Actor, Actor, string)>();

        bool _lastMinuteFired;
        int _lastCount = 99;

        public bool Playing() => State == MatchState.Playing && !Paused;
        public bool CanRespawn() => State == MatchState.Playing;

        public void Setup(string mode = "turf", float duration = 0f, bool practice = false, bool attract = false)
        {
            Mode = mode;
            Practice = practice;
            Attract = attract;
            Duration = duration > 0f ? duration : (mode == "zones" ? GameData.Config.zones.Float("duration", 180f)
                                                                      : GameData.Config.match.Float("defaultDuration", 180f));
            Time = Duration;
            State = MatchState.Init;
            Actors.Clear();

            var cfg = GameData.Config;
            int teamSize = cfg.match.Int("teamSize", 4);
            var weaponOrder = cfg.weaponOrder;
            var names = new[] { "Coral", "Kelp", "Tide", "Reef", "Gull", "Squall", "Brine", "Drift" };
            int ni = 0;

            for (int team = 0; team < (practice ? 1 : 2); team++)
            {
                for (int s = 0; s < (practice ? 1 : teamSize); s++)
                {
                    bool isLocal = team == 0 && s == 0 && !attract;
                    string weapon = weaponOrder[(team * teamSize + s) % weaponOrder.Count];
                    var a = new Actor(team, isLocal ? "You" : names[ni++ % names.Length], isLocal, !isLocal, s);
                    a.SetWeapon(weapon);
                    a.Bot = isLocal ? null : new BotBrain(a, cfg.difficulty.ContainsKey("normal") ? cfg.difficulty["normal"] : null);
                    Actors.Add(a);
                }
            }
            Local = Actors.Find(a => a.IsLocal);

            // initial placement on the spawn decks
            foreach (var a in Actors)
            {
                var pads = GameContext.Level.SpawnPads;
                if (pads.Count == 0) continue;
                var pad = pads[Mathf.Clamp(a.Team, 0, pads.Count - 1)];
                float ang = (a.Slot / 4f) * Mathf.PI * 2f + 0.6f, rr = 1.2f;
                a.SpawnAt(new Vector3(pad.x + Mathf.Cos(ang) * rr, pad.y, pad.z + Mathf.Sin(ang) * rr),
                          a.Team == 0 ? 0f : Mathf.PI);
                a.Invuln = 0f;
            }
            GameContext.Match = this;
        }

        public void Start()
        {
            SetState(Attract || Practice ? MatchState.Playing : MatchState.Intro);
        }

        public void SetState(MatchState s)
        {
            State = s;
            StateT = 0f;
            GameContext.Emit("match:state", new Dictionary<string, object> { { "state", s.ToString() } });
        }

        public void Update(float dt)
        {
            if (Paused) return;
            StateT += dt;
            switch (State)
            {
                case MatchState.Intro:
                    if (StateT > 4.2f) SetState(MatchState.Playing);
                    break;
                case MatchState.Playing:
                {
                    if (Practice) break;
                    Time -= dt;
                    var m = GameData.Config.match;
                    if (!_lastMinuteFired && Time <= 60f && Duration > 60f)
                    {
                        _lastMinuteFired = true;
                        GameContext.Emit("match:oneminute", null);
                    }
                    int c = Mathf.CeilToInt(Time);
                    if (Time <= m.Int("finalCountdown", 10) && c != _lastCount && c > 0)
                    {
                        _lastCount = c;
                        GameContext.Emit("match:count", new Dictionary<string, object> { { "n", c } });
                    }
                    if (Time <= 0f) { Time = 0f; SetState(MatchState.Finish); }
                    break;
                }
                case MatchState.Finish:
                    if (StateT > 2.6f && Result == null) Judge();
                    break;
            }

            bool live = State == MatchState.Playing;
            foreach (var a in Actors)
            {
                if (a.Bot != null)
                {
                    if (live) a.Bot.Update(dt);
                    else
                    {
                        a.Intent.Move = Vector3.zero;
                        a.Intent.Fire = a.Intent.Squid = a.Intent.Sub = a.Intent.Jump = a.Intent.Special = false;
                    }
                }
            }
            foreach (var a in Actors) a.Update(dt);

            // soft push between actors
            float r = GameData.P.Float("radius", 0.38f) * 1.7f;
            for (int i = 0; i < Actors.Count; i++)
            {
                for (int j = i + 1; j < Actors.Count; j++)
                {
                    var a = Actors[i]; var b = Actors[j];
                    if (!a.Alive || !b.Alive) continue;
                    float dx = b.Pos.x - a.Pos.x, dz = b.Pos.z - a.Pos.z, dy = b.Pos.y - a.Pos.y;
                    float d2 = dx * dx + dz * dz;
                    if (d2 < r * r && Mathf.Abs(dy) < 1.2f && d2 > 1e-5f)
                    {
                        float d = Mathf.Sqrt(d2), push = r - d;
                        a.Pos.x -= (dx / d) * push * 0.5f; a.Pos.z -= (dz / d) * push * 0.5f;
                        b.Pos.x += (dx / d) * push * 0.5f; b.Pos.z += (dz / d) * push * 0.5f;
                    }
                }
            }
        }

        void Judge()
        {
            var cov = GameContext.Paint.Coverage();
            int win = Mathf.Approximately(cov[0], cov[1]) ? (UnityEngine.Random.value < 0.5f ? 0 : 1) : (cov[0] > cov[1] ? 0 : 1);
            Result = new MatchResult { Coverage = cov, Winner = win, Mode = Mode };
            SetState(MatchState.Judge);
        }

        /// The live turf score (the number the HUD shows).
        public int[] TurfCounts() => new[] { GameContext.Paint.TurfCount(0), GameContext.Paint.TurfCount(1) };
    }

    /// A simple bot: it walks toward the nearest enemy, keeps its distance, and shoots when it has line of sight.
    /// The web build's BotBrain (src/game/bots.js) is far deeper — navigation, ink farming, sub timing, super jumps.
    public class BotBrain
    {
        readonly Actor _a;
        readonly JObj _diff;
        float _think, _fireCd, _strafe, _strafeT;
        Vector3 _target;

        public BotBrain(Actor a, JObj difficulty)
        {
            _a = a;
            _diff = difficulty;
        }

        public void Update(float dt)
        {
            _think -= dt; _fireCd -= dt; _strafeT -= dt;
            if (_think <= 0f)
            {
                _think = 0.2f;
                Think();
            }

            // aim
            if (_target != Vector3.zero)
            {
                Vector3 d = _target - _a.Pos;
                float want = Mathf.Atan2(d.x, d.z);
                _a.AimYaw = MathUtil.DampAngle(_a.AimYaw, want, 12f, dt);
                float wantPitch = Mathf.Atan2(d.y, Mathf.Max(0.2f, new Vector2(d.x, d.z).magnitude));
                _a.AimPitch = MathUtil.Damp(_a.AimPitch, wantPitch, 12f, dt);
                _a.AimPoint = _target;
            }

            // move: toward the target, keeping a comfortable distance
            Vector3 to = _target - _a.Pos;
            to.y = 0f;
            float dist = to.magnitude;
            Vector3 dir = dist > 0.01f ? to / dist : Vector3.zero;
            Vector3 side = new Vector3(-dir.z, 0f, dir.x);
            if (_strafeT <= 0f) { _strafeT = 1.2f; _strafe = UnityEngine.Random.value < 0.5f ? -1f : 1f; }
            Vector3 move = Vector3.zero;
            if (dist > 9f) move = dir;
            else if (dist < 4f) move = -dir;
            move += side * (_strafe * 0.6f);
            _a.Intent.Move = Vector3.ClampMagnitude(move, 1f);

            // shoot when the target is in front and there is line of sight
            bool los = _target != Vector3.zero && GameContext.Physics.Los(_a.Pos + Vector3.up * 1.05f, _target);
            if (los && _fireCd <= 0f)
            {
                _a.Intent.Fire = true;
                float interval = _a.Weapon.Float("fireInterval", 0.28f);
                float discipline = _diff != null ? _diff.Float("fireDiscipline", 0.8f) : 0.8f;
                _fireCd = interval / Mathf.Max(0.15f, discipline);
            }
            else _a.Intent.Fire = false;
        }

        void Think()
        {
            Actor best = null;
            float bestD = float.MaxValue;
            var actors = GameContext.Match != null ? GameContext.Match.Actors : null;
            if (actors != null)
            {
                for (int i = 0; i < actors.Count; i++)
                {
                    var e = actors[i];
                    if (e == null || e.Team == _a.Team || !e.Alive) continue;
                    float d = (e.Pos - _a.Pos).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = e; }
                }
            }
            _target = best != null ? best.Pos + Vector3.up * 0.9f : Vector3.zero;
        }
    }
}
