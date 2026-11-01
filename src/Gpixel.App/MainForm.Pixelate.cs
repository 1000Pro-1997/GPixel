using Gpixel.App.Localization;
using Gpixel.Core.Edit;
using Gpixel.Core.Model;
using Gpixel.Core.Pixelate;
using Gpixel.Core.References;

namespace Gpixel.App;

// AI 그림 픽셀로 정리: AI 메뉴(빈 창 — 창에서 끌어 놓기·붙여 넣기·파일 찾기), 파일 브라우저 우클릭(그 파일) 또는 현재 레이어(AI 메뉴)를 원본으로 창을 열고,
// 고른 후보를 현재 문서의 새 레이어(ImageDrop — 실행취소 한 단계) 또는 새 문서로 넣는다. 고르면 팔레트 저장·AI 참고 자료·AI 다듬기(채팅에 미리 채움)까지.
public sealed partial class MainForm
{
    private PixelateDialog? _pixelate;

    private static MenuItem WithIcon(MenuItem item, VectorIcon icon)
    {
        item.Icon = Ui.Icon(icon, 14);
        return item;
    }

    /// <summary>그림 파일을 원본으로 연다(파일 브라우저 우클릭).</summary>
    private void PixelateFile(string path)
    {
        if (PixelateDialog.LoadImage(path) is not { } image)
        {
            Ui.Error(this, UiStrings.FormatPixelate_Unreadable(name: Path.GetFileName(path)));
            return;
        }
        ShowPixelate(new PixelateInput(image, Path.GetFileNameWithoutExtension(path), path), 1);
    }

    /// <summary>현재 레이어를 원본으로 연다: 그림이 있는 프레임이 여럿이면 가로로 이어 붙이고(프레임마다 한 칸) 격자로 나눈다.</summary>
    private void PixelateCurrentLayer()
    {
        if (_ctx.Project is not { } p) return;
        _ctx.CommitFloating();
        int layer = _ctx.CurrentLayer;
        if (_ctx.CurrentLayerObject is not { IsGroup: false } current)
        {
            ShowMessage(MainStrings.Pixelate_EmptyLayer);
            return;
        }
        var cels = new List<PixelImage>();
        for (int f = 0; f < p.Frames.Count; f++)
            if (p.GetCel(layer, f) is { } cel && !cel.IsRegionEmpty(cel.Bounds)) cels.Add(cel);
        if (cels.Count == 0)
        {
            ShowMessage(MainStrings.Pixelate_EmptyLayer);
            return;
        }
        PixelImage image;
        if (cels.Count == 1) image = cels[0].Clone();
        else
        {
            int w = cels[0].Width, h = cels[0].Height;
            image = new PixelImage(w * cels.Count, h);
            for (int i = 0; i < cels.Count; i++) image.Blit(cels[i], i * w, 0);
        }
        ShowPixelate(new PixelateInput(image, current.Name, null), cels.Count);
    }

    /// <summary>창을 연다(<paramref name="input"/>이 null이면 빈 창 — 창에서 끌어 놓기·붙여 넣기·파일 찾기로 원본을 넣음).</summary>
    /// <param name="preset">처음 단계 설정(채팅의 "변환 설정 보기" — 그 설정을 기억한 설정 대신 쓰고, 다시 최적화하지 않음). null = 기억한 설정.</param>
    private void ShowPixelate(PixelateInput? input, int sourceFrames, PixelateSettings? preset = null)
    {
        _pixelate?.Close();
        var dlg = new PixelateDialog(_settings, input, _colors.Palettes, _ctx.Project is not null && !_ctx.IsCanvasResizing,
            ApplyPixelate, AskAiAboutCandidates, () => _browser.CurrentFolder ?? _settings.LastFolder, ReadClipboard,
            img => { _ctx.Clipboard = img; PutOnSystemClipboard(img); }, sourceFrames, preset);
        dlg.Closed += (_, _) => { if (ReferenceEquals(_pixelate, dlg)) _pixelate = null; };
        _pixelate = dlg;
        dlg.Show(this);
    }

    void Ai.IAiHost.ShowPixelate(PixelateInput input, int sourceFrames, PixelateSettings settings) => ShowPixelate(input, sourceFrames, settings);

    bool Ai.IAiHost.InsertPixelArt(string name, IReadOnlyList<PixelImage> frames, int frameDurationMs, bool newDocument, out string message)
        => InsertPixelated(name, frames, frameDurationMs, newDocument, interactive: false, out message);

    private void ApplyPixelate(PixelateOutcome o)
    {
        var frames = o.Result.CommonFrames();
        if (frames.Count == 0) return;
        var name = o.Input.Name;
        if (!InsertPixelated(name, frames, o.FrameDurationMs, o.NewDocument, interactive: true, out var message)) return;

        if (o.SavePalette && o.Result.Palette.Count > 0)
            _colors.AddPalette(MainStrings.FormatPixelate_PaletteName(name: name), o.Result.Palette);
        if (o.AddReference)
        {
            try
            {
                ((Ai.IAiHost)this).References.AddImage(ReferenceFrames.Single(o.Input.Image), name, null, null, null, ReferenceKinds.Character, CaptionSources.None);
                message += MainStrings.Pixelate_ReferenceAdded;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                message += MainStrings.FormatPixelate_ReferenceFailed(message: ex.Message);
            }
        }
        ShowMessage(message);
        if (o.AiTouchUp && !string.IsNullOrWhiteSpace(o.AiPrompt))
        {
            ToggleChat();
            _chat?.SelectProvider(o.AiProvider);
            _chat?.AttachImage(o.Input.Image, pixelArt: false);
            _chat?.PrefillInput(o.AiPrompt);
        }
    }

    /// <summary>
    /// 정리한 프레임을 넣는다: 열린 문서의 새 레이어(ImageDrop — 실행취소 한 단계, 프레임이 모자라면 더함) 또는 새 탭의 새 문서(결과 크기).
    /// <paramref name="interactive"/>면(창의 적용) 새 레이어로 넣은 뒤 변형 모드로 들어간다(AI 도구는 그대로 둠).
    /// </summary>
    private bool InsertPixelated(string name, IReadOnlyList<PixelImage> frames, int durationMs, bool newDocument, bool interactive, out string message)
    {
        message = "";
        if (frames.Count == 0) return false;
        var size = frames[0].Size;
        if (!newDocument && _ctx.Project is { } p && !_ctx.IsCanvasResizing)
        {
            _ctx.CommitFloating();
            var durations = Enumerable.Repeat(Math.Max(1, durationMs), frames.Count).ToList();
            DroppedImage[] images = [new DroppedImage(name, frames, durations)];
            int frame = _ctx.CurrentFrame;
            ImageDropResult? result = null;
            _ctx.Structural(() => result = ImageDrop.Apply(p, images, _ctx.CurrentLayer, frame,
                new Point(p.Width / 2, p.Height / 2), MainStrings.Pixelate_UndoLabel));
            if (result is not { Layers.Count: > 0 }) return false;
            _ctx.SetCurrentLayer(p.IndexOf(result.Layers[^1]));
            message = MainStrings.FormatPixelate_AddedLayer(name: result.Layers[^1].Name, frames: frames.Count, width: size.Width, height: size.Height);
            if (result.AddedFrames > 0) message += MainStrings.FormatDrop_FramesAdded(count: result.AddedFrames);
            // 창의 적용이면 넣은 그림을 잘리기 전 그대로 들고 변형 모드로(옮기거나 크기를 맞춘 뒤 Enter — 그때 캔버스 밖이 잘림).
            var (transforming, hint) = interactive ? TransformDropped(result, images, frame) : (false, "");
            if (result.Oversized && !transforming) message += MainStrings.Drop_Oversized;
            message += hint;
        }
        else
        {
            var doc = BuildProject(size.Width, size.Height, null);
            doc.Edit(MainStrings.Pixelate_UndoLabel, ProjectChange.Layers | ProjectChange.Frames | ProjectChange.Pixels, () =>
            {
                doc.Layers[0].Name = name;
                for (int i = 0; i < frames.Count; i++)
                {
                    if (i > 0) doc.AddFrame();
                    doc.SetFrameDuration(i, Math.Max(1, durationMs));
                    doc.SetCel(0, i, frames[i].Clone());
                }
            });
            doc.History.Clear();
            OpenInNewTab(new OpenDocument(doc, null));
            doc.IsDirty = true;
            message = MainStrings.FormatPixelate_NewDocument(frames: frames.Count, width: size.Width, height: size.Height);
        }
        return true;
    }

    /// <summary>"AI에게 후보 평가 부탁": 채팅을 열어 원본과 후보 그림을 첨부하고 부탁 글을 채운다(보내지 않음).</summary>
    private void AskAiAboutCandidates(string provider, string prompt, IReadOnlyList<PixelImage> images)
    {
        ToggleChat();
        if (_chat is not { } chat) return;
        chat.SelectProvider(provider);
        for (int i = 0; i < images.Count; i++) chat.AttachImage(images[i], pixelArt: i > 0);
        chat.PrefillInput(prompt);
        chat.Activate();
    }
}
