// The global game context — the Unity counterpart of the web build's src/core/ctx.js (the `G` object plus its
// event bus). Everything that needs the level, the physics or the match asks for it here.
using System;
using System.Collections.Generic;
using INKWAVE.Data;
using INKWAVE.World;
using UnityEngine;
using InkPhysics = INKWAVE.World.Physics;   // UnityEngine.Physics would be ambiguous

namespace INKWAVE.Core
{
    public class GameEvent
    {
        public string Name;
        public object Data;
    }

    public static class GameContext
    {
        public static Level Level;
        public static InkPhysics Physics;
        public static Gameplay.PaintSystem Paint;
        public static Gameplay.Match Match;
        public static Gameplay.WeaponRunner Weapons;

        public static float Time;              // seconds since the match started
        public static float DeltaTime = 1f / 60f;
        public static string StageId = "tidewater";
        public static string ModeId = "turf";
        public static Data.ZoneLayout StageZones;   // the zone layout of the loaded stage (Zone Control)

        static readonly Dictionary<string, Action<object>> _handlers = new Dictionary<string, Action<object>>();

        /// The team colours (from the palette the player picked).
        public static readonly Color[] TeamColors = { new Color(1f, 0.541f, 0.078f), new Color(0.184f, 0.357f, 1f) };

        public static Color TeamColor(int team) => TeamColors[team < 0 ? 0 : (team > 1 ? 1 : team)];

        public static void On(string name, Action<object> handler)
        {
            _handlers[name] = handler;
        }

        public static void Emit(string name, object data = null)
        {
            Action<object> h;
            if (_handlers.TryGetValue(name, out h)) h(data);
        }

        /// Load a stage + mode into the world.
        public static void LoadStage(string stageId, string modeId)
        {
            GameData.Load();
            var stage = GameData.Stage(stageId);
            if (stage == null) { Debug.LogError($"[inkwave] unknown stage '{stageId}'"); return; }
            StageId = stageId;
            ModeId = modeId;
            var lvl = Level;
            if (lvl == null) { lvl = new Level(); Level = lvl; }
            lvl.Build(stage, stage.modes.TryGetValue(modeId, out var mode) ? mode : stage.modes["turf"]);
            StageZones = stage.zones;
            Physics = new InkPhysics(lvl);
            Paint = new Gameplay.PaintSystem(lvl, Physics);
            Debug.Log($"[inkwave] stage '{stageId}' ({modeId}): {lvl.Blocks.Count} blocks · {lvl.Faces.Count} faces");
        }
    }
}
