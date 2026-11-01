using Gpixel.Core.Edit;
using Gpixel.Core.Model;

namespace Gpixel.Core.Effects;

/// <summary>잔상 효과 정의: 설정 schema, 예전 형식 키, 렌더러·여백 연결.</summary>
public static class AfterimageDefinition
{
    public const string Id = "gpixel.effect.afterimage";

    public static EffectDefinition Definition { get; } = EffectDefinition.Create<AfterimageEffect>(Id, "afterimage", AfterimageRender.Apply, s =>
    [
        s.Int("count", 1, AfterimageEffect.MaxCount, e => e.Count, (e, v) => e with { Count = v }),
        s.Int("interval", 1, AfterimageEffect.MaxInterval, e => e.Interval, (e, v) => e with { Interval = v }),
        s.Int("opacity", 1, 100, e => e.Opacity, (e, v) => e with { Opacity = v }),
        s.Bool("tint", e => e.Tint, (e, v) => e with { Tint = v }),
        s.Color("tintColor", e => e.TintColor, (e, v) => e with { TintColor = v }, legacy: ["color"]),
    ], usesOtherFrames: true);
}
