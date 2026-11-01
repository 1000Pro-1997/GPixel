using Gpixel.Core.Effects;
using Gpixel.Core.Model;
using Gpixel.App.Localization;

namespace Gpixel.App;

public sealed partial class LayerEffectsDialog
{
    /// <summary>효과 창·타임라인 표시: 이름, 설명, 타임라인 색, 설정 칸, 목록 요약.</summary>
    private static EffectUi ShockwaveUi => EffectUi.For<ShockwaveEffect>(ShockwaveDefinition.Id, UiStrings.S_LayerEffectsDialog_KindTable_71D60FCC7E, UiStrings.S_LayerEffectsDialog_KindTable_CB2F9D0913,
        Avalonia.Media.Color.FromRgb(255, 214, 40), (dialog, e, set) => dialog.ShockwavePanel(e, set),
        wave => string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Summary_D07B4E5446,wave.Radius,wave.Thickness,(wave.Behind ? UiStrings.S_LayerEffectsDialog_Summary_E3B52B00EB : UiStrings.S_LayerEffectsDialog_Summary_BC26FFFDAA)));

    /// <summary>충격파 고리 설정: 중심(+ 직접 X·Y), 최대 반지름, 두께, 색, 사라짐, 앞/뒤.</summary>
    private void ShockwavePanel(ShockwaveEffect e, Action<Func<LayerEffect, LayerEffect>> set)
    {
        static ShockwaveEffect W(LayerEffect v) => (ShockwaveEffect)v;
        var cx = NumEditor(-PixelImage.MaxSize, PixelImage.MaxSize, e.CenterX, CenterKind(vertical: false), v => set(o => W(o) with { CenterX = v }));
        var cy = NumEditor(-PixelImage.MaxSize, PixelImage.MaxSize, e.CenterY, CenterKind(vertical: true), v => set(o => W(o) with { CenterY = v }));
        cx.IsEnabled = cy.IsEnabled = e.Center == ExplodeCenter.Custom;
        SettingsRow(UiStrings.S_ShockwavePanel_ShockwavePanel_005BCC51DE, ChoiceEditor(Centers, (int)e.Center, k =>
        {
            cx.IsEnabled = cy.IsEnabled = (ExplodeCenter)k == ExplodeCenter.Custom;
            set(o => W(o) with { Center = (ExplodeCenter)k });
        }));
        SettingsSubRow("X", cx, Ui.Label("Y"), cy, Ui.Label(UiStrings.S_ShockwavePanel_ShockwavePanel_8A907D46C9));
        SettingsRow(UiStrings.S_ShockwavePanel_ShockwavePanel_CBB90B0ACA, NumEditor(1, ShockwaveEffect.MaxRadius, e.Radius, NumKinds.Distance, v => set(o => W(o) with { Radius = v })), Ui.Label("px"),
            Ui.Label(UiStrings.S_ShockwavePanel_ShockwavePanel_61134FA980), NumEditor(1, ShockwaveEffect.MaxThickness, e.Thickness, NumKinds.Thickness, v => set(o => W(o) with { Thickness = v })), Ui.Label("px"));
        SettingsRow(UiStrings.S_ShockwavePanel_ShockwavePanel_D20485811B, EffectColorEditor(e, nameof(ShockwaveEffect.Color),
            ColorEditor(e.Color, v => set(o => W(o) with { Color = v })), set));
        SettingsRow("", CheckEditor(UiStrings.S_ShockwavePanel_ShockwavePanel_DF8C5BBEB2, e.Fade, v => set(o => W(o) with { Fade = v })));
        SettingsRow(UiStrings.S_ShockwavePanel_ShockwavePanel_632117D3C6, ChoiceEditor([UiStrings.S_ShockwavePanel_ShockwavePanel_B5365A0F6F, UiStrings.S_ShockwavePanel_ShockwavePanel_0E8726B50C], e.Behind ? 1 : 0, k => set(o => W(o) with { Behind = k == 1 })));
        AddMotionEditors(Selected, set);
        _settings.Children.Add(Hint(UiStrings.S_ShockwavePanel_ShockwavePanel_2DDDFC38DA));
    }
}
