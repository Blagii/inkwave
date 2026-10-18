// The HUD: the ink meter, the score and the timer, the weapon and the special, the crosshair.
//
// It is drawn with IMGUI (no UI assets needed, so the build works straight after the scene is created);
// the layout follows the web build's HUD: the ink bar bottom-left, the score top-centre, the weapon bottom-right.
using INKWAVE.Core;
using INKWAVE.Data;
using INKWAVE.Gameplay;
using UnityEngine;

namespace INKWAVE.Render
{
    public class Hud : MonoBehaviour
    {
        public Match Match;
        public Actor Local;

        GUIStyle _label, _big, _center;
        Texture2D _white;
        float _flash;
        bool _stylesReady;

        void Update()
        {
            if (Local != null) _flash = Mathf.Max(0f, _flash - Time.deltaTime);
        }

        void OnGUI()
        {
            if (Local == null) return;
            if (!_stylesReady) MakeStyles();
            if (Match == null) return;

            float w = Screen.width, h = Screen.height;
            var ink = Local.Ink / 100f;

            // ---- the score + the timer (top centre)
            var counts = Match.Scores();
            string clock = FormatTime(Match.Time);
            _center.fontSize = Mathf.Max(18, (int)(h * 0.045f));
            GUI.Label(new Rect(0f, h * 0.02f, w, h * 0.07f), clock, _center);

            float scoreW = w * 0.16f;
            _big.fontSize = Mathf.Max(16, (int)(h * 0.04f));
            GUI.Label(new Rect(w * 0.5f - scoreW * 1.6f, h * 0.085f, scoreW, h * 0.06f), counts[0].ToString(), _label);
            GUI.Label(new Rect(w * 0.5f + scoreW * 0.6f, h * 0.085f, scoreW, h * 0.06f), counts[1].ToString(), _label);

            // ---- Zone Control: who holds the live objective
            if (Match.Zones != null)
            {
                var z = Match.Zones;
                _label.alignment = TextAnchor.MiddleCenter;
                string who = z.Owner < 0 ? "CONTESTED" : z.Owner == Local.Team ? "WE HOLD" : "ENEMY HOLDS";
                GUI.Label(new Rect(w * 0.5f - w * 0.2f, h * 0.145f, w * 0.4f, h * 0.05f),
                          $"{z.Active.Id.ToUpperInvariant()} · {who}", _label);
                _label.alignment = TextAnchor.MiddleLeft;
            }

            // ---- the ink meter (bottom left)
            float barW = w * 0.22f, barH = h * 0.028f;
            float x = w * 0.03f, y = h - barH - h * 0.06f;
            DrawBar(new Rect(x, y, barW, barH), ink, GameContext.TeamColor(Local.Team));
            _label.fontSize = Mathf.Max(12, (int)(h * 0.026f));
            GUI.Label(new Rect(x, y - h * 0.045f, barW, h * 0.04f), $"INK  {Mathf.RoundToInt(ink * 100f)}%", _label);

            // ---- the weapon + the special (bottom right)
            var weapon = Local.Weapon;
            string wname = weapon != null ? weapon.Str("name", weapon.Str("id", "-")) : "-";
            _label.alignment = TextAnchor.MiddleRight;
            GUI.Label(new Rect(w - w * 0.35f, h - h * 0.12f, w * 0.32f, h * 0.05f), wname, _label);
            float special = Local.SpecialFrac();
            DrawBar(new Rect(w - w * 0.25f, h - h * 0.065f, w * 0.22f, barH * 0.6f), special, Color.white);
            _label.alignment = TextAnchor.MiddleLeft;
            GUI.Label(new Rect(w - w * 0.35f, h - h * 0.185f, w * 0.32f, h * 0.04f), "SPECIAL", _label);

            // ---- the crosshair
            var cam = Camera.main;
            if (cam != null)
            {
                Vector3 sp = cam.WorldToScreenPoint(Local.AimPoint);
                if (sp.z > 0f)
                {
                    float r = Mathf.Max(6f, h * 0.012f);
                    DrawRing(new Vector2(sp.x, sp.y), r, Color.white * 0.85f);
                }
            }

            // ---- the low-ink warning
            if (ink < 0.18f)
            {
                _center.fontSize = Mathf.Max(16, (int)(h * 0.04f));
                GUI.Label(new Rect(0f, h * 0.72f, w, h * 0.06f), "LOW INK", _center);
            }
        }

        void DrawBar(Rect r, float fill, Color color)
        {
            if (_white == null) _white = Texture2D.whiteTexture;
            var prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.45f);
            GUI.DrawTexture(r, _white);
            GUI.color = new Color(1f, 1f, 1f, 0.18f);
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, r.height * 0.5f), _white);
            GUI.color = color;
            GUI.DrawTexture(new Rect(r.x, r.y + r.height * (1f - fill), r.width, r.height * fill), _white);
            GUI.color = prev;
        }

        void DrawRing(Vector2 c, float r, Color c0)
        {
            if (_white == null) _white = Texture2D.whiteTexture;
            var prev = GUI.color;
            GUI.color = c0;
            GUI.DrawTexture(new Rect(c.x - r, c.y - 1f, r * 2f, 2f), _white);
            GUI.DrawTexture(new Rect(c.x - r, c.y + r - 1f, r * 2f, 2f), _white);
            GUI.DrawTexture(new Rect(c.x - 1f, c.y - r, 2f, r * 2f), _white);
            GUI.DrawTexture(new Rect(c.x + r - 1f, c.y - r, 2f, r * 2f), _white);
            GUI.color = prev;
        }

        void MakeStyles()
        {
            _stylesReady = true;
            _label = new GUIStyle(GUI.skin.label);
            _label.fontSize = 20;
            _label.normal.textColor = Color.white;
            _label.shadow = true;
            _big = new GUIStyle(_label);
            _center = new GUIStyle(_label);
            _center.alignment = TextAnchor.MiddleCenter;
        }

        static string FormatTime(float seconds)
        {
            if (seconds < 0f) seconds = 0f;
            int s = Mathf.FloorToInt(seconds);
            return $"{s / 60:00}:{s % 60:00}";
        }
    }
}
