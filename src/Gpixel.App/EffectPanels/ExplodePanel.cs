using Gpixel.Core.Effects;
using Gpixel.Core.Model;
using Gpixel.App.Localization;

namespace Gpixel.App;

public sealed partial class LayerEffectsDialog
{
    /// <summary>효과 창·타임라인 표시: 이름, 설명, 타임라인 색, 설정 칸, 목록 요약.</summary>
    private static EffectUi ExplodeUi => EffectUi.For<ExplodeEffect>(ExplodeDefinition.Id, UiStrings.S_LayerEffectsDialog_KindTable_AC8A98B707, UiStrings.S_LayerEffectsDialog_KindTable_91F153F61A,
        Avalonia.Media.Color.FromRgb(236, 72, 40), (dialog, e, set) => dialog.ExplodePanel(e, set),
        x => string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Summary_DE0B23D063,x.DistanceX,x.DistanceY,Centers[(int)x.Center].Split(' ')[0]) + (x.KeepOriginal ? UiStrings.S_LayerEffectsDialog_Summary_30757AF9AF : ""));

    /// <summary>터지기 설정: 거리, 중심(+ 직접 X·Y), 흩어짐, 중력, 조각 크기·작아짐, 사라짐, 번쩍임, 원본 남기기, 시드.</summary>
    private void ExplodePanel(ExplodeEffect x, Action<Func<LayerEffect, LayerEffect>> set)
    {
        ExplodeEffect X(LayerEffect v) => (ExplodeEffect)v;
        var distanceY = NumEditor(0, ExplodeEffect.MaxDistance, x.DistanceY, NumKinds.Distance, v => set(o => X(o) with { DistanceY = v }));
        ToolTip.SetTip(distanceY, UiStrings.Explode_DistanceYTip);
        SettingsRow(UiStrings.S_LayerEffectsDialog_FillSettings_98D374602B,
            Ui.Label("X"), NumEditor(0, ExplodeEffect.MaxDistance, x.DistanceX, NumKinds.Distance, v => set(o => X(o) with { DistanceX = v })), Ui.Label("px"),
            Ui.Label("Y"), distanceY, Ui.Label("px"));
        var cx = NumEditor(-PixelImage.MaxSize, PixelImage.MaxSize, x.CenterX, CenterKind(vertical: false), v => set(o => X(o) with { CenterX = v }));
        var cy = NumEditor(-PixelImage.MaxSize, PixelImage.MaxSize, x.CenterY, CenterKind(vertical: true), v => set(o => X(o) with { CenterY = v }));
        cx.IsEnabled = cy.IsEnabled = x.Center == ExplodeCenter.Custom;
        SettingsRow(UiStrings.S_LayerEffectsDialog_FillSettings_5C5C39442A, ChoiceEditor(Centers, (int)x.Center, k =>
        {
            cx.IsEnabled = cy.IsEnabled = (ExplodeCenter)k == ExplodeCenter.Custom;
            set(o => X(o) with { Center = (ExplodeCenter)k });
        }));
        SettingsSubRow("X", cx, Ui.Label("Y"), cy, Ui.Label(UiStrings.S_LayerEffectsDialog_FillSettings_A89FB839CA));
        SettingsRow(UiStrings.S_LayerEffectsDialog_FillSettings_9E6E8E6EFE,
            Ui.Label("X"), NumEditor(0, 100, x.SpreadX, NumKinds.Random, v => set(o => X(o) with { SpreadX = v })), Ui.Label("%"),
            Ui.Label("Y"), NumEditor(0, 100, x.SpreadY, NumKinds.Random, v => set(o => X(o) with { SpreadY = v })), Ui.Label("%"));
        SettingsRow(UiStrings.S_LayerEffectsDialog_FillSettings_1C82F0E839,
            NumEditor(-ExplodeEffect.MaxGravity, ExplodeEffect.MaxGravity, x.Gravity, NumKinds.Gravity, v => set(o => X(o) with { Gravity = v })), Ui.Label("px"));
        SettingsRow(UiStrings.S_LayerEffectsDialog_FillSettings_E1CC351651, NumEditor(1, ExplodeEffect.MaxPieceSize, x.PieceSize, NumKinds.Piece, v => set(o => X(o) with { PieceSize = v })), Ui.Label("px"),
            CheckEditor(UiStrings.S_LayerEffectsDialog_FillSettings_880B57A4CE, x.Shrink, v => set(o => X(o) with { Shrink = v })));
        var fadeStart = NumEditor(0, 100, x.FadeStart, NumKinds.Progress, v => set(o => X(o) with { FadeStart = v }));
        fadeStart.IsEnabled = x.Fade;
        SettingsCheckRow(CheckEditor(UiStrings.S_LayerEffectsDialog_FillSettings_08EF532DFB, x.Fade, v => { fadeStart.IsEnabled = v; set(o => X(o) with { Fade = v }); }).Tip(UiStrings.S_LayerEffectsDialog_FillSettings_E831CF80BB),
            Ui.Label(UiStrings.S_LayerEffectsDialog_FillSettings_E8E9684F4B), fadeStart, Ui.Label("%"));
        SettingsRow(UiStrings.S_LayerEffectsDialog_FillSettings_288AED0ECC, NumEditor(0, ExplodeEffect.MaxFlashFrames, x.FlashFrames, NumKinds.Frames, v => set(o => X(o) with { FlashFrames = v })), Ui.Label(UiStrings.S_LayerEffectsDialog_FillSettings_5F8BA1F369));
        SettingsSubRow(UiStrings.S_LayerEffectsDialog_FillSettings_6D8B67BF59, EffectColorEditor(x, nameof(ExplodeEffect.FlashColor), ColorEditor(x.FlashColor, v => set(o => X(o) with { FlashColor = v })), set));
        SettingsRow("", CheckEditor(UiStrings.S_LayerEffectsDialog_FillSettings_7049E2D1FE, x.KeepOriginal, v => set(o => X(o) with { KeepOriginal = v })));
        var seed = NumEditor(0, ExplodeEffect.MaxSeed, x.Seed, NumKinds.Seed, v => set(o => X(o) with { Seed = v }));
        var reroll = Ui.IconButton(VectorIcon.Dice, null, (_, _) => seed.Value = System.Random.Shared.Next(0, ExplodeEffect.MaxSeed + 1), 36);
        ToolTip.SetTip(reroll, UiStrings.S_LayerEffectsDialog_FillSettings_BE76D99E55);
        SettingsRow(UiStrings.S_LayerEffectsDialog_FillSettings_E37116317A, seed, reroll);
        AddMotionEditors(Selected, set);
        _settings.Children.Add(Hint(UiStrings.S_LayerEffectsDialog_FillSettings_171EBAB542
                                    + UiStrings.S_LayerEffectsDialog_FillSettings_CD2E150E76));
    }
}
