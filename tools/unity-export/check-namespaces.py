#!/usr/bin/env python3
"""A namespace check for the Unity C# sources.

A missing or misspelled `using` is a CS0246 / CS0103 that the Unity compiler reports, but the sandbox has no C#
compiler. This verifies that

  * every `using` of a project namespace points at a namespace the project really declares,
  * every qualified `INKWAVE.X.Y` reference points at a namespace the project really declares,
  * every file that reaches into a known external namespace has the matching `using`.

    python3 tools/unity-export/check-namespaces.py
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
FOLDER = os.path.join(ROOT, "unity", "INKWAVE", "Assets", "INKWAVE")

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

files = []
for base, _dirs, names in os.walk(FOLDER):
    for n in names:
        if n.endswith(".cs"):
            files.append(os.path.join(base, n))
files.sort()

project_ns = set()
texts = {}
for p in files:
    with open(p, "r", encoding="utf-8") as f:
        src = f.read()
    texts[p] = src
    for m in NS.finditer(src):
        project_ns.add(m.group(1))

# a namespace may be declared through a parent (INKWAVE.Audio) — the children are valid too
problems = []
for p, src in texts.items():
    rel = os.path.relpath(p, ROOT)
    usings = set(USING.findall(src))
    for u in usings:
        if u.startswith("INKWAVE") and u not in project_ns:
            problems.append(f"{rel}: the using '{u}' points at a namespace the project does not declare")
    for m in QUAL.finditer(src):
        q = m.group(0)
        # INKWAVE itself and INKWAVE.<namespace> are fine; INKWAVE.<ns>.<Type> needs the namespace to exist
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

if not problems:
    print(f"✓ the namespace check is clean — {len(project_ns)} project namespaces, {len(files)} files")
    sys.exit(0)

print("· the namespace check found these problems:")
for p in sorted(set(problems)):
    print("  ✗ " + p)
sys.exit(1)
