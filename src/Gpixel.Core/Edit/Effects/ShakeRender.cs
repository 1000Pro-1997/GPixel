using Gpixel.Core.Model;

namespace Gpixel.Core.Edit;

/// <summary>흔들기(<see cref="ShakeEffect"/>)·둥실둥실(<see cref="BobEffect"/>) 그리기: 그림 전체를 정수 픽셀만큼 옮긴다.</summary>
internal static class ShakeRender
{
    /// <summary>흔드는 축으로 세기만큼 넓힌다.</summary>
    public static EffectPadding Padding(ShakeEffect s, int width, int height)
    {
        int x = s.Axis == EffectAxis.Vertical ? 0 : s.Amount, y = s.Axis == EffectAxis.Horizontal ? 0 : s.Amount;
        return new EffectPadding(x, y, x, y);
    }

    /// <summary>오르내리는 축으로 높이만큼 넓힌다.</summary>
    public static EffectPadding Padding(BobEffect o, int width, int height)
        => o.Axis == EffectAxis.Horizontal ? new EffectPadding(o.Height, 0, o.Height, 0) : new EffectPadding(0, o.Height, 0, o.Height);

    public static void Apply(PixelImage image, ShakeEffect e)
    {
        var (dx, dy) = e.Offset(e.Progress);
        Shift(image, dx, dy);
    }

    public static void Apply(PixelImage image, BobEffect e)
    {
        var (dx, dy) = e.Offset(e.Progress);
        Shift(image, dx, dy);
    }

    /// <summary>그림을 (<paramref name="dx"/>, <paramref name="dy"/>)px 옮긴다(밖으로 나간 부분은 잘리고 빈 자리는 투명).</summary>
    private static void Shift(PixelImage image, int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        int w = image.Width, h = image.Height;
        var px = image.Pixels;
        if (Math.Abs(dx) >= w || Math.Abs(dy) >= h)
        {
            Array.Clear(px);
            return;
        }
        // 복사본 없이 제자리에서 줄을 옮긴다: 아래로 옮기면 아래 줄부터, 위로(또는 옆으로만) 옮기면 위 줄부터 — 아직 읽지 않은 줄을 덮지 않게.
        // 같은 줄 안에서 겹치는 옮기기는 Array.Copy가 알아서 처리한다.
        int count = w - Math.Abs(dx), from = Math.Max(0, -dx), to = Math.Max(0, dx);
        for (int k = 0; k < h; k++)
        {
            int y = dy > 0 ? h - 1 - k : k, sy = y - dy, row = y * w;
            if ((uint)sy >= (uint)h)
            {
                Array.Clear(px, row, w);
                continue;
            }
            Array.Copy(px, sy * w + from, px, row + to, count);
            if (dx > 0) Array.Clear(px, row, dx);
            else if (dx < 0) Array.Clear(px, row + count, -dx);
        }
    }
}
