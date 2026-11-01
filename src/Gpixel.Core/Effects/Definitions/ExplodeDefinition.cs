using Gpixel.Core.Edit;
using Gpixel.Core.Model;

namespace Gpixel.Core.Effects;

/// <summary>터지기 효과 정의: 설정 schema, 예전 형식 키, 렌더러·여백 연결.</summary>
public static class ExplodeDefinition
{
    public const string Id = "gpixel.effect.explode";

    public static EffectDefinition Definition { get; } = EffectDefinition.Create<ExplodeEffect>(Id, "explode", ExplodeRender.Apply, s =>
    [
        s.Int("distanceX", 0, ExplodeEffect.MaxDistance, e => e.DistanceX, (e, v) => e with { DistanceX = v }, legacy: ["distanceX", "distance"]),
        s.Int("distanceY", 0, ExplodeEffect.MaxDistance, e => e.DistanceY, (e, v) => e with { DistanceY = v }, legacy: ["distanceY", "distance"]),
        s.Choice("center", e => e.Center, (e, v) => e with { Center = v }),
        s.Int("centerX", -PixelImage.MaxSize, PixelImage.MaxSize, e => e.CenterX, (e, v) => e with { CenterX = v }, legacy: ["x"]),
        s.Int("centerY", -PixelImage.MaxSize, PixelImage.MaxSize, e => e.CenterY, (e, v) => e with { CenterY = v }, legacy: ["y"]),
        s.Int("spreadX", 0, 100, e => e.SpreadX, (e, v) => e with { SpreadX = v }, legacy: ["spreadX", "spread"]),
        s.Int("spreadY", 0, 100, e => e.SpreadY, (e, v) => e with { SpreadY = v }, legacy: ["spreadY", "spread"]),
        s.Int("gravity", -ExplodeEffect.MaxGravity, ExplodeEffect.MaxGravity, e => e.Gravity, (e, v) => e with { Gravity = v }),
        s.Int("pieceSize", 1, ExplodeEffect.MaxPieceSize, e => e.PieceSize, (e, v) => e with { PieceSize = v }),
        s.Bool("shrink", e => e.Shrink, (e, v) => e with { Shrink = v }),
        s.Bool("fade", e => e.Fade, (e, v) => e with { Fade = v }),
        s.Int("fadeStart", 0, 100, e => e.FadeStart, (e, v) => e with { FadeStart = v }),
        s.Int("flashFrames", 0, ExplodeEffect.MaxFlashFrames, e => e.FlashFrames, (e, v) => e with { FlashFrames = v }),
        s.Color("flashColor", e => e.FlashColor, (e, v) => e with { FlashColor = v }),
        s.Bool("keepOriginal", e => e.KeepOriginal, (e, v) => e with { KeepOriginal = v }),
        s.Int("seed", 0, ExplodeEffect.MaxSeed, e => e.Seed, (e, v) => e with { Seed = v }),
    ], padding: ExplodeRender.Padding);
}
