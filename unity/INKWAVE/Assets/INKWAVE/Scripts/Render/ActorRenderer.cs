// The actor's body: a procedurally built squid kid (no art assets — the shapes are made from Unity's
// primitives and coloured per team), with a squid form for the swim.
//
// It replaces the placeholder capsule: the rig follows the actor, the eyes track the aim, the arms carry
// the weapon, and the pose reacts to the state (walking, firing, rolling, charging, swimming).
using INKWAVE.Core;
using INKWAVE.Gameplay;
using UnityEngine;

namespace INKWAVE.Render
{
    public class ActorRenderer : MonoBehaviour
    {
        public Actor Actor;
        public Material Material;              // the team material (assigned by the match)

        Transform _t;                          // the rig
        Transform _kid, _squid;
        Transform _body, _head, _tank;
        Transform _armL, _armR, _legL, _legR, _gun, _blob;
        Transform _pupilL, _pupilR;
        Renderer[] _renderers;

        Vector3 _bodyBase;
        float _bob, _squash, _roll, _lastYaw;
        bool _built;

        static readonly Material[] _teamMats = new Material[2];   // the per-team materials (built once)

        void LateUpdate()
        {
            if (Actor == null) return;
            if (!_built) Build();
            if (_t == null) return;

            float dt = Time.deltaTime;
            _t.position = Actor.VisualPos;
            float yaw = Actor.Yaw * Mathf.Rad2Deg;
            _t.rotation = Quaternion.Euler(0f, yaw, 0f);

            bool squid = Actor.Form == ActorForm.Squid;
            if (_kid != null) _kid.gameObject.SetActive(!squid);
            if (_squid != null) _squid.gameObject.SetActive(squid);
            if (squid) AnimateSquid(dt);
            else AnimateKid(dt);

            // the eyes follow the aim
            Vector3 aim = Actor.AimDir;
            Vector3 local = _t.InverseTransformDirection(aim);
            float lx = Mathf.Clamp(local.x * 0.06f, -0.045f, 0.045f);
            float ly = Mathf.Clamp(local.y * 0.04f, -0.03f, 0.035f);
            if (_pupilL != null) _pupilL.localPosition = new Vector3(-0.075f + lx, 0.02f + ly, 0.235f);
            if (_pupilR != null) _pupilR.localPosition = new Vector3(0.075f + lx, 0.02f + ly, 0.235f);
        }

        // --------------------------------------------------------------------------------- the kid
        void AnimateKid(float dt)
        {
            var wr = Actor.Weapons;
            bool rolling = wr != null && wr.Rolling;
            bool firing = wr != null && wr.FiringPose();
            float speed = new Vector2(Actor.Vel.x, Actor.Vel.z).magnitude;

            // the walk bob + the landing squash
            _bob += dt * Mathf.Max(2f, speed * 2.2f);
            float bob = Actor.Grounded ? Mathf.Sin(_bob) * 0.035f * Mathf.Clamp01(speed / 5f) : 0f;
            _squash = Mathf.MoveTowards(_squash, 0f, dt * 3f);
            float squash = 1f - _squash * 0.35f;

            if (_body != null)
            {
                _body.localPosition = _bodyBase + new Vector3(0f, bob, 0f);
                _body.localScale = new Vector3(0.72f * (2f - squash), 0.62f * squash, 0.72f * (2f - squash));
            }
            if (_head != null)
            {
                _head.localPosition = new Vector3(0f, 1.16f + bob * 1.3f, 0.02f);
                // the head leans into the aim
                _head.localRotation = Quaternion.Euler(Mathf.Clamp(Actor.AimPitch * Mathf.Rad2Deg * 0.4f, -22f, 22f), 0f, 0f);
            }

            // the arms: the right one holds the weapon and points at the aim
            float aimPitch = Mathf.Clamp(Actor.AimPitch * Mathf.Rad2Deg, -55f, 55f);
            if (_armR != null)
                _armR.localRotation = Quaternion.Euler(-70f - aimPitch, 0f, -8f);
            if (_armL != null)
                _armL.localRotation = Quaternion.Euler(firing ? -62f : -14f, 0f, 12f);

            // the legs
            float stride = Actor.Grounded ? Mathf.Sin(_bob) * Mathf.Clamp01(speed / 6f) * 32f : 8f;
            if (_legL != null) _legL.localRotation = Quaternion.Euler(stride, 0f, 0f);
            if (_legR != null) _legR.localRotation = Quaternion.Euler(-stride, 0f, 0f);

            // the roll: the drum spins around the forward axis
            if (rolling)
            {
                _roll += dt * 14f;
                if (_body != null) _body.localRotation = Quaternion.Euler(_roll, 0f, 0f);
            }
            else if (_body != null) _body.localRotation = Quaternion.Slerp(_body.localRotation, Quaternion.identity, dt * 8f);

            // the weapon swaps its pose: the roller is held down, the chargers are drawn
            if (_gun != null)
            {
                string kind = Actor.Weapon != null ? Actor.Weapon.Str("kind") : "shooter";
                _gun.localRotation = Quaternion.Euler(kind == "roller" ? 20f : kind == "charger" ? 35f : 0f, 0f, 0f);
                float len = kind == "roller" ? 0.42f : kind == "charger" ? 0.5f : kind == "blade" ? 0.66f : 0.34f;
                _gun.localScale = new Vector3(0.09f, 0.09f, len);
            }
        }

        // -------------------------------------------------------------------------------- the squid
        void AnimateSquid(float dt)
        {
            _bob += dt * 6f;
            float speed = new Vector2(Actor.Vel.x, Actor.Vel.z).magnitude;
            float pulse = 1f + Mathf.Sin(_bob) * 0.06f * Mathf.Clamp01(speed / 4f);
            if (_blob != null)
            {
                _blob.localPosition = new Vector3(0f, 0.3f + Mathf.Sin(_bob * 0.7f) * 0.02f, 0f);
                _blob.localScale = new Vector3(0.78f / pulse, 0.56f * pulse, 0.78f / pulse);
            }
        }

        // ----------------------------------------------------------------------------------- build
        void Build()
        {
            _built = true;
            _t = new GameObject("Rig").transform;
            _t.SetParent(transform, false);

            int team = Actor != null ? Actor.Team : 0;
            var body = TeamMaterial(team);
            var dark = MakeColor(new Color(0.09f, 0.1f, 0.13f));
            var white = MakeColor(new Color(0.95f, 0.96f, 0.98f));
            var skin = MakeColor(SkinColor(team));
            var ink = MakeColor(new Color(0.05f, 0.06f, 0.09f));

            // ---- the kid
            _kid = new GameObject("Kid").transform;
            _kid.SetParent(_t, false);

            _body = Part(_kid, "Body", PrimitiveType.Capsule, body, new Vector3(0f, 0.66f, 0f), new Vector3(0.72f, 0.34f, 0.72f));
            _bodyBase = _body.localPosition;
            _head = Part(_kid, "Head", PrimitiveType.Sphere, skin, new Vector3(0f, 1.16f, 0.02f), new Vector3(0.52f, 0.5f, 0.52f));

            Part(_head, "EyeL", PrimitiveType.Sphere, white, new Vector3(-0.075f, 0.02f, 0.215f), new Vector3(0.13f, 0.15f, 0.1f));
            Part(_head, "EyeR", PrimitiveType.Sphere, white, new Vector3(0.075f, 0.02f, 0.215f), new Vector3(0.13f, 0.15f, 0.1f));
            _pupilL = Part(_head, "PupilL", PrimitiveType.Sphere, dark, new Vector3(-0.075f, 0.02f, 0.235f), new Vector3(0.07f, 0.08f, 0.06f));
            _pupilR = Part(_head, "PupilR", PrimitiveType.Sphere, dark, new Vector3(0.075f, 0.02f, 0.235f), new Vector3(0.07f, 0.08f, 0.06f));

            _tank = Part(_kid, "Tank", PrimitiveType.Cylinder, ink, new Vector3(0f, 0.78f, -0.26f), new Vector3(0.36f, 0.16f, 0.36f));
            _tank.localRotation = Quaternion.Euler(8f, 0f, 0f);

            _armL = Part(_kid, "ArmL", PrimitiveType.Capsule, skin, new Vector3(-0.34f, 0.78f, 0.04f), new Vector3(0.13f, 0.2f, 0.13f));
            _armR = Part(_kid, "ArmR", PrimitiveType.Capsule, skin, new Vector3(0.34f, 0.78f, 0.04f), new Vector3(0.13f, 0.2f, 0.13f));
            _legL = Part(_kid, "LegL", PrimitiveType.Capsule, skin, new Vector3(-0.15f, 0.2f, 0f), new Vector3(0.16f, 0.14f, 0.16f));
            _legR = Part(_kid, "LegR", PrimitiveType.Capsule, skin, new Vector3(0.15f, 0.2f, 0f), new Vector3(0.16f, 0.14f, 0.16f));

            // the weapon sits in the right hand
            var gun = new GameObject("Gun").transform;
            gun.SetParent(_armR, false);
            gun.localPosition = new Vector3(0f, -0.16f, 0.2f);
            gun.localScale = new Vector3(0.09f, 0.09f, 0.34f);
            Part(gun, "Barrel", PrimitiveType.Cube, dark, Vector3.zero, Vector3.one);
            _gun = gun;

            // ---- the squid
            _squid = new GameObject("Squid").transform;
            _squid.SetParent(_t, false);
            _blob = Part(_squid, "Blob", PrimitiveType.Sphere, body, new Vector3(0f, 0.3f, 0f), new Vector3(0.78f, 0.56f, 0.78f));
            Part(_blob, "EyeL", PrimitiveType.Sphere, white, new Vector3(-0.16f, 0.1f, 0.62f), new Vector3(0.2f, 0.24f, 0.16f));
            Part(_blob, "EyeR", PrimitiveType.Sphere, white, new Vector3(0.16f, 0.1f, 0.62f), new Vector3(0.2f, 0.24f, 0.16f));
            Part(_blob, "PupilL", PrimitiveType.Sphere, dark, new Vector3(-0.16f, 0.1f, 0.71f), new Vector3(0.1f, 0.12f, 0.08f));
            Part(_blob, "PupilR", PrimitiveType.Sphere, dark, new Vector3(0.16f, 0.1f, 0.71f), new Vector3(0.1f, 0.12f, 0.08f));
            for (int i = 0; i < 4; i++)
            {
                float a = i * Mathf.PI * 0.5f + 0.6f;
                var tent = Part(_blob, "Tent" + i, PrimitiveType.Capsule, body,
                                new Vector3(Mathf.Cos(a) * 0.26f, -0.28f, Mathf.Sin(a) * 0.26f),
                                new Vector3(0.12f, 0.18f, 0.12f));
                tent.localRotation = Quaternion.Euler(Mathf.Sin(a) * 22f, 0f, Mathf.Cos(a) * 22f);
            }
            _squid.gameObject.SetActive(false);
        }

        Transform Part(Transform parent, string name, PrimitiveType type, Material mat, Vector3 pos, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            return go.transform;
        }

        /// The team colour: the material the match hands over, the one built in Resources, or a runtime one.
        Material TeamMaterial(int team)
        {
            if (Material != null) return Material;
            team = team < 0 ? 0 : (team > 1 ? 1 : team);
            if (_teamMats[team] == null)
            {
                _teamMats[team] = Resources.Load<Material>($"INKWAVE/Materials/Team{team}");
                if (_teamMats[team] == null) _teamMats[team] = MakeColor(GameContext.TeamColor(team));
            }
            return _teamMats[team];
        }

        static Material MakeColor(Color c)
        {
            var shader = Shader.Find("INKWAVE/Character");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            var m = new Material(shader);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.25f);
            return m;
        }

        static Color SkinColor(int team)
        {
            // a warm skin tone that reads on both team colours
            return new Color(0.93f, 0.82f, 0.72f);
        }
    }
}
