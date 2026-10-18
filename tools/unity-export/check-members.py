#!/usr/bin/env python3
"""A member check for the Unity C# sources.

The port is written in a sandbox without a C# compiler, so this catches the typos the Unity compiler would report
as CS0117 / CS1061: it collects every member the project declares (fields, methods, properties) with a tree-sitter
parse and then flags the member accesses whose name is declared nowhere in the project and is not a known
Unity / .NET member.

When the type of the receiver is known (a project class, a local, a field), the check is stricter: the member has
to exist **on that type** (or be a MonoBehaviour member). That is what catches the mistakes a name-only check
cannot see, e.g. reading a field of a delegate.

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
NextDouble Next Int64
GetOrAdd TryAdd RemoveWhere
ARGB
Audio Camera Collections Core Editor EmptyScene Gameplay IO InputSystem Inputs IsNullOrEmpty Json
None RecalculateBounds RemoveAt Render Rendering SaveAsPrefabAsset SceneManagement
SetColor SetDirty SetFloat SetMotorSpeeds SetNormals SetTexture SetTriangles SetUVs SetVector SetVertices
Single Skybox Sort StandaloneLinux64 StandaloneOSX StandaloneWindows64 ToArray Type World WorldToScreenPoint
EDITOR aKey alignment buttonSouth buttonWest cKey clearFlags current dKey delta deltaTime downArrowKey dpad
eKey fKey farClipPlane fieldOfView fontSize identity intensity leftArrowKey leftButton leftShiftKey leftStick
leftTrigger mKey main nearClipPlane normal qKey rightArrowKey rightButton rightShiftKey rightShoulder rightStick
rightStickButton rightTrigger sKey selectButton shadow shadowCastingMode shadows sharedMaterial spaceKey tabKey
text type upArrowKey wKey whiteTexture
h height shader skin white red green blue black yellow cyan magenta gray grey
""".split())

# the members every MonoBehaviour has
MONO = set("""
ToString Equals GetHashCode GetType
transform gameObject name tag hideFlags enabled isActiveAndEnabled useGUILayout runInEditMode
GetComponent GetComponentInChildren GetComponentInParent GetComponents GetComponentsInChildren
GetComponentsInParent AddComponent TryGetComponent GetComponentInParent
StartCoroutine StopCoroutine StartCoroutineCoroutine Invoke CancelInvoke IsInvoking
GetInstanceID Destroy DontDestroyOnLoad Instantiate FindObjectOfType FindObjectsOfType FindAnyObjectByType
CompareTag SetActive SendMessage BroadcastMessage
""".split())

# the types of the .NET library: a member access on one of them has to be in the KNOWN list
BCL = set("""
MatchState ActorForm Kind
string int float double bool long short byte char decimal object
List Dictionary ArrayList HashSet SortedList Queue Stack
Func Action Predicate Comparison IEnumerable IEnumerator StringBuilder
Math Convert Random Guid DateTime TimeSpan Version Exception
""".split())


def text(node, src):
    return src[node.start_byte:node.end_byte].decode("utf-8")


def walk(node):
    stack = [node]
    while stack:
        n = stack.pop()
        yield n
        stack.extend(reversed(n.children))


# ---- the classes: their members and their fields ---------------------------------------------

classes = {}      # class -> {"members": set, "fields": {name: type}, "methods": {name: [return types]}}
files = []
for base, _dirs, names in os.walk(FOLDER):
    for n in names:
        if n.endswith(".cs"):
            files.append(os.path.join(base, n))
files.sort()



def enum_members(node):
    """The names of the members of an enum (they live in a list node)."""
    names = set()
    for child in walk(node):
        if child.type == "enum_member_declaration":
            for c in child.children:
                if c.type == "identifier":
                    names.add(c.text.decode("utf-8"))
    return names


def class_of(node):
    parent = node.parent
    while parent is not None:
        if parent.type in ("class_declaration", "struct_declaration"):
            for c in parent.children:
                if c.type == "identifier":
                    return c.text.decode("utf-8")
        parent = parent.parent
    return None


for p in files:
    with open(p, "rb") as f:
        src = f.read()
    tree = PARSER.parse(src)
    # the enums at the namespace level (their members are used as Enum.Member)
    for node in walk(tree.root_node):
        if node.type != "enum_declaration":
            continue
        if class_of(node) is not None:
            continue
        ename = None
        for c in node.children:
            if c.type == "identifier" and ename is None:
                ename = c.text.decode("utf-8")
        if ename is None:
            continue
        members = enum_members(node)
        info = classes.setdefault(ename, {"members": set(), "fields": {}, "methods": {}})
        info["members"] |= members
    for node in walk(tree.root_node):
        if node.type not in ("class_declaration", "struct_declaration"):
            continue
        name = None
        body = None
        for c in node.children:
            if c.type == "identifier" and name is None:
                name = c.text.decode("utf-8")
            if c.type == "declaration_list":
                body = c
        if not name or body is None:
            continue
        info = {"members": set(), "fields": {}, "methods": {}}
        for m in body.children:
            if m.type == "method_declaration":
                kids = list(m.children)
                plist_at = next((i for i, c in enumerate(kids) if c.type == "parameter_list"), -1)
                if plist_at <= 0 or kids[plist_at - 1].type != "identifier":
                    continue
                mname = kids[plist_at - 1].text.decode("utf-8")
                rtype = None
                for c in kids[:plist_at]:
                    if c.type in ("identifier", "predefined_type", "nullable_type", "array_type", "generic_type",
                                  "type", "scoped_type", "qualified_name", "void"):
                        rtype = text(c, src).strip()
                        break
                info["members"].add(mname)
                info["methods"].setdefault(mname, []).append(rtype or "?")
            elif m.type in ("field_declaration", "property_declaration", "event_declaration",
                            "constructor_declaration", "indexer_declaration", "enum_declaration"):
                ftype = None
                for c in m.children:
                    if c.type in ("identifier", "predefined_type", "nullable_type", "array_type", "generic_type",
                                  "type", "scoped_type", "qualified_name"):
                        ftype = text(c, src).strip()
                        break
                for c in m.children:
                    if c.type == "variable_declaration":
                        for d in c.children:
                            if d.type == "variable_declarator":
                                for g in d.children:
                                    if g.type == "identifier":
                                        info["members"].add(g.text.decode("utf-8"))
                                        info["fields"][g.text.decode("utf-8")] = ftype
                    elif c.type == "identifier":
                        info["members"].add(c.text.decode("utf-8"))
                    elif c.type == "variable_declaration":
                        pass
                for c in m.children:
                    if c.type == "identifier" and m.type == "property_declaration":
                        info["members"].add(c.text.decode("utf-8"))
        # the nested types + the enum members
        for c in body.children:
            if c.type in ("class_declaration", "struct_declaration", "enum_declaration", "interface_declaration"):
                for d in c.children:
                    if d.type == "identifier":
                        info["members"].add(d.text.decode("utf-8"))
            if c.type == "enum_declaration":
                info["members"] |= enum_members(c)
        if name in classes:                       # a partial class: merge the members
            old_info = classes[name]
            old_info["members"] |= info["members"]
            old_info["fields"].update(info["fields"])
            for k, v in info["methods"].items():
                old_info["methods"].setdefault(k, []).extend(v)
        else:
            classes[name] = info

# ---- the scopes ------------------------------------------------------------------------------

def strip_code(src):
    """Drop the comments and the string literals (they only add noise)."""
    out = bytearray(src)
    i = 0
    n = len(src)
    while i < n:
        c = src[i:i + 1]
        if src[i:i + 2] == b"//":
            while i < n and src[i:i + 1] != b"\n":
                out[i] = 32
                i += 1
        elif src[i:i + 2] == b"/*":
            while i < n and src[i:i + 2] != b"*/":
                out[i] = 32
                i += 1
            out[i] = 32
            if i + 1 < n:
                out[i + 1] = 32
            i += 2
        elif c == b'"':
            out[i] = 32
            i += 1
            while i < n and src[i:i + 1] != b'"':
                if src[i:i + 1] == b"\\":
                    out[i] = 32
                    i += 1
                if i < n:
                    out[i] = 32
                i += 1
            if i < n:
                out[i] = 32
            i += 1
        else:
            i += 1
    return bytes(out)


def base_type(t):
    if not t:
        return None
    t = t.strip()
    if t.endswith("[]"):
        return base_type(t[:-2])
    if "<" in t:
        t = t.split("<", 1)[0]
    if "." in t:
        t = t.split(".")[-1]
    return t


def build_scopes(src, tree, own_classes):
    """The base scope (the fields) and one scope per method (the fields + the parameters + the locals)."""
    base = {}
    for node in walk(tree.root_node):
        if node.type not in ("class_declaration", "struct_declaration"):
            continue
        for m in node.children:
            if m.type != "declaration_list":
                continue
            for d in m.children:
                if d.type != "field_declaration":
                    continue
                ftype = None
                for c in d.children:
                    if c.type in ("identifier", "predefined_type", "nullable_type", "array_type", "generic_type",
                                  "type", "scoped_type", "qualified_name"):
                        ftype = text(c, src).strip()
                        break
                for c in d.children:
                    if c.type == "variable_declaration":
                        for vd in c.children:
                            if vd.type == "variable_declarator":
                                for idn in vd.children:
                                    if idn.type == "identifier":
                                        base[idn.text.decode("utf-8")] = ftype
    methods = []
    for node in walk(tree.root_node):
        if node.type != "method_declaration":
            continue
        scope = dict(base)
        for child in walk(node):
            if child.type == "parameter":
                ptype = None
                pname = None
                for g in child.children:
                    if g.type in ("identifier", "predefined_type", "nullable_type", "array_type", "generic_type",
                                  "type", "scoped_type", "qualified_name"):
                        if ptype is None:
                            ptype = text(g, src).strip()
                        else:
                            pname = g.text.decode("utf-8")
                if pname and ptype:
                    scope[pname] = ptype
            elif child.type == "variable_declaration":
                vtype = None
                for g in child.children:
                    if g.type == "variable_declarator":
                        continue
                    if g.type in ("identifier", "predefined_type", "nullable_type", "array_type", "generic_type",
                                  "type", "scoped_type", "qualified_name"):
                        vtype = text(g, src).strip()
                        break
                for g in child.children:
                    if g.type != "variable_declarator":
                        continue
                    vname = None
                    value = None
                    for h in g.children:
                        if h.type == "identifier":
                            if vname is None:
                                vname = h.text.decode("utf-8")
                            else:
                                value = h
                        elif h.type != "=" and value is None and vname is not None:
                            value = h
                    if not vname:
                        continue
                    if vtype and vtype != "var":
                        scope[vname] = vtype
                    else:
                        scope[vname] = infer(value, src, scope, own_classes)
        methods.append((node, scope))
    return base, methods


def infer(node, src, scope, own_classes):
    """The type of an expression, when it is obvious."""
    if node is None:
        return None
    t = node.type
    if t == "object_creation_expression":
        for c in node.children:
            if c.type == "identifier":
                return c.text.decode("utf-8")
        return None
    if t == "identifier":
        name = node.text.decode("utf-8")
        if name in own_classes:
            return name
        return scope.get(name)
    if t == "member_access_expression":
        recv = node.children[0]
        mem = node.children[-1].text.decode("utf-8")
        rtype = resolve(recv, src, scope, own_classes)
        if rtype in own_classes:
            return own_classes[rtype]["fields"].get(mem)
        return None
    if t == "invocation_expression":
        fn = None
        for c in node.children:
            if c.type in ("member_access_expression", "identifier"):
                fn = c
        if fn is None:
            return None
        if fn.type == "identifier":
            name = fn.text.decode("utf-8")
            for info in own_classes.values():
                if name in info["methods"]:
                    return info["methods"][name][0]
            return None
        recv = fn.children[0]
        mem = fn.children[-1].text.decode("utf-8")
        rtype = resolve(recv, src, scope, own_classes)
        if rtype in own_classes:
            rts = own_classes[rtype]["methods"].get(mem)
            return rts[0] if rts else None
        return None
    if t == "cast_expression":
        for c in node.children:
            if c.type in ("identifier", "predefined_type", "nullable_type", "array_type", "generic_type",
                          "type", "scoped_type", "qualified_name"):
                return text(c, src).strip()
    return None


def resolve(node, src, scope, own_classes):
    """The type of a receiver expression, when it is obvious."""
    if node.type == "identifier":
        name = node.text.decode("utf-8")
        if name in own_classes:
            return name
        return scope.get(name)
    if node.type == "member_access_expression":
        obj = node.children[0]
        mem = node.children[-1].text.decode("utf-8")
        base = resolve(obj, src, scope, own_classes)
        if base in own_classes:
            return own_classes[base]["fields"].get(mem)
    return None


DECLARED = set(classes.keys())
for _info in classes.values():
    DECLARED |= _info["members"]
# the names of the tuple elements (a named tuple: (float f, float h))
for _p in files:
    with open(_p, "rb") as _f:
        _src = _f.read()
    for _node in walk(PARSER.parse(_src).root_node):
        if _node.type == "tuple_type":
            for _c in walk(_node):
                if _c.type == "identifier":
                    DECLARED.add(_c.text.decode("utf-8"))


# ---- the check -------------------------------------------------------------------------------

ACCESS = re.compile(r"(?<![\w\.])([A-Za-z_]\w*)\s*\.\s*([A-Za-z_]\w*)".encode())
unknown = {}
typed = {}

for p in files:
    with open(p, "rb") as f:
        raw = f.read()
    src = strip_code(raw)
    tree = PARSER.parse(src)
    base, methods = build_scopes(src, tree, classes)
    rel = os.path.relpath(p, ROOT)
    for m in ACCESS.finditer(src):
        mem = m.group(2).decode("utf-8")
        recv = m.group(1).decode("utf-8")
        line = src[:m.start()].count(b"\n") + 1
        scope = base
        for mnode, mscope in methods:
            if mnode.start_byte <= m.start() < mnode.end_byte:
                scope = mscope
                break
        rtype = scope.get(recv)
        if rtype is not None:
            bt = base_type(rtype)
            if bt in classes:
                if mem in classes[bt]["members"]:
                    continue
                if mem in MONO:
                    continue
                typed.setdefault(f"{rel}:{line}", []).append(
                    f"the member '.{mem}' does not exist on '{bt}' (a {recv})")
                continue
            if bt in BCL:
                if mem in KNOWN:
                    continue
                typed.setdefault(f"{rel}:{line}", []).append(
                    f"the member '.{mem}' does not exist on '{bt}' (a {recv})")
                continue
        if mem in KNOWN or mem in DECLARED:
            continue
        # the name is declared nowhere in the project
        unknown.setdefault(mem, []).append((rel, line, recv))

if not unknown and not typed:
    print(f"✓ the member check is clean — {len(classes)} classes, {len(files)} files")
    sys.exit(0)

if unknown:
    print("· the member check found these unknown members (check them by hand):")
    for mem in sorted(unknown):
        rel, line, recv = unknown[mem][0]
        extra = f" (+{len(unknown[mem]) - 1} more)" if len(unknown[mem]) > 1 else ""
        print(f"  ✗ .{mem}  — {rel}:{line}  (…{recv}){extra}")
if typed:
    print("· the member check found these members that are not on their type:")
    for where in sorted(typed):
        for msg in typed[where]:
            print(f"  ✗ {where}: {msg}")
sys.exit(1)
