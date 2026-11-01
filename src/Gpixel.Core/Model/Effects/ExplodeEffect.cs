namespace Gpixel.Core.Model;

/// <summary>터지기의 중심.</summary>
public enum ExplodeCenter
{
    /// <summary>보이는 그림의 가운데.</summary>
    Middle,
    /// <summary>피벗(부위면 부위 피벗, 그 밖에는 프레임 피벗).</summary>
    Pivot,
    /// <summary>보이는 그림의 아래 가운데(위로 솟구침).</summary>
    BottomCenter,
    /// <summary>직접 (<see cref="ExplodeEffect.CenterX"/>, <see cref="ExplodeEffect.CenterY"/>) 픽셀.</summary>
    Custom,
}

/// <summary>
/// 터지기: 보이는 픽셀(또는 <see cref="PieceSize"/>×<see cref="PieceSize"/> 조각)이 중심에서 바깥쪽으로 튀어 날아가며 사라진다.
/// 각 축의 이동 = 해당 축의 <see cref="DistanceX"/>·<see cref="DistanceY"/> × (0.4 + 0.6 × 중심에서 떨어진 정도) × 진행 × 방향 성분.
/// <see cref="SpreadX"/>·<see cref="SpreadY"/>는 방향·거리를 무작위로 바꾸며, Y에는 <see cref="Gravity"/> × 시간²가 별도로 더해진다.
/// 진행 0(범위 첫 프레임)은 원래 그림 그대로. 조각은 정수 픽셀에 놓이고 알파만 흐려진다(번짐 없음).
/// </summary>
public sealed record ExplodeEffect : AnimatedEffect
{
    public const int MaxDistance = 128, MaxGravity = 64, MaxPieceSize = 4, MaxFlashFrames = 8, MaxSeed = 9999;

    private readonly int _distanceX = 16, _distanceY = 16, _spreadX = 50, _spreadY = 50, _gravity = 6, _piece = 1, _fadeStart = 40, _seed = 1, _flash;
    private readonly int _cx, _cy;

    public ExplodeEffect() => Easing = EffectEasing.EaseOut;

    /// <summary>수평으로 날아가는 최대 거리 0~<see cref="MaxDistance"/>px. 0이면 수평 이동 없음.</summary>
    public int DistanceX { get => _distanceX; init => _distanceX = Math.Clamp(value, 0, MaxDistance); }

    /// <summary>수직으로 날아가는 최대 거리 0~<see cref="MaxDistance"/>px. 0이어도 중력은 별도로 적용된다.</summary>
    public int DistanceY { get => _distanceY; init => _distanceY = Math.Clamp(value, 0, MaxDistance); }

    public ExplodeCenter Center { get; init; } = ExplodeCenter.Middle;

    /// <summary>직접 중심: 원본(셀·그룹이면 캔버스) 왼쪽 위 기준 픽셀.</summary>
    public int CenterX { get => _cx; init => _cx = Math.Clamp(value, -PixelImage.MaxSize, PixelImage.MaxSize); }
    public int CenterY { get => _cy; init => _cy = Math.Clamp(value, -PixelImage.MaxSize, PixelImage.MaxSize); }

    /// <summary>수평 무작위 변화 0~100%: X 이동의 방향·거리를 변형한다. 0이어도 기본 이동은 남는다.</summary>
    public int SpreadX { get => _spreadX; init => _spreadX = Math.Clamp(value, 0, 100); }

    /// <summary>수직 무작위 변화 0~100%: Y 이동의 방향·거리를 변형한다. 0이어도 기본 이동은 남는다.</summary>
    public int SpreadY { get => _spreadY; init => _spreadY = Math.Clamp(value, 0, 100); }

    /// <summary>중력: 끝에서 더 떨어지는 px(음수 = 떠오름), ±<see cref="MaxGravity"/>.</summary>
    public int Gravity { get => _gravity; init => _gravity = Math.Clamp(value, -MaxGravity, MaxGravity); }

    /// <summary>조각 크기 1~<see cref="MaxPieceSize"/>px(원본 왼쪽 위 기준 격자).</summary>
    public int PieceSize { get => _piece; init => _piece = Math.Clamp(value, 1, MaxPieceSize); }

    /// <summary>사라짐: 켜면 <see cref="FadeStart"/>% 진행부터 알파가 줄어 끝에서 0.</summary>
    public bool Fade { get; init; } = true;

    /// <summary>흐려지기 시작하는 진행 0~100%.</summary>
    public int FadeStart { get => _fadeStart; init => _fadeStart = Math.Clamp(value, 0, 100); }

    /// <summary>작아지기: 조각(2px 이상)이 끝으로 갈수록 작아진다.</summary>
    public bool Shrink { get; init; }

    /// <summary>시드 0~<see cref="MaxSeed"/>(같으면 같은 흩어짐).</summary>
    public int Seed { get => _seed; init => _seed = Math.Clamp(value, 0, MaxSeed); }

    /// <summary>원본 남기기: 켜면 원래 그림은 그 자리에 남고 복사본이 날아간다.</summary>
    public bool KeepOriginal { get; init; }

    /// <summary>번쩍임: 시작 다음 프레임부터 이 프레임 수 동안 조각을 <see cref="FlashColor"/> 쪽으로 물들인다(점점 약하게, 0 = 없음).</summary>
    public int FlashFrames { get => _flash; init => _flash = Math.Clamp(value, 0, MaxFlashFrames); }

    public Color32 FlashColor { get; init; } = Color32.White;

    public override string DisplayName => Gpixel.Core.Localization.DomainStrings.Effect_Explode;
}
