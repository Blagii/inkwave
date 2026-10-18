// The bot brain: a port of the web build's src/game/bots.js (the Turf War behaviour).
//
// The web build's bot has ~2000 lines: a nav graph with A*, threat AI (the enemy devices), the Zone Control
// team plan and a boss mode. This port keeps the parts that carry the Turf War feel:
//
//   · perception   — the nearest enemy it can see, with the reaction time and the acquisition error
//   · the modes    — paint / fight / refill / retreat
//   · the aiming   — the lead by the projectile's flight time, plus a human aim error
//   · the steering — toward the goal, with a wall probe and a strafe
//   · the weapons  — the fire range per kind, the charge, the sub and the special
//   · the swim     — it dives for cover when the ink runs low
//
// The nav graph is not ported: the goals are picked from the level's own turf faces (the same cells the
// players paint), so a bot walks to ink it has not covered yet.
using System.Collections.Generic;
using INKWAVE.Core;
using INKWAVE.Data;
using INKWAVE.World;
using UnityEngine;

namespace INKWAVE.Gameplay
{
    public class BotBrain
    {
        readonly Actor _a;
        readonly JObj _diff;

        // the perception
        Actor _target;
        float _seeTimer, _reactCd, _think, _lostTimer;
        float _acqT = 9f, _acqSignY, _acqSignP, _ph1, _ph2, _t;

        // the aim
        float _aimYaw, _aimPitch;

        // the navigation
        string _mode = "paint";
        Vector3 _goal;
        bool _hasGoal;
        float _goalTimer, _repath;
        float _strafe = 1f, _strafeT, _stuck, _lastDist;
        Vector3 _lastPos;

        // the actions
        float _fireCd, _bombCd = 3f, _chargeHold, _chargeRelease = 0.95f, _refillUntil, _retreatT, _jumpCd;
        bool _wasDead;

        static readonly Hit _hit = new Hit();

        public BotBrain(Actor a, JObj difficulty)
        {
            _a = a;
            _diff = difficulty;
            _aimYaw = a.Yaw;
            _ph1 = UnityEngine.Random.value * 20f;
            _ph2 = UnityEngine.Random.value * 20f;
            _t = UnityEngine.Random.value * 10f;
            _strafe = UnityEngine.Random.value < 0.5f ? 1f : -1f;
        }

        // ---------------------------------------------------------------------------------- the update
        public void Update(float dt)
        {
            var it = _a.Intent;
            if (!_a.Alive)
            {
                it.Move = Vector3.zero;
                it.Fire = it.Squid = it.Sub = it.Jump = it.Special = false;
                _target = null; _hasGoal = false; _wasDead = true;
                return;
            }
            if (_wasDead)
            {
                _wasDead = false;
                _aimYaw = _a.Yaw; _aimPitch = 0f;
                _acqT = 9f; _target = null; _hasGoal = false; _goalTimer = 0f;
            }
            var match = GameContext.Match;
            if (match == null || !match.Playing()) { it.Move = Vector3.zero; return; }

            _think -= dt; _reactCd -= dt; _strafeT -= dt; _bombCd -= dt; _jumpCd -= dt;
            _acqT += dt; _t += dt;

            // ---- perception
            if (_think <= 0f)
            {
                _think = 0.15f + UnityEngine.Random.value * 0.1f;
                Perceive();
            }
            if (_target != null && !_target.Alive) _target = null;
            if (_target == null) _seeTimer = Mathf.Max(0f, _seeTimer - dt);

            // ---- the mode
            float inkFrac = _a.Ink / 100f;
            float hpFrac = _a.Hp / 100f;
            var w = _a.Weapon;
            bool melee = w.Str("kind") == "roller" || w.Str("kind") == "brush";

            if (_mode == "retreat")
            {
                _retreatT -= dt;
                if (hpFrac > 0.85f || _retreatT <= 0f || (_target == null && hpFrac > 0.6f))
                {
                    _mode = "paint"; _hasGoal = false; _goalTimer = 0f;
                }
            }
            else if (_target != null && _seeTimer > 0f && ((hpFrac < 0.34f && !melee && _a.LastDamage < 0.8f) || hpFrac < 0.2f)
                     && UnityEngine.Random.value < 0.6f * dt * 60f * FireDiscipline)
            {
                _mode = "retreat"; _retreatT = 2.2f + UnityEngine.Random.value * 1.4f; _repath = 0f; PickRetreat();
            }
            if (_mode != "refill" && _mode != "retreat" && inkFrac < 0.12f
                && !(_target != null && _seeTimer > 0f && !melee && inkFrac > 0.05f))
            {
                _mode = "refill"; _refillUntil = 0.85f + UnityEngine.Random.value * 0.1f;
            }
            if (_mode == "refill" && inkFrac >= _refillUntil) _mode = "paint";
            if (_mode != "refill" && _mode != "retreat") _mode = _target != null ? "fight" : "paint";

            // ---- the goal
            _goalTimer -= dt; _repath -= dt;
            if (_mode == "fight" && _target != null)
            {
                _goal = _target.Pos;
                _hasGoal = true;
            }
            else if (_mode == "refill")
            {
                if (_repath <= 0f || !_hasGoal) PickRefill();
            }
            else if (_mode == "retreat")
            {
                if (_repath <= 0f || !_hasGoal) PickRetreat();
            }
            else if (_goalTimer <= 0f || !_hasGoal)
            {
                PickPaintGoal();
            }

            // ---- the steering
            Vector3 move = Steer(dt);

            // ---- the aim
            Aim(dt);

            // ---- the actions
            it.Fire = false; it.Sub = false; it.Special = false; it.Squid = false; it.Jump = false;
            Actions(dt, move, w);
            it.Move = Vector3.ClampMagnitude(move, 1f);
        }

        // ------------------------------------------------------------------------------- the perception
        void Perceive()
        {
            var actors = GameContext.Match != null ? GameContext.Match.Actors : null;
            if (actors == null) return;
            Actor best = null;
            float bestScore = float.MaxValue;
            for (int i = 0; i < actors.Count; i++)
            {
                var e = actors[i];
                if (e == null || e.Team == _a.Team || !e.Alive) continue;
                float d = (e.Pos - _a.Pos).magnitude;
                if (d > Awareness) continue;
                // it has to see the target (the eyes are at the head height)
                var eye = _a.Pos + new Vector3(0f, 1.05f, 0f);
                var at = e.Pos + new Vector3(0f, 0.85f, 0f);
                if (!GameContext.Physics.Los(eye, at)) continue;
                // the closer the better, but a target in front counts more
                float score = d;
                if (score < bestScore) { bestScore = score; best = e; }
            }
            if (best == null)
            {
                _target = null;
                return;
            }
            if (best != _target)
            {
                _target = best;
                _reactCd = Reaction;
                _acqT = 0f;
                _acqSignY = UnityEngine.Random.value < 0.5f ? -1f : 1f;
                _acqSignP = UnityEngine.Random.value < 0.5f ? -1f : 1f;
            }
        }

        // --------------------------------------------------------------------------------- the aiming
        void Aim(float dt)
        {
            if (_target == null) return;
            var t = _target;
            var w = _a.Weapon;
            float dx = t.Pos.x - _a.Pos.x, dz = t.Pos.z - _a.Pos.z;
            float dist = Mathf.Sqrt(dx * dx + dz * dz);
            string kind = w.Str("kind");

            // lead the target by the projectile's time to arrive
            float lead;
            if (kind == "charger") lead = 0f;
            else if (kind == "slosher") lead = w.Float("windup", 0.13f) + dist / (w.Float("projSpeed", 15f) * 0.88f);
            else lead = dist / (w.Float("projSpeed", 30f) > 0f ? w.Float("projSpeed", 30f) : 30f);
            var aim = new Vector3(t.Pos.x + t.Vel.x * lead,
                                   t.Pos.y + (t.Form == ActorForm.Squid ? 0.3f : 0.85f),
                                   t.Pos.z + t.Vel.z * lead);

            var from = _a.Pos + new Vector3(0f, 1.1f, 0f);
            var d = aim - from;
            float idealYaw = Mathf.Atan2(d.x, d.z);
            float idealPitch = Mathf.Atan2(d.y, Mathf.Max(0.1f, new Vector2(d.x, d.z).magnitude));

            // the human error: a slow wander plus an acquisition error that settles over the reaction time
            float e = AimError;
            float acq = Mathf.Exp(-_acqT / Mathf.Max(0.12f, Reaction * 0.9f));
            float wantYaw = idealYaw + e * (0.75f * Wander(_t * 1.7f + _ph1) + 2.4f * acq * _acqSignY);
            float wantPitch = idealPitch + e * 0.6f * (0.75f * Wander(_t * 2.1f + _ph2) + 1.6f * acq * _acqSignP);

            _aimYaw = MathUtil.DampAngle(_aimYaw, wantYaw, AimOmega, dt);
            _aimPitch = MathUtil.Damp(_aimPitch, wantPitch, AimOmega, dt);
            _a.AimYaw = _aimYaw;
            _a.AimPitch = _aimPitch;
            _a.AimPoint = aim;
        }

        static float Wander(float x) => Mathf.Sin(x) * 0.6f + Mathf.Sin(x * 2.27f + 1.3f) * 0.4f;

        // ------------------------------------------------------------------------------- the steering
        Vector3 Steer(float dt)
        {
            if (!_hasGoal) return Vector3.zero;
            var to = _goal - _a.Pos;
            to.y = 0f;
            float dist = to.magnitude;
            if (dist < 0.8f) return Vector3.zero;
            var dir = to / dist;

            // the strafe: the bots keep moving sideways while they fight
            if (_strafeT <= 0f) { _strafeT = 0.9f + UnityEngine.Random.value * 1.1f; _strafe = -_strafe; }
            var side = new Vector3(-dir.z, 0f, dir.x);

            // the wall probe: if the way ahead is blocked, try the sides
            var probe = _a.Pos + new Vector3(0f, 0.9f, 0f);
            var ahead = probe + dir * 1.6f;
            bool blocked = !GameContext.Physics.Los(probe, ahead);
            if (blocked)
            {
                var left = probe + (dir * 0.4f + side * 1.2f);
                var right = probe + (dir * 0.4f - side * 1.2f);
                bool lOk = GameContext.Physics.Los(probe, left);
                bool rOk = GameContext.Physics.Los(probe, right);
                if (lOk && !rOk) dir = (dir * 0.4f + side).normalized;
                else if (rOk && !lOk) dir = (dir * 0.4f - side).normalized;
                else dir = (dir * 0.4f + side * _strafe).normalized;
            }

            Vector3 move = dir;
            // while fighting, the bot circles the target instead of walking into it
            if (_mode == "fight" && _target != null)
            {
                var toT = _target.Pos - _a.Pos;
                toT.y = 0f;
                float d = toT.magnitude;
                float range = Range(_a.Weapon);
                if (d < range * 0.6f) move = -toT.normalized + side * 0.7f;
                else if (d > range * 1.1f) move = toT.normalized + side * 0.4f;
                else move = side;
            }
            else move += side * _strafe * 0.35f;

            // the stuck check: if it has not moved, jump or pick a new goal
            float moved = (_a.Pos - _lastPos).magnitude;
            _lastPos = _a.Pos;
            if (moved < 0.02f && move.sqrMagnitude > 0.01f) _stuck += dt; else _stuck = Mathf.Max(0f, _stuck - dt * 2f);
            if (_stuck > 0.9f)
            {
                _stuck = 0f;
                _goalTimer = 0f;
                if (_jumpCd <= 0f) { _a.Intent.Jump = true; _jumpCd = 1.2f; }
                move = -move;
            }
            return Vector3.ClampMagnitude(move, 1f);
        }

        // ---------------------------------------------------------------------------------- the actions
        void Actions(float dt, Vector3 move, JObj w)
        {
            var it = _a.Intent;
            string kind = w.Str("kind");
            bool sees = _target != null && _seeTimer > 0f;
            float dist = sees ? (_target.Pos - _a.Pos).magnitude : 99f;
            float range = Range(w);

            // the charge weapons hold the trigger and release at the right moment
            bool charge = kind == "charger" || kind == "spinner" || kind == "splatling" || kind == "bow";
            if (sees && dist < range * 1.3f)
            {
                if (charge)
                {
                    _chargeHold += dt;
                    it.Fire = _chargeHold < _chargeRelease;
                }
                else
                {
                    it.Fire = true;
                    _fireCd = w.Float("fireInterval", 0.28f) / Mathf.Max(0.15f, FireDiscipline);
                }
            }
            else _chargeHold = 0f;

            // the sub weapon: thrown at a target in the open
            if (sees && dist > 6f && dist < 18f && _bombCd <= 0f && _a.Ink > 60f)
            {
                it.Sub = true;
                _bombCd = 3f + UnityEngine.Random.value * 4f;
            }

            // the special: when it is charged and the target is close
            if (sees && dist < 9f && _a.SpecialReady()) it.Special = true;

            // the swim: out of ink and an enemy nearby → dive
            if (sees && _a.Ink < 30f && dist < 8f && _a.Grounded && _a.Form == ActorForm.Kid)
            {
                it.Squid = true;
                it.Fire = false;
            }
        }

        // ------------------------------------------------------------------------------ the goal picking
        void PickPaintGoal()
        {
            _goalTimer = 2.2f + UnityEngine.Random.value * 2.4f;
            _repath = 0.6f;
            var level = GameContext.Level;
            var paint = GameContext.Paint;
            if (level == null || paint == null) return;

            // Zone Control: the objective comes first — the bot heads for a cell of the live zone
            var match = GameContext.Match;
            if (match != null && match.Zones != null)
            {
                var zones = match.Zones.Active.Zones;
                for (int attempt = 0; attempt < 10 && zones.Count > 0; attempt++)
                {
                    var zone = zones[UnityEngine.Random.Range(0, zones.Count)];
                    if (zone.Cells.Count == 0) continue;
                    int k = zone.Cells[UnityEngine.Random.Range(0, zone.Cells.Count)];
                    Vector3 p;
                    if (!paint.CellWorld(k, out p)) continue;
                    byte owner = paint.SampleWorld(paint.CellFaceId(k), p);
                    if (owner != 0 && owner - 1 == _a.Team) continue;    // our own ink: go elsewhere
                    if (Vector3.Distance(p, _a.Pos) < 3f) continue;
                    _goal = p;
                    _hasGoal = true;
                    return;
                }
            }

            // a random turf face that is not already ours
            for (int attempt = 0; attempt < 14; attempt++)
            {
                var faces = level.Faces;
                if (faces.Count == 0) break;
                var f = faces[UnityEngine.Random.Range(0, faces.Count)];
                if (!f.Turf || !f.Paintable) continue;
                var p = f.Origin + f.U * (f.Nu * f.Cu * 0.5f) + f.V * (f.Nv * f.Cv * 0.5f);
                byte owner = paint.SampleWorld(f.Id, p);
                if (owner != 0 && owner - 1 == _a.Team) continue;     // our own ink: go elsewhere
                if (Vector3.Distance(p, _a.Pos) < 3f) continue;
                _goal = p;
                _hasGoal = true;
                return;
            }
            // nothing found: wander toward the enemy's side
            if (level.SpawnPads.Count > 0)
            {
                var pad = level.SpawnPads[1 - _a.Team];
                _goal = new Vector3(pad.x + UnityEngine.Random.Range(-8f, 8f), _a.Pos.y, pad.z + UnityEngine.Random.Range(-8f, 8f));
                _hasGoal = true;
            }
        }

        void PickRefill()
        {
            _repath = 0.5f;
            var level = GameContext.Level;
            var paint = GameContext.Paint;
            if (level == null || paint == null) return;
            for (int attempt = 0; attempt < 12; attempt++)
            {
                var faces = level.Faces;
                if (faces.Count == 0) break;
                var f = faces[UnityEngine.Random.Range(0, faces.Count)];
                if (!f.Turf || !f.Paintable) continue;
                var p = f.Origin + f.U * (f.Nu * f.Cu * 0.5f) + f.V * (f.Nv * f.Cv * 0.5f);
                byte owner = paint.SampleWorld(f.Id, p);
                if (owner == 0 || owner - 1 != _a.Team) continue;     // our own ink: refill here
                _goal = p;
                _hasGoal = true;
                return;
            }
            _hasGoal = false;
        }

        void PickRetreat()
        {
            _repath = 0.7f;
            if (_target != null)
            {
                var away = _a.Pos - _target.Pos;
                away.y = 0f;
                if (away.sqrMagnitude < 0.01f) away = Vector3.forward;
                _goal = _a.Pos + away.normalized * 12f;
            }
            else _hasGoal = false;
            _hasGoal = true;
        }

        // --------------------------------------------------------------------------------- the helpers
        float AimError => _diff != null ? _diff.Float("aimError", 0.03f) : 0.03f;
        float Reaction => _diff != null ? _diff.Float("reaction", 0.28f) : 0.28f;
        float FireDiscipline => _diff != null ? _diff.Float("fireDiscipline", 0.8f) : 0.8f;
        float Awareness => _diff != null ? _diff.Float("awareness", 21f) : 21f;
        float AimOmega => _diff != null ? _diff.Float("aimOmega", 13f) : 13f;

        /// The weapon's effective range (the config's own number, the way the web build reads it).
        static float Range(JObj w)
        {
            string kind = w.Str("kind");
            switch (kind)
            {
                case "charger": return Mathf.Lerp(w.Float("rangeMin", 11f), w.Float("rangeMax", 27f), 0.5f);
                case "spinner": return Mathf.Lerp(w.Float("rangeMin", 10f), w.Float("rangeMax", 21f), 0.5f);
                case "splatling": return w.Float("range", 15f);
                case "bow": return w.Float("range", 25f);
                case "blade": return w.Float("range", 6.5f);
                case "mitts": return w.Float("range", 5.3f);
                case "brolly": return w.Float("range", 7f);
                case "slosher": return w.Float("range", 9.5f);
                case "bucket": return w.Float("reach", 9.5f);
                case "roller": return 5.5f;
                case "brush": return 4f;
                case "twins": return w.Float("range", 11f);
                case "dualies": return w.Float("range", 11f);
                case "blaster": return w.Float("range", 10.5f);
            }
            return w.Float("range", 12.5f);
        }
    }
}
