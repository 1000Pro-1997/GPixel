using Gpixel.Core.Edit;
using Gpixel.Core.Model;

namespace Gpixel.Core.Effects;

/// <summary>나타나기·사라지기 효과 정의: 설정 schema, 예전 형식 키, 렌더러·여백 연결.</summary>
public static class AppearDefinition
{
    public const string Id = "gpixel.effect.appear";

    public static EffectDefinition Definition { get; } = EffectDefinition.Create<AppearEffect>(Id, "appear", AppearRender.Apply, s =>
    [
        s.Bool("disappear", e => e.Disappear, (e, v) => e with { Disappear = v }),
        s.Choice("style", e => e.Style, (e, v) => e with { Style = v }),
        s.Choice("from", e => e.From, (e, v) => e with { From = v }, legacy: ["direction"]),
        s.Int("edge", 0, AppearEffect.MaxEdge, e => e.Edge, (e, v) => e with { Edge = v }),
        s.Int("seed", 0, AppearEffect.MaxSeed, e => e.Seed, (e, v) => e with { Seed = v }),
    ]);
}
