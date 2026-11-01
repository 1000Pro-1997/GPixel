using System.Runtime.InteropServices;
using Avalonia.Platform;
using Gpixel.Core.IO;
using Gpixel.Core.Model;
using SkiaSharp;

namespace Gpixel.App;

/// <summary>PixelImage ↔ Avalonia 비트맵 변환과 썸네일·점 확대 그리기.</summary>
public static class ImageUtil
{
    /// <summary>같은 크기의 BGRA(직선 알파) 비트맵을 만든다. Color32 메모리 배치(ARGB uint, 리틀 엔디언)와 같다.</summary>
    public static WriteableBitmap ToBitmap(PixelImage image)
    {
        var bmp = NewBitmap(image.Width, image.Height);
        CopyToBitmap(image, bmp);
        return bmp;
    }

    /// <summary>빈 BGRA(직선 알파) 비트맵.</summary>
    public static WriteableBitmap NewBitmap(int width, int height)
        => new(new PixelSize(Math.Max(1, width), Math.Max(1, height)), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);

    /// <summary>이미지 픽셀을 같은 크기 비트맵에 그대로 복사한다.</summary>
    public static void CopyToBitmap(PixelImage image, WriteableBitmap bmp) => CopyToBitmap(image.Pixels, image.Width, image.Height, bmp);

    private static unsafe void CopyToBitmap(ReadOnlySpan<Color32> src, int width, int height, WriteableBitmap bmp)
    {
        using var fb = bmp.Lock();
        int w = Math.Min(width, fb.Size.Width), h = Math.Min(height, fb.Size.Height);
        for (int y = 0; y < h; y++)
            src.Slice(y * width, w).CopyTo(new Span<Color32>((byte*)fb.Address + (long)y * fb.RowBytes, w));
    }

    /// <summary>
    /// 점 단위 확대(정수 배율, 최근접)로 <paramref name="area"/> 가운데에 그린다. 그린 화면 사각형을 돌려준다.
    /// <paramref name="fixedScale"/>가 0보다 크면 그 배율, 아니면 들어가는 가장 큰 정수 배율(작으면 축소).
    /// </summary>
    public static Rect DrawFitted(DrawingContext g, Bitmap bmp, Rectangle src, Rect area, int fixedScale = 0, bool checker = false)
    {
        if (src.Width <= 0 || src.Height <= 0 || area.Width <= 0 || area.Height <= 0) return default;
        double scale;
        if (fixedScale > 0) scale = fixedScale;
        else
        {
            double fit = Math.Min(area.Width / src.Width, area.Height / src.Height);
            scale = fit >= 1 ? Math.Floor(fit) : fit;
        }
        double w = Math.Max(1, Math.Floor(src.Width * scale)), h = Math.Max(1, Math.Floor(src.Height * scale));
        var dest = new Rect(Math.Floor(area.X + (area.Width - w) / 2), Math.Floor(area.Y + (area.Height - h) / 2), w, h);
        if (checker) AppTheme.DrawChecker(g, dest, Math.Max(2, Math.Min(8, (int)scale * 2)));
        using (Pixelated(g))
            g.DrawImage(bmp, new Rect(src.X, src.Y, src.Width, src.Height), dest);
        return dest;
    }

    /// <summary>
    /// 원래 <paramref name="source"/> 크기 그림을 <see cref="DrawFitted"/>와 같은 자리·크기에 그리되, 줄인 썸네일 <paramref name="thumb"/>을 쓴다
    /// (최근접 — 원본을 줄여 그린 것과 같은 모양). 그린 화면 사각형을 돌려준다.
    /// </summary>
    public static Rect DrawFittedThumb(DrawingContext g, Bitmap thumb, Size source, Rect area, bool checker = false)
    {
        if (source.Width <= 0 || source.Height <= 0 || area.Width <= 0 || area.Height <= 0) return default;
        double fit = Math.Min(area.Width / source.Width, area.Height / source.Height);
        double scale = fit >= 1 ? Math.Floor(fit) : fit;
        double w = Math.Max(1, Math.Floor(source.Width * scale)), h = Math.Max(1, Math.Floor(source.Height * scale));
        var dest = new Rect(Math.Floor(area.X + (area.Width - w) / 2), Math.Floor(area.Y + (area.Height - h) / 2), w, h);
        if (checker) AppTheme.DrawChecker(g, dest, Math.Max(2, Math.Min(8, (int)scale * 2)));
        using (Pixelated(g))
            g.DrawImage(thumb, new Rect(0, 0, thumb.PixelSize.Width, thumb.PixelSize.Height), dest);
        return dest;
    }

    /// <summary>비트맵을 최근접(번지지 않게)으로 확대해 그리는 구간. using으로 감싼다.</summary>
    public static DrawingContext.PushedState Pixelated(DrawingContext g)
        => g.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None });

    /// <summary>체커 배경 위에 이미지를 정수 배율로 맞춘 정사각 썸네일(UI 스레드가 아니어도 만들 수 있음).</summary>
    public static WriteableBitmap MakeThumbnail(PixelImage image, int size)
    {
        var px = new Color32[size * size];
        var bg = AppTheme.InputColor.ToColor32();
        Array.Fill(px, bg);
        int inner = size - 2;
        if (image.Width > 0 && image.Height > 0 && inner > 0)
        {
            double fit = Math.Min(inner / (double)image.Width, inner / (double)image.Height);
            double scale = fit >= 1 ? Math.Floor(fit) : fit;
            int w = Math.Max(1, (int)(image.Width * scale)), h = Math.Max(1, (int)(image.Height * scale));
            int ox = 1 + (inner - w) / 2, oy = 1 + (inner - h) / 2;
            int cell = Math.Max(2, Math.Min(8, (int)scale * 2));
            var ca = AppTheme.CheckerA.ToColor32();
            var cb = AppTheme.CheckerB.ToColor32();
            for (int y = 0; y < h; y++)
            {
                int sy = Math.Min(image.Height - 1, (int)(y / scale));
                for (int x = 0; x < w; x++)
                {
                    int sx = Math.Min(image.Width - 1, (int)(x / scale));
                    var back = ((x / cell) + (y / cell)) % 2 == 0 ? ca : cb;
                    px[(oy + y) * size + ox + x] = Blend(back, image.Pixels[sy * image.Width + sx]);
                }
            }
        }
        var bmp = NewBitmap(size, size);
        CopyToBitmap(px, size, size, bmp);
        return bmp;
    }

    /// <summary>
    /// 그림 파일(PNG·APNG·JPG·BMP·GIF·WebP·ICO·WBMP — 움직이는 것 포함)을 프레임마다 완성된 그림(RGBA 그대로)과 길이(ms)로 푼다.
    /// GIF는 <see cref="GifReader"/>(처리 방법·투명 직접 합성), PNG는 <see cref="PngIO"/>(움직이는 PNG면 <see cref="ApngReader"/>),
    /// 나머지는 SkiaSharp(움직이는 WebP는 코덱이 앞 프레임 위에 합성). <paramref name="maxFrames"/>까지만 푼다.
    /// 풀 수 없거나 크기가 <see cref="PixelImage.MaxSize"/>를 넘으면 null. 파일을 열 수 없으면 IO 예외.
    /// </summary>
    public static (IReadOnlyList<PixelImage> Images, IReadOnlyList<int> DurationsMs)? DecodeFrames(string path, int maxFrames = int.MaxValue)
    {
        if (DecodeAnimation(path, maxFrames) is not { } a) return null;
        return (a.Images, a.DurationsMs);
    }

    /// <summary><see cref="DecodeFrames"/>와 같고 결과를 <see cref="DecodedAnimation"/>으로.</summary>
    public static DecodedAnimation? DecodeAnimation(string path, int maxFrames = int.MaxValue)
    {
        try
        {
            var header = ReadHeader(path);
            if (GifReader.IsGif(header)) return GifReader.Load(path, maxFrames);
            if (IsPng(header))
            {
                if (ApngReader.Load(path, maxFrames) is { } apng) return apng;
                var png = PngIO.Load(path);
                return new DecodedAnimation(png.Width, png.Height, [png], [Frame.DefaultDurationMs]);
            }
        }
        catch (InvalidDataException)
        {
            // 직접 읽기가 못 읽는 파일은 SkiaSharp에 맡긴다.
        }
        return DecodeWithSkia(path, maxFrames);
    }

    /// <summary>프레임 수만 센다(픽셀은 풀지 않음 — 많은 프레임을 열기 전에 묻기용). 읽을 수 없으면 0.</summary>
    public static int CountFrames(string path)
    {
        try
        {
            var header = ReadHeader(path);
            if (GifReader.IsGif(header)) return GifReader.Count(path).Frames;
            if (IsPng(header)) return Math.Max(1, ApngReader.CountFrames(path));
            using var codec = SKCodec.Create(path);
            return codec is null ? 0 : Math.Max(1, codec.FrameCount);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return 0;
        }
    }

    private static byte[] ReadHeader(string path)
    {
        using var fs = File.OpenRead(path);
        var buf = new byte[8];
        int n = fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
        return buf[..n];
    }

    private static bool IsPng(ReadOnlySpan<byte> header) => header.Length >= 8 && header[..8].SequenceEqual((ReadOnlySpan<byte>)[137, 80, 78, 71, 13, 10, 26, 10]);

    private static DecodedAnimation? DecodeWithSkia(string path, int maxFrames)
    {
        using var codec = SKCodec.Create(path);
        if (codec is null) return null;
        int w = codec.Info.Width, h = codec.Info.Height;
        if (w <= 0 || h <= 0 || w > PixelImage.MaxSize || h > PixelImage.MaxSize) return null;
        var infos = codec.FrameInfo;
        int count = Math.Clamp(infos.Length, 1, Math.Max(1, maxFrames));
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        var images = new List<PixelImage>(count);
        var durations = new List<int>(count);
        for (int i = 0; i < count; i++)
        {
            // 앞 프레임 위에 이어 푼다(코덱이 그 프레임을 바탕으로 쓸 수 없다고 하면 — 앞 프레임이 "이전으로"면 — 처음부터: 코덱이 필요한 프레임을 스스로 푼다).
            var result = codec.GetPixels(info, bitmap.GetPixels(), new SKCodecOptions(i, i > 0 ? i - 1 : -1));
            if (result == SKCodecResult.InvalidParameters && i > 0) result = codec.GetPixels(info, bitmap.GetPixels(), new SKCodecOptions(i, -1));
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) break;
            var pixels = new Color32[w * h];
            MemoryMarshal.Cast<byte, Color32>(bitmap.GetPixelSpan()).Slice(0, pixels.Length).CopyTo(pixels);
            images.Add(new PixelImage(w, h, pixels));
            durations.Add(FrameDurations.FromFile(i < infos.Length ? infos[i].Duration : 0));
        }
        if (images.Count == 0) return null;
        if (codec.EncodedOrigin != SKEncodedOrigin.TopLeft)
            for (int i = 0; i < images.Count; i++) images[i] = Orient(images[i], codec.EncodedOrigin);
        return new DecodedAnimation(images[0].Width, images[0].Height, images, durations);
    }

    /// <summary>EXIF 방향(사진의 회전·뒤집기)을 픽셀에 적용한다.</summary>
    private static PixelImage Orient(PixelImage src, SKEncodedOrigin origin)
    {
        int w = src.Width, h = src.Height;
        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int ow = swap ? h : w, oh = swap ? w : h;
        var dst = new PixelImage(ow, oh);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (dx, dy) = origin switch
                {
                    SKEncodedOrigin.TopRight => (w - 1 - x, y),
                    SKEncodedOrigin.BottomRight => (w - 1 - x, h - 1 - y),
                    SKEncodedOrigin.BottomLeft => (x, h - 1 - y),
                    SKEncodedOrigin.LeftTop => (y, x),
                    SKEncodedOrigin.RightTop => (h - 1 - y, x),
                    SKEncodedOrigin.RightBottom => (h - 1 - y, w - 1 - x),
                    SKEncodedOrigin.LeftBottom => (y, w - 1 - x),
                    _ => (x, y),
                };
                dst.Pixels[dy * ow + dx] = src.Pixels[y * w + x];
            }
        return dst;
    }

    /// <summary>그림 파일의 크기만 읽는다(픽셀은 풀지 않음). 읽을 수 없으면 null.</summary>
    public static Size? ReadImageSize(string path)
    {
        try
        {
            if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                var (w, h) = Gpixel.Core.IO.PngIO.ReadSize(path);
                return new Size(w, h);
            }
            using var codec = SKCodec.Create(path);
            if (codec is null) return null;
            bool swap = codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
            return swap ? new Size(codec.Info.Height, codec.Info.Width) : new Size(codec.Info.Width, codec.Info.Height);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>불투명 바탕 위에 색을 덮는다.</summary>
    private static Color32 Blend(Color32 back, Color32 c)
    {
        if (c.A == 255) return c;
        if (c.A == 0) return back;
        int a = c.A, ia = 255 - a;
        return new Color32((byte)((c.R * a + back.R * ia) / 255), (byte)((c.G * a + back.G * ia) / 255), (byte)((c.B * a + back.B * ia) / 255), 255);
    }

    /// <summary>Avalonia 색으로.</summary>
    public static Color ToColor(this Color32 c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    public static Color32 ToColor32(this Color c) => new(c.R, c.G, c.B, c.A);

    /// <summary>색 칸(투명이면 체커 위에)을 그린다.</summary>
    public static void DrawSwatch(DrawingContext g, Rect r, Color32 c)
    {
        if (c.A < 255) AppTheme.DrawChecker(g, r, Math.Max(2, r.Height / 4));
        g.FillRectangle(AppTheme.Brush(c.ToColor()), r);
    }
}
