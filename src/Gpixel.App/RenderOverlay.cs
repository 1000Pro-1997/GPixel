namespace Gpixel.App;

/// <summary>
/// 합성을 작업 스레드에서 계산하는 동안의 표시: 아직 최종 결과가 아니라는 회색 반투명 덮개와, 돌아가는 로딩 아이콘 + 진행 막대.
/// </summary>
internal static class RenderOverlay
{
    private static readonly IBrush Veil = AppTheme.Brush(Color.FromArgb(120, 96, 96, 96));
    private static readonly IBrush BadgeBrush = AppTheme.Brush(Color.FromArgb(190, 20, 20, 24));
    private static readonly IBrush TrackBrush = AppTheme.Brush(Color.FromArgb(70, 255, 255, 255));
    private static readonly IBrush IconBrush = AppTheme.Brush(Colors.White);

    /// <summary>로딩 아이콘이 돌 동안 다시 그리는 간격.</summary>
    public static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>회색 반투명 덮개.</summary>
    public static void DrawVeil(DrawingContext g, Rect area)
    {
        if (area.Width > 0 && area.Height > 0) g.FillRectangle(Veil, area);
    }

    /// <summary>
    /// <paramref name="area"/> 오른쪽 위에 돌아가는 로딩 아이콘과 진행 막대(<paramref name="progress"/> 0~1). 영역이 작으면 아이콘을 줄인다.
    /// </summary>
    public static void DrawBusy(DrawingContext g, Rect area, double progress, double iconSize = 18)
    {
        if (area.Width < 8 || area.Height < 8) return;
        double size = Math.Min(iconSize, Math.Min(area.Width, area.Height) - 6);
        if (size < 6) return;
        double pad = Math.Round(size / 3);
        var badge = new Rect(area.Right - size - pad * 3, area.Top + pad, size + pad * 2, size + pad * 2 + 3);
        g.FillRectangle(BadgeBrush, badge, 4);
        var icon = new Rect(badge.X + pad, badge.Y + pad, size, size);
        double angle = Environment.TickCount64 % 900 / 900.0 * 360;
        var center = icon.Center;
        using (g.PushTransform(Matrix.CreateTranslation(-center.X, -center.Y) * Matrix.CreateRotation(angle * Math.PI / 180)
                               * Matrix.CreateTranslation(center.X, center.Y)))
            VectorIcons.Draw(g, VectorIcon.LoaderCircle, icon, size, IconBrush, 2.5);
        var track = new Rect(badge.X + pad, badge.Bottom - pad / 2 - 3, size, 2);
        g.FillRectangle(TrackBrush, track);
        g.FillRectangle(IconBrush, track.WithWidth(Math.Round(size * Math.Clamp(progress, 0, 1))));
    }
}
