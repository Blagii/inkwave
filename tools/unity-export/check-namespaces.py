#!/usr/bin/env python3
"""A namespace check for the Unity C# sources.

A missing or misspelled `using` is a CS0246 / CS0103 that the Unity compiler reports, but the sandbox has no C#
compiler. This verifies that

  * every `using` of a project namespace points at a namespace the project really declares,
  * every qualified `INKWAVE.X.Y` reference points at a namespace the project really declares,
  * every file that reaches into a known external namespace has the matching `using`,
  * every project type a file uses is reachable: its namespace is imported, or the file lives in it.

    python3 tools/unity-export/check-namespaces.py
"""
import os
import re
import sys

try:
    from tree_sitter import Language, Parser
    import tree_sitter_c_sharp
except ImportError:
    print("· the namespace check is skipped (pip install tree_sitter tree_sitter_c_sharp)")
    sys.exit(0)

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
FOLDER = os.path.join(ROOT, "unity", "INKWAVE", "Assets", "INKWAVE")

try:
    LANG = Language(tree_sitter_c_sharp.language())
except AttributeError:
    LANG = tree_sitter_c_sharp.language()
PARSER = Parser(LANG)

# the types of the Unity engine and the .NET library that the port imports (the ambiguous names)
EXTERNAL_TYPES = {
    "UnityEngine": ["Physics", "Random", "Object", "Debug", "Time", "Input", "Resources", "Application",
                    "Quality", "Layer", "Collider", "Animation", "Event", "Screen", "Material", "Shader",
                    "Mesh", "Texture", "Texture2D", "RenderTexture", "Camera", "Light", "AudioSource",
                    "AudioClip", "GameObject", "Transform", "Vector2", "Vector3", "Vector4", "Color",
                    "Quaternion", "Bounds", "Rect", "Plane", "Ray", "Matrix4x4", "GUI", "Mathf", "Sprite",
                    "Font", "Coroutine", "WaitForSeconds"],
    "System": ["Random", "Object", "Math", "Action", "Func", "Array", "Console", "Environment", "Buffer",
               "Convert", "Uri", "Exception", "Type", "DateTime", "TimeSpan", "Version", "Enum", "Guid",
               "StringBuilder", "Attribute", "Index", "Range", "Span", "Text", "String"],
    "System.Collections.Generic": ["List", "Dictionary", "HashSet", "SortedSet", "Queue", "Stack",
                                   "IEnumerable", "IList", "ICollection", "IComparer", "EqualityComparer",
                                   "IReadOnlyList", "KeyValuePair"],
    "Newtonsoft.Json": ["JsonConvert", "JsonSerializer", "JsonProperty", "JsonObject", "JsonArray", "JsonReader"],
    "Newtonsoft.Json.Linq": ["JObject", "JArray", "JToken", "JValue", "JProperty", "JTokenType"],
    "UnityEngine.InputSystem": ["Keyboard", "Mouse", "Gamepad", "InputAction", "InputDevice"],
    "UnityEditor": ["AssetDatabase", "EditorUtility", "PrefabUtility", "EditorSceneManager", "Selection"],
}

# the namespaces the port may use from outside the project
EXTERNAL = {
    "System", "System.Collections.Generic", "System.IO", "System.Linq",
    "Newtonsoft.Json", "Newtonsoft.Json.Linq",
    "UnityEngine", "UnityEngine.Rendering", "UnityEngine.Rendering.Universal",
    "UnityEngine.InputSystem", "UnityEngine.InputSystem.Controls",
    "UnityEngine.SceneManagement", "UnityEditor", "UnityEditor.SceneManagement",
    "GLTFast", "TMPro",
}

NS = re.compile(r"^\s*namespace\s+([A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)", re.M)
USING = re.compile(r"^\s*using\s+([A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s*;", re.M)
QUAL = re.compile(r"\bINKWAVE\.[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*")
IDENT = re.compile(r"\b[A-Za-z_]\w*\b")


def text(node, src):
    return src[node.start_byte:node.end_byte].decode("utf-8", "replace")


def base_name(t):
    """The simple name of a type (List<Actor> -> List, int[] -> int)."""
    t = t.strip()
    if t.endswith("[]"):
        t = t[:-2].strip()
    if "<" in t:
        t = t.split("<", 1)[0].strip()
    if "." in t:
        t = t.split(".")[-1].strip()
    return t


def type_usages(tree, src):
    """The names of the types the file uses (in a type position)."""
    names = set()
    stack = [tree.root_node]
    while stack:
        node = stack.pop()
        t = node.type
        if t in ("variable_declaration", "parameter", "object_creation_expression", "cast_expression",
                 "type_argument", "base_list", "generic_name", "tuple_element"):
            for c in node.children:
                if c.type in ("variable_declarator", "argument_list", "=", "(", ")"):
                    continue
                if c.type in ("identifier", "predefined_type", "nullable_type", "array_type", "generic_type",
                              "type", "scoped_type", "qualified_name", "generic_name"):
                    t = text(c, src)
                    if "." in t:
                        names.add(t)      # a qualified name: the namespace is explicit
                    else:
                        names.add(base_name(t))
                    break
        stack.extend(node.children)
    return names


def strip_code(src):
    """Drop the comments and the string literals (they only add noise)."""
    out = bytearray(src)
    i = 0
    n = len(src)
    while i < n:
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
        elif src[i:i + 1] == b'"':
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


files = []
for base, _dirs, names in os.walk(FOLDER):
    for n in names:
        if n.endswith(".cs"):
            files.append(os.path.join(base, n))
files.sort()

project_ns = set()
texts = {}
type_ns = {}          # the type name -> the namespace it lives in
for p in files:
    with open(p, "rb") as f:
        src = f.read()
    texts[p] = src
    for m in NS.finditer(src.decode("utf-8", "replace")):
        project_ns.add(m.group(1))
    # the types and their namespace
    tree = PARSER.parse(src)
    stack = [tree.root_node]
    while stack:
        node = stack.pop()
        if node.type in ("class_declaration", "struct_declaration", "enum_declaration", "interface_declaration"):
            name = None
            for c in node.children:
                if c.type == "identifier":
                    name = c.text.decode("utf-8")
                    break
            if name:
                # the namespace: the closest one up the tree
                ns = ""
                parent = node.parent
                while parent is not None:
                    if parent.type in ("namespace_declaration", "file_scoped_namespace_declaration"):
                        for c in parent.children:
                            if c.type in ("identifier", "qualified_name"):
                                ns = c.text.decode("utf-8")
                                break
                        break
                    parent = parent.parent
                type_ns.setdefault(name, ns)
        stack.extend(node.children)

ns_types = {}
for _name, _ns in type_ns.items():
    ns_types.setdefault(_ns, []).append(_name)
for _ns, _names in EXTERNAL_TYPES.items():
    ns_types.setdefault(_ns, []).extend(_names)

problems = []
for p, raw in texts.items():
    rel = os.path.relpath(p, ROOT)
    src = strip_code(raw).decode("utf-8", "replace")
    usings = set(USING.findall(src))
    own_ns = ""
    for m in NS.finditer(src):
        own_ns = m.group(1)
    for u in usings:
        if u.startswith("INKWAVE") and u not in project_ns:
            problems.append(f"{rel}: the using '{u}' points at a namespace the project does not declare")
    for m in QUAL.finditer(src):
        q = m.group(0)
        if q in project_ns:
            continue
        parts = q.split(".")
        if len(parts) == 2 and parts[1] in {n.split(".")[-1] for n in project_ns}:
            continue
        if len(parts) >= 2 and parts[1] not in {n.split(".")[-1] for n in project_ns}:
            problems.append(f"{rel}: the reference '{q}' points at a namespace the project does not declare")
    # the external namespaces a file reaches into must be imported
    for ext, full in (("UnityEditor", "UnityEditor"), ("UnityEngine", "UnityEngine"),
                      ("Newtonsoft", "Newtonsoft.Json"), ("System", "System")):
        if re.search(r"\b" + ext + r"\.", src) and not any(u == full or u.startswith(full + ".") for u in usings):
            problems.append(f"{rel}: it uses '{ext}.' but does not import it")
    # the types the file really uses (in a type position)
    tree = PARSER.parse(raw)
    declared = type_usages(tree, raw)
    # the names that two of the imported namespaces both provide (CS0104)
    imported = {u for u in usings if u} | ({own_ns} if own_ns else set())
    for name in list(declared):
        if "." in name:
            continue
        sources = {n for n, names in ns_types.items() if name in names and n in imported}
        if len(sources) > 1:
            problems.append(f"{rel}: the name '{name}' is ambiguous between {', '.join(sorted(sources))}")
    for name, ns in type_ns.items():
        if name not in declared or "." in name:
            continue
        if ns == own_ns or ns in usings or ns + "." in [u for u in usings]:
            continue
        # the nested types live in their parent class, so the parent's namespace is enough
        if ns and ns.startswith("INKWAVE") and any(u.startswith(ns + ".") for u in usings):
            continue
        problems.append(f"{rel}: it uses the type '{name}' but does not import '{ns}'")

if not problems:
    print(f"✓ the namespace check is clean — {len(project_ns)} project namespaces, {len(files)} files")
    sys.exit(0)

print("· the namespace check found these problems:")
for p in sorted(set(problems)):
    print("  ✗ " + p)
sys.exit(1)
