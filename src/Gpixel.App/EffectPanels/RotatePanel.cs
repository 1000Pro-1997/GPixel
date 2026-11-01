using Gpixel.Core.Effects;
using Gpixel.Core.Model;
using Gpixel.App.Localization;

namespace Gpixel.App;

public sealed partial class LayerEffectsDialog
{
    /// <summary>효과 창·타임라인 표시: 이름, 설명, 타임라인 색, 설정 칸, 목록 요약.</summary>
    private static EffectUi RotateUi => EffectUi.For<RotateEffect>(RotateDefinition.Id, UiStrings.S_LayerEffectsDialog_KindTable_B65A656A1B, UiStrings.S_LayerEffectsDialog_KindTable_34F6760645,
        Avalonia.Media.Color.FromRgb(70, 96, 232), (dialog, e, set) => dialog.RotatePanel(e, set),
        rotate => string.Format(global::System.Globalization.CultureInfo.CurrentCulture, UiStrings.S_LayerEffectsDialog_Summary_4AD4C2E7D2, rotate.Angle, Anchors[(int)rotate.Anchor].Split(' ')[0]));

    /// <summary>회전 설정: 부호 있는 각도(범위 동안 도는 전체), 기준점.</summary>
    private void RotatePanel(RotateEffect e, Action<Func<LayerEffect, LayerEffect>> set)
    {
        static RotateEffect R(LayerEffect v) => (RotateEffect)v;
        SettingsRow(UiStrings.S_RotatePanel_RotatePanel_BE26BAB163, NumEditor(-RotateEffect.MaxAngle, RotateEffect.MaxAngle, e.Angle, NumKinds.Rotation, v => set(o => R(o) with { Angle = v })), Ui.Label(UiStrings.S_RotatePanel_RotatePanel_32751BEF8A));
        SettingsRow(UiStrings.S_RotatePanel_RotatePanel_C002C942CF, ChoiceEditor(Anchors, (int)e.Anchor, k => set(o => R(o) with { Anchor = (SquashAnchor)k })));
        AddMotionEditors(Selected, set);
        _settings.Children.Add(Hint(UiStrings.S_RotatePanel_RotatePanel_CF8C27D904
                                    + UiStrings.S_RotatePanel_RotatePanel_C0881989CD));
    }
}
