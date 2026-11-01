using Gpixel.Core.Effects;
using Gpixel.Core.Model;
using Gpixel.App.Localization;

namespace Gpixel.App;

public sealed partial class LayerEffectsDialog
{
    /// <summary>효과 창·타임라인 표시: 이름, 설명, 타임라인 색, 설정 칸, 목록 요약.</summary>
    private static EffectUi OutlineUi => EffectUi.For<OutlineEffect>(OutlineDefinition.Id, UiStrings.S_LayerEffectsDialog_KindTable_5C190C1C74, UiStrings.S_LayerEffectsDialog_KindTable_45261BC59C,
        Avalonia.Media.Color.FromRgb(232, 160, 72), (dialog, e, set) => dialog.OutlinePanel(e, set),
        o => string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Summary_E7A8C83171,(o.Inside ? UiStrings.S_LayerEffectsDialog_Summary_192DBA1F7C : UiStrings.S_LayerEffectsDialog_Summary_78B20FF1F4),o.Thickness,(o.Diagonal ? UiStrings.S_LayerEffectsDialog_Summary_009BCE5A4C : UiStrings.S_LayerEffectsDialog_Summary_2629FC9A9D),
            o.ColorMode == OutlineColorMode.Shade ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture, UiStrings.EffectOutline_ShadeSummary, o.ShadeAmount.ToString("+0;−0;0", global::System.Globalization.CultureInfo.CurrentCulture)) : o.Color.ToHex())
            + (o.HideOriginal ? UiStrings.EffectOutline_HideOriginalSummary : ""));

    /// <summary>외곽선 설정: 색 방식(지정 색 / 명암), 색 또는 밝기 변화, 두께, 모양(4/8방향), 위치(바깥/안쪽), 원본 지우기.</summary>
    private void OutlinePanel(OutlineEffect o, Action<Func<LayerEffect, LayerEffect>> set)
    {
        SettingsRow(UiStrings.EffectOutline_ColorMode, ChoiceEditor([UiStrings.EffectOutline_ModeFixed, UiStrings.EffectOutline_ModeShade], (int)o.ColorMode, k =>
        {
            set(x => (OutlineEffect)x with { ColorMode = (OutlineColorMode)k });
            ShowSettings(); // 그 방식의 색·명암 칸으로
        }).Tip(UiStrings.EffectOutline_ModeTip));
        if (o.ColorMode == OutlineColorMode.Shade)
            SettingsSubRow(UiStrings.EffectOutline_ShadeAmount, NumEditor(-100, 100, o.ShadeAmount, NumKinds.Adjust, v => set(x => (OutlineEffect)x with { ShadeAmount = v })).Tip(UiStrings.EffectOutline_ShadeAmountTip), Ui.Label("%"));
        else
            SettingsSubRow(UiStrings.S_LayerEffectsDialog_FillSettings_F6DB7C3B67, EffectColorEditor(o, nameof(OutlineEffect.Color), ColorEditor(o.Color, c => set(x => (OutlineEffect)x with { Color = c })), set));
        SettingsRow(UiStrings.S_LayerEffectsDialog_FillSettings_A81FAFD1EE, NumEditor(1, OutlineEffect.MaxThickness, o.Thickness, NumKinds.Thickness, v => set(x => (OutlineEffect)x with { Thickness = v })), Ui.Label("px"));
        SettingsRow(UiStrings.S_LayerEffectsDialog_FillSettings_7AEF607F6B, ChoiceEditor([UiStrings.S_LayerEffectsDialog_FillSettings_85BBD645E0, UiStrings.S_LayerEffectsDialog_FillSettings_D2ECBA6FD8], o.Diagonal ? 1 : 0, k => set(x => (OutlineEffect)x with { Diagonal = k == 1 })));
        SettingsRow(UiStrings.S_LayerEffectsDialog_FillSettings_E78408846D, ChoiceEditor([UiStrings.S_LayerEffectsDialog_FillSettings_62FD728E7C, UiStrings.S_LayerEffectsDialog_FillSettings_4331838592], o.Inside ? 1 : 0, k => set(x => (OutlineEffect)x with { Inside = k == 1 })));
        SettingsRow("", CheckEditor(UiStrings.EffectOutline_HideOriginal, o.HideOriginal, v => set(x => (OutlineEffect)x with { HideOriginal = v })).Tip(UiStrings.EffectOutline_HideOriginalTip));
    }
}
