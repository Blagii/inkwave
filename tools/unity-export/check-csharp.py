#!/usr/bin/env python3
"""A syntax check for the Unity C# sources.

The sandbox that writes the port has no C# compiler, so this parses every .cs file with a tree-sitter C# grammar and
reports the syntax errors (the ones the Unity compiler would call CS1xxx). It only needs `tree_sitter` and
`tree_sitter_c_sharp`; without them it exits quietly (the check is optional).

    pip install tree_sitter tree_sitter_c_sharp
    python3 tools/unity-export/check-csharp.py [folder]
"""
import os
import sys

try:
    from tree_sitter import Language, Parser
    import tree_sitter_c_sharp
except ImportError:
    print("· the C# syntax check is skipped (pip install tree_sitter tree_sitter_c_sharp)")
    sys.exit(0)

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
FOLDER = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "unity", "INKWAVE", "Assets", "INKWAVE")

try:
    LANG = Language(tree_sitter_c_sharp.language())
except AttributeError:      # older tree_sitter
    LANG = tree_sitter_c_sharp.language()
PARSER = Parser(LANG)

files = []
for base, _dirs, names in os.walk(FOLDER):
    for n in names:
        if n.endswith(".cs"):
            files.append(os.path.join(base, n))
files.sort()

errors = 0
for path in files:
    with open(path, "rb") as f:
        src = f.read()
    tree = PARSER.parse(src)
    stack = [tree.root_node]
    while stack:
        node = stack.pop()
        if node.type == "ERROR" or node.is_missing:
            errors += 1
            line = node.start_point[0] + 1
            text = src[node.start_byte:node.end_byte].decode("utf-8", "replace")
            text = text if len(text) < 90 else text[:90] + "…"
            print(f"  ✗ {os.path.relpath(path, ROOT)}:{line}: {text.strip()!r}")
            for child in node.children:
                if child.is_error or child.is_missing:
                    stack.append(child)
            continue
        stack.extend(node.children)

rel = os.path.relpath(FOLDER, ROOT)
if errors:
    print(f"✗ the C# syntax check found {errors} error(s) in {rel}")
    sys.exit(1)
print(f"✓ the C# syntax check is clean — {len(files)} files in {rel}")
