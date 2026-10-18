// Camera rig: the third-person follow camera.
//
// A port of the web build's src/game/cameraRig.js (the follow part): the pivot rides critically-damped springs
// toward the character's visual position, the boom length comes from the soft cylinder probe, and the orientation is
// exactly the aim (yaw/pitch) — the aim ray is taken from the rendered camera, so the crosshair is always the truth.
using INKWAVE.Core;
using INKWAVE.Data;
using UnityEngine;

namespace INKWAVE.Gameplay
{
    /// A critically-damped spring (exact integration — stable and frame-rate independent).
    public class Spring
    {
        public float X, V;
        public void Reset(float x) { X = x; V = 0f; }
        public float Step(float target, float omega, float dt)
        {
            float d = X - target, e = Mathf.Exp(-omega * dt), k = (V + omega * d) * dt;
            X = target + (d + k) * e;
            V = (V - omega * k) * e;
            return X;
        }
    }

    public class CameraRig : MonoBehaviour
    {
        public Camera Cam;
        public Actor Target;

        public float Yaw = 0f, Pitch = -0.1f;
        public float Dist = 4.5f;
        public float CurDist = 4.5f;
        public float BaseFov = 70f;

        public Vector3 Pivot;
        public float PivotY;
        public Vector3 LookAt;
        public Vector3 AimPoint;

        readonly Spring _sx = new Spring(), _sy = new Spring(), _sz = new Spring();
        readonly Spring _boom = new Spring();
        readonly Spring _hgt = new Spring();
        readonly Spring _side = new Spring();
        float _dip, _dipV;
        readonly World.CameraProbe _probe = new World.CameraProbe();

        void Awake()
        {
            if (Cam == null) Cam = GetComponent<Camera>();
            _boom.Reset(Dist);
            _hgt.Reset(1.85f);
        }

        public void SetTarget(Actor a)
        {
            Target = a;
            if (a != null) Pivot = a.VisualPos;
        }

        public void ApplyLook(Vector2 delta, float dt)
        {
            Yaw -= delta.x * 0.0022f;
            Pitch -= delta.y * 0.0022f;
            Pitch = Mathf.Clamp(Pitch, -1.05f, 1.15f);
        }

        void LateUpdate()
        {
            if (Target == null || GameContext.Physics == null) return;
            float dt = Mathf.Max(1e-4f, Time.deltaTime);

            // ---- pivot: critically-damped springs toward the visual position, with velocity feed-forward
            Vector3 vis = Target.VisualPos;
            Vector3 target = vis + Target.Vel * 0.08f;
            float omega = Target.Grounded ? 18f : 11f;
            Pivot = new Vector3(
                _sx.Step(target.x, omega, dt),
                _sy.Step(target.y, omega * 0.7f, dt),
                _sz.Step(target.z, omega, dt));

            // ---- pivot height above the feet (the form change glides)
            float heightT = Target.Form == ActorForm.Squid ? 0.75f : 1.35f;
            PivotY = _hgt.Step(heightT, 12f, dt);

            // ---- strafe look-ahead: a hair more view where you are heading sideways
            float lat = Vector3.Dot(Target.Vel, Right());
            float sideT = Mathf.Clamp(lat * 0.045f, -0.32f, 0.32f);
            float so = _side.Step(sideT, 3.2f, dt);

            // ---- landing dip
            if (!Target.Grounded && Target.LandT < 0.05f) { _dipV -= 26f * dt; }
            _dipV += (-90f * _dip - 14f * _dipV) * dt;
            _dip = Mathf.Clamp(_dip + _dipV * dt, -0.35f, 0.1f);

            Vector3 pivot = Pivot + new Vector3(0f, PivotY + _dip, 0f);
            Vector3 back = new Vector3(-Mathf.Sin(Yaw) * Mathf.Cos(Pitch), -Mathf.Sin(Pitch), -Mathf.Cos(Yaw) * Mathf.Cos(Pitch));

            // ---- boom length: the soft cylinder probe (fast in, slow out)
            float want = (Target.Form == ActorForm.Squid ? 4.1f : Dist);
            GameContext.Physics.CameraProbe(pivot, back, want, 0.45f, _probe, 0.3f);
            float tgt = _probe.Soft;
            float deep = Mathf.Clamp((_boom.X - _probe.Hard) / 1f, 0f, 1f);
            _boom.Step(tgt, tgt < _boom.X ? 22f + 26f * deep : 3.6f, dt);
            if (_boom.X > _probe.Hard + 1.6f) { _boom.X = _probe.Hard + 1.6f; _boom.V = Mathf.Min(_boom.V, 0f); }
            if (_probe.Floor && _boom.X > _probe.Hard) { _boom.X = _probe.Hard; _boom.V = Mathf.Min(_boom.V, 0f); }
            if (_boom.X < 0.45f) { _boom.X = 0.45f; _boom.V = Mathf.Max(_boom.V, 0f); }
            CurDist = _boom.X;

            // ---- place the lens
            Vector3 lens = pivot + back * CurDist + Right() * so;
            // keep the lens off the floor under it
            var down = GameContext.Physics.Raycast(lens, Vector3.down, 3f, null, true);
            if (down.Hit_ && down.Point.y > lens.y) lens.y = down.Point.y + 0.05f;

            transform.position = lens;
            LookAt = pivot + back * -1.2f;
            transform.rotation = Quaternion.LookRotation((pivot - lens).normalized, Vector3.up);

            // ---- the aim ray comes from the rendered camera, so the crosshair is the truth
            Vector3 ray = transform.forward;
            var hit = GameContext.Physics.Raycast(lens, ray, 200f, null, true);
            AimPoint = hit.Hit_ ? hit.Point : lens + ray * 200f;
        }

        public Vector3 Right()
        {
            float cy = Mathf.Cos(Yaw), sy = Mathf.Sin(Yaw);
            return new Vector3(-cy, 0f, sy);
        }

        public Vector3 Forward()
        {
            float sy = Mathf.Sin(Yaw), cy = Mathf.Cos(Yaw);
            return new Vector3(sy, 0f, cy);
        }
    }
}
