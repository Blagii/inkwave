// The game data, read from the JSON the exporter writes (tools/unity-export/export-data.mjs).
//
// The Unity build does not carry its own copy of the maps or the tuning numbers: it reads the same data the web
// build uses, exported straight from the JavaScript sources. That is what keeps the two builds identical — a
// change in src/ lands in Unity on the next export, with no hand-editing and no drift.
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace INKWAVE.Data
{
    /// A thin typed accessor over a JSON object. The tuning data has a lot of optional, per-kind fields
    /// (each weapon brings its own numbers), so the port reads them by name instead of mirroring every shape.
    public class JObj
    {
        public readonly JObject Raw;
        public readonly string Path;

        public JObj(JObject raw, string path = "")
        {
            Raw = raw ?? new JObject();
            Path = path;
        }

        public JObj Child(string key)
        {
            var v = Raw[key];
            if (v is JObject o) return new JObj(o, Path + "." + key);
            if (v == null) return new JObj(new JObject(), Path + "." + key);
            return new JObj(new JObject(), Path + "." + key);
        }

        public bool Has(string key) => Raw[key] != null;

        public float Float(string key, float fallback = 0f)
        {
            var v = Raw[key];
            if (v == null) return fallback;
            if (v.Type == JTokenType.Float || v.Type == JTokenType.Integer) return v.Value<float>();
            if (v.Type == JTokenType.Boolean) return v.Value<bool>() ? 1f : 0f;
            return fallback;
        }

        public int Int(string key, int fallback = 0)
        {
            var v = Raw[key];
            if (v == null) return fallback;
            if (v.Type == JTokenType.Float || v.Type == JTokenType.Integer) return v.Value<int>();
            return fallback;
        }

        public bool Bool(string key, bool fallback = false)
        {
            var v = Raw[key];
            if (v == null) return fallback;
            if (v.Type == JTokenType.Boolean) return v.Value<bool>();
            if (v.Type == JTokenType.Integer) return v.Value<int>() != 0;
            if (v.Type == JTokenType.Float) return v.Value<float>() != 0f;
            return fallback;
        }

        public string Str(string key, string fallback = null)
        {
            var v = Raw[key];
            if (v == null || v.Type == JTokenType.Null) return fallback;
            return v.Value<string>();
        }

        public float[] Floats(string key)
        {
            var v = Raw[key] as JArray;
            if (v == null) return null;
            var arr = new float[v.Count];
            for (int i = 0; i < v.Count; i++) arr[i] = v[i].Value<float>();
            return arr;
        }

        public JObj[] Objects(string key)
        {
            var v = Raw[key] as JArray;
            if (v == null) return null;
            var arr = new JObj[v.Count];
            for (int i = 0; i < v.Count; i++) arr[i] = new JObj(v[i] as JObject ?? new JObject(), Path + "." + key + "[" + i + "]");
            return arr;
        }
    }

    // --------------------------------------------------------------------------------------- stages.json
    [Serializable]
    public class StageFile
    {
        public List<StageDef> stages = new List<StageDef>();
    }

    [Serializable]
    public class StageDef
    {
        public string id;
        public string name;
        public string blurb;
        public string theme;
        public Dictionary<string, string> times;
        public bool onlineOnly;
        public bool noBots;
        public bool noBoss;
        public BoundsDef bounds;
        public List<float[]> spawnPads = new List<float[]>();
        public float spawnBarrier;
        public IntroDef intro;
        public string water;
        public ZoneLayout zones;
        public Dictionary<string, ModeDef> modes = new Dictionary<string, ModeDef>();
    }

    [Serializable]
    public class ModeDef
    {
        public bool variant;
        public List<BlockDef> blocks = new List<BlockDef>();
        public List<JObj> props = new List<JObj>();
    }

    /// One piece of the arena: an axis-aligned box, a box turned about Y, or a ramp slab.
    /// (the three kinds the web build's level.js understands)
    [Serializable]
    public class BlockDef
    {
        public string kind;              // 'box' | 'obox' | 'ramp'
        public float[] min;              // box
        public float[] max;              // box
        public float[] center;           // obox
        public float[] size;             // obox
        public float rotY;               // obox (degrees)
        public float[] low;              // ramp
        public float[] high;             // ramp
        public float width;              // ramp
        public float thickness;          // ramp
        public string color;
        public int pattern;
        public string tag;
        public bool paint = true;
        public bool solid = true;
        public bool grate;
        public bool roof;
        public bool perch;
        public bool noNav;
        public bool rail;
        public bool hidden;
        public bool thin;
        public float[] bevel;
        public List<MuralDef> mural;
        public List<float[]> noPaint;
    }

    /// A mural panel painted onto one side of a block.
    [Serializable]
    public class MuralDef
    {
        public float[] n;   // the face normal this mural belongs to
        public int id;
    }

    [Serializable]
    public class BoundsDef
    {
        public float minX, maxX, minZ, maxZ;
    }

    [Serializable]
    public class IntroDef
    {
        public float[] from;
        public float[] lookFrom;
        public float toBack;
    }

    /// The zone layout of a stage (turf war "zones" mode): a centre area and one side area per team.
    [Serializable]
    public class ZoneLayout
    {
        public List<ZonePoly> center = new List<ZonePoly>();
        public ZonePoly side;
    }

    [Serializable]
    public class ZonePoly
    {
        public List<float[]> poly = new List<float[]>();
        public List<List<float[]>> polys = new List<List<float[]>>();
        public float y0;
        public float y1;
    }

    // --------------------------------------------------------------------------------------- config.json
    [Serializable]
    public class ConfigFile
    {
        public string version;
        public string title;
        public string subtitle;
        public List<JObj> teamPalettes = new List<JObj>();
        public JObj colorblindPalette;
        public List<string> teamNames = new List<string>();
        public JObj player;
        public Dictionary<string, JObj> weapons = new Dictionary<string, JObj>();
        public List<string> weaponOrder = new List<string>();
        public Dictionary<string, JObj> subs = new Dictionary<string, JObj>();
        public List<string> subOrder = new List<string>();
        public Dictionary<string, JObj> specials = new Dictionary<string, JObj>();
        public List<string> specialOrder = new List<string>();
        public JObj match;
        public JObj zones;
        public Dictionary<string, JObj> difficulty = new Dictionary<string, JObj>();
        public JObj progression;
        public JObj defaultSettings;
        public Dictionary<string, JObj> quality = new Dictionary<string, JObj>();
        public List<string> times = new List<string>();
        public List<JObj> maps = new List<JObj>();
    }

    // ----------------------------------------------------------------------------------------- i18n.json
    [Serializable]
    public class I18nFile
    {
        public List<JObj> languages = new List<JObj>();
        public Dictionary<string, string> en = new Dictionary<string, string>();
        public Dictionary<string, string> zh = new Dictionary<string, string>();
    }

    // --------------------------------------------------------------------------------------------- loader
    /// Loads the exported data once and hands it out. Everything else in the game reads it from here.
    public static class GameData
    {
        public static StageFile Stages { get; private set; }
        public static ConfigFile Config { get; private set; }
        public static I18nFile I18n { get; private set; }

        public static bool Loaded => Stages != null && Config != null;

        public static void Load()
        {
            if (Loaded) return;
            Stages = Read<StageFile>("INKWAVE/stages");
            Config = Read<ConfigFile>("INKWAVE/config");
            I18n = Read<I18nFile>("INKWAVE/i18n");
            int blocks = 0, props = 0;
            foreach (var s in Stages.stages)
                foreach (var m in s.modes.Values) { blocks += m.blocks.Count; props += m.props.Count; }
            Debug.Log($"[inkwave] data loaded: {Stages.stages.Count} stages · {blocks} blocks · {props} props · " +
                      $"{Config.weapons.Count} weapons");
        }

        static T Read<T>(string resourcePath) where T : class
        {
            var asset = Resources.Load<TextAsset>(resourcePath);
            if (asset == null)
            {
                Debug.LogError($"[inkwave] missing resource '{resourcePath}' — run " +
                               "node tools/unity-export/export-data.mjs");
                return Activator.CreateInstance<T>();
            }
            return JsonConvert.DeserializeObject<T>(asset.text);
        }

        public static StageDef Stage(string id)
        {
            Load();
            foreach (var s in Stages.stages) if (s.id == id) return s;
            return null;
        }

        public static JObj Weapon(string id)
        {
            Load();
            if (id == null) return null;
            JObj w;
            return Config.weapons.TryGetValue(id, out w) ? w : null;
        }

        public static JObj Sub(string id)
        {
            Load();
            if (id == null) return null;
            JObj w;
            return Config.subs.TryGetValue(id, out w) ? w : null;
        }

        public static JObj Special(string id)
        {
            Load();
            if (id == null) return null;
            JObj w;
            return Config.specials.TryGetValue(id, out w) ? w : null;
        }

        /// The player tuning (the web build's PLAYER object).
        public static JObj P
        {
            get { Load(); return Config.player; }
        }
    }
}
