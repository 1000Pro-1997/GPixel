using Gpixel.Core.Effects;
using Gpixel.Core.Model;
using Gpixel.App.Localization;

namespace Gpixel.App;

public sealed partial class LayerEffectsDialog
{
    /// <summary>효과 창·타임라인 표시: 이름, 설명, 타임라인 색, 설정 칸, 목록 요약.</summary>
    private static EffectUi FadeUi => EffectUi.For<FadeEffect>(FadeDefinition.Id, UiStrings.Fade_Name, UiStrings.Fade_Description,
        Avalonia.Media.Color.FromRgb(120, 194, 220), (dialog, e, set) => dialog.FadePanel(e, set),
        fade => $"{fade.DisplayName} · {(fade.FadeOut ? UiStrings.Fade_Out : UiStrings.Fade_In)}");

    /// <summary>서서히 나타나기·사라지기 설정: 방향(나타나기 / 사라지기).</summary>
    private void FadePanel(FadeEffect fade, Action<Func<LayerEffect, LayerEffect>> set)
    {
        SettingsRow(UiStrings.Fade_Direction, ChoiceEditor([UiStrings.Fade_In, UiStrings.Fade_Out], fade.FadeOut ? 1 : 0,
            k => set(x => (FadeEffect)x with { FadeOut = k == 1 })));
        AddMotionEditors(Selected, set);
    }
}
