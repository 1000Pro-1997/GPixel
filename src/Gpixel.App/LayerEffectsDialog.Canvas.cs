using Gpixel.Core.Edit;
using Gpixel.Core.Model;
using Gpixel.App.Localization;

namespace Gpixel.App;

public sealed partial class LayerEffectsDialog
{
    /// <summary>
    /// 늘린 뒤 모든 프레임에서 이 레이어의 켜진 효과 결과가 차지하는 캔버스 좌표 범위.
    /// 실제 불투명 픽셀만 재므로 한 번 확대한 뒤 같은 효과로 다시 확대해도 더 커지지 않는다.
    /// </summary>
    private Rectangle EffectOutputBounds(IReadOnlyList<LayerEffect> all)
    {
        var bounds = new Rectangle(0, 0, _project.Width, _project.Height);
        int ox = _layer.IsPart ? _layer.PartOffset.X : 0;
        int oy = _layer.IsPart ? _layer.PartOffset.Y : 0;
        for (int frame = 0; frame < VirtualCount; frame++)
        {
            var (source, effects) = AnimateAt(frame, all);
            if (source is null || !LayerEffects.AnyEnabled(effects)) continue;
            var (rendered, dx, dy) = LayerEffects.Render(source, effects);
            var opaque = rendered.GetOpaqueBounds();
            if (opaque.IsEmpty) continue;
            opaque.Offset(ox + dx, oy + dy);
            bounds = Rectangle.Union(bounds, opaque);
        }
        return bounds;
    }

    /// <summary>지금 효과·프레임 늘리기와 선택한 방식으로 확인 때 적용할 캔버스 확대(<see cref="WaitOutputBounds"/>).</summary>
    private EffectCanvasExpansion CanvasExpansion(IReadOnlyList<LayerEffect> all)
        => EffectCanvasExpansion.FromBounds(_project.Width, _project.Height, WaitOutputBounds(all), s_canvasExpandMode);

    /// <summary>
    /// 모든 프레임의 효과 결과 범위. 작업 스레드가 다 그렸으면 그 결과를 쓰고, 아직 그리는 중이면 덮개(퍼센트)를 띄우고 끝날 때까지 기다린다 —
    /// 그리던 것을 이어 그리므로 처음부터 다시 그리지 않는다(예전에는 확인을 누르면 모든 프레임을 화면 스레드에서 다시 그려 몇 초씩 멈췄다).
    /// 그리던 것이 없으면 덮개를 띄우고 작업 스레드에서 모두 그려 잰다.
    /// </summary>
    private Rectangle WaitOutputBounds(IReadOnlyList<LayerEffect> all)
    {
        if (RenderedOutputBounds() is { } done) return done;
        if (_renders is { } job && !job.Cts.IsCancellationRequested)
        {
            WorkOverlay.Run(this, UiStrings.Busy_EffectBounds, progress =>
            {
                using var stage = progress.Begin(job.Count);
                int reported = 0;
                while (!job.Task.IsCompleted)
                {
                    job.Task.Wait(30);
                    int finished = job.Count - Volatile.Read(ref job.Remaining);
                    stage.Step(finished - reported);
                    reported = finished;
                }
            });
            if (RenderedOutputBounds() is { } bounds) return bounds;
        }
        return WorkOverlay.Run(this, UiStrings.Busy_EffectBounds, _ => EffectOutputBounds(all));
    }

    /// <summary>모든 프레임에 공통인 전·후 미리보기 영역과 확인 후 캔버스(원래 캔버스 좌표).</summary>
    private Rectangle _previewBounds, _previewCanvas;

    /// <summary>
    /// 모든 프레임의 결과로 확인 때 적용할 캔버스를 계산하고, 미리보기 배율·위치를 고정한다. 아직 모든 프레임을 그리지 않았으면
    /// 지금 자리·배율을 그대로 둔다(다 그리면 다시 부름).
    /// </summary>
    private void SyncCanvasExpansion()
    {
        if (RenderedOutputBounds() is not { } output) return;
        var currentCanvas = new Rectangle(0, 0, _project.Width, _project.Height);
        _previewBounds = _previewCanvas = currentCanvas;
        try
        {
            if (_expandCanvas.On())
            {
                var plan = EffectCanvasExpansion.FromBounds(_project.Width, _project.Height, output, s_canvasExpandMode);
                _previewCanvas = new Rectangle(-plan.OffsetX, -plan.OffsetY, plan.Width, plan.Height);
                _previewBounds = _previewCanvas;
                _expandNote.Foreground = AppTheme.DimTextBrush;
                _expandNote.Text = plan.IsNone(_project.Width, _project.Height)
                    ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture, UiStrings.S_LayerEffectsDialog_Canvas_SyncCanvasExpansion_BDEB632BC8, _project.Width, _project.Height)
                    : $"{_project.Width}×{_project.Height} → {plan.Width}×{plan.Height}"
                      + (s_canvasExpandMode == EffectCanvasExpandMode.KeepRatio && !plan.RatioPreserved ? UiStrings.S_LayerEffectsDialog_Canvas_SyncCanvasExpansion_6BBDE7B81F : "");
                ToolTip.SetTip(_expandNote, plan.IsNone(_project.Width, _project.Height)
                    ? UiStrings.S_LayerEffectsDialog_Canvas_SyncCanvasExpansion_EFFF97903C
                    : string.Format(global::System.Globalization.CultureInfo.CurrentCulture, UiStrings.S_LayerEffectsDialog_Canvas_SyncCanvasExpansion_AA4018CCC5, plan.OffsetX, plan.OffsetY));
            }
            else if (output.Width <= PixelImage.MaxSize && output.Height <= PixelImage.MaxSize)
            {
                // 확장을 끈 경우 잘릴 효과는 캔버스 바깥에 흐리게 남기되, 프레임마다 배율이 흔들리지 않는다.
                _previewBounds = output;
            }
        }
        catch (InvalidOperationException ex)
        {
            _expandNote.Foreground = AppTheme.WarningBrush;
            _expandNote.Text = ex.Message;
            ToolTip.SetTip(_expandNote, ex.Message);
        }
    }

    /// <summary>그림 크기는 그대로 두고 계획한 크기·자리로 프로젝트 캔버스를 늘린다.</summary>
    private void ApplyCanvasExpansion(EffectCanvasExpansion plan)
        => _project.ResizeCanvasAndScale(plan.Width, plan.Height,
            new Rectangle(plan.OffsetX, plan.OffsetY, _project.Width, _project.Height));

    private static string CanvasExpansionSummary(int oldWidth, int oldHeight, EffectCanvasExpansion plan)
        => string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Canvas_CanvasExpansionSummary_332F7D70DF,oldWidth,oldHeight,plan.Width,plan.Height);
}
