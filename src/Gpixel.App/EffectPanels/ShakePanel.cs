using Gpixel.Core.Effects;
using Gpixel.Core.Model;
using Gpixel.App.Localization;

namespace Gpixel.App;

public sealed partial class LayerEffectsDialog
{
    /// <summary>효과 창·타임라인 표시: 이름, 설명, 타임라인 색, 설정 칸, 목록 요약.</summary>
    private static EffectUi ShakeUi => EffectUi.For<ShakeEffect>(ShakeDefinition.Id, UiStrings.S_LayerEffectsDialog_KindTable_2C5AB3754D, UiStrings.S_LayerEffectsDialog_KindTable_043CDEE3B8,
        Avalonia.Media.Color.FromRgb(186, 84, 60), (dialog, e, set) => dialog.ShakePanel(e, set),
        shake => string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Summary_92D1EF3203,Axes[(int)shake.Axis],shake.Amount,shake.Count) + (shake.Decay ? UiStrings.S_LayerEffectsDialog_Summary_1D40117C02 : "") + (shake.Regular ? UiStrings.S_LayerEffectsDialog_Summary_4DA5BACD5A : ""));

    private static readonly string[] Axes = [UiStrings.S_ShakePanel_Axes_53CC4B039D, UiStrings.S_ShakePanel_Axes_8ACB726509, UiStrings.S_ShakePanel_Axes_0BA14E7C64];

    /// <summary>흔들기 설정: 방향, 세기, 횟수, 줄어듦, 움직임(무작위 시드 / 규칙적).</summary>
    private void ShakePanel(ShakeEffect e, Action<Func<LayerEffect, LayerEffect>> set)
    {
        static ShakeEffect S(LayerEffect v) => (ShakeEffect)v;
        SettingsRow(UiStrings.S_ShakePanel_ShakePanel_96BBC223BF, ChoiceEditor(Axes, (int)e.Axis, k => set(o => S(o) with { Axis = (EffectAxis)k })));
        SettingsRow(UiStrings.S_ShakePanel_ShakePanel_06FDF78CFA, NumEditor(1, ShakeEffect.MaxAmount, e.Amount, NumKinds.Px, v => set(o => S(o) with { Amount = v })), Ui.Label("px"),
            Ui.Label(UiStrings.S_ShakePanel_ShakePanel_2DBC91C1A8), NumEditor(1, ShakeEffect.MaxCount, e.Count, NumKinds.Count, v => set(o => S(o) with { Count = v })), Ui.Label(UiStrings.S_ShakePanel_ShakePanel_3A0EE81AC1));
        SettingsRow("", CheckEditor(UiStrings.S_ShakePanel_ShakePanel_C4C270EF23, e.Decay, v => set(o => S(o) with { Decay = v })));
        var seed = NumEditor(0, ShakeEffect.MaxSeed, e.Seed, NumKinds.Seed, v => set(o => S(o) with { Seed = v }));
        var reroll = Ui.IconButton(VectorIcon.Dice, UiStrings.S_ShakePanel_ShakePanel_9C336915F0, (_, _) => seed.Value = System.Random.Shared.Next(0, ShakeEffect.MaxSeed + 1), 36);
        seed.IsEnabled = reroll.IsEnabled = !e.Regular;
        SettingsRow(UiStrings.S_ShakePanel_ShakePanel_F0EB0186CC, ChoiceEditor([UiStrings.S_ShakePanel_ShakePanel_39DA4FC0D1, UiStrings.S_ShakePanel_ShakePanel_C65B0A22CA], e.Regular ? 1 : 0, k =>
        {
            seed.IsEnabled = reroll.IsEnabled = k == 0;
            set(o => S(o) with { Regular = k == 1 });
        }));
        SettingsSubRow(UiStrings.S_ShakePanel_ShakePanel_84B7AD2C03, seed, reroll);
        AddMotionEditors(Selected, set);
        _settings.Children.Add(Hint(UiStrings.S_ShakePanel_ShakePanel_129203CA72));
    }
}
