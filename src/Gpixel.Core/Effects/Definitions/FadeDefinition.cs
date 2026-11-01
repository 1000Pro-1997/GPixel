using Gpixel.Core.Edit;
using Gpixel.Core.Model;

namespace Gpixel.Core.Effects;

/// <summary>서서히 나타나기·사라지기 효과 정의: 설정 schema, 예전 형식 키, 렌더러·여백 연결.</summary>
public static class FadeDefinition
{
    public const string Id = "gpixel.effect.fade";

    public static EffectDefinition Definition { get; } = EffectDefinition.Create<FadeEffect>(Id, "fade", (image, e, _) => FadeRender.Apply(image, e), s =>
    [
        s.Bool("fadeOut", e => e.FadeOut, (e, v) => e with { FadeOut = v }, legacy: ["disappear"]),
    ]);
}
