namespace Gpixel.Core.Model;

/// <summary>빛이 오는 방향(가장자리 빛·그늘).</summary>
public enum LightDirection
{
    TopLeft, Top, TopRight,
    Left, Right,
    BottomLeft, Bottom, BottomRight,
}

/// <summary>
/// 가장자리 빛·그늘: 빛 방향으로 지정한 거리 안에 투명 픽셀이 있으면 빛 색을, 반대쪽이면 그늘 색을 덧칠한다(알파는 그대로).
/// 대각선 방향이면 그 두 축 중 하나라도 비면 가장자리다. 한 픽셀이 양쪽 다 해당하면 빛이 이긴다. 색 알파 0 = 그쪽은 칠하지 않음.
/// </summary>
public sealed record RimLightEffect : LayerEffect
{
    public const int MaxSize = 16;
    private readonly int _size = 1;

    /// <summary>가장자리에서 안쪽으로 칠할 칸 수 1~<see cref="MaxSize"/>.</summary>
    public int Size { get => _size; init => _size = Math.Clamp(value, 1, MaxSize); }
    public Color32 Light { get; init; } = new(255, 255, 255, 160);
    public Color32 Shade { get; init; } = new(0, 0, 0, 96);
    public LightDirection Direction { get; init; } = LightDirection.TopLeft;

    public override string DisplayName => Gpixel.Core.Localization.DomainStrings.Effect_EdgeLightShade;

    /// <summary>빛이 오는 쪽 (dx, dy) — 각각 −1, 0, 1(왼쪽 위 원점).</summary>
    public (int X, int Y) Vector => Direction switch
    {
        LightDirection.TopLeft => (-1, -1),
        LightDirection.Top => (0, -1),
        LightDirection.TopRight => (1, -1),
        LightDirection.Left => (-1, 0),
        LightDirection.Right => (1, 0),
        LightDirection.BottomLeft => (-1, 1),
        LightDirection.Bottom => (0, 1),
        _ => (1, 1),
    };
}
