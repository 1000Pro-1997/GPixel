using Gpixel.Core.Effects;
using Gpixel.Core.Model;
using Gpixel.App.Localization;

namespace Gpixel.App;

public sealed partial class LayerEffectsDialog
{
    /// <summary>효과 창·타임라인 표시: 이름, 설명, 타임라인 색, 설정 칸, 목록 요약.</summary>
    private static EffectUi ColorOverlayUi => EffectUi.For<ColorOverlayEffect>(ColorOverlayDefinition.Id, UiStrings.S_LayerEffectsDialog_KindTable_C29C82BC8E, UiStrings.S_LayerEffectsDialog_KindTable_03AE924348,
        Avalonia.Media.Color.FromRgb(196, 96, 170), (dialog, e, set) => dialog.ColorOverlayPanel(e, set),
        c => string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Summary_F824955A0D,Blends[(int)c.Blend],c.Amount,c.Color.ToHex()));

    /// <summary>색 덮기 설정: 색, 세기, 섞기.</summary>
    private void ColorOverlayPanel(ColorOverlayEffect c, Action<Func<LayerEffect, LayerEffect>> set)
    {
        SettingsRow(UiStrings.S_LayerEffectsDialog_FillSettings_F6DB7C3B67, EffectColorEditor(c, nameof(ColorOverlayEffect.Color), ColorEditor(c.Color, v => set(x => (ColorOverlayEffect)x with { Color = v })), set));
        SettingsRow(UiStrings.S_LayerEffectsDialog_FillSettings_5522C8DF65, NumEditor(0, 100, c.Amount, NumKinds.Strength, v => set(x => (ColorOverlayEffect)x with { Amount = v })), Ui.Label("%"));
        SettingsRow(UiStrings.S_LayerEffectsDialog_FillSettings_5DD23B47AC, ChoiceEditor(Blends, (int)c.Blend, k => set(x => (ColorOverlayEffect)x with { Blend = (EffectBlend)k })));
        _settings.Children.Add(Hint(UiStrings.S_LayerEffectsDialog_FillSettings_BD67E1B5C4));
    }
}
