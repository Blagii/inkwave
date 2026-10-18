// Keyboard + mouse + gamepad, producing the same per-frame snapshot as the web build's src/core/input.js.
//
// The key bindings are the ones the web build uses, so the game plays the same:
//   move  WASD / arrows / left stick
//   fire  left mouse / right trigger
//   sub   right mouse / E / right bumper
//   jump  space / A
//   squid shift / left trigger
//   special F / Q / Y / right stick click
using UnityEngine;
using UnityEngine.InputSystem;

namespace INKWAVE.Inputs
{
    public class PlayerInput : MonoBehaviour
    {
        public float Sensitivity = 1f;
        public float PadSensitivity = 1f;
        public bool InvertY;
        public float RumbleScale = 1f;

        public Vector2 Look;          // the mouse / stick delta of this frame
        public Vector3 Move;          // the movement intent (camera relative)
        public bool Fire, Sub, Jump, Squid, Special;
        public bool MapHeld;
        public bool Cheer;

        Keyboard _kb;
        Mouse _mouse;
        Gamepad _pad;
        float _prevFire, _prevSub, _prevJump, _prevSquid, _prevSpecial;
        bool _cheerPressed;

        void Update()
        {
            _kb = Keyboard.current;
            _mouse = Mouse.current;
            _pad = Gamepad.current;

            Look = Vector2.zero;
            if (_mouse != null)
            {
                Look += new Vector2(_mouse.delta.x, _mouse.delta.y) * Sensitivity;
            }

            float mx = 0f, mz = 0f;
            if (_kb != null)
            {
                if (_kb.wKey.isPressed || _kb.upArrowKey.isPressed) mz += 1f;
                if (_kb.sKey.isPressed || _kb.downArrowKey.isPressed) mz -= 1f;
                if (_kb.aKey.isPressed || _kb.leftArrowKey.isPressed) mx -= 1f;
                if (_kb.dKey.isPressed || _kb.rightArrowKey.isPressed) mx += 1f;
            }
            if (_pad != null)
            {
                Vector2 stick = PadStick(_pad.leftStick.ReadValue(), 0.14f, 0.95f);
                mx += stick.x;
                mz -= stick.y;
                Vector2 rs = _pad.rightStick.ReadValue();
                Look += new Vector2(rs.x, rs.y) * PadSensitivity * 3.2f;
            }
            float ml = Mathf.Sqrt(mx * mx + mz * mz);
            if (ml > 1f) { mx /= ml; mz /= ml; }
            Move = new Vector3(mx, 0f, mz);

            bool fire = (_mouse != null && _mouse.leftButton.isPressed) || (_pad != null && _pad.rightTrigger.ReadValue() > 0.3f);
            bool sub = (_mouse != null && _mouse.rightButton.isPressed) || (_kb != null && _kb.eKey.isPressed)
                       || (_pad != null && _pad.rightShoulder.isPressed);
            bool jump = (_kb != null && _kb.spaceKey.isPressed) || (_pad != null && _pad.buttonSouth.isPressed);
            bool squid = (_kb != null && (_kb.leftShiftKey.isPressed || _kb.rightShiftKey.isPressed))
                         || (_pad != null && _pad.leftTrigger.ReadValue() > 0.3f);
            bool special = (_kb != null && (_kb.fKey.isPressed || _kb.qKey.isPressed))
                           || (_pad != null && (_pad.buttonWest.isPressed || _pad.rightStickButton.isPressed));
            MapHeld = (_kb != null && (_kb.tabKey.isPressed || _kb.mKey.isPressed))
                      || (_pad != null && _pad.selectButton.isPressed);

            if (InvertY) Look = new Vector2(Look.x, -Look.y);

            // edge detection for the one-shot signals
            if (fire && !_prevFire) { }
            Cheer = (_kb != null && _kb.cKey.wasPressedThisFrame) || (_pad != null && _pad.dpad.up.wasPressedThisFrame && !MapHeld);
            _prevFire = fire; _prevSub = sub; _prevJump = jump; _prevSquid = squid; _prevSpecial = special;

            Fire = fire; Sub = sub; Jump = jump; Squid = squid; Special = special;
            if (MapHeld) { Fire = false; Sub = false; }
        }

        /// A stick with a radial dead zone and an outer dead zone (the direction is preserved).
        public static Vector2 PadStick(Vector2 raw, float dz = 0.12f, float outer = 0.96f)
        {
            float m = raw.magnitude;
            if (m <= dz) return Vector2.zero;
            float k = Mathf.Min(1f, (m - dz) / (outer - dz));
            return raw / m * k;
        }

        /// Dual-rumble pulse (strong = low motor, weak = high motor, 0..1).
        public void Rumble(float strong, float weak, float ms = 60f)
        {
            if (_pad == null || RumbleScale <= 0f) return;
            _pad.SetMotorSpeeds(Mathf.Clamp01(strong * RumbleScale), Mathf.Clamp01(weak * RumbleScale));
            CancelInvoke(nameof(StopRumble));
            Invoke(nameof(StopRumble), ms / 1000f);
        }

        void StopRumble()
        {
            if (_pad != null) _pad.SetMotorSpeeds(0f, 0f);
        }
    }
}
