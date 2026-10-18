#!/usr/bin/env python3
"""A call check for the Unity C# sources.

The Unity compiler reports a wrong argument type as CS1503 and a wrong argument count as CS1501/CS1739, but the
sandbox has no C# compiler. This walks the call sites of the project's own types and compares the arguments with
the parameters: the count must match (the optional ones may be missing) and a literal argument must be assignable
to the parameter (an int literal goes into a float parameter, never the other way round).

    pip install tree_sitter tree_sitter_c_sharp
    python3 tools/unity-export/check-calls.py
"""
import os
import re
import sys

try:
    from tree_sitter import Language, Parser
    import tree_sitter_c_sharp
except ImportError:
    print("· the call check is skipped (pip install tree_sitter tree_sitter_c_sharp)")
    sys.exit(0)

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
FOLDER = os.path.join(ROOT, "unity", "INKWAVE", "Assets", "INKWAVE")

try:
    LANG = Language(tree_sitter_c_sharp.language())
except AttributeError:
    LANG = tree_sitter_c_sharp.language()
PARSER = Parser(LANG)


def text(node, src):
    return src[node.start_byte:node.end_byte].decode("utf-8")


def walk(node):
    stack = [node]
    while stack:
        n = stack.pop()
        yield n
        stack.extend(reversed(n.children))


def type_of(expr, src):
    """The C# type of an expression, when it is obvious from the source text."""
    t = text(expr, src).strip()
    while t.startswith("(") and t.endswith(")"):
        t = t[1:-1].strip()
    if re.fullmatch(r"[-+]?\d+", t):
        return "int"
    if re.fullmatch(r"[-+]?\d+[fF]", t):
        return "float"
    if re.fullmatch(r"[-+]?\d+[lL]", t):
        return "long"
    if re.fullmatch(r"[-+]?\d*\.\d+([eE][-+]?\d+)?[fF]?", t):
        return "float"
    if re.fullmatch(r"[-+]?\d+[eE][-+]?\d+", t):
        return "double"
    if t in ("true", "false"):
        return "bool"
    if t.startswith('"'):
        return "string"
    if t == "null":
        return None
    if t.endswith("[]"):
        return "array"
    if re.fullmatch(r"[A-Z][A-Za-z0-9_]*", t):
        return t
    m = re.fullmatch(r"([A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\.([A-Z][A-Za-z0-9_]*)", t)
    if m:
        return m.group(2)
    return None



def resolve(node, scope, classes):
    """The type of a receiver expression, when it is obvious."""
    if node.type == "identifier":
        name = node.text.decode("utf-8")
        if name in classes:
            return name
        return scope.get(name)
    if node.type == "member_access_expression":
        obj = node.children[0]
        mem = node.children[-1].text.decode("utf-8")
        base = resolve(obj, scope, classes)
        if base in classes:
            return classes[base]["fields"].get(mem)
    return None


NUMERIC = {"int", "float", "double", "long", "short", "byte", "decimal"}

# the static API of the engine and the library
API = set("""
Mathf Vector2 Vector3 Vector4 Quaternion Color Color32 Matrix4x4 Bounds Rect Random Time Debug Screen Camera
GUI Shader Material Texture2D RenderTexture GameObject Object Physics Resources Application Input JsonUtility
PlayerPrefs Array Math Convert Path Directory File Enumerable string int float double bool long byte char
StringBuilder Action Func List Dictionary HashSet JObject JArray JToken JsonConvert Activator
Destroy Instantiate DontDestroyOnLoad Invoke CancelInvoke IsInvoking nameof GetComponent AddComponent
SetActive GetComponentInChildren GetComponentsInChildren FindObjectOfType FindObjectsOfType
DestroyImmediate SetParent GetChild SendMessage BroadcastMessage CompareTag StartCoroutine StopCoroutine
""".split())

# ---- collect the project's classes, their methods and their fields --------------------------

classes = {}          # class name -> {"methods": {name: [(type, name), ...]}, "fields": {name: type}}
files = []
for base, _dirs, names in os.walk(FOLDER):
    for n in names:
        if n.endswith(".cs"):
            files.append(os.path.join(base, n))
files.sort()

texts = {}
for p in files:
    with open(p, "rb") as f:
        src = f.read()
    texts[p] = src
    tree = PARSER.parse(src)
    stack = [tree.root_node]
    while stack:
        node = stack.pop()
        if node.type in ("class_declaration", "struct_declaration"):
            name = None
            body = None
            for c in node.children:
                if c.type == "identifier" and name is None:
                    name = c.text.decode("utf-8")
                if c.type == "declaration_list":
                    body = c
            if name and body is not None:
                info = {"methods": {}, "fields": {}}
                for m in body.children:
                    if m.type == "method_declaration":
                        kids = list(m.children)
                        plist_at = next((i for i, c in enumerate(kids) if c.type == "parameter_list"), -1)
                        if plist_at <= 0 or kids[plist_at - 1].type != "identifier":
                            continue
                        mname = kids[plist_at - 1].text.decode("utf-8")
                        params = []
                        required = 0
                        for pc in kids[plist_at].children:
                            if pc.type not in ("parameter", "optional_parameter"):
                                continue
                            ptype = None
                            pname = None
                            for g in pc.children:
                                if g.type in ("nullable_type", "array_type", "generic_type", "predefined_type",
                                              "type", "scoped_type", "qualified_name"):
                                    if ptype is None:
                                        ptype = text(g, src).strip()
                                elif g.type == "identifier":
                                    if ptype is None:
                                        ptype = text(g, src).strip()
                                    else:
                                        pname = g.text.decode("utf-8")
                            has_default = any(c.type == "=" for c in pc.children)
                            if not has_default:
                                required += 1
                            params.append((ptype or "?", pname or "?"))
                        info["methods"].setdefault(mname, []).append((params, required, len(params)))
                    elif m.type in ("field_declaration", "property_declaration"):
                        ftype = None
                        for c in m.children:
                            if c.type in ("nullable_type", "array_type", "generic_type", "predefined_type", "type",
                                          "scoped_type", "qualified_name", "identifier"):
                                ftype = text(c, src).strip()
                                break
                        for c in m.children:
                            if c.type == "variable_declaration":
                                for d in c.children:
                                    if d.type == "variable_declarator":
                                        for g in d.children:
                                            if g.type == "identifier":
                                                info["fields"][g.text.decode("utf-8")] = ftype
                if name in classes:               # a partial class: merge the members
                    old_info = classes[name]
                    for k, v in info["fields"].items():
                        old_info["fields"].setdefault(k, v)
                    for k, v in info["methods"].items():
                        old_info["methods"].setdefault(k, []).extend(v)
                else:
                    classes[name] = info
        stack.extend(node.children)

# ---- check the call sites ------------------------------------------------------------------

problems = []
for p, src in texts.items():
    rel = os.path.relpath(p, ROOT)
    tree = PARSER.parse(src)
    # the names in scope: name -> declared type (flat; the last declaration wins — good enough)
    scope = {}
    for cls_name, info in classes.items():
        for fname, ftype in info["fields"].items():
            if fname not in scope:
                scope[fname] = ftype
    for node in walk(tree.root_node):
        if node.type == "parameter":
            ptype = None
            pname = None
            for g in node.children:
                if g.type in ("nullable_type", "array_type", "generic_type", "predefined_type", "type"):
                    ptype = text(g, src).strip()
                if g.type == "identifier":
                    pname = g.text.decode("utf-8")
            if pname and ptype:
                scope[pname] = ptype
        elif node.type == "variable_declaration":
            vtype = None
            for g in node.children:
                if g.type == "variable_declarator":
                    continue
                if g.type in ("identifier", "nullable_type", "array_type", "generic_type", "predefined_type",
                              "type", "scoped_type", "qualified_name"):
                    vtype = text(g, src).strip()
                    break
            for g in node.children:
                if g.type != "variable_declarator":
                    continue
                kids = [h for h in g.children if h.type == "identifier"]
                if not kids:
                    continue
                name = kids[0].text.decode("utf-8")
                scope[name] = vtype
                if vtype is None:
                    # "var x = new Foo()" -> the type is Foo
                    for d in g.children:
                        if d.type == "object_creation_expression":
                            for e in d.children:
                                if e.type == "identifier":
                                    scope[name] = e.text.decode("utf-8")
                                    break
    # the parameters of the methods: name -> type
    for node in walk(tree.root_node):
        if node.type != "invocation_expression":
            continue
        fn = None
        args = None
        for c in node.children:
            if c.type == "member_access_expression":
                fn = c
            elif c.type == "identifier":
                fn = c
            elif c.type == "argument_list":
                args = c
        if fn is None or args is None:
            continue
        if fn.type == "member_access_expression":
            recv = fn.children[0]
            mname = fn.children[-1].text.decode("utf-8")
            rtype = resolve(recv, scope, classes)
            if rtype is None or rtype not in classes:
                continue
            overloads = classes[rtype]["methods"].get(mname)
            if not overloads:
                continue
        else:
            # a call to a plain name: it has to be a method of the project, a local or a known API
            fname = fn.text.decode("utf-8")
            if fname not in scope and fname not in API and not any(fname in i["methods"] for i in classes.values()):
                line = src[:node.start_byte].count(b"\n") + 1
                problems.append(f"{rel}:{line}: the call '{fname}()' does not exist in the project")
            continue
        arg_nodes = [c for c in args.children if c.type == "argument"]
        n_args = len(arg_nodes)
        best = None
        for ov in overloads:
            required, total = ov[1], ov[2]
            if required <= n_args <= total:
                best = ov
                break
        if best is None:
            required = overloads[0][1]
            total = overloads[0][2]
            problems.append(f"{rel}:{node.start_point[0] + 1}: {mname}() takes {required}–{total} arguments, "
                            f"{n_args} given")
            continue
        params = best[0]
        for i, an in enumerate(arg_nodes):
            if i >= len(params):
                break
            ptype = params[i][0]
            atype = type_of(an, src)
            if atype is None or ptype in ("?", "var", "params", "out", "ref"):
                continue
            base_p = ptype.replace("[]", "").strip()
            if base_p in NUMERIC and atype in NUMERIC:
                if base_p in ("int", "long", "short", "byte") and atype in ("float", "double", "decimal"):
                    problems.append(f"{rel}:{an.start_point[0] + 1}: the argument '{text(an, src)}' is a "
                                    f"{atype} but the parameter '{params[i][1]}' is a {base_p}")
                continue
            if base_p == "string" and atype in ("int", "float", "double", "bool", "long"):
                problems.append(f"{rel}:{an.start_point[0] + 1}: the argument '{text(an, src)}' is a "
                                f"{atype} but the parameter '{params[i][1]}' is a string")
            if base_p in NUMERIC and atype == "string":
                problems.append(f"{rel}:{an.start_point[0] + 1}: the argument '{text(an, src)}' is a "
                                f"string but the parameter '{params[i][1]}' is a {base_p}")
            if base_p == "bool" and atype == "string":
                problems.append(f"{rel}:{an.start_point[0] + 1}: the argument '{text(an, src)}' is a "
                                f"string but the parameter '{params[i][1]}' is a bool")
            if base_p == "bool" and atype in ("int", "float"):
                problems.append(f"{rel}:{an.start_point[0] + 1}: the argument '{text(an, src)}' is a "
                                f"{atype} but the parameter '{params[i][1]}' is a bool")
            if atype and base_p not in NUMERIC and atype not in NUMERIC and base_p != atype and atype != "array":
                if atype in classes and base_p in classes and atype != base_p:
                    problems.append(f"{rel}:{an.start_point[0] + 1}: the argument '{text(an, src)}' is a "
                                    f"{atype} but the parameter '{params[i][1]}' is a {base_p}")

if not problems:
    print(f"✓ the call check is clean — {len(classes)} classes, {len(files)} files")
    sys.exit(0)

print("· the call check found these problems:")
for p in sorted(set(problems)):
    print("  ✗ " + p)
sys.exit(1)
