using Gpixel.Core.Edit;
using Gpixel.Core.Model;

namespace Gpixel.Core.Effects;

/// <summary>늘었다 줄기 효과 정의: 설정 schema, 예전 형식 키, 렌더러·여백 연결.</summary>
public static class SquashDefinition
{
    public const string Id = "gpixel.effect.squash";

    public static EffectDefinition Definition { get; } = EffectDefinition.Create<SquashEffect>(Id, "squash", SquashRender.Apply, s =>
    [
        s.Int("widthAmount", 0, SquashEffect.MaxAmount, e => e.WidthAmount, (e, v) => e with { WidthAmount = v }, legacy: ["widthAmount", "amount"]),
        s.Int("heightAmount", 0, SquashEffect.MaxAmount, e => e.HeightAmount, (e, v) => e with { HeightAmount = v }, legacy: ["heightAmount", "amount"]),
        s.Int("bounces", 1, SquashEffect.MaxBounces, e => e.Bounces, (e, v) => e with { Bounces = v }),
        s.Choice("anchor", e => e.Anchor, (e, v) => e with { Anchor = v }),
        s.Bool("stretchFirst", e => e.StretchFirst, (e, v) => e with { StretchFirst = v }),
    ], padding: SquashRender.Padding);
}
