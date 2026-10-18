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
