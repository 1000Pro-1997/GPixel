using Gpixel.Core.Edit;
using Gpixel.Core.Model;

namespace Gpixel.Core.Effects;

/// <summary>충격파 고리 효과 정의: 설정 schema, 예전 형식 키, 렌더러·여백 연결.</summary>
public static class ShockwaveDefinition
{
    public const string Id = "gpixel.effect.shockwave";

    public static EffectDefinition Definition { get; } = EffectDefinition.Create<ShockwaveEffect>(Id, "shockwave", ShockwaveRender.Apply, s =>
    [
        s.Choice("center", e => e.Center, (e, v) => e with { Center = v }),
        s.Int("centerX", -PixelImage.MaxSize, PixelImage.MaxSize, e => e.CenterX, (e, v) => e with { CenterX = v }, legacy: ["x"]),
        s.Int("centerY", -PixelImage.MaxSize, PixelImage.MaxSize, e => e.CenterY, (e, v) => e with { CenterY = v }, legacy: ["y"]),
        s.Int("radius", 1, ShockwaveEffect.MaxRadius, e => e.Radius, (e, v) => e with { Radius = v }, legacy: ["distance"]),
        s.Int("thickness", 1, ShockwaveEffect.MaxThickness, e => e.Thickness, (e, v) => e with { Thickness = v }),
        s.Color("color", e => e.Color, (e, v) => e with { Color = v }),
        s.Bool("fade", e => e.Fade, (e, v) => e with { Fade = v }),
        s.Bool("behind", e => e.Behind, (e, v) => e with { Behind = v }),
    ], padding: ShockwaveRender.Padding);
}
