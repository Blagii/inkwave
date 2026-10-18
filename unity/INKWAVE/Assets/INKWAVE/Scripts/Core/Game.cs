// The game bootstrap: loads the data, builds the stage, runs the match loop.
//
// This is the Unity counterpart of the web build's src/main.js — same order of operations, same rules, same feel.
using System.Collections.Generic;
using INKWAVE.Data;
using INKWAVE.Gameplay;
using INKWAVE.Inputs;
using INKWAVE.Render;
using UnityEngine;

namespace INKWAVE.Core
{
    public class Game : MonoBehaviour
    {
        public string StageId = "tidewater";
        public string ModeId = "turf";
        public float MatchDuration = 180f;

        public CameraRig Rig;
        public LevelRenderer LevelView;
        public Material ActorMaterial;

        Match _match;
        PlayerInput _input;
        float _lastTime;

        void Awake()
        {
            GameData.Load();
            GameContext.LoadStage(StageId, ModeId);
            if (LevelView != null) LevelView.Build(GameContext.Level, GameContext.Paint);
            LoadProps();

            _input = GetComponent<PlayerInput>();
            _match = new Match();
            _match.Setup(ModeId, MatchDuration);
            _match.Start();

            foreach (var a in _match.Actors)
            {
                var ar = new GameObject("Actor_" + a.Name).AddComponent<ActorRenderer>();
                ar.Actor = a;
                ar.Material = ActorMaterial;
                ar.transform.SetParent(transform, false);
            }
            if (Rig != null && _match.Local != null) Rig.SetTarget(_match.Local);

            // the HUD
            var hudGo = new GameObject("Hud");
            hudGo.transform.SetParent(transform, false);
            var hud = hudGo.AddComponent<Hud>();
            hud.Match = _match;
            hud.Local = _match.Local;
        }

        /// The baked set dressing (npm run unity:bake → Tools → INKWAVE → Build Prop Prefabs).
        void LoadProps()
        {
            var prefab = Resources.Load<GameObject>($"INKWAVE/Props/{StageId}-{ModeId}");
            if (prefab == null)
            {
                Debug.LogWarning($"[inkwave] no prop prefab for '{StageId}-{ModeId}' — run `npm run unity:bake` " +
                                 "and then Tools → INKWAVE → Build Prop Prefabs");
                return;
            }
            var go = Instantiate(prefab);
            go.name = "Props";
            go.transform.SetParent(transform, false);
        }

        void Update()
        {
            float dt = Mathf.Min(0.05f, Time.deltaTime);
            GameContext.DeltaTime = dt;
            GameContext.Time += dt;

            // ---- the local player's intent
            if (_match.Local != null && _input != null && _match.Playing())
            {
                var a = _match.Local;
                a.Intent.Move = _input.Move;
                a.Intent.Fire = _input.Fire;
                a.Intent.Sub = _input.Sub;
                a.Intent.Jump = _input.Jump;
                a.Intent.Squid = _input.Squid;
                a.Intent.Special = _input.Special;
                if (Rig != null)
                {
                    Rig.ApplyLook(_input.Look, dt);
                    a.AimYaw = Rig.Yaw;
                    a.AimPitch = Rig.Pitch;
                    a.AimPoint = Rig.AimPoint;
                }
            }

            _match.Update(dt);
            Projectiles.Update(dt);
            if (GameContext.Paint != null) GameContext.Paint.StepFloods(dt);
        }
    }
}
