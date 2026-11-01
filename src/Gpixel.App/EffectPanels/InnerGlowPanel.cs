using Gpixel.Core.Effects;
using Gpixel.Core.Model;
using Gpixel.App.Localization;

namespace Gpixel.App;

public sealed partial class LayerEffectsDialog
{
    /// <summary>효과 창·타임라인 표시: 이름, 설명, 타임라인 색, 설정 칸, 목록 요약.</summary>
    private static EffectUi InnerGlowUi => EffectUi.For<InnerGlowEffect>(InnerGlowDefinition.Id, UiStrings.S_LayerEffectsDialog_KindTable_25FE384908, UiStrings.S_LayerEffectsDialog_KindTable_6134E7833F,
        Avalonia.Media.Color.FromRgb(250, 236, 170), (dialog, e, set) => dialog.InnerGlowPanel(e, set),
        g => string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Summary_B8FE86B3A0,g.Size,g.Strength,g.Color.ToHex()));

    /// <summary>안쪽 빛 설정: 색, 두께, 세기, 섞기, 안쪽으로 약해짐.</summary>
    private void InnerGlowPanel(InnerGlowEffect g, Action<Func<LayerEffect, LayerEffect>> set)
    {
        static InnerGlowEffect G(LayerEffect v) => (InnerGlowEffect)v;
        SettingsRow(UiStrings.S_InnerGlowPanel_InnerGlowPanel_4E24CB6117, EffectColorEditor(g, nameof(InnerGlowEffect.Color),
            ColorEditor(g.Color, v => set(o => G(o) with { Color = v })), set));
        SettingsRow(UiStrings.S_InnerGlowPanel_InnerGlowPanel_F17849B03D, NumEditor(1, InnerGlowEffect.MaxSize, g.Size, NumKinds.Thickness, v => set(o => G(o) with { Size = v })), Ui.Label("px"),
            Ui.Label(UiStrings.S_InnerGlowPanel_InnerGlowPanel_71F36E8E3A), NumEditor(0, 100, g.Strength, NumKinds.Strength, v => set(o => G(o) with { Strength = v })), Ui.Label("%"));
        SettingsRow(UiStrings.S_InnerGlowPanel_InnerGlowPanel_A12FD928D4, ChoiceEditor(Blends, (int)g.Blend, k => set(o => G(o) with { Blend = (EffectBlend)k })));
        SettingsRow("", CheckEditor(UiStrings.S_InnerGlowPanel_InnerGlowPanel_960F2D4C03, g.Fade, v => set(o => G(o) with { Fade = v })));
        _settings.Children.Add(Hint(UiStrings.S_InnerGlowPanel_InnerGlowPanel_E8C879B3C5));
    }
}
