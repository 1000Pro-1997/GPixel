namespace Gpixel.Core.Model;

/// <summary>
/// 외곽선: 그림 둘레에 선(바깥 = 그림 밖 투명 자리, 안쪽 = 그림 가장자리 픽셀). <see cref="HideOriginal"/>이면 선만 남는다.
/// 선 색은 <see cref="ColorMode"/>에 따라 한 색이거나, 맞닿은 그림 픽셀 색을 <see cref="ShadeAmount"/>만큼 진하게·연하게 한 색이다.
/// </summary>
public sealed record OutlineEffect : LayerEffect
{
    public const int MaxThickness = 3;
    public const int DefaultShadeAmount = -40;

    private readonly int _thickness = 1;
    private readonly int _shadeAmount = DefaultShadeAmount;

    public Color32 Color { get; init; } = Color32.Black;

    /// <summary>두께 1~<see cref="MaxThickness"/>px.</summary>
    public int Thickness { get => _thickness; init => _thickness = Math.Clamp(value, 1, MaxThickness); }

    /// <summary>true = 8방향(모서리 포함, 네모난 선), false = 4방향(십자, 모서리가 비는 선).</summary>
    public bool Diagonal { get; init; }

    /// <summary>true = 안쪽(그림 가장자리 픽셀을 칠함, 크기 그대로), false = 바깥(그림 밖으로 두께만큼 커짐).</summary>
    public bool Inside { get; init; }

    /// <summary>
    /// true = 원본 지우기: 선을 따라 그린 모양(레이어 그림과 앞선 효과 결과)을 투명하게 지워 선만 남긴다.
    /// 바깥이면 바깥 고리만, 안쪽이면 가장자리 고리만 남는다(안쪽 칸은 투명).
    /// </summary>
    public bool HideOriginal { get; init; }

    /// <summary>선 색을 정하는 방법. 예전 파일은 지정 색.</summary>
    public OutlineColorMode ColorMode { get; init; } = OutlineColorMode.Fixed;

    /// <summary>
    /// 명암 모드의 밝기 변화 −100~100(%): 음수 = 진하게(−100 = 검정), 양수 = 연하게(100 = 흰색).
    /// OKLab 밝기를 그만큼 검정·흰색 쪽으로 옮기고 색상은 그대로 둔다(sRGB 밖이면 채도만 줄임).
    /// </summary>
    public int ShadeAmount { get => _shadeAmount; init => _shadeAmount = Math.Clamp(value, -100, 100); }

    public override string DisplayName => Gpixel.Core.Localization.DomainStrings.Effect_Outline;
}

/// <summary>외곽선 색을 정하는 방법.</summary>
public enum OutlineColorMode
{
    /// <summary>지정 색(<see cref="OutlineEffect.Color"/>, 그라데이션 가능).</summary>
    Fixed,
    /// <summary>
    /// 인접 픽셀 기준 명암: 바깥 선은 맞닿은 그림 픽셀(4/8방향 이웃 중 가로·세로 이웃 먼저, 그중 가장 많은 색), 안쪽 선은 덮는 픽셀 자신의 색을
    /// <see cref="OutlineEffect.ShadeAmount"/>만큼 진하게·연하게 한다. 알파는 그 픽셀 알파. 두께가 2 이상이면 바깥쪽 고리는 안쪽 고리의 기준 색을 이어받는다.
    /// </summary>
    Shade,
}
