using System.IO.Compression;
using System.Security.Cryptography;
using Gpixel.Core.Edit;
using Gpixel.Core.Effects;
using Gpixel.Core.IO;
using Gpixel.Core.Model;

namespace Gpixel.Core.Tests;

/// <summary>
/// 효과 코드를 옮기거나 나눠도 저장 형식(project.json)과 그린 결과가 그대로인지 고정한다.
/// 렌더 해시는 코드를 옮기기 전 상태에서 계산한 값이다 — 값이 바뀌면 렌더 결과가 바뀐 것이다.
/// project.json 해시는 프로젝트 형식 4(안정된 효과 종류 ID와 설정 객체, <see cref="EffectCodec"/>)로 바꾸며 새로 정했다.
/// 형식 3 이하로 저장한 같은 프로젝트는 <c>fixtures/effects/effects-v3.gpixel</c>로 남겨 읽기 호환을 검사한다(<see cref="EffectCompatibilityTests"/>).
/// </summary>
public class EffectCharacterizationTests
{
    private const int Frames = 7;
    private static readonly Color32 Red = new(255, 0, 0);
    private static readonly Color32 Green = new(0, 255, 0);
    private static readonly Color32 Blue = new(0, 0, 255);

    /// <summary>효과 종류마다 기본값이 아닌 설정 하나씩(모든 종류).</summary>
    internal static LayerEffect[] AllKinds() =>
    [
        new OutlineEffect { Color = new Color32(20, 30, 40), Thickness = 2, Diagonal = true, ColorMode = OutlineColorMode.Shade, ShadeAmount = -40 },
        new ShadowEffect { Color = new(10, 20, 30), OffsetX = -2, OffsetY = 3, Dither = ShadowDither.Half50 },
        new RimLightEffect { Size = 2, Light = new(255, 240, 200, 200), Shade = new(0, 0, 40, 120), Direction = LightDirection.BottomRight },
        new ColorOverlayEffect
        {
            ColorGradients = new Dictionary<string, EffectColorGradient>
            {
                [nameof(ColorOverlayEffect.Color)] = new EffectColorGradient
                {
                    Stops = [new(0, new Color32(1, 2, 3)), new(1, new Color32(200, 100, 50, 220))],
                    Dither = GradientDither.Bayer2, Cycles = 2, Easing = EffectEasing.EaseIn, PingPong = true,
                },
            },
            Amount = 60, Blend = EffectBlend.Multiply,
        },
        new InnerGlowEffect { Color = new(9, 200, 7), Size = 3, Strength = 70, Fade = false, Blend = EffectBlend.Screen },
        new PaletteSwapEffect { Pairs = [new(Red, Blue), new(Green, Color32.Transparent)], Tolerance = 12 },
        new ColorAdjustEffect { Mode = ColorAdjustMode.BrightnessContrast, Amount = 40, Hue = -90, Brightness = 20, Contrast = -30, Saturation = 70 },
        new ReflectionEffect { Gap = 1, Height = 80, OpacityStart = 90, OpacityEnd = 10, DitherFade = true, Tint = new(0, 100, 200, 90), Wave = 1, Direction = ReflectionDirection.Down },
        new ExplodeEffect
        {
            DistanceX = 6, DistanceY = 4, Center = ExplodeCenter.Custom, CenterX = 3, CenterY = -2, SpreadX = 77, SpreadY = 12, Gravity = -2, PieceSize = 2, FadeStart = 10,
            Shrink = true, Seed = 1234, KeepOriginal = true, FlashFrames = 2, FlashColor = new Color32(1, 2, 3), Easing = EffectEasing.EaseInOut,
            Repeat = EffectRepeat.PingPong, RepeatCount = 3,
        },
        new ShineEffect { Angle = 300, Width = 3, Color = new Color32(255, 240, 0), Strength = 55, Softness = 2, Repeat = EffectRepeat.Loop, RepeatCount = 2 },
        new SquashEffect { WidthAmount = 35, HeightAmount = 15, Anchor = SquashAnchor.Middle, StretchFirst = true, Bounces = 2 },
        new HitFlashEffect { Color = new Color32(255, 40, 40), Strength = 70, Count = 3, Shape = HitFlashShape.Fade },
        new BlinkEffect { OnFrames = 2, OffFrames = 1, Hide = BlinkHide.Translucent, Opacity = 25 },
        new ShakeEffect { Axis = EffectAxis.Both, Amount = 3, Count = 7, Decay = false, Regular = true, Seed = 77, Easing = EffectEasing.EaseIn },
        new BobEffect { Height = 3, Cycles = 1, Axis = EffectAxis.Horizontal, Easing = EffectEasing.EaseIn },
        new AppearEffect { Disappear = true, Style = AppearStyle.Wipe, From = WipeDirection.Right, Edge = 2, Seed = 12 },
        new FadeEffect { FadeOut = true, Easing = EffectEasing.EaseInOut },
        new MeltEffect { Distance = 6, Irregularity = 30, Stretch = false, Fade = false, Seed = 99 },
        new RotateEffect { Angle = -270, Anchor = SquashAnchor.Middle },
        new AfterimageEffect { Count = 2, Interval = 1, Opacity = 80, Tint = true, TintColor = new Color32(10, 20, 30, 128) },
        new ColorCycleEffect { Colors = [Red, Blue, Green, Color32.White], Steps = 3, Reverse = true },
        new ShockwaveEffect { Center = ExplodeCenter.Custom, CenterX = -1, CenterY = 5, Radius = 8, Thickness = 2, Color = Blue, Fade = false, Behind = true },
        new HologramEffect
        {
            Color = new Color32(255, 80, 200), TintStrength = 60, ScanGap = 3, ScanStrength = 70, ScanStyle = HologramScanStyle.Fade, ScanScroll = false,
            Band = false, BandSize = 5, BandStrength = 30, Opacity = 55, Dither = true, FlickerRate = 33, FlickerStrength = 80, GlitchRate = 45, GlitchShift = 3,
            RgbSplit = false, EdgeGlow = true, EdgeOpacity = 20, Seed = 4321, Easing = EffectEasing.EaseInOut, Repeat = EffectRepeat.Loop, RepeatCount = 3,
        },
    ];

    /// <summary>프레임마다 조금씩 다른 결정적 그림(빨강·초록·파랑·반투명 섞임).</summary>
    internal static SpriteProject Project(IReadOnlyList<LayerEffect> effects)
    {
        var p = new SpriteProject(12, 12);
        for (int i = 1; i < Frames; i++) p.DuplicateFrame(0);
        Color32[] palette = [Red, Green, Blue, new Color32(200, 150, 40, 180)];
        for (int f = 0; f < Frames; f++)
        {
            var cel = new PixelImage(12, 12);
            for (int y = 3; y < 9; y++)
                for (int x = 3 + f % 2; x < 9; x++)
                    cel[x, y] = palette[(x * 3 + y + f) % palette.Length];
            p.SetCel(0, f, cel);
        }
        p.SetLayerEffects(0, effects);
        return p;
    }

    internal static string Hash(SpriteProject p)
    {
        using var sha = SHA256.Create();
        for (int f = 0; f < Frames; f++)
        {
            var img = p.Flatten(f);
            var bytes = new byte[8 + img.Pixels.Length * 4];
            BitConverter.TryWriteBytes(bytes.AsSpan(0), img.Width);
            BitConverter.TryWriteBytes(bytes.AsSpan(4), img.Height);
            for (int i = 0; i < img.Pixels.Length; i++)
            {
                var c = img.Pixels[i];
                bytes[8 + i * 4] = c.R;
                bytes[9 + i * 4] = c.G;
                bytes[10 + i * 4] = c.B;
                bytes[11 + i * 4] = c.A;
            }
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!)[..16];
    }

    private static readonly Dictionary<string, string> ExpectedRender = new()
    {
        ["OutlineEffect"] = "B076FC7E34A5638C",
        ["ShadowEffect"] = "2EDF12E19BCA486B",
        ["RimLightEffect"] = "A620169C99F07FF9",
        ["ColorOverlayEffect"] = "B546B5B584DEFF04",
        ["InnerGlowEffect"] = "195D06BB30853904",
        ["PaletteSwapEffect"] = "0E7E35CAFCD40E2F",
        ["ColorAdjustEffect"] = "C98F3564C6E84EF5",
        ["ReflectionEffect"] = "CD8E1FA42084C2DB",
        ["ExplodeEffect"] = "42E37E2B5BE3FAD3",
        ["ShineEffect"] = "CF08CA9B228A25BB",
        ["SquashEffect"] = "D2FD66B2AFA4E0A5",
        ["HitFlashEffect"] = "0F6C98FDB701E25A",
        ["BlinkEffect"] = "DC42A3E99A5DE63A",
        ["ShakeEffect"] = "1BE86C5CC8CD40EE",
        ["BobEffect"] = "68917662005717FF",
        ["AppearEffect"] = "C99B92564AD0A232",
        ["FadeEffect"] = "E1FE28AEA0561A17",
        ["MeltEffect"] = "110196D701866F82",
        ["RotateEffect"] = "87FBF6B07036D60E",
        ["AfterimageEffect"] = "E6D194E8AE6C0F53",
        ["ColorCycleEffect"] = "F1E13EFFFB7A415F",
        ["ShockwaveEffect"] = "724D046E64AEB333",
        ["HologramEffect"] = "B943DE66A4C5939F",
    };

    private const string ExpectedProjectJson = "7E01004FC5059BE3";

    /// <summary>저장 형식 고정과 예전 형식 fixture에 쓰는 목록: 모든 종류 + 꺼진 효과 하나 + 프레임 범위·클리핑이 있는 효과 하나.</summary>
    internal static LayerEffect[] SavedKinds()
    {
        var fx = AllKinds();
        fx[1] = fx[1] with { Enabled = false };
        fx[4] = fx[4] with { Frames = new FrameRange(1, 3), ClipToPrevious = true };
        return fx;
    }

    private const string ExpectedStack = "5F234376E5767A4E";

    [Fact]
    public void AllEffectKinds_AreCovered()
        => Assert.Equal(EffectRegistry.BuiltIn.Select(d => d.ModelType.Name).Order(), AllKinds().Select(e => e.GetType().Name).Order());

    [Fact]
    public void Render_EachKind_MatchesPinnedPixels()
    {
        var actual = AllKinds().ToDictionary(e => e.GetType().Name, e => Hash(Project([e])));
        Assert.Equal(ExpectedRender, actual);
    }

    [Fact]
    public void Render_FullStack_MatchesPinnedPixels()
        => Assert.Equal(ExpectedStack, Hash(Project(AllKinds())));

    [Fact]
    public void Save_ProjectJson_MatchesPinnedBytes()
    {
        var p = Project(SavedKinds());
        using var stream = new MemoryStream();
        ProjectIO.Save(p, stream);
        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        using var entry = zip.GetEntry(ProjectIO.ProjectEntryName)!.Open();
        using var json = new MemoryStream();
        entry.CopyTo(json);
        Assert.Equal(ExpectedProjectJson, Convert.ToHexString(SHA256.HashData(json.ToArray()))[..16]);
    }
}
