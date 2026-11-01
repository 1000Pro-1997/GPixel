using Gpixel.Core.Edit;
using Gpixel.Core.Model;

namespace Gpixel.Core.Effects;

/// <summary>녹아내리기 효과 정의: 설정 schema, 예전 형식 키, 렌더러·여백 연결.</summary>
public static class MeltDefinition
{
    public const string Id = "gpixel.effect.melt";

    public static EffectDefinition Definition { get; } = EffectDefinition.Create<MeltEffect>(Id, "melt", MeltRender.Apply, s =>
    [
        s.Int("distance", 1, MeltEffect.MaxDistance, e => e.Distance, (e, v) => e with { Distance = v }),
        s.Int("irregularity", 0, 100, e => e.Irregularity, (e, v) => e with { Irregularity = v }, legacy: ["spread"]),
        s.Bool("stretch", e => e.Stretch, (e, v) => e with { Stretch = v }),
        s.Bool("fade", e => e.Fade, (e, v) => e with { Fade = v }),
        s.Int("seed", 0, MeltEffect.MaxSeed, e => e.Seed, (e, v) => e with { Seed = v }),
    ], padding: MeltRender.Padding);
}
