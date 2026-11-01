using System.Drawing;
using Gpixel.Core.Edit;
using Gpixel.Core.Model;

namespace Gpixel.Core.Tests;

/// <summary>효과를 줄마다 나눠(병렬) 그린 결과가 차례대로 그린 결과와 바이트 단위로 같다.</summary>
public class EffectParallelTests
{
    // 줄 묶음이 여러 개로 나뉘고 묶음마다 여러 줄이 되도록 넉넉한 크기(홀수 너비)
    private const int Width = 161, Height = 120;

    private static readonly Color32[] Palette =
    [
        new(40, 80, 200), new(70, 150, 240), new(170, 230, 255), new(200, 40, 60), new(30, 30, 30),
        new(250, 220, 90), new(90, 200, 120), new(200, 40, 60, 128), new(255, 255, 255, 200),
    ];

    private static uint Noise(int x, int y, int salt)
    {
        uint h = (uint)x * 0x9E3779B1u ^ (uint)y * 0x85EBCA77u ^ (uint)(salt + 7) * 0xC2B2AE3Du;
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 12;
        return h;
    }

    /// <summary>타원 몸통(구멍·반투명 포함), 대각선, 흩어진 점, RGB가 남은 투명 픽셀이 섞인 그림. <paramref name="frame"/>마다 조금씩 옮겨진다.</summary>
    private static PixelImage Sprite(int frame)
    {
        var img = new PixelImage(Width, Height);
        double cx = Width / 2.0 + frame % 3 - 1, cy = Height / 2.0 + frame % 2, rx = Width * 0.36, ry = Height * 0.34;
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                uint r = Noise(x, y, 1);
                double dx = (x + 0.5 - cx) / rx, dy = (y + 0.5 - cy) / ry;
                bool body = dx * dx + dy * dy <= 1 && r % 23 != 0;
                if (body || r % 97 == 0 || (x == y && x % 5 != 0))
                    img.Pixels[y * Width + x] = r % 11 == 0 ? Palette[r % (uint)Palette.Length] : Palette[(x / 3 + y / 4) % 7];
                else if (r % 5 == 0)
                    img.Pixels[y * Width + x] = new Color32((byte)r, (byte)(r >> 8), (byte)(r >> 16), 0);
            }
        return img;
    }

    private static Dictionary<string, EffectColorGradient> Gradient(string slot, EffectColorGradient gradient) => new() { [slot] = gradient };

    private static readonly EffectColorGradient Linear = new() { Stops = [new(0, new(255, 0, 0)), new(1, new(0, 0, 255))], Angle = 30 };

    private static readonly EffectColorGradient RadialAnimated = new()
    {
        Shape = EffectGradientShape.Radial, Animated = true, Cycles = 2,
        Stops = [new(0, new(255, 200, 0)), new(0.5, new(0, 255, 120)), new(1, new(80, 0, 255))],
        AlphaStops = [new(0, 255), new(0.7, 90), new(1, 0)],
    };

    private static readonly EffectColorGradient SquareDither = new()
    {
        Shape = EffectGradientShape.Square, Angle = 20, Dither = GradientDither.Bayer4, Stops = [new(0, new(10, 10, 10)), new(1, new(240, 240, 240))],
    };

    /// <summary>이름 → (효과 목록, 그릴 프레임, 프레임 수). 효과 종류마다 실제로 픽셀이 바뀌는 설정과 진행.</summary>
    private static readonly Dictionary<string, (LayerEffect[] Effects, int Frame, int Frames)> Scenes = new()
    {
        ["outline outside"] = ([new OutlineEffect { Thickness = 3, Diagonal = true }], 0, 1),
        ["outline inside gradient"] = ([new OutlineEffect { Thickness = 2, Inside = true, ColorGradients = Gradient("Color", RadialAnimated) }], 2, 5),
        ["outline shade outside"] = ([new OutlineEffect { Thickness = 3, ColorMode = OutlineColorMode.Shade, ShadeAmount = -55 }], 0, 1),
        ["outline shade inside hidden"] = ([new OutlineEffect { Thickness = 2, Inside = true, HideOriginal = true, ColorMode = OutlineColorMode.Shade, ShadeAmount = 35 }], 0, 1),
        ["shadow"] = ([new ShadowEffect { OffsetX = -5, OffsetY = 3 }], 0, 1),
        ["shadow dither gradient"] = ([new ShadowEffect { OffsetX = 4, OffsetY = -2, Dither = ShadowDither.Half50, ColorGradients = Gradient("Color", SquareDither) }], 0, 1),
        ["rim light"] = ([new RimLightEffect { Size = 3, Direction = LightDirection.BottomRight, ColorGradients = Gradient("Light", Linear) }], 0, 1),
        ["color overlay"] = ([new ColorOverlayEffect { Blend = EffectBlend.Multiply, Amount = 70, ColorGradients = Gradient("Color", Linear) }], 0, 1),
        ["explode"] = ([new ExplodeEffect { PieceSize = 2, KeepOriginal = true, FlashFrames = 3, ColorGradients = Gradient("FlashColor", Linear) }], 2, 9),
        ["shine"] = ([new ShineEffect { Width = 8, Softness = 2, Angle = 135 }], 4, 9),
        ["squash"] = ([new SquashEffect { Anchor = SquashAnchor.Middle, WidthAmount = 35, HeightAmount = 15 }], 1, 9),
        ["inner glow"] = ([new InnerGlowEffect { Size = 4, Blend = EffectBlend.Normal, ColorGradients = Gradient("Color", SquareDither) }], 0, 1),
        ["palette swap"] = ([new PaletteSwapEffect { Tolerance = 20, Pairs = [new(new(40, 80, 200), new(255, 0, 0)), new(new(200, 40, 60), new(0, 255, 0, 128))] }], 0, 1),
        ["palette swap gradient"] = ([new PaletteSwapEffect { Pairs = [new(new(70, 150, 240), new(255, 0, 0))], ColorGradients = Gradient("Pairs[0].To", Linear) }], 0, 1),
        ["color adjust"] = ([new ColorAdjustEffect { Mode = ColorAdjustMode.HueShift, Hue = -120 }], 0, 1),
        ["reflection down"] = ([new ReflectionEffect { Gap = 2, Wave = 2, Height = 70, OpacityStart = 90, OpacityEnd = 10 }], 0, 1),
        ["reflection right dither"] = ([new ReflectionEffect { Direction = ReflectionDirection.Right, DitherFade = true, Wave = 1, Tint = new(70, 130, 230, 120) }], 0, 1),
        ["hit flash"] = ([new HitFlashEffect { Shape = HitFlashShape.Fade, ColorGradients = Gradient("Color", RadialAnimated) }], 1, 9),
        ["blink translucent"] = ([new BlinkEffect { Hide = BlinkHide.Translucent, Opacity = 33 }], 1, 5),
        ["blink dither"] = ([new BlinkEffect { Hide = BlinkHide.Dither }], 1, 5),
        ["shake"] = ([new ShakeEffect { Axis = EffectAxis.Both, Amount = 5 }], 3, 9),
        ["bob"] = ([new BobEffect { Height = 7 }], 2, 9),
        ["appear scatter"] = ([new AppearEffect { Style = AppearStyle.Scatter }], 4, 9),
        ["appear wipe"] = ([new AppearEffect { Style = AppearStyle.Wipe, From = WipeDirection.Left, Edge = 2 }], 4, 9),
        ["fade"] = ([new FadeEffect { FadeOut = true }], 2, 5),
        ["melt"] = ([new MeltEffect { Irregularity = 100, Distance = 30 }], 4, 9),
        ["rotate"] = ([new RotateEffect { Angle = 360, Anchor = SquashAnchor.Middle }], 1, 9),
        ["rotate quarter"] = ([new RotateEffect { Angle = 360, Anchor = SquashAnchor.BottomCenter }], 2, 9),
        ["color cycle"] = ([new ColorCycleEffect { Colors = [new(40, 80, 200), new(70, 150, 240), new(200, 40, 60)] }], 2, 5),
        ["color cycle gradient"] = ([new ColorCycleEffect { Colors = [new(40, 80, 200), new(70, 150, 240), new(200, 40, 60)], ColorGradients = Gradient("Colors[1]", Linear) }], 2, 5),
        ["shockwave"] = ([new ShockwaveEffect { Radius = 60, Thickness = 4, Behind = true, ColorGradients = Gradient("Color", Linear) }], 4, 9),
        ["hologram glitch"] = ([new HologramEffect { GlitchRate = 100, GlitchShift = 8, BandSize = 6, ColorGradients = Gradient("Color", RadialAnimated) }], 3, 6),
        ["hologram dither"] = ([new HologramEffect { Dither = true, FlickerRate = 100, ScanStyle = HologramScanStyle.Fade }], 1, 6),
        ["afterimage"] = ([new OutlineEffect(), new AfterimageEffect { Count = 3, Tint = true, ColorGradients = Gradient("TintColor", Linear) }, new ShakeEffect { Amount = 3 }], 5, 8),
        ["clip outline overlay"] = ([new OutlineEffect { Thickness = 2 }, new ColorOverlayEffect { ClipToPrevious = true, Color = new(255, 0, 0) }], 0, 1),
        ["clip outline reflection"] = ([new OutlineEffect(), new ReflectionEffect { ClipToPrevious = true, Gap = 1, Height = 80 }], 0, 1),
        ["clip hidden outline shine"] = ([new OutlineEffect { Thickness = 2, HideOriginal = true }, new ShineEffect { ClipToPrevious = true, Width = 6 }], 2, 5),
        ["clip shadow afterimage"] = ([new ShadowEffect { OffsetX = 3, OffsetY = 1 }, new AfterimageEffect { Count = 3, ClipToPrevious = true }], 4, 8),
    };

    public static TheoryData<string> SceneNames => [.. Scenes.Keys];

    private static (PixelImage Image, int X, int Y) Render(string scene)
    {
        var (effects, frame, frames) = Scenes[scene];
        var at = LayerEffects.At(effects, frame, frames, new PointF(Width * 0.4f, Height * 0.6f), Sprite);
        return LayerEffects.Render(Sprite(frame), at);
    }

    [Theory]
    [MemberData(nameof(SceneNames))]
    public void ParallelRender_MatchesSequential(string scene)
    {
        (PixelImage Image, int X, int Y) sequential, parallel;
        using (EffectParallel.Use(EffectParallel.Mode.Sequential)) sequential = Render(scene);
        using (EffectParallel.Use(EffectParallel.Mode.Parallel)) parallel = Render(scene);

        var untouched = new PixelImage(sequential.Image.Width, sequential.Image.Height);
        untouched.Blit(Sprite(Scenes[scene].Frame), -sequential.X, -sequential.Y);
        Assert.False(sequential.Image.ContentEquals(untouched), "장면이 그림을 바꿔야 비교가 의미 있다");
        Assert.Equal((sequential.X, sequential.Y), (parallel.X, parallel.Y));
        Assert.True(parallel.Image.ContentEquals(sequential.Image));
    }

    [Fact]
    public void ParallelComposite_MatchesSequential()
    {
        PixelImage Composite()
        {
            var canvas = Sprite(1);
            canvas.Composite(Sprite(2), -7, 5, 180);
            canvas.Composite(Sprite(0), 13, -9);
            return canvas;
        }
        PixelImage sequential, parallel;
        using (EffectParallel.Use(EffectParallel.Mode.Sequential)) sequential = Composite();
        using (EffectParallel.Use(EffectParallel.Mode.Parallel)) parallel = Composite();

        Assert.True(parallel.ContentEquals(sequential));
    }

    [Fact]
    public void ParallelOpaqueBounds_MatchSequential()
    {
        var img = new PixelImage(Width, Height);
        img.Pixels[17 * Width + 90] = new Color32(1, 2, 3); // 가장 위
        img.Pixels[101 * Width + 4] = new Color32(1, 2, 3, 1); // 가장 왼쪽·아래
        img.Pixels[60 * Width + 150] = new Color32(9, 9, 9); // 가장 오른쪽
        img.Pixels[5 * Width + 5] = new Color32(9, 9, 9, 0); // 투명(RGB만 있음)은 빠진다
        var within = new Rectangle(-3, 10, 150, 100);
        Rectangle sequential, sequentialWithin, parallel, parallelWithin;
        using (EffectParallel.Use(EffectParallel.Mode.Sequential))
        {
            sequential = img.GetOpaqueBounds();
            sequentialWithin = img.GetOpaqueBounds(within);
        }
        using (EffectParallel.Use(EffectParallel.Mode.Parallel))
        {
            parallel = img.GetOpaqueBounds();
            parallelWithin = img.GetOpaqueBounds(within);
        }

        Assert.Equal(Rectangle.FromLTRB(4, 17, 151, 102), sequential);
        Assert.Equal(Rectangle.FromLTRB(4, 17, 91, 102), sequentialWithin);
        Assert.Equal(sequential, parallel);
        Assert.Equal(sequentialWithin, parallelWithin);
    }
}
