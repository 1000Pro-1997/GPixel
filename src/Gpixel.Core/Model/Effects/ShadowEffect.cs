namespace Gpixel.Core.Model;

/// <summary>
/// 그림자: 그림 모양 그대로(단단한 그림자) 한 색으로 (X, Y)px 옮겨 뒤에 깐다.
/// <see cref="Dither"/>를 고르면 반투명 대신 불투명한 색을 그 밀도의 Bayer 무늬 칸에만 깐다(색의 알파는 쓰지 않음).
/// </summary>
public sealed record ShadowEffect : LayerEffect
{
    public const int MaxOffset = 16;

    private readonly int _x = 1, _y = 1;

    /// <summary>그림자 색(알파 = 그림자 진하기).</summary>
    public Color32 Color { get; init; } = new(0, 0, 0, 128);

    /// <summary>오른쪽으로 옮길 px(음수 = 왼쪽), ±<see cref="MaxOffset"/>.</summary>
    public int OffsetX { get => _x; init => _x = Math.Clamp(value, -MaxOffset, MaxOffset); }

    /// <summary>아래로 옮길 px(음수 = 위), ±<see cref="MaxOffset"/>.</summary>
    public int OffsetY { get => _y; init => _y = Math.Clamp(value, -MaxOffset, MaxOffset); }

    /// <summary>디더 밀도(없음 = 색의 알파로 반투명). 예전 파일은 없음.</summary>
    public ShadowDither Dither { get; init; } = ShadowDither.None;

    public override string DisplayName => Gpixel.Core.Localization.DomainStrings.Effect_Shadow;
}

/// <summary>디더 그림자 밀도(4×4 Bayer 무늬에서 칠하는 칸 비율). 50% = 바둑판.</summary>
public enum ShadowDither
{
    None,
    Light25,
    Half50,
    Dense75,
}
