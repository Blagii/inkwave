// A placeholder body for an actor: a capsule that stands in for the squidkid until the character mesh is ported.
//
// It follows the actor's visual position and yaw, and swaps between the kid and the squid proportions, so the
// gameplay reads correctly while the character art is still being ported.
using INKWAVE.Core;
using INKWAVE.Gameplay;
using UnityEngine;

namespace INKWAVE.Render
{
    public class ActorRenderer : MonoBehaviour
    {
        public Actor Actor;
        public Material Material;

        Transform _t;
        GameObject _body;

        void LateUpdate()
        {
            if (Actor == null) return;
            if (_t == null)
            {
                _t = new GameObject("Actor").transform;
                _t.SetParent(transform, false);
                _body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                _body.name = "Body";
                _body.transform.SetParent(_t, false);
                var col = _body.GetComponent<Collider>();
                if (col != null) Destroy(col);
                if (Material != null) _body.GetComponent<MeshRenderer>().sharedMaterial = Material;
            }
            _t.position = Actor.VisualPos;
            _t.rotation = Quaternion.Euler(0f, Actor.Yaw * Mathf.Rad2Deg, 0f);
            float h = Actor.Form == ActorForm.Squid ? 0.55f : 1.45f;
            _body.transform.localScale = new Vector3(0.76f, h * 0.5f, 0.76f);
            _body.transform.localPosition = new Vector3(0f, h * 0.5f, 0f);
        }
    }
}
