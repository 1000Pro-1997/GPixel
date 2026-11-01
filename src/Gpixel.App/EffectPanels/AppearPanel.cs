using Gpixel.Core.Effects;
using Gpixel.Core.Model;
using Gpixel.App.Localization;

namespace Gpixel.App;

public sealed partial class LayerEffectsDialog
{
    /// <summary>효과 창·타임라인 표시: 이름, 설명, 타임라인 색, 설정 칸, 목록 요약.</summary>
    private static EffectUi AppearUi => EffectUi.For<AppearEffect>(AppearDefinition.Id, UiStrings.S_LayerEffectsDialog_KindTable_F6C9786974, UiStrings.S_LayerEffectsDialog_KindTable_FFF387B9B9,
        Avalonia.Media.Color.FromRgb(206, 232, 86), (dialog, e, set) => dialog.AppearPanel(e, set),
        appear => $"{appear.DisplayName} · {AppearStyles[(int)appear.Style]}" + (appear.Style == AppearStyle.Wipe ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Summary_C36AC46BAE,WipeFroms[(int)appear.From]) : ""));

    private static readonly string[] AppearStyles = [UiStrings.S_AppearPanel_AppearStyles_E0D9F50D27, UiStrings.S_AppearPanel_AppearStyles_ECD851BCD0, UiStrings.S_AppearPanel_AppearStyles_46BC34F4A2, UiStrings.S_AppearPanel_AppearStyles_9CC60E3158];
    private static readonly string[] WipeFroms = [UiStrings.S_AppearPanel_WipeFroms_EB53C9C591, UiStrings.S_AppearPanel_WipeFroms_0918652A12, UiStrings.S_AppearPanel_WipeFroms_958C17A305, UiStrings.S_AppearPanel_WipeFroms_A2A7C74FBD];

    /// <summary>나타나기 / 사라지기 설정: 모드, 방식, 쓸기 시작 쪽·가장자리, 흩어짐 시드.</summary>
    private void AppearPanel(AppearEffect e, Action<Func<LayerEffect, LayerEffect>> set)
    {
        static AppearEffect A(LayerEffect v) => (AppearEffect)v;
        SettingsRow(UiStrings.S_AppearPanel_AppearPanel_F80AD5770E, ChoiceEditor([UiStrings.S_AppearPanel_AppearPanel_90162B77C1, UiStrings.S_AppearPanel_AppearPanel_BBFA2D6AB2], e.Disappear ? 1 : 0, k => set(o => A(o) with { Disappear = k == 1 })));
        var from = ChoiceEditor(WipeFroms, (int)e.From, k => set(o => A(o) with { From = (WipeDirection)k }));
        var edge = ChoiceEditor([UiStrings.S_AppearPanel_AppearPanel_098B9035F3, UiStrings.S_AppearPanel_AppearPanel_FD5AFA1D94, UiStrings.S_AppearPanel_AppearPanel_4E8113A1CD], e.Edge, k => set(o => A(o) with { Edge = k }));
        var seed = NumEditor(0, AppearEffect.MaxSeed, e.Seed, NumKinds.Seed, v => set(o => A(o) with { Seed = v }));
        var reroll = Ui.IconButton(VectorIcon.Dice, UiStrings.S_AppearPanel_AppearPanel_C98C9F62B6, (_, _) => seed.Value = System.Random.Shared.Next(0, AppearEffect.MaxSeed + 1), 36);
        void Sync(AppearStyle s)
        {
            from.IsEnabled = edge.IsEnabled = s == AppearStyle.Wipe;
            seed.IsEnabled = reroll.IsEnabled = s == AppearStyle.Scatter;
        }
        Sync(e.Style);
        SettingsRow(UiStrings.S_AppearPanel_AppearPanel_A482C10A17, ChoiceEditor(AppearStyles, (int)e.Style, k =>
        {
            Sync((AppearStyle)k);
            set(o => A(o) with { Style = (AppearStyle)k });
        }));
        SettingsSubRow(UiStrings.S_AppearPanel_AppearPanel_9968F0C72F, from);
        SettingsSubRow(UiStrings.S_AppearPanel_AppearPanel_5B6934AD2F, edge);
        SettingsSubRow(UiStrings.S_AppearPanel_AppearPanel_B64448AEFD, seed, reroll);
        AddMotionEditors(Selected, set);
        _settings.Children.Add(Hint(UiStrings.S_AppearPanel_AppearPanel_43B9AEFDD3
                                    + UiStrings.S_AppearPanel_AppearPanel_EDC4509A6B));
    }
}
