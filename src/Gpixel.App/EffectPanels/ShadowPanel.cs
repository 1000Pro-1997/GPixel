using Gpixel.Core.Effects;
using Gpixel.Core.Model;
using Gpixel.App.Localization;

namespace Gpixel.App;

public sealed partial class LayerEffectsDialog
{
    /// <summary>효과 창·타임라인 표시: 이름, 설명, 타임라인 색, 설정 칸, 목록 요약.</summary>
    private static EffectUi ShadowUi => EffectUi.For<ShadowEffect>(ShadowDefinition.Id, UiStrings.S_LayerEffectsDialog_KindTable_10CD6603BC, UiStrings.S_LayerEffectsDialog_KindTable_2576225185,
        Avalonia.Media.Color.FromRgb(118, 124, 196), (dialog, e, set) => dialog.ShadowPanel(e, set),
        s => string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Summary_E233F2401C,s.OffsetX,s.OffsetY,s.Color.ToHex()) + (s.Dither != ShadowDither.None ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Summary_FDDC95CFEA,ShadowDithers[(int)s.Dither]) : ""));

    private static readonly string[] ShadowDithers = [UiStrings.S_LayerEffectsDialog_ShadowDithers_85A6B2AD26, "25%", UiStrings.S_LayerEffectsDialog_ShadowDithers_BC77BAD2B0, "75%"];

    /// <summary>그림자 설정: 색, X·Y 옮김, 디더 밀도.</summary>
    private void ShadowPanel(ShadowEffect s, Action<Func<LayerEffect, LayerEffect>> set)
    {
        SettingsRow(UiStrings.S_LayerEffectsDialog_FillSettings_F6DB7C3B67, EffectColorEditor(s, nameof(ShadowEffect.Color), ColorEditor(s.Color, c => set(x => (ShadowEffect)x with { Color = c })), set));
        SettingsRow("X",
            NumEditor(-ShadowEffect.MaxOffset, ShadowEffect.MaxOffset, s.OffsetX, NumKinds.Offset, v => set(x => (ShadowEffect)x with { OffsetX = v })),
            Ui.Label("Y"),
            NumEditor(-ShadowEffect.MaxOffset, ShadowEffect.MaxOffset, s.OffsetY, NumKinds.Offset, v => set(x => (ShadowEffect)x with { OffsetY = v })),
            Ui.Label("px"));
        SettingsRow(UiStrings.S_LayerEffectsDialog_FillSettings_FEA25AE8B2, ChoiceEditor(ShadowDithers, (int)s.Dither, k => set(x => (ShadowEffect)x with { Dither = (ShadowDither)k })));
        _settings.Children.Add(Hint(UiStrings.S_LayerEffectsDialog_FillSettings_9577438EC7));
    }
}
