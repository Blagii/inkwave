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
}
