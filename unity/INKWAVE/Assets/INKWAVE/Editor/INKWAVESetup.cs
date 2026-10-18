// Editor helpers: build the playable scene and the materials, so the project can be set up in one click.
//
//   Tools → INKWAVE → Build Play Scene      (a new scene with the game, the camera rig and the light)
//   Tools → INKWAVE → Build Materials       (the ink surface + the splat material)
using System.IO;
using INKWAVE.Core;
using INKWAVE.Inputs;
using INKWAVE.Render;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace INKWAVE.EDITOR
{
    public static class INKWAVESetup
    {
        const string ScenePath = "Assets/INKWAVE/Scenes/Play.unity";
        const string MatDir = "Assets/INKWAVE/Materials";
        const string PropDir = "Assets/INKWAVE/Props";
        const string PropPrefabDir = "Assets/Resources/INKWAVE/Props";

        [MenuItem("Tools/INKWAVE/Build Materials")]
        public static void BuildMaterials()
        {
            Directory.CreateDirectory(MatDir);
            MakeMaterial("INKWAVE/InkSurface", "Assets/INKWAVE/Materials/InkSurface.mat");
            MakeMaterial("INKWAVE/InkSplat", "Assets/INKWAVE/Materials/InkSplat.mat");
            AssetDatabase.SaveAssets();
            Debug.Log("[inkwave] the materials are ready");
        }

        static void MakeMaterial(string shaderName, string path)
        {
            var shader = Shader.Find(shaderName);
            if (shader == null) { Debug.LogError($"[inkwave] the shader '{shaderName}' is missing"); return; }
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            else mat.shader = shader;
            EditorUtility.SetDirty(mat);
        }

        [MenuItem("Tools/INKWAVE/Build Prop Prefabs")]
        public static void BuildPropPrefabs()
        {
            // The baked glTF files (npm run unity:bake) become prefabs under Resources so the game can load them.
            Directory.CreateDirectory(PropPrefabDir);
            var guids = AssetDatabase.FindAssets("t:Model", new[] { PropDir });
            int made = 0;
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".glb")) continue;
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (model == null) continue;
                var name = Path.GetFileNameWithoutExtension(path);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{PropPrefabDir}/{name}.prefab");
                if (prefab == null)
                {
                    prefab = PrefabUtility.InstantiatePrefab(model) as GameObject;
                    ApplyPropMaterials(prefab);
                    PrefabUtility.SaveAsPrefabAsset(prefab, $"{PropPrefabDir}/{name}.prefab");
                    Object.DestroyImmediate(prefab);
                }
                made++;
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[inkwave] {made} prop prefabs are ready");
        }

        /// The prop colours live in the vertex colours, so every material is the same shader with its own
        /// roughness / metalness (the values the web build's prop materials use).
        static void ApplyPropMaterials(GameObject root)
        {
            var shader = Shader.Find("INKWAVE/VertexColorLit");
            if (shader == null) { Debug.LogError("[inkwave] the prop shader is missing"); return; }
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var src = r.sharedMaterial;
                var mat = new Material(shader);
                if (src != null)
                {
                    mat.name = src.name;
                    if (src.HasProperty("_Metallic")) mat.SetFloat("_Metallic", src.GetFloat("_Metallic"));
                    if (src.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", src.GetFloat("_Smoothness"));
                    else if (src.HasProperty("_Roughness")) mat.SetFloat("_Smoothness", 1f - src.GetFloat("_Roughness"));
                }
                r.sharedMaterial = mat;
            }
        }

        [MenuItem("Tools/INKWAVE/Build Play Scene")]
        public static void BuildScene()
        {
            BuildMaterials();
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // the camera + the rig
            var camGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            camGo.tag = "MainCamera";
            var cam = camGo.GetComponent<Camera>();
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.fieldOfView = 70f;
            cam.nearClipPlane = 0.15f;
            cam.farClipPlane = 650f;
            var rig = camGo.AddComponent<CameraRig>();
            rig.Cam = cam;

            // the light
            var lightGo = new GameObject("Directional Light", typeof(Light));
            var light = lightGo.GetComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            light.shadows = LightShadows.Soft;
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            // the game
            var gameGo = new GameObject("INKWAVE", typeof(Game), typeof(PlayerInput));
            var game = gameGo.GetComponent<Game>();
            game.Rig = rig;
            var levelViewGo = new GameObject("LevelView", typeof(LevelRenderer));
            var levelView = levelViewGo.GetComponent<LevelRenderer>();
            levelView.SurfaceMaterial = AssetDatabase.LoadAssetAtPath<Material>(MatDir + "/InkSurface.mat");
            levelView.transform.SetParent(gameGo.transform, false);
            game.LevelView = levelView;

            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log($"[inkwave] the scene is ready: {ScenePath}");
        }
    }
}
