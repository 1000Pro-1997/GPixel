namespace Gpixel.Core.Model;

/// <summary>
/// 빛 지나가기: 빛 띠가 <see cref="Angle"/> 방향으로 보이는 픽셀 위를 지나간다(동전·단추의 반짝임). 진행 0에는 그림 한쪽 바로 밖, 1에는 반대쪽 바로 밖.
/// 보이는 픽셀에만 스크린(밝게 더하기)으로 색 쪽으로 <see cref="Strength"/>%만큼, 알파는 그대로. 부드러움은 띠 양옆 1~2px을 계단식으로 약하게.
/// </summary>
public sealed record ShineEffect : AnimatedEffect
{
    public const int MaxWidth = 32, MaxSoftness = 2;

    private readonly int _angle = 45, _width = 3, _strength = 80, _soft;

    /// <summary>지나가는 방향(도, 0 = 오른쪽, 90 = 위, 45 = 왼쪽 아래 → 오른쪽 위).</summary>
    public int Angle { get => _angle; init => _angle = ((value % 360) + 360) % 360; }

    /// <summary>띠 폭 1~<see cref="MaxWidth"/>px.</summary>
    public int Width { get => _width; init => _width = Math.Clamp(value, 1, MaxWidth); }

    /// <summary>빛 색(알파는 쓰지 않음).</summary>
    public Color32 Color { get; init; } = Color32.White;

    /// <summary>세기 0~100%.</summary>
    public int Strength { get => _strength; init => _strength = Math.Clamp(value, 0, 100); }

    /// <summary>부드러움 0(딱딱한 띠)~<see cref="MaxSoftness"/>px(양옆 계단식 가장자리).</summary>
    public int Softness { get => _soft; init => _soft = Math.Clamp(value, 0, MaxSoftness); }

    public override string DisplayName => Gpixel.Core.Localization.DomainStrings.Effect_LightSweep;
}
