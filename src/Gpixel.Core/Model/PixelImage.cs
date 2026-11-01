using System.Drawing;

namespace Gpixel.Core.Model;

/// <summary>캔버스 크기를 바꿀 때 기존 그림을 붙일 기준점(9방향).</summary>
public enum Anchor
{
    TopLeft, TopCenter, TopRight,
    MiddleLeft, Center, MiddleRight,
    BottomLeft, BottomCenter, BottomRight,
}

/// <summary>
/// RGBA 픽셀 배열 이미지. 좌표는 왼쪽 위 원점, 행 우선(<c>index = y * Width + x</c>).
/// </summary>
public sealed class PixelImage
{
    /// <summary>허용하는 최대 가로·세로(Unity 최대 텍스처 크기).</summary>
    public const int MaxSize = 16384;

    public int Width { get; }
    public int Height { get; }

    /// <summary>픽셀 원본 배열(길이 Width*Height). 직접 수정해도 된다.</summary>
    public Color32[] Pixels { get; }

    public PixelImage(int width, int height)
    {
        ValidateSize(width, height);
        Width = width;
        Height = height;
        Pixels = new Color32[width * height];
    }

    /// <summary>기존 배열을 그대로 감싼다(복사하지 않음).</summary>
    public PixelImage(int width, int height, Color32[] pixels)
    {
        ValidateSize(width, height);
        if (pixels.Length != width * height)
            throw new ArgumentException(Gpixel.Core.Localization.DomainStrings.Image_InvalidPixelArray, nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    private static void ValidateSize(int width, int height)
    {
        if (width < 1 || height < 1 || width > MaxSize || height > MaxSize)
            throw new ArgumentOutOfRangeException(nameof(width), Gpixel.Core.Localization.DomainStrings.FormatImage_InvalidSize(max: MaxSize, width: width, height: height));
    }

    public Rectangle Bounds => new(0, 0, Width, Height);
    public Size Size => new(Width, Height);

    public bool Contains(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height;

    /// <summary>범위 밖이면 투명을 돌려준다.</summary>
    public Color32 Get(int x, int y) => Contains(x, y) ? Pixels[y * Width + x] : Color32.Transparent;

    /// <summary>범위 밖이면 무시한다. 값이 실제로 바뀌었으면 true.</summary>
    public bool Set(int x, int y, Color32 color)
    {
        if (!Contains(x, y)) return false;
        ref var p = ref Pixels[y * Width + x];
        if (p == color) return false;
        p = color;
        return true;
    }

    public Color32 this[int x, int y]
    {
        get => Get(x, y);
        set => Set(x, y, value);
    }

    public PixelImage Clone() => new(Width, Height, (Color32[])Pixels.Clone());

    /// <summary>같은 크기의 다른 이미지 내용을 그대로 복사해 온다.</summary>
    public void CopyFrom(PixelImage other)
    {
        if (other.Width != Width || other.Height != Height)
            throw new ArgumentException(Gpixel.Core.Localization.DomainStrings.Image_DifferentCopySize, nameof(other));
        Array.Copy(other.Pixels, Pixels, Pixels.Length);
    }

    public void Clear(Color32 color = default) => Array.Fill(Pixels, color);

    /// <summary>사각형 영역을 한 색으로 채운다(이미지 밖은 잘림).</summary>
    public void FillRect(Rectangle rect, Color32 color)
    {
        var r = Rectangle.Intersect(rect, Bounds);
        for (int y = r.Top; y < r.Bottom; y++)
            Array.Fill(Pixels, color, y * Width + r.Left, r.Width);
    }

    /// <summary>영역을 잘라 새 이미지로 만든다. 이미지 밖 부분은 투명.</summary>
    public PixelImage Crop(Rectangle rect)
    {
        var result = new PixelImage(Math.Max(1, rect.Width), Math.Max(1, rect.Height));
        result.Blit(this, rect, 0, 0, alphaBlend: false);
        return result;
    }

    /// <summary>
    /// <paramref name="source"/>의 <paramref name="srcRect"/> 영역을 (dx,dy)에 복사한다.
    /// 양쪽 이미지 경계 밖은 잘린다. <paramref name="alphaBlend"/>가 true면 알파 합성, false면 덮어쓰기.
    /// </summary>
    public void Blit(PixelImage source, Rectangle srcRect, int dx, int dy, bool alphaBlend = false)
    {
        // 원본 경계로 자르고 같은 만큼 대상 위치도 옮긴다.
        var s = Rectangle.Intersect(srcRect, source.Bounds);
        dx += s.X - srcRect.X;
        dy += s.Y - srcRect.Y;
        // 대상 경계로 자르기
        int x0 = Math.Max(dx, 0), y0 = Math.Max(dy, 0);
        int x1 = Math.Min(dx + s.Width, Width), y1 = Math.Min(dy + s.Height, Height);
        if (x1 <= x0 || y1 <= y0) return;
        int sx0 = s.X + (x0 - dx), sy0 = s.Y + (y0 - dy);
        int w = x1 - x0;
        for (int y = y0; y < y1; y++)
        {
            int si = (sy0 + (y - y0)) * source.Width + sx0;
            int di = y * Width + x0;
            if (!alphaBlend)
            {
                Array.Copy(source.Pixels, si, Pixels, di, w);
                continue;
            }
            for (int i = 0; i < w; i++)
                Pixels[di + i] = Color32.BlendOver(Pixels[di + i], source.Pixels[si + i]);
        }
    }

    /// <summary>원본 전체를 (dx,dy)에 복사한다.</summary>
    public void Blit(PixelImage source, int dx, int dy, bool alphaBlend = false)
        => Blit(source, source.Bounds, dx, dy, alphaBlend);

    /// <summary>
    /// <paramref name="source"/> 전체를 (dx,dy)에 불투명도 <paramref name="opacity"/>(0~255)를 곱해
    /// 알파 합성(source-over)한다. 레이어 합성·양파 껍질에 쓴다.
    /// </summary>
    public void Composite(PixelImage source, int dx = 0, int dy = 0, byte opacity = 255)
    {
        if (opacity == 0) return;
        int x0 = Math.Max(dx, 0), y0 = Math.Max(dy, 0);
        int x1 = Math.Min(dx + source.Width, Width), y1 = Math.Min(dy + source.Height, Height);
        if (x1 <= x0) return;
        var src = source.Pixels;
        var dst = Pixels;
        int sw = source.Width, w = Width;
        void Row(int y)
        {
            var from = src.AsSpan((y - dy) * sw + (x0 - dx), x1 - x0);
            var to = dst.AsSpan(y * w + x0, x1 - x0);
            for (int x = 0; x < to.Length; x++)
            {
                var s = from[x];
                if (s.A == 0) continue;
                if (opacity != 255) s = s.WithAlpha((byte)((s.A * opacity + 127) / 255));
                to[x] = Color32.BlendOver(to[x], s);
            }
        }
        // 자기 자신을 겹쳐 놓으면 먼저 합성한 줄을 다시 읽을 수 있어 차례대로, 아니면 줄마다 나눠서
        if (ReferenceEquals(src, dst))
            for (int y = y0; y < y1; y++) Row(y);
        else
            Edit.EffectParallel.For(y0, y1, x1 - x0, Row);
    }

    /// <summary>
    /// 캔버스 크기를 바꾼 새 이미지를 만든다. 기존 그림은 <paramref name="anchor"/> 기준으로 놓이고
    /// 늘어난 부분은 <paramref name="fill"/>(기본 투명)로 채워진다.
    /// </summary>
    public PixelImage ResizeCanvas(int newWidth, int newHeight, Anchor anchor, Color32 fill = default)
    {
        var result = new PixelImage(newWidth, newHeight);
        if (fill != default) result.Clear(fill);
        var (ox, oy) = AnchorOffset(Width, Height, newWidth, newHeight, anchor);
        result.Blit(this, ox, oy);
        return result;
    }

    /// <summary>캔버스 크기 변경 시 기존 그림의 왼쪽 위가 놓일 위치.</summary>
    public static (int X, int Y) AnchorOffset(int oldW, int oldH, int newW, int newH, Anchor anchor)
    {
        int col = (int)anchor % 3, row = (int)anchor / 3;
        int dx = newW - oldW, dy = newH - oldH;
        int x = col switch { 0 => 0, 1 => dx / 2, _ => dx };
        int y = row switch { 0 => 0, 1 => dy / 2, _ => dy };
        return (x, y);
    }

    /// <summary>영역 안 픽셀이 모두 투명이면 true(이미지 밖은 투명 취급).</summary>
    public bool IsRegionEmpty(Rectangle rect)
    {
        var r = Rectangle.Intersect(rect, Bounds);
        for (int y = r.Top; y < r.Bottom; y++)
        {
            int row = y * Width;
            for (int x = r.Left; x < r.Right; x++)
                if (Pixels[row + x].A != 0) return false;
        }
        return true;
    }

    /// <summary>투명이 아닌 픽셀을 모두 감싸는 사각형. 모두 투명이면 <see cref="Rectangle.Empty"/>.</summary>
    public Rectangle GetOpaqueBounds(Rectangle? within = null)
    {
        var r = Rectangle.Intersect(within ?? Bounds, Bounds);
        if (r.Width <= 0 || r.Height <= 0) return Rectangle.Empty;
        var px = Pixels;
        int w = Width, left = r.Left, right = r.Right;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        // 줄마다 양 끝에서 안쪽으로 첫 불투명 픽셀을 찾고, 여러 줄이 함께 최솟값·최댓값에 합친다
        Edit.EffectParallel.For(r.Top, r.Bottom, r.Width, y =>
        {
            var row = px.AsSpan(y * w + left, right - left);
            int first = 0;
            while (first < row.Length && row[first].A == 0) first++;
            if (first == row.Length) return;
            int last = row.Length - 1;
            while (row[last].A == 0) last--;
            LowerTo(ref minX, left + first);
            RaiseTo(ref maxX, left + last);
            LowerTo(ref minY, y);
            RaiseTo(ref maxY, y);
        });
        return maxX < 0 ? Rectangle.Empty : Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    private static void LowerTo(ref int target, int value)
    {
        for (int current = Volatile.Read(ref target); value < current;)
        {
            int seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current) return;
            current = seen;
        }
    }

    private static void RaiseTo(ref int target, int value)
    {
        for (int current = Volatile.Read(ref target); value > current;)
        {
            int seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current) return;
            current = seen;
        }
    }

    /// <summary>두 이미지의 크기와 모든 픽셀이 같으면 true.</summary>
    public bool ContentEquals(PixelImage other)
        => other.Width == Width && other.Height == Height && Pixels.AsSpan().SequenceEqual(other.Pixels);
}
