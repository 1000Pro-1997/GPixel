namespace Gpixel.Core.Model;

/// <summary>늘었다 줄기의 기준점(이 점은 움직이지 않음).</summary>
public enum SquashAnchor
{
    /// <summary>보이는 그림의 아래 가운데(발이 땅에 붙음).</summary>
    BottomCenter,
    /// <summary>보이는 그림의 가운데.</summary>
    Middle,
    /// <summary>피벗(부위면 부위 피벗, 그 밖에는 프레임 피벗).</summary>
    Pivot,
}

/// <summary>
/// 늘었다 줄기(젤리): 가로 배율 1 + 가로 세기·f(t), 세로 배율 1 − 세로 세기·f(t). f는 0에서 시작해 <see cref="Bounces"/>번 출렁이며 줄어들어 끝(t = 1)에서 0인 감쇠 사인
/// (첫 봉우리 = 1). 옆으로 먼저면 먼저 납작·넓게, 위로 먼저면 먼저 좁고 길게. 최근접 샘플링이라 픽셀이 또렷하다.
/// </summary>
public sealed record SquashEffect : AnimatedEffect
{
    public const int MaxAmount = 50, MaxBounces = 3;

    private readonly int _widthAmount = 20, _heightAmount = 20, _bounces = 2;

    /// <summary>가로 최대 배율 변화 0~<see cref="MaxAmount"/>%(0 = 가로 고정).</summary>
    public int WidthAmount { get => _widthAmount; init => _widthAmount = Math.Clamp(value, 0, MaxAmount); }

    /// <summary>세로 최대 배율 변화 0~<see cref="MaxAmount"/>%(0 = 세로 고정).</summary>
    public int HeightAmount { get => _heightAmount; init => _heightAmount = Math.Clamp(value, 0, MaxAmount); }

    public SquashAnchor Anchor { get; init; } = SquashAnchor.BottomCenter;

    /// <summary>true = 위로 먼저(좁고 길게 → 넓게), false = 옆으로 먼저(넓고 낮게 → 좁고 길게).</summary>
    public bool StretchFirst { get; init; }

    /// <summary>튕김 횟수 1~<see cref="MaxBounces"/>(출렁임 수).</summary>
    public int Bounces { get => _bounces; init => _bounces = Math.Clamp(value, 1, MaxBounces); }

    public override string DisplayName => Gpixel.Core.Localization.DomainStrings.Effect_SquashStretch;

    private const double Damping = 2.5;
    private static readonly double[] Peaks = Enumerable.Range(1, MaxBounces).Select(b =>
        Enumerable.Range(0, 1001).Max(i => Math.Sin(2 * Math.PI * b * i / 1000.0) * Math.Exp(-Damping * i / 1000.0))).ToArray();

    /// <summary>
    /// 진행 <paramref name="u"/>(0~1)의 출렁임 f: sin(2π·튕김·u)·e^(−2.5u)를 첫 봉우리가 1이 되게 나눈 값(양수 = 옆으로 넓게, 순서가 위로 먼저면 부호 반대).
    /// u = 0, 1에서 0.
    /// </summary>
    public double Wobble(double u)
    {
        if (u <= 0 || u >= 1) return 0;
        double f = Math.Clamp(Math.Sin(2 * Math.PI * Bounces * u) * Math.Exp(-Damping * u) / Peaks[Bounces - 1], -1, 1);
        return StretchFirst ? -f : f;
    }
}
