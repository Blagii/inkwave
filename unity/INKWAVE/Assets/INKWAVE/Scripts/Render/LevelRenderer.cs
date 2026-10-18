// The level renderer: turns the level's oriented boxes into Unity meshes.
//
// Each paintable face gets its own quad, with UVs pointing into its rectangle of the ink atlas, so the ink shows up
// on the surface exactly where the paint system put it. The props (the dressing objects) come from the exported
// placements — their geometry is baked from the web build's three.js props into glTF (see docs/UNITY-PORT.md).
using System.Collections.Generic;
using INKWAVE.Core;
using INKWAVE.Data;
using INKWAVE.Gameplay;
using INKWAVE.World;
using UnityEngine;

namespace INKWAVE.Render
{
    public class LevelRenderer : MonoBehaviour
    {
        public Material SurfaceMaterial;
        public Material PlainMaterial;
        public Texture2D White;

        GameObject _root;
        readonly List<Mesh> _meshes = new List<Mesh>();

        public void Build(Level level, PaintSystem paint)
        {
            Clear();
            _root = new GameObject("Level");
            _root.transform.SetParent(transform, false);

            var inkMat = SurfaceMaterial != null ? new Material(SurfaceMaterial) : null;
            var plainMat = PlainMaterial != null ? new Material(PlainMaterial) : null;
            if (inkMat != null && paint != null)
            {
                var rt = new RenderTexture(paint.AtlasSize, paint.AtlasSize, 0, RenderTextureFormat.ARGB32);
                rt.name = "InkAtlas";
                rt.Create();
                inkMat.SetTexture("_InkAtlas", rt);
                inkMat.SetVector("_InkParams", new Vector4(paint.AtlasSize, paint.AtlasSize, paint.UsedHeight, 0));
            }

            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            var plainVerts = new List<Vector3>();
            var plainNorms = new List<Vector3>();
            var plainUvs = new List<Vector2>();
            var plainTris = new List<int>();

            foreach (var b in level.Blocks)
            {
                if (b.Hidden) continue;
                foreach (var face in FacesOf(b, level))
                {
                    if (face == null) continue;
                    Vector3 origin = face.Origin;
                    Vector3 u = face.U, v = face.V;
                    int start = verts.Count;
                    verts.Add(origin);
                    verts.Add(origin + u);
                    verts.Add(origin + u + v);
                    verts.Add(origin + v);
                    for (int i = 0; i < 4; i++) norms.Add(face.N);
                    Vector2 uv0 = AtlasUv(face, 0f, 0f, paint);
                    Vector2 uv1 = AtlasUv(face, face.Su, 0f, paint);
                    Vector2 uv2 = AtlasUv(face, face.Su, face.Sv, paint);
                    Vector2 uv3 = AtlasUv(face, 0f, face.Sv, paint);
                    uvs.Add(uv0); uvs.Add(uv1); uvs.Add(uv2); uvs.Add(uv3);
                    tris.Add(start); tris.Add(start + 2); tris.Add(start + 1);
                    tris.Add(start); tris.Add(start + 3); tris.Add(start + 2);
                }
            }

            if (verts.Count > 0 && inkMat != null)
            {
                var mesh = new Mesh { name = "LevelInk" };
                mesh.SetVertices(verts);
                mesh.SetNormals(norms);
                mesh.SetUVs(0, uvs);
                mesh.SetTriangles(tris, 0);
                mesh.RecalculateBounds();
                var go = new GameObject("LevelInk");
                go.transform.SetParent(_root.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = inkMat;
                _meshes.Add(mesh);
            }
        }

        static IEnumerable<Face> FacesOf(Block b, Level level)
        {
            for (int i = 0; i < 6; i++)
            {
                int fid = b.Faces[i];
                if (fid < 0 || fid >= level.Faces.Count) continue;
                yield return level.Faces[fid];
            }
        }

        /// The UV of a point on a face inside the ink atlas.
        static Vector2 AtlasUv(Face f, float u, float v, PaintSystem paint)
        {
            if (paint == null || f.Aw <= 0) return new Vector2(0.5f, 0.5f);
            const int pad = 8;
            float s = paint.AtlasSize;
            float px = (f.Ax + pad + u * f.Ppm) / s;
            float py = (f.Ay + pad + v * f.Ppm) / s;
            return new Vector2(px, py);
        }

        public void Clear()
        {
            if (_root != null) Destroy(_root);
            _root = null;
            foreach (var m in _meshes) if (m != null) Destroy(m);
            _meshes.Clear();
        }
    }
}
