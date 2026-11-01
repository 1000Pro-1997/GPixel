using Gpixel.Core.Assets;
using Gpixel.Core.Effects;
using Gpixel.Core.IO;
using Gpixel.Core.Model;

namespace Gpixel.App;

/// <summary>
/// 에셋 상세의 미리보기: 패키지가 넣은 미리보기 PNG가 있으면 그 그림(보일 때 한 번 decode), 없으면 효과 레시피를 견본 그림에 입혀 그린다
/// (움직이는 효과는 <see cref="AssetPreview.Steps"/>칸을 돌려 보임). 화면에서 떨어지면 타이머와 비트맵을 버린다.
/// </summary>
public sealed class AssetPreviewView : Control
{
    private readonly AssetEntry? _entry;
    private readonly IReadOnlyList<LayerEffect>? _effects;
    private readonly bool _useStoredPreview;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private WriteableBitmap?[] _frames = [];
    private int _shown;
    private int _tick;

    public AssetPreviewView(AssetEntry entry, bool useStoredPreview = true) : this(entry, entry.EffectTemplate?.Effects)
        => _useStoredPreview = useStoredPreview;

    public AssetPreviewView(EffectDefinition definition) : this(null, [definition.Create()]) => Height = 92;

    private AssetPreviewView(AssetEntry? entry, IReadOnlyList<LayerEffect>? effects)
    {
        _entry = entry;
        _effects = effects;
        Height = 160;
        _timer.Tick += (_, _) =>
        {
            // 움직이는 효과: 0 → 끝까지 돌리고 끝 모습을 잠깐 보여 준 뒤 처음부터(효과 창 상자와 같음).
            _tick = (_tick + 1) % (AssetPreview.Steps + 4);
            Show(Math.Min(_tick, AssetPreview.Steps - 1));
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_useStoredPreview && _entry?.PreviewPath is { } path && TryLoad(path) is { } image) _frames = [ImageUtil.ToBitmap(image)];
        else if (_effects is { } effects)
        {
            bool animated = effects.Any(effect => effect is AnimatedEffect || effect.ColorGradients.Values.Any(g => g.Animated));
            _frames = new WriteableBitmap?[animated ? AssetPreview.Steps : 1];
            Show(0);
            if (animated) _timer.Start();
        }
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
        foreach (var frame in _frames) frame?.Dispose();
        _frames = [];
    }

    private static PixelImage? TryLoad(string path)
    {
        try
        {
            return PngIO.Load(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }

    private void Show(int step)
    {
        if (_effects is not { } effects || _frames.Length == 0) return;
        step = Math.Clamp(step, 0, _frames.Length - 1);
        _frames[step] ??= ImageUtil.ToBitmap(AssetPreview.Render(effects, AssetPreview.SampleSprite(), step));
        _shown = step;
        InvalidateVisual();
    }

    public override void Render(DrawingContext g)
    {
        var b = new Rect(Bounds.Size);
        g.FillRectangle(AppTheme.PanelBrush, b);
        g.DrawRectangle(new Pen(AppTheme.BorderBrush, 1), b.Deflate(0.5));
        if (_frames.Length == 0 || _frames[Math.Min(_shown, _frames.Length - 1)] is not { } bmp) return;
        var area = b.Deflate(8);
        var src = new Rectangle(0, 0, bmp.PixelSize.Width, bmp.PixelSize.Height);
        double fit = Math.Min(area.Width / src.Width, area.Height / src.Height);
        ImageUtil.DrawFitted(g, bmp, src, area, fit >= 1 ? (int)Math.Floor(fit) : 0, checker: true);
    }
}
