using Gpixel.Core.Effects;
using Gpixel.Core.Model;
using Gpixel.App.Localization;

namespace Gpixel.App;

public sealed partial class LayerEffectsDialog
{
    /// <summary>효과 창·타임라인 표시: 이름, 설명, 타임라인 색, 설정 칸, 목록 요약.</summary>
    private static EffectUi MeltUi => EffectUi.For<MeltEffect>(MeltDefinition.Id, UiStrings.S_LayerEffectsDialog_KindTable_4A3575A660, UiStrings.S_LayerEffectsDialog_KindTable_1B49B1765C,
        Avalonia.Media.Color.FromRgb(160, 112, 72), (dialog, e, set) => dialog.MeltPanel(e, set),
        melt => string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Summary_DA48E085AF,melt.Distance,melt.Irregularity));

    /// <summary>녹아내리기 설정: 거리, 불규칙함, 늘어짐, 사라짐, 시드.</summary>
    private void MeltPanel(MeltEffect e, Action<Func<LayerEffect, LayerEffect>> set)
    {
        static MeltEffect M(LayerEffect v) => (MeltEffect)v;
        SettingsRow(UiStrings.S_MeltPanel_MeltPanel_F9F8B83973, NumEditor(1, MeltEffect.MaxDistance, e.Distance, NumKinds.Distance, v => set(o => M(o) with { Distance = v })), Ui.Label(UiStrings.S_MeltPanel_MeltPanel_6EDA79E298));
        SettingsRow(UiStrings.S_MeltPanel_MeltPanel_51CBEF5EB8, NumEditor(0, 100, e.Irregularity, NumKinds.Random, v => set(o => M(o) with { Irregularity = v })), Ui.Label(UiStrings.S_MeltPanel_MeltPanel_54B1D35C64));
        SettingsRow("", CheckEditor(UiStrings.S_MeltPanel_MeltPanel_05D178E28F, e.Stretch, v => set(o => M(o) with { Stretch = v })),
            CheckEditor(UiStrings.S_MeltPanel_MeltPanel_A67CF586F8, e.Fade, v => set(o => M(o) with { Fade = v })));
        var seed = NumEditor(0, MeltEffect.MaxSeed, e.Seed, NumKinds.Seed, v => set(o => M(o) with { Seed = v }));
        var reroll = Ui.IconButton(VectorIcon.Dice, UiStrings.S_MeltPanel_MeltPanel_B26E6FC89A, (_, _) => seed.Value = System.Random.Shared.Next(0, MeltEffect.MaxSeed + 1), 36);
        SettingsRow(UiStrings.S_MeltPanel_MeltPanel_E8772786EA, seed, reroll);
        AddMotionEditors(Selected, set);
        _settings.Children.Add(Hint(UiStrings.S_MeltPanel_MeltPanel_CB59847BAC
                                    + UiStrings.S_MeltPanel_MeltPanel_C1680743FF));
    }
}
