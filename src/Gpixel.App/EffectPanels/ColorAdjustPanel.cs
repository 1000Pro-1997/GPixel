using Gpixel.Core.Effects;
using Gpixel.Core.Model;
using Gpixel.App.Localization;

namespace Gpixel.App;

public sealed partial class LayerEffectsDialog
{
    /// <summary>효과 창·타임라인 표시: 이름, 설명, 타임라인 색, 설정 칸, 목록 요약.</summary>
    private static EffectUi ColorAdjustUi => EffectUi.For<ColorAdjustEffect>(ColorAdjustDefinition.Id, UiStrings.S_LayerEffectsDialog_KindTable_CA9E953421, UiStrings.S_LayerEffectsDialog_KindTable_02A04E0B04,
        Avalonia.Media.Color.FromRgb(160, 160, 160), (dialog, e, set) => dialog.ColorAdjustPanel(e, set),
        c => string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Summary_62017EE627,AdjustSummary(c)));

    private static readonly string[] AdjustModes = [UiStrings.S_ColorAdjustPanel_AdjustModes_2C243D40ED, UiStrings.S_ColorAdjustPanel_AdjustModes_7D62C1B761, UiStrings.S_ColorAdjustPanel_AdjustModes_C19765AF80, UiStrings.S_ColorAdjustPanel_AdjustModes_0FF6A836A6, UiStrings.S_ColorAdjustPanel_AdjustModes_66B88ADD3C];

    private static string AdjustSummary(ColorAdjustEffect c) => AdjustModes[(int)c.Mode] + c.Mode switch
    {
        ColorAdjustMode.HueShift => $" {c.Hue:+0;-0;0}°",
        ColorAdjustMode.BrightnessContrast => $" {c.Brightness:+0;-0;0} / {c.Contrast:+0;-0;0}",
        ColorAdjustMode.Saturation => $" {c.Saturation:+0;-0;0}%",
        _ => $" {c.Amount}%",
    };

    /// <summary>색조 보정 설정: 방식(바꾸면 그 방식의 값만 보임)과 값.</summary>
    private void ColorAdjustPanel(ColorAdjustEffect c, Action<Func<LayerEffect, LayerEffect>> set)
    {
        static ColorAdjustEffect C(LayerEffect v) => (ColorAdjustEffect)v;
        SettingsRow(UiStrings.S_ColorAdjustPanel_ColorAdjustPanel_1F1978AFAD, ChoiceEditor(AdjustModes, (int)c.Mode, k =>
        {
            set(o => C(o) with { Mode = (ColorAdjustMode)k });
            ShowSettings(); // 그 방식의 값 칸으로
        }));
        switch (c.Mode)
        {
            case ColorAdjustMode.Grayscale:
            case ColorAdjustMode.Sepia:
                SettingsRow(UiStrings.S_ColorAdjustPanel_ColorAdjustPanel_703B3CD995, NumEditor(0, 100, c.Amount, NumKinds.Strength, v => set(o => C(o) with { Amount = v })), Ui.Label("%"));
                break;
            case ColorAdjustMode.HueShift:
                SettingsRow(UiStrings.S_ColorAdjustPanel_ColorAdjustPanel_7292A6B699, NumEditor(-ColorAdjustEffect.MaxHue, ColorAdjustEffect.MaxHue, c.Hue, NumKinds.Hue, v => set(o => C(o) with { Hue = v })), Ui.Label(UiStrings.S_ColorAdjustPanel_ColorAdjustPanel_F55E326D7F));
                break;
            case ColorAdjustMode.BrightnessContrast:
                SettingsRow(UiStrings.S_ColorAdjustPanel_ColorAdjustPanel_8395FE49C9, NumEditor(-100, 100, c.Brightness, NumKinds.Adjust, v => set(o => C(o) with { Brightness = v })), Ui.Label("%"),
                    Ui.Label(UiStrings.S_ColorAdjustPanel_ColorAdjustPanel_001F11B58B), NumEditor(-100, 100, c.Contrast, NumKinds.Adjust, v => set(o => C(o) with { Contrast = v })), Ui.Label("%"));
                break;
            case ColorAdjustMode.Saturation:
                SettingsRow(UiStrings.S_ColorAdjustPanel_ColorAdjustPanel_B92B34DA64, NumEditor(-100, 100, c.Saturation, NumKinds.Adjust, v => set(o => C(o) with { Saturation = v })), Ui.Label(UiStrings.S_ColorAdjustPanel_ColorAdjustPanel_DFBB738FE0));
                break;
        }
        _settings.Children.Add(Hint(UiStrings.S_ColorAdjustPanel_ColorAdjustPanel_65D7B7FC26));
    }
}
