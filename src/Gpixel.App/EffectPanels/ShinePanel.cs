using Gpixel.Core.Effects;
using Gpixel.Core.Model;
using Gpixel.App.Localization;

namespace Gpixel.App;

public sealed partial class LayerEffectsDialog
{
    /// <summary>효과 창·타임라인 표시: 이름, 설명, 타임라인 색, 설정 칸, 목록 요약.</summary>
    private static EffectUi ShineUi => EffectUi.For<ShineEffect>(ShineDefinition.Id, UiStrings.S_LayerEffectsDialog_KindTable_174D9850CC, UiStrings.S_LayerEffectsDialog_KindTable_E97AB47F1D,
        Avalonia.Media.Color.FromRgb(120, 214, 236), (dialog, e, set) => dialog.ShinePanel(e, set),
        s => string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Summary_F451DAAC0A,s.Angle,s.Width,s.Strength));

    /// <summary>빛 지나가기 설정: 각도, 폭·세기, 색, 부드러움.</summary>
    private void ShinePanel(ShineEffect s, Action<Func<LayerEffect, LayerEffect>> set)
    {
        ShineEffect S(LayerEffect v) => (ShineEffect)v;
        SettingsRow(UiStrings.S_LayerEffectsDialog_FillSettings_1192DC8149, NumEditor(0, 359, s.Angle, NumKinds.Angle, v => set(o => S(o) with { Angle = v })), Ui.Label(UiStrings.S_LayerEffectsDialog_FillSettings_C4DC26AC14));
        SettingsRow(UiStrings.S_LayerEffectsDialog_FillSettings_D370965ED0, NumEditor(1, ShineEffect.MaxWidth, s.Width, NumKinds.Px, v => set(o => S(o) with { Width = v })), Ui.Label("px"),
            Ui.Label(UiStrings.S_LayerEffectsDialog_FillSettings_6C80368CF6), NumEditor(0, 100, s.Strength, NumKinds.Strength, v => set(o => S(o) with { Strength = v })), Ui.Label("%"));
        SettingsRow(UiStrings.S_LayerEffectsDialog_FillSettings_F6DB7C3B67, EffectColorEditor(s, nameof(ShineEffect.Color), ColorEditor(s.Color, v => set(o => S(o) with { Color = v })), set));
        SettingsRow(UiStrings.S_LayerEffectsDialog_FillSettings_48FAB42CF8, ChoiceEditor([UiStrings.S_LayerEffectsDialog_FillSettings_28DDBC6B0B, UiStrings.S_LayerEffectsDialog_FillSettings_AB233EA745, UiStrings.S_LayerEffectsDialog_FillSettings_541ACE652F], s.Softness, k => set(o => S(o) with { Softness = k })));
        AddMotionEditors(Selected, set);
        _settings.Children.Add(Hint(UiStrings.S_LayerEffectsDialog_FillSettings_2118984CA5));
    }
}
