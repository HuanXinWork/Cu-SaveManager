using System.Globalization;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    internal sealed partial class SaveManagerWindow
    {
        private void DrawPauseDialSection()
        {
            DrawSectionHeader(I18n.T("sec.pause_dial"));
            GUILayout.BeginVertical(BlackWhiteSkin.CardStyle);

            bool newAutoAvoid = DrawSwitch(I18n.T("sw.pause_dial_auto_avoid"), _cfg.PauseDialAutoAvoid.Value);
            if (newAutoAvoid != _cfg.PauseDialAutoAvoid.Value)
            {
                _cfg.PauseDialAutoAvoid.Value = newAutoAvoid;
            }

            GUILayout.Space(10f);

            DrawFloatSlider(
                I18n.F("fmt.pause_dial_angle", _cfg.PauseDialAngleDeg.Value),
                _cfg.PauseDialAngleDeg,
                -180f,
                180f,
                snap: 1f,
                onChanged: null,
                fallbackBadge: _cfg.PauseDialAngleDeg.Value.ToString("0.#", CultureInfo.InvariantCulture) + "°");

            DrawFloatSlider(
                I18n.F("fmt.pause_dial_radius", _cfg.PauseDialRadiusOffset.Value),
                _cfg.PauseDialRadiusOffset,
                PauseDialLayoutSolver.MinRadiusOffset,
                PauseDialLayoutSolver.MaxRadiusOffset,
                snap: 1f,
                onChanged: null,
                fallbackBadge: _cfg.PauseDialRadiusOffset.Value.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture) + " px");

            DrawIntSlider(
                I18n.F("fmt.pause_dial_scale", _cfg.PauseDialScalePercent.Value),
                _cfg.PauseDialScalePercent,
                60,
                160);

            GUILayout.Space(4f);
            GUILayout.BeginHorizontal();
            string resetText = I18n.T("btn.pause_dial_reset");
            float resetW = BlackWhiteSkin.CalcWidth(BlackWhiteSkin.SubtleBtnStyle, resetText, 220f, 14f);
            if (GUILayout.Button(resetText, BlackWhiteSkin.SubtleBtnStyle,
                GUILayout.Width(resetW), GUILayout.Height(RowMinHeight)))
            {
                _cfg.ResetPauseDialLayout();
                ClearTextFocus();
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            GUILayout.Label(
                I18n.T("hint.pause_dial_drag"),
                BlackWhiteSkin.MutedLabelStyle,
                GUILayout.ExpandWidth(true),
                GUILayout.MinHeight(34f));

            GUILayout.EndVertical();
        }
    }
}
