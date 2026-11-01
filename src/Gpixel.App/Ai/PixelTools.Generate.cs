using System.Globalization;
using System.Text.Json.Nodes;
using Gpixel.Core.AiDraw;
using Gpixel.Core.IO;
using Gpixel.Core.Model;
using Gpixel.Core.Pixelate;
using Gpixel.Core.References;
using Gpixel.App.Localization;

namespace Gpixel.App.Ai;

/// <summary>그림 생성이 끝남: 기록(다시 열기용), 결과 그림(애니메이션이면 프레임을 나란히), 사용자에게 보일 결과 글.</summary>
public sealed record AiDrawOutcome(AiDrawJob Job, PixelImage Preview, string Message);

/// <summary>
/// 그림 생성 도구(<c>generate_pixel_art</c>): 사용자의 Codex CLI 이미지 생성으로 그림을 만들고(애니메이션은 프레임마다 하나씩 — 첫 프레임·앞 프레임을
/// 함께 보내 같은 캐릭터로), 프레임을 이어 한 원본으로 픽셀 아트 변환(자동 최적화, 공유 팔레트, 외곽선 1px)한 뒤 문서에 넣는다(새 레이어 = 차례의
/// 실행취소 한 단계, 또는 새 문서). 기록은 <see cref="AiDrawJobStore"/>에 남겨 채팅의 "변환 설정 보기"로 정리 창에서 다시 연다.
/// 이 도구만 비동기다(<see cref="CallAsync"/> — UI 스레드에서 기다리며 진행을 알림).
/// </summary>
public sealed partial class PixelTools
{
    private const string GenerateTool = "generate_pixel_art";
    /// <summary>스타일 참고 그림을 보낼 때 키울 긴 변(px, 정수 배 최근접).</summary>
    private const int StyleImageSide = 512;

    private static readonly string[] AnimationValues = ["none", .. AiDrawActions.All, AiDrawActions.Custom];

    private readonly AiDrawJobStore _jobs = new(Path.Combine(Platform.AppDataDirectory, "Gpixel", "AiDraw"));
    private CancellationTokenSource? _aiDrawCts;
    /// <summary>마지막으로 생성한 첫 그림(<c>previous_result_as_subject</c> — 방금 그린 캐릭터를 움직이거나 고칠 때 같은 디자인으로).</summary>
    private string? _lastGenerated;

    /// <summary>생성 기록 보관소.</summary>
    public AiDrawJobStore Jobs => _jobs;

    /// <summary>편집기(채팅 창이 "변환 설정 보기"로 정리 창을 열 때 씀).</summary>
    public IAiHost Host => _host;

    /// <summary>지금 차례에 사용자가 붙인 그림(PNG, 픽셀 아트인지) — 보낸 순서. 채팅 창이 차례마다 넣고 끝나면 비운다.</summary>
    public IReadOnlyList<(byte[] Png, bool PixelArt)> TurnImages { get; set; } = [];

    /// <summary>그림 생성 진행(사용자에게 보일 짧은 글 — "프레임 2/6 생성 중"). UI 스레드.</summary>
    public event Action<string>? AiDrawProgress;

    /// <summary>그림 생성이 끝나 문서에 넣음. UI 스레드.</summary>
    public event Action<AiDrawOutcome>? AiDrawCompleted;

    /// <summary>오래 걸리는 도구(MCP 서버가 SSE로 답하며 기다림).</summary>
    public static bool IsLongRunning(string name) => name == GenerateTool;

    /// <summary>도는 그림 생성을 멈춘다(채팅의 멈춤).</summary>
    public void CancelAiDraw() => _aiDrawCts?.Cancel();

    /// <summary>
    /// 도구 하나를 실행한다(UI 스레드). 그림 생성은 기다리며 <paramref name="progress"/>로 진행을 알리고 <paramref name="ct"/>(클라이언트가 끊음)로 멈춘다.
    /// 다른 도구는 <see cref="Call"/>과 같다. 예외는 밖으로 내보내지 않는다.
    /// </summary>
    public async Task<JsonObject> CallAsync(string name, JsonObject? arguments, Action<string>? progress, CancellationToken ct)
    {
        if (name != GenerateTool) return Call(name, arguments);
        try
        {
            return await GeneratePixelArtAsync(arguments, progress, ct);
        }
        catch (ToolError e)
        {
            return TextResult($"{name}: {e.Message}", isError: true);
        }
        catch (AiDrawException e)
        {
            return TextResult($"{name}: {e.Message}", isError: true);
        }
        catch (OperationCanceledException)
        {
            return TextResult($"{name}: {UiStrings.AiDraw_Cancelled}", isError: true);
        }
        catch (Exception e)
        {
            return TextResult(string.Format(CultureInfo.CurrentCulture, UiStrings.S_PixelTools_Call_B686C7AE84, name, CleanMessage(e)), isError: true);
        }
    }

    private static IEnumerable<JsonObject> GenerateToolDefinitions()
    {
        yield return Tool(GenerateTool,
            "Draw new pixel art the high-quality way: the app generates the image(s) with Codex image generation (the user's own Codex login) and " +
            "converts them to clean pixel art (auto-optimized grid and palette, 1px outline, background removed), then inserts the result into the " +
            "document as a new layer or a new document (per the user's settings) as one undo step. For animations it plans the frames (default count " +
            "from the user's settings) and, by default, generates all frames at once as one sprite sheet so the character stays identical (the user can " +
            "switch to one image per frame), then splits and converts all frames together with one shared grid and palette. Takes about 30-90 seconds per " +
            "image plus up to about 2 minutes of conversion auto-optimization. Returns the converted result as an image and a summary; if the conversion fails " +
            "(empty result — wrong pixel size detected) nothing is inserted and an error is returned.",
            new JsonObject
            {
                ["subject"] = Str("What to draw, in English, with concrete visual details: who/what it is, body shape and proportions, outfit, colors, " +
                    "held items, expression (e.g. \"a small knight in silver armor with a blue cape, round helmet with a red plume, holding a short sword\"). " +
                    "For an animation describe the character only; the motion goes in `animation`/`poses`."),
                ["kind"] = Enum("What it is (default character).", KindValues),
                ["animation"] = Enum("Animation to make (default none = a single image). custom needs `poses`.", AnimationValues),
                ["frames"] = Int($"Number of animation frames ({AiDrawSettings.MinFrames}-{AiDrawSettings.MaxFrames}). Default: the user's setting for that animation (e.g. walk 6, idle 4), or the number of `poses`.",
                    AiDrawSettings.MinFrames, AiDrawSettings.MaxFrames),
                ["poses"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                    ["description"] = "Optional English pose description per frame, in order (for custom motions or to override the built-in poses of a standard animation).",
                },
                ["view"] = Str("Camera/facing, in English (default: side view facing right for animations, front three-quarter view for a still character)."),
                ["background"] = Str("Only if the user explicitly asked for a background or scene: describe it in English. Omit otherwise; a plain transparent background is used."),
                ["size"] = Int($"Rough size of the sprite in pixels (long side, {AiDrawSettings.MinSize}-{AiDrawSettings.MaxSize}) when the user mentions one (e.g. \"64x64\"). It is a scale hint for the " +
                    "generated drawing; the conversion keeps the pixel grid found in the image unless the user turned on result-size correction in the settings. Omit otherwise.",
                    AiDrawSettings.MinSize, AiDrawSettings.MaxSize),
                ["subject_image"] = Int("Number N of an image the user attached to this message (written as [Image #N] in the message) that shows the character/item to draw " +
                    "(e.g. \"make this character as pixel art\"). A suitable pixel-art style reference is then added automatically.", 1),
                ["previous_result_as_subject"] = Bool("Attach the image generated by the previous generate_pixel_art call (its first frame or its sprite sheet) as the subject reference, to keep exactly the same " +
                    "character (e.g. animating or re-posing the character you just drew). Ignored when subject_image is given."),
                ["style_reference"] = Str("Reference id (from search_references) to use as the pixel-art style reference. Omit to let the app choose per the user's settings."),
                ["name"] = Str("Name for the new layer/document (default: from the subject)."),
            },
            "subject");
    }

    private async Task<JsonObject> GeneratePixelArtAsync(JsonObject? a, Action<string>? progress, CancellationToken ct)
    {
        if (_aiDrawCts is not null) throw new ToolError(UiStrings.AiDraw_Error_Busy);
        var options = _host.Settings.AiDraw;
        var subject = ReqString(a, "subject").Trim();
        if (subject.Length == 0) throw new ToolError(UiStrings.AiDraw_Error_NoSubject);
        var kind = OptEnum(a, "kind", KindValues) ?? ReferenceKinds.Character;
        var animation = OptEnum(a, "animation", AnimationValues) is { } an && an != "none" ? an : null;
        var poses = OptStringList(a, "poses");
        if (animation == AiDrawActions.Custom && poses.Count == 0) throw new ToolError(UiStrings.AiDraw_Error_CustomPoses);
        int? frames = OptInt(a, "frames", AiDrawSettings.MinFrames, AiDrawSettings.MaxFrames);
        // 크기: 생성 프롬프트에는 대략적인 크기 힌트로, 변환은 "결과 크기 보정"이 켜졌을 때만 그 크기로 고정(꺼지면 자동 크기 + 자동 최적화).
        var (size, convertSize) = options.SizesFor(OptInt(a, "size", AiDrawSettings.MinSize, AiDrawSettings.MaxSize));
        int? subjectIndex = OptInt(a, "subject_image", 1, Math.Max(1, TurnImages.Count)) is { } number ? number - 1 : null;
        if (subjectIndex >= TurnImages.Count) throw new ToolError(UiStrings.AiDraw_Error_NoImageAttached);
        var styleId = OptString(a, "style_reference")?.Trim();
        var name = OptString(a, "name")?.Trim() is { Length: > 0 } n ? n : ShortName(subject);
        var request = new AiDrawRequest
        {
            Subject = subject,
            Kind = kind,
            Action = animation,
            View = OptString(a, "view"),
            Background = OptString(a, "background"),
            SizeTarget = size,
        };

        // 넣을 곳: "지금 그림만"이면 그 문서의 새 레이어, 열린 문서가 없으면 새 문서.
        bool newDocument = Scope is null && (options.Insert == AiDrawInsert.NewDocument || Ctx.Project is null);
        if (!newDocument) BeginMutation();
        var codexPath = AiProvider.Codex.Locate(_host.Settings) ?? throw new ToolError(UiStrings.AiDraw_Error_NoCodex);

        int count = animation is null ? 1 : frames ?? (poses.Count >= AiDrawSettings.MinFrames ? poses.Count : options.FramesFor(animation));
        var plan = AiDrawPlanner.Plan(animation, count, poses);
        var jobId = _jobs.Create();
        var dir = _jobs.DirectoryOf(jobId);

        // 함께 보낼 그림: 사용자가 붙인 대상 그림, 픽셀 아트 스타일 참고.
        string? subjectFile = null;
        bool subjectIsPixelArt = false;
        if (subjectIndex is { } si)
        {
            subjectFile = Path.Combine(dir, "subject.png");
            await File.WriteAllBytesAsync(subjectFile, TurnImages[si].Png, ct);
            subjectIsPixelArt = TurnImages[si].PixelArt;
        }
        else if (OptBool(a, "previous_result_as_subject") == true)
        {
            if (_lastGenerated is not { } previous || !File.Exists(previous)) throw new ToolError(UiStrings.AiDraw_Error_NoPrevious);
            subjectFile = Path.Combine(dir, "subject.png");
            File.Copy(previous, subjectFile, overwrite: true);
            // 이미 픽셀 아트 그림이라 스타일 참고는 붙이지 않는다.
            subjectIsPixelArt = true;
        }
        var style = StyleReference(options, styleId, subject, kind, size, subjectFile is not null && !subjectIsPixelArt, subjectIsPixelArt);
        string? styleFile = null;
        if (style is not null)
        {
            styleFile = Path.Combine(dir, "style.png");
            PngIO.Save(ScaleUpTo(style.Frame, StyleImageSide), styleFile);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _aiDrawCts = cts;
        try
        {
            var generator = new CodexImageGenerator(codexPath, _host.Settings.CodexModel);
            var prompts = new List<string>(count);
            // 처음 그림에 붙일 것: 대상 그림, 스타일 참고.
            var firstRoles = new List<AiDrawImageRole>();
            var firstFiles = new List<string>();
            if (subjectFile is not null) { firstRoles.Add(AiDrawImageRole.Subject); firstFiles.Add(subjectFile); }
            if (styleFile is not null) { firstRoles.Add(AiDrawImageRole.Style); firstFiles.Add(styleFile); }
            bool sheet = count > 1 && options.AnimationMethod == AiDrawAnimationMethod.SingleSheet;
            bool keepOffsets = animation == AiDrawActions.Jump;
            var settings = AiDrawConversion.Settings(new PixelateSettings(), count, convertSize, options.RemoveBackground, keepOffsets);
            PixelImage source;
            string firstImage;
            var layout = AiDrawSheetLayout.For(count);
            if (sheet)
            {
                // 애니메이션 전체를 한 번에 한 장(스프라이트 시트)으로 — 같은 그림 안이라 프레임끼리 캐릭터가 맞는다.
                Report(progress, UiStrings.FormatAiDraw_Progress_Sheet(count: count));
                var prompt = AiDrawPrompts.Sheet(request, plan, layout, firstRoles);
                prompts.Add(prompt);
                firstImage = _jobs.SheetPath(jobId);
                await generator.GenerateAsync(AiDrawPrompts.CodexInstruction(prompt, request.Transparent, firstFiles.Count, landscape: true), firstFiles, firstImage, cts.Token);
                source = AiDrawConversion.Downscale(LoadGenerated(firstImage), AiDrawConversion.MaxSheetSide);
            }
            else
            {
                var images = new List<PixelImage>(count);
                foreach (var f in plan)
                {
                    Report(progress, count == 1 ? UiStrings.AiDraw_Progress_Image : UiStrings.FormatAiDraw_Progress_Frame(index: f.Index + 1, count: count));
                    var roles = new List<AiDrawImageRole>();
                    var files = new List<string>();
                    if (f.Index == 0)
                    {
                        roles.AddRange(firstRoles);
                        files.AddRange(firstFiles);
                    }
                    else
                    {
                        roles.Add(AiDrawImageRole.FirstFrame);
                        files.Add(_jobs.FramePath(jobId, 0));
                        if (f.Index > 1) { roles.Add(AiDrawImageRole.PreviousFrame); files.Add(_jobs.FramePath(jobId, f.Index - 1)); }
                    }
                    var prompt = AiDrawPrompts.Frame(request, f, roles);
                    prompts.Add(prompt);
                    var output = _jobs.FramePath(jobId, f.Index);
                    await generator.GenerateAsync(AiDrawPrompts.CodexInstruction(prompt, request.Transparent, files.Count), files, output, cts.Token);
                    images.Add(LoadGenerated(output));
                }
                firstImage = _jobs.FramePath(jobId, 0);
                source = AiDrawConversion.Combine([.. images.Select(i => AiDrawConversion.Downscale(i, AiDrawConversion.MaxFrameSide))]);
            }

            Report(progress, options.AutoOptimize ? UiStrings.AiDraw_Progress_Optimizing : UiStrings.AiDraw_Progress_Converting);
            // 정리 창과 같은 자동 최적화: 체크한 첫 최적화 프리셋(없으면 첫 프리셋 — PixelateOptions.PrimaryPreset)의 항목·예산·점수 기준으로 — 1위를 그대로 넣는다.
            var pixelate = _host.Settings.Pixelate ??= new PixelateOptions();
            pixelate.Migrate();
            var optimize = pixelate.PrimaryPreset.Options();
            // 진행: UI 스레드로(이 메서드의 동기화 문맥), 0.2초에 한 번쯤.
            IProgress<PixelateOptimizeProgress> ui = new Progress<PixelateOptimizeProgress>(p => Report(progress, UiStrings.FormatAiDraw_Progress_OptimizeTrial(
                pass: p.Pass, done: p.Done, total: p.Budget, score: p.BestScore.ToString("0.0", CultureInfo.CurrentCulture))));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long lastReport = -1000;
            void OnProgress(PixelateOptimizeProgress p)
            {
                long now = clock.ElapsedMilliseconds, prev = Interlocked.Read(ref lastReport);
                if (now - prev >= 200 && Interlocked.CompareExchange(ref lastReport, now, prev) == prev) ui.Report(p);
            }
            PixelateResult result;
            PixelateSettings used;
            try
            {
                (result, used) = await Task.Run(() => AiDrawConversion.Convert(source, settings, count, sheet, keepOffsets, options.AutoOptimize, optimize, OnProgress, cts.Token), cts.Token);
            }
            catch (InvalidOperationException)
            {
                throw new AiDrawException(UiStrings.AiDraw_Error_NoSprite);
            }
            // 1위가 실패한 결과(비었거나 거의 비었음·한 색·너무 작음 — 칸 크기 감지가 잘못됨)면 넣지 않는다.
            if (result.Failed)
                throw new AiDrawException(UiStrings.FormatAiDraw_Error_ConversionFailed(
                    reason: PixelateDialog.FailureReason(result.Failure), scale: result.DetectedScale.ToString("0.##", CultureInfo.CurrentCulture)));
            var outFrames = result.CommonFrames();
            if (outFrames.Count == 0) throw new AiDrawException(UiStrings.AiDraw_Error_NoSprite);

            // 생성하는 동안 사용자가 문서를 바꿨을 수 있다.
            if (Scope is { } scope && !ReferenceEquals(Ctx.Project, scope.Project)) throw new ToolError(UiStrings.Ai_Scope_OtherDocument);
            if (!newDocument && Ctx.Project is null) newDocument = true;
            int duration = AiDrawActions.DurationMs(animation);
            // 기다리는 동안 저장·열기 덮개가 떴으면(작업 스레드가 프로젝트를 읽는 중) 끝난 뒤에 넣는다.
            while (WorkOverlay.IsActive) await WorkOverlay.WhenIdle();
            if (!_host.InsertPixelArt(name, outFrames, duration, newDocument, out var message)) throw new ToolError(message);

            var job = new AiDrawJob
            {
                Id = jobId,
                Name = name,
                Frames = count,
                Action = animation,
                Settings = PixelatePresets.SerializeSettings(used, indented: false),
                Prompts = prompts,
                CreatedUtc = DateTime.UtcNow,
                FrameDurationMs = duration,
            };
            try
            {
                _jobs.Save(job, source);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 기록을 남기지 못해도 결과는 이미 넣었다("변환 설정 보기"만 안 됨).
            }
            _lastGenerated = firstImage;
            var preview = Strip(outFrames);
            AiDrawCompleted?.Invoke(new AiDrawOutcome(job, preview, message));

            var (png, zoom) = Enlarge(preview, null);
            var summary = (sheet ? $"Generated one sprite sheet with {count} frames" : $"Generated {count} image(s)") + " with Codex image generation and converted it to pixel art: " + $"{outFrames.Count} frame(s) of " +
                          $"{outFrames[0].Width}x{outFrames[0].Height} px, {result.Palette.Count} colors, inserted " +
                          (newDocument ? "as a new document" : "as a new layer in the open document") + $" (\"{name}\")" +
                          (style is not null ? $", pixel-art style reference: {style.Item.Id}" : "") +
                          $". The image shows the result{(outFrames.Count > 1 ? " frames side by side" : "")} at {zoom}. " +
                          "The user can tweak the conversion with the \"View conversion settings\" button in the chat. Reply briefly.";
            return ImageResult(png, summary);
        }
        finally
        {
            _aiDrawCts = null;
        }
    }

    private void Report(Action<string>? progress, string text)
    {
        progress?.Invoke(text);
        AiDrawProgress?.Invoke(text);
    }

    /// <summary>
    /// 스타일 참고 그림(없으면 null). 인자로 id를 주면 그것, 설정이 "사용 안 함"이면 없음, "지정"이면 그 자료, "자동"이면 보관소에서 고른다:
    /// 사용자가 픽셀 아트가 아닌 그림을 대상으로 붙였으면 늘, 그림을 붙이지 않았으면 대상 설명과 글이 맞는 자료가 있을 때만, 픽셀 아트를 붙였으면 없음.
    /// </summary>
    private StyleChoice? StyleReference(AiDrawSettings options, string? explicitId, string subject, string kind, int size, bool restyle, bool subjectIsPixelArt)
    {
        var lib = Library();
        ReferenceItem? item;
        if (!string.IsNullOrEmpty(explicitId))
            item = lib.Find(explicitId) is { IsImage: true } found ? found : throw new ToolError(UiStrings.FormatRefs_Tool_NotFound(id: explicitId));
        else
        {
            switch (options.StyleReference)
            {
                case AiDrawStyleReference.None:
                    return null;
                case AiDrawStyleReference.Specific:
                    item = lib.Find(options.StyleReferenceId) is { IsImage: true } chosen ? chosen : null;
                    break;
                default:
                    if (subjectIsPixelArt) return null;
                    var scores = lib.Search(new ReferenceQuery { Text = subject, IncludeNotes = false, IncludeRules = false, Limit = 0 })
                        .ToDictionary(h => h.Item.Id, h => h.Score, StringComparer.Ordinal);
                    item = AiDrawStyle.Pick(lib.AllItems, scores, kind, size, requireText: !restyle);
                    break;
            }
        }
        if (item is null) return null;
        try
        {
            return new StyleChoice(item, lib.LoadFrames(item).Images[0]);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private sealed record StyleChoice(ReferenceItem Item, PixelImage Frame);

    /// <summary>정수 배 최근접으로 긴 변이 <paramref name="side"/>에 가깝게(넘지 않게, 적어도 1배) 키운다.</summary>
    private static PixelImage ScaleUpTo(PixelImage src, int side)
    {
        int k = Math.Max(1, side / Math.Max(src.Width, src.Height));
        if (k == 1) return src;
        var dst = new PixelImage(src.Width * k, src.Height * k);
        for (int y = 0; y < dst.Height; y++)
            for (int x = 0; x < dst.Width; x++)
                dst.Pixels[y * dst.Width + x] = src.Pixels[(y / k) * src.Width + x / k];
        return dst;
    }

    /// <summary>프레임을 가로로 나란히(하나면 그대로).</summary>
    private static PixelImage Strip(IReadOnlyList<PixelImage> frames)
    {
        if (frames.Count == 1) return frames[0];
        int w = frames[0].Width, h = frames[0].Height, gap = 1;
        var strip = new PixelImage(w * frames.Count + gap * (frames.Count - 1), h);
        for (int i = 0; i < frames.Count; i++) strip.Blit(frames[i], i * (w + gap), 0);
        return strip;
    }

    /// <summary>생성한 그림 파일(PNG — 못 읽으면 SkiaSharp). 못 읽으면 도구 오류.</summary>
    private static PixelImage LoadGenerated(string path)
    {
        try
        {
            return PngIO.Load(path);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException)
        {
            return ImageUtil.DecodeFrames(path) is { Images.Count: > 0 } f ? f.Images[0] : throw new AiDrawException(UiStrings.AiDraw_Error_Read);
        }
    }

    /// <summary>대상 설명 앞부분으로 만든 이름(레이어·문서).</summary>
    private static string ShortName(string subject)
    {
        var words = subject.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(4);
        var s = string.Join(' ', words).Trim(',', '.', ';');
        return s.Length > 32 ? s[..32] : s.Length == 0 ? "AI" : s;
    }

    private static List<string> OptStringList(JsonObject? a, string key)
    {
        if (Arg(a, key) is not { } n) return [];
        if (n is not JsonArray arr) throw new ToolError(string.Format(CultureInfo.CurrentCulture, UiStrings.S_PixelTools_ReqArray_C2A791FDD4, key));
        return [.. arr.Select(v => AsString(v)?.Trim() ?? "")];
    }
}
