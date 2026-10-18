#!/usr/bin/env python3
"""A loose member check for the Unity C# sources.

The port is written in a sandbox without a C# compiler, so this catches the typos the Unity compiler would report
as CS0117 / CS1061: it collects every member the project declares (fields, methods, properties) with a tree-sitter
parse and then flags the member accesses whose name is declared nowhere in the project and is not a known
Unity / .NET member.

It is a heuristic (it does not know the types), so it only reports the names that are unknown everywhere.

    pip install tree_sitter tree_sitter_c_sharp
    python3 tools/unity-export/check-members.py
"""
import os
import re
import sys

try:
    from tree_sitter import Language, Parser
    import tree_sitter_c_sharp
except ImportError:
    print("· the member check is skipped (pip install tree_sitter tree_sitter_c_sharp)")
    sys.exit(0)

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
FOLDER = os.path.join(ROOT, "unity", "INKWAVE", "Assets", "INKWAVE")

try:
    LANG = Language(tree_sitter_c_sharp.language())
except AttributeError:
    LANG = tree_sitter_c_sharp.language()
PARSER = Parser(LANG)

# members of the Unity engine and the .NET library that the port uses
KNOWN = set("""
magnitude normalized sqrMagnitude x y z w r g b a
Count Length Capacity Key Value Values Item
ToString Format Parse TryParse GetType Equals GetHashCode
Add Remove Clear Contains ContainsKey TryGetValue TryAdd AddRange
Substring Split Trim TrimStart TrimEnd ToLower ToUpper IndexOf Replace
StartsWith EndsWith Join
Min Max Clamp Clamp01 ClampMagnitude Lerp LerpUnclamped MoveTowards SmoothStep Sign Abs
Sqrt Pow Exp Log Log10 Sin Cos Tan Asin Acos Atan Atan2 Floor Ceil Round FloorToInt CeilToInt RoundToInt
DeltaAngle Repeat PI PI2 Deg2Rad Rad2Deg Epsilon Infinity NaN
Approximately
Vector2 Vector3 Vector4 Quaternion Color Color32 Matrix4x4 Bounds Rect
zero one up down left right forward back
Dot Cross Normalize Distance Angle Slerp LookRotation Euler AngleAxis
Random value Range InsideUnitSphere OnUnitSphere
Instantiate Destroy DontDestroyOnLoad CreatePrimitive GetComponent AddComponent
GetComponentsInChildren GetComponentsInParent GetComponentInChildren GetComponentInParent
SetActive activeSelf activeInHierarchy transform gameObject name tag layer
enabled isPlaying playOnAwake loop volume pitch priority pan spatialBlend rolloffMode
minDistance maxDistance dopplerLevel outputAudioMixerGroup
clip samples channels frequency SetData Create
position localPosition localScale rotation localRotation eulerAngles lossyPosition
parent childCount GetChild SetParent
lightmapBakeType fog
Func Action Math Activator Convert Directory Path PathCombine File
CreateAsset CreateDirectory FindAssets LoadAssetAtPath DeleteAsset
DestroyImmediate SaveScene NewScene NewSceneSetup EditorSceneManager
BuildPipeline BuildPlayerOptions BuildPlayer BuildTarget BuildSummary
AssetDatabase Shader Material Texture2D RenderTexture RenderTextureFormat Graphics
GUI GUIStyle GUISkin GUILayout GUIUtility Event DrawTexture DrawTextureWithTexCoords
Texture Rect Vector2 Color Screen Width Height
Physics Capsule Sphere Box Ray Plane
Debug Log LogError LogWarning
Time Input Axis KeyCode GetAxis GetButton GetKey
JObject JArray JToken JTokenType JsonConvert JsonProperty
Null Value Kind Properties PropertyName
SerializeField HideInInspector Range Tooltip Header Space

ARGB32 ARGBHalf R8G8B8A8 ARGBFloat DefaultFormat Depth AutoGenerateMips
Boolean Integer Float String Array Object Null
Cube Sphere Capsule Cylinder Plane Quad
Directional Spot Point Area
MiddleCenter MiddleLeft MiddleRight UpperLeft UpperCenter UpperRight LowerLeft LowerCenter LowerRight
Label Box Button TextField Window
Combine GetCurrentDirectory GetDirectoryName GetFileNameWithoutExtension GetFullPath ChangeExtension
CreateInstance DeserializeObject SerializeObject
Find FindAssets GUIDToAssetPath CreateAsset CreateDirectory DeleteAsset LoadAssetAtPath SaveAssets Refresh InstantiatePrefab
HasProperty HasValue GetFloat GetInt GetString GetProperty GetValue TryGetValue SetValue
InverseTransformDirection TransformDirection LocalToWorldMatrix
IsNegativeInfinity IsPositiveInfinity IsNaN IsInfinity NegativeInfinity PositiveInfinity MaxValue MinValue
Item1 Item2 Item3 Key1 Key2 Key3
NextDouble Next Int64 MaxValue
GetOrAdd TryAdd RemoveWhere
ARGB

EDITOR aKey alignment buttonSouth buttonWest cKey clearFlags current dKey delta deltaTime downArrowKey dpad
eKey fKey farClipPlane fieldOfView fontSize identity intensity leftArrowKey leftButton leftShiftKey leftStick
leftTrigger mKey main nearClipPlane normal qKey rightArrowKey rightButton rightShiftKey rightShoulder rightStick
rightStickButton rightTrigger sKey selectButton shadow shadowCastingMode shadows sharedMaterial spaceKey tabKey
text type upArrowKey wKey whiteTexture

Audio Camera Collections Core Editor EmptyScene Gameplay IO InputSystem Inputs IsNullOrEmpty Json
None RecalculateBounds RemoveAt Render Rendering SaveAsPrefabAsset SceneManagement
SetColor SetDirty SetFloat SetMotorSpeeds SetNormals SetTexture SetTriangles SetUVs SetVector SetVertices
Single Skybox Sort StandaloneLinux64 StandaloneOSX StandaloneWindows64 ToArray Type World WorldToScreenPoint
""".split())

def collect(tree, src):
    """Every member name the project declares."""
    names = set()
    stack = [tree.root_node]
    while stack:
        node = stack.pop()
        t = node.type
        if t == "variable_declarator":
            for c in node.children:
                if c.type == "identifier":
                    names.add(c.text.decode("utf-8"))
        elif t in ("method_declaration", "property_declaration", "event_declaration", "constructor_declaration",
                   "indexer_declaration", "delegate_declaration", "enum_member_declaration", "operator_declaration"):
            for c in node.children:
                if c.type == "identifier":
                    names.add(c.text.decode("utf-8"))
        elif t in ("enum_declaration", "class_declaration", "struct_declaration", "interface_declaration",
                   "record_declaration", "namespace_declaration", "file_scoped_namespace_declaration"):
            for c in node.children:
                if c.type == "identifier":
                    names.add(c.text.decode("utf-8"))
        stack.extend(node.children)
    return names

files = []
for base, _dirs, names in os.walk(FOLDER):
    for n in names:
        if n.endswith(".cs"):
            files.append(os.path.join(base, n))
files.sort()

declared = set()
texts = {}
for p in files:
    with open(p, "rb") as f:
        src = f.read()
    texts[p] = src
    declared |= collect(PARSER.parse(src), src)

def strip_code(src):
    """Drop the comments and the string literals (they only add noise)."""
    out = bytearray(src)
    i = 0
    n = len(src)
    while i < n:
        c = src[i:i+1]
        if c == b"/" and src[i:i+2] == b"//":
            while i < n and src[i:i+1] != b"\n":
                out[i] = 32; i += 1
        elif c == b"/" and src[i:i+2] == b"/*":
            while i < n and src[i:i+2] != b"*/":
                out[i] = 32; i += 1
            out[i] = 32
            if i + 1 < n: out[i+1] = 32
            i += 2
        elif c == b'"':
            out[i] = 32; i += 1
            while i < n and src[i:i+1] != b'"':
                if src[i:i+1] == b"\\": out[i] = 32; i += 1
                if i < n: out[i] = 32
                i += 1
            if i < n: out[i] = 32
            i += 1
        else:
            i += 1
    return bytes(out)

ACCESS = re.compile(rb"(?<![\w\.])([A-Za-z_]\w*)\s*\.\s*([A-Za-z_]\w*)")
unknown = {}
for p, src in texts.items():
    src = strip_code(src)
    for m in ACCESS.finditer(src):
        mem = m.group(2).decode("utf-8")
        if mem in declared or mem in KNOWN:
            continue
        line = src[:m.start()].count(b"\n") + 1
        unknown.setdefault(mem, []).append((os.path.relpath(p, ROOT), line, m.group(1).decode("utf-8")))

if not unknown:
    print("✓ the member check is clean — every member access resolves to something the project declares")
    sys.exit(0)

print("· the member check found these unknown members (check them by hand):")
for mem in sorted(unknown):
    where = unknown[mem][0]
    extra = f" (+{len(unknown[mem]) - 1} more)" if len(unknown[mem]) > 1 else ""
    print(f"  ✗ .{mem}  — {where[0]}:{where[1]}  (…{where[2]}){extra}")
sys.exit(1)
