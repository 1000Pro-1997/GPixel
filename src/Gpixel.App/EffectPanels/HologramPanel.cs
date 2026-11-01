using Gpixel.Core.Effects;
using Gpixel.Core.Model;
using Gpixel.App.Localization;

namespace Gpixel.App;

public sealed partial class LayerEffectsDialog
{
    /// <summary>효과 창·타임라인 표시: 이름, 설명, 타임라인 색, 설정 칸, 목록 요약.</summary>
    private static EffectUi HologramUi => EffectUi.For<HologramEffect>(HologramDefinition.Id, UiStrings.S_LayerEffectsDialog_KindTable_2B322D7A38, UiStrings.S_LayerEffectsDialog_KindTable_EF5C5A3497,
        Avalonia.Media.Color.FromRgb(0, 180, 255), (dialog, e, set) => dialog.HologramPanel(e, set),
        holo => string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Summary_8FC3C16840,holo.Color.ToHex(),holo.TintStrength,holo.Opacity) + (holo.Dither ? UiStrings.S_LayerEffectsDialog_Summary_823544BC03 : "")
            + (holo.GlitchRate > 0 ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Summary_7B535BB782,holo.GlitchRate) : ""));

    /// <summary>홀로그램 설정: 색·색 세기, 주사선(간격·세기·방식·흐르기), 밝은 띠, 투명도(디더), 깜빡임, 글리치(RGB 분리), 테두리 빛, 시드.</summary>
    private void HologramPanel(HologramEffect e, Action<Func<LayerEffect, LayerEffect>> set)
    {
        static HologramEffect H(LayerEffect v) => (HologramEffect)v;
        SettingsRow(UiStrings.S_HologramPanel_HologramPanel_E28C969851, EffectColorEditor(e, nameof(HologramEffect.Color),
            ColorEditor(e.Color, v => set(o => H(o) with { Color = v })), set));
        SettingsRow(UiStrings.S_HologramPanel_HologramPanel_1FC95DFB01, NumEditor(0, 100, e.TintStrength, NumKinds.Strength, v => set(o => H(o) with { TintStrength = v })), Ui.Label("%"));
        SettingsRow(UiStrings.S_HologramPanel_HologramPanel_F5867029B4, NumEditor(HologramEffect.MinScanGap, HologramEffect.MaxScanGap, e.ScanGap, NumKinds.ScanGap, v => set(o => H(o) with { ScanGap = v })), Ui.Label(UiStrings.S_HologramPanel_HologramPanel_972F2E71DA),
            Ui.Label(UiStrings.S_HologramPanel_HologramPanel_6178D2E678), NumEditor(0, 100, e.ScanStrength, NumKinds.Strength, v => set(o => H(o) with { ScanStrength = v })), Ui.Label(UiStrings.S_HologramPanel_HologramPanel_9B9738A14C));
        SettingsSubRow(UiStrings.S_HologramPanel_HologramPanel_AF6E8C1272, ChoiceEditor([UiStrings.S_HologramPanel_HologramPanel_BB87A93F94, UiStrings.S_HologramPanel_HologramPanel_07629FDFDF], (int)e.ScanStyle, k => set(o => H(o) with { ScanStyle = (HologramScanStyle)k })),
            CheckEditor(UiStrings.S_HologramPanel_HologramPanel_966D3D0180, e.ScanScroll, v => set(o => H(o) with { ScanScroll = v })));
        var bandSize = NumEditor(1, HologramEffect.MaxBandSize, e.BandSize, NumKinds.Thickness, v => set(o => H(o) with { BandSize = v }));
        var bandStrength = NumEditor(0, 100, e.BandStrength, NumKinds.Strength, v => set(o => H(o) with { BandStrength = v }));
        bandSize.IsEnabled = bandStrength.IsEnabled = e.Band;
        SettingsCheckRow(CheckEditor(UiStrings.S_HologramPanel_HologramPanel_2A2020E4D9, e.Band, v => { bandSize.IsEnabled = bandStrength.IsEnabled = v; set(o => H(o) with { Band = v }); }),
            Ui.Label(UiStrings.S_HologramPanel_HologramPanel_9F93A9BB2A), bandSize, Ui.Label("px"), Ui.Label(UiStrings.S_HologramPanel_HologramPanel_6178D2E678), bandStrength, Ui.Label("%"));
        SettingsRow(UiStrings.S_HologramPanel_HologramPanel_1BF34BE317, NumEditor(0, 100, e.Opacity, NumKinds.Opacity, v => set(o => H(o) with { Opacity = v })), Ui.Label("%"));
        SettingsRow("", CheckEditor(UiStrings.S_HologramPanel_HologramPanel_BAED2225EC, e.Dither, v => set(o => H(o) with { Dither = v })));
        SettingsRow(UiStrings.S_HologramPanel_HologramPanel_C49A6BC313, NumEditor(0, 100, e.FlickerRate, NumKinds.Rate, v => set(o => H(o) with { FlickerRate = v })), Ui.Label("%"),
            Ui.Label(UiStrings.S_HologramPanel_HologramPanel_6178D2E678), NumEditor(0, 100, e.FlickerStrength, NumKinds.Strength, v => set(o => H(o) with { FlickerStrength = v })), Ui.Label("%"));
        SettingsRow(UiStrings.S_HologramPanel_HologramPanel_E9515F56FA, NumEditor(0, 100, e.GlitchRate, NumKinds.Rate, v => set(o => H(o) with { GlitchRate = v })), Ui.Label("%"),
            Ui.Label(UiStrings.S_HologramPanel_HologramPanel_48D9F3060D), NumEditor(1, HologramEffect.MaxGlitchShift, e.GlitchShift, NumKinds.Px, v => set(o => H(o) with { GlitchShift = v })), Ui.Label("px"));
        SettingsRow("", CheckEditor(UiStrings.S_HologramPanel_HologramPanel_A5CD9BF54F, e.RgbSplit, v => set(o => H(o) with { RgbSplit = v })).Tip(UiStrings.S_HologramPanel_HologramPanel_389F58CAAE));
        var edge = NumEditor(0, 100, e.EdgeOpacity, NumKinds.Strength, v => set(o => H(o) with { EdgeOpacity = v }));
        edge.IsEnabled = e.EdgeGlow;
        SettingsCheckRow(CheckEditor(UiStrings.S_HologramPanel_HologramPanel_412239FF25, e.EdgeGlow, v => { edge.IsEnabled = v; set(o => H(o) with { EdgeGlow = v }); }).Tip(UiStrings.S_HologramPanel_HologramPanel_7898EACDA1),
            Ui.Label(UiStrings.S_HologramPanel_HologramPanel_13DAB2BCA6), edge, Ui.Label("%"));
        var seed = NumEditor(0, HologramEffect.MaxSeed, e.Seed, NumKinds.Seed, v => set(o => H(o) with { Seed = v }));
        var reroll = Ui.IconButton(VectorIcon.Dice, UiStrings.S_HologramPanel_HologramPanel_B535B6DA1C, (_, _) => seed.Value = System.Random.Shared.Next(0, HologramEffect.MaxSeed + 1), 36);
        SettingsRow(UiStrings.S_HologramPanel_HologramPanel_22ACBFBD6E, seed, reroll);
        AddMotionEditors(Selected, set);
        _settings.Children.Add(Hint(UiStrings.S_HologramPanel_HologramPanel_8BEC9DAD82
                                    + UiStrings.S_HologramPanel_HologramPanel_D3554CA8CE));
        _settings.Children.Add(Hint(UiStrings.S_HologramPanel_HologramPanel_359C86A8C6
                                    + UiStrings.S_HologramPanel_HologramPanel_CEBC71D120));
    }
}
