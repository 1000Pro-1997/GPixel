using Gpixel.Core.Assets;
using Gpixel.Core.IO;

namespace Gpixel.App;

/// <summary>
/// 둘러보기 목록/상세의 원격 미리보기 PNG: 화면에 붙을 때만 공유된 요청(디스크 cache 먼저)을 기다려 decode한다.
/// 효과는 앱의 병아리 견본과 카탈로그 그림을 나란히 보여 준다. 떨어지면 결과를 무시하고 비트맵을 버린다.
/// </summary>
public sealed class RemotePreviewView : Control
{
    private static readonly WriteableBitmap Chick = ImageUtil.ToBitmap(AssetPreview.SampleSprite());
    private readonly Func<Task<byte[]?>> _getPreview;
    private readonly bool _showSource;
    private CancellationTokenSource? _load;
    private WriteableBitmap? _bitmap;

    public RemotePreviewView(Func<Task<byte[]?>> getPreview, double height = 160, bool showSource = false)
    {
        _getPreview = getPreview;
        _showSource = showSource;
        Height = height;
    }

    protected override async void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var load = _load = new CancellationTokenSource();
        try
        {
            if (await _getPreview() is not { } png || load.IsCancellationRequested) return;
            using var stream = new MemoryStream(png, writable: false);
            _bitmap = ImageUtil.ToBitmap(PngIO.Load(stream));
            InvalidateVisual();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException) { }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // 목록과 상세가 요청을 공유하므로 이 뷰만 취소한다. 창이 닫히면 공유 요청을 취소한다.
        _load?.Cancel();
        _load = null;
        _bitmap?.Dispose();
        _bitmap = null;
    }

    public override void Render(DrawingContext g)
    {
        var b = new Rect(Bounds.Size);
        g.FillRectangle(AppTheme.PanelBrush, b);
        g.DrawRectangle(new Pen(AppTheme.BorderBrush, 1), b.Deflate(0.5));
        var area = b.Deflate(8);
        if (_showSource)
        {
            double width = Math.Min(area.Height, area.Width * 0.48);
            var sourceArea = new Rect(area.X, area.Y, width, area.Height);
            var sprite = new Rectangle(0, 0, Chick.PixelSize.Width, Chick.PixelSize.Height);
            double sourceFit = Math.Min(sourceArea.Width / sprite.Width, sourceArea.Height / sprite.Height);
            ImageUtil.DrawFitted(g, Chick, sprite, sourceArea, sourceFit >= 1 ? (int)Math.Floor(sourceFit) : 0, checker: true);
            area = new Rect(area.X + width, area.Y, area.Width - width, area.Height);
        }
        if (_bitmap is not { } bmp) return;
        var src = new Rectangle(0, 0, bmp.PixelSize.Width, bmp.PixelSize.Height);
        double fit = Math.Min(area.Width / src.Width, area.Height / src.Height);
        ImageUtil.DrawFitted(g, bmp, src, area, fit >= 1 ? (int)Math.Floor(fit) : 0, checker: true);
    }
}
