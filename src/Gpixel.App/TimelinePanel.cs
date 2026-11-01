using Gpixel.Core.Model;
using Gpixel.Core.Scripting;
using Gpixel.App.Localization;

namespace Gpixel.App;

/// <summary>
/// Aseprite식 타임라인: 행 = 레이어(위가 맨 위 레이어), 열 = 프레임, 위쪽 띠 = 태그(애니메이션).
/// 셀 썸네일은 실시간으로 갱신된다. 도구 단추 없이 우클릭 메뉴(프레임 머리글·셀·레이어 이름·빈 곳), 마지막 프레임 뒤
/// "다음 프레임" 칸(⧉ 복사해서 추가 · ＋ 빈 프레임 추가)과 마지막 행 아래 "다음 레이어" 줄(이름 칸 전체 ＋ 빈 레이어 추가 · 프레임 칸 전체 ⧉ 마지막 레이어 복사해서 추가)로
/// 편집한다. 셀·프레임 복사는 고른 뒤 Ctrl/⌘+C·V.
/// 선택은 한 번에 한 종류다: 프레임(머리글), 레이어(이름 칸), 효과(효과 줄 이름·상자), 셀 묶음(행 × 프레임). 고르지 않은 칸을 누른 채 끌면 사각형으로 여러 개를 고르고,
/// Delete(⌫)는 고른 종류대로 프레임 삭제 · 레이어 삭제 · 효과 삭제 · 셀 그림 지우기다. 효과 이름을 끌면 효과 순서를 바꾸거나 다른 레이어로 옮긴다.
/// 우클릭 "태그 만들기"로 태그(애니메이션)로 묶는다. 재생은 미리보기에서(태그 안에서 반복).
/// <para>
/// 그룹 레이어 행은 ▾/▸로 접고 펼치며, 그 아래에 부위 행이 들여 써서 보인다(이름 + 부위 크기). 그룹 행의 셀은 그룹 합성 썸네일이다.
/// 고른 머리글·이름을 끌면 순서가 바뀐다: 일반 레이어·그룹은 맨 위 단계끼리(그룹은 부위와 함께), 부위는 같은 그룹 안에서만.
/// </para>
/// <para>
/// 그룹은 스킨 슬롯이다: 그룹 행 이름 칸 오른쪽 칩에 지금 스킨 이름이, 부위 행 칩에는 그 부위가 보이는 스킨 이름(부위만 따로 고르면 강조)이
/// 보이고, 칩을 누르면 스킨 관리 창(<see cref="SkinManagerWindow"/>)이 그 줄로 열린다. 우클릭 메뉴 "스킨"에서 추가·복제·이름·삭제·순서를,
/// 부위는 "이 부위 스킨"에서 부위만 따로 입힐 스킨을 고른다. 지금 스킨에 자기 그림이 없어 기본 스킨 셀을 빌려 보이는 부위 셀은 흐리게 그린다.
/// </para>
/// </summary>
public sealed class TimelinePanel : UserControl
{
    /// <summary>움직임 테스트와 연결되는 태그 이름 제안.</summary>
    public static readonly string[] SuggestedNames =
    [
        "idle_down", "idle_up", "idle_left", "idle_right",
        "walk_down", "walk_up", "walk_left", "walk_right",
        "idle", "run", "jump", "fall",
    ];

    private readonly EditorContext _ctx;
    private readonly PreviewPanel _preview;
    private readonly TimelineGrid _grid;
    private readonly TextBlock _info;

    public TimelinePanel(EditorContext ctx, PreviewPanel preview)
    {
        _ctx = ctx;
        _preview = preview;
        Background = AppTheme.PanelBrush;

        _info = Ui.Label("");
        _info.Foreground = AppTheme.DimTextBrush;

        _grid = new TimelineGrid(this);
        var root = new DockPanel { LastChildFill = true };
        // 제목 줄 오른쪽에 현재 프레임·레이어 정보(도구 줄을 한 줄 아껴 레이어 행이 더 보이게).
        _info.Margin = new Thickness(8, 0, 6, 0);
        _info.TextTrimming = TextTrimming.CharacterEllipsis;
        _info.HorizontalAlignment = HorizontalAlignment.Right;
        var headerRow = new DockPanel();
        var title = new TextBlock { Text = UiStrings.S_TimelinePanel_TimelinePanel_7AE6DC18E2, FontWeight = FontWeight.Bold, Foreground = AppTheme.TextBrush, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(title, Dock.Left);
        headerRow.Children.Add(title);
        // 스킨 관리 창은 이름 칸의 스킨 칩(클릭)·레이어 우클릭·프레임·레이어 메뉴에서 연다.
        headerRow.Children.Add(_info);
        var header = new Border { Height = 24, Background = AppTheme.BackBrush, Padding = new Thickness(6, 0, 0, 0), Child = headerRow };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(_grid);
        Content = root;

        // 다른 파일을 열면 바로 새 배치로, 같은 프로젝트 안의 변경(실행취소 포함)은 행·열이 자라고 줄어드는 애니메이션으로.
        ctx.ProjectReplaced += (_, _) => { _grid.EndRename(false); _grid.ResetRange(); _grid.SyncLayout(animate: false); RefreshUi(); };
        ctx.ProjectChanged += (_, _) => { _grid.ClampRange(); _grid.SyncLayout(animate: true); RefreshUi(); };
        ctx.CurrentChanged += (_, _) =>
        {
            ExpandToCurrent();
            _grid.ClampRange();
            RefreshUi();
            _grid.EnsureCurrentVisible();
        };
        ctx.ViewChanged += (_, _) => _grid.InvalidateVisual();
        ctx.RenderUpdated += (_, _) => _grid.InvalidateVisual();
        preview.PlayingFrameChanged += (_, f) => _grid.PlayingFrame = f;
        _grid.SyncLayout(animate: false);
        RefreshUi();
    }

    private void RefreshUi()
    {
        IsEnabled = _ctx.HasProject;
        if (_ctx.Project is { } p)
        {
            var layer = p.Layers[_ctx.CurrentLayer];
            var tag = _ctx.ActiveTag;
            int total = p.Frames.Sum(f => f.DurationMs);
            string skin = SkinManagerWindow.SkinStatus(p, layer, _ctx.CurrentFrame);
            string kind = layer.Kind switch
            {
                LayerKind.Group => UiStrings.S_TimelinePanel_RefreshUi_974D2C80BE,
                LayerKind.Part => string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_RefreshUi_FB257005C1,layer.PartSize.Width,layer.PartSize.Height,layer.PartOffset.X,layer.PartOffset.Y),
                _ => "",
            };
            _info.Text = string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_RefreshUi_4411E2A453,_ctx.CurrentFrame + 1,p.Frames.Count,layer.Name) + (kind.Length > 0 ? $" ({kind})" : "")
                         + (skin.Length > 0 ? " · " + skin : "")
                         + (tag is null ? "" : string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_RefreshUi_32131C1BFE,tag.Name)) + string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_RefreshUi_52FFAF00E4,total);
        }
        else _info.Text = "";
        _grid.UpdateScrollSize();
        _grid.InvalidateVisual();
    }

    // ───────────── 동작 ─────────────

    private SpriteProject? P => _ctx.Project;

    /// <summary>타임라인 선택 종류(한 번에 하나): 셀 묶음(기본 = 현재 칸) · 프레임(머리글) · 레이어(이름 칸) · 효과(효과 줄 이름·상자).</summary>
    private enum SelKind { Cels, Frames, Layers, Effects }

    /// <summary>고른 프레임(오름차순): 프레임 선택이면 그 프레임들, 셀 묶음이면 그 프레임 범위, 아니면 현재 프레임.</summary>
    private List<int> SelectedFrames()
    {
        if (_grid.Kind == SelKind.Frames) return _grid.SelectedFrameList();
        var (a, b) = _grid.Range;
        return _grid.Kind == SelKind.Cels && a >= 0 ? Enumerable.Range(a, b - a + 1).ToList() : [_ctx.CurrentFrame];
    }

    /// <summary>셀 동작(지우기·복사)의 대상 행(레이어 번호, 위→아래) × 프레임: 레이어 선택이면 그 레이어들의 모든 프레임, 아니면 셀 묶음.</summary>
    private (List<int> Rows, List<int> Frames) CelBlock()
        => _grid.Kind == SelKind.Layers
            ? (_grid.SelectedLayerList(), Enumerable.Range(0, _ctx.FrameCount).ToList())
            : (_grid.SelectedRowLayers(), SelectedFrames());

    /// <summary>
    /// 변형(Ctrl+T)·이동 도구가 함께 바꿀 셀들(레이어 번호, 프레임 번호): 셀 묶음이면 그 칸들, 프레임 선택이면 현재 레이어의 그 프레임들,
    /// 레이어 선택이면 그 레이어들의 모든 프레임(그룹 행은 그 부위들). 효과 선택이면 현재 칸 하나.
    /// </summary>
    public List<(int Layer, int Frame)> SelectedCelTargets()
    {
        if (P is not { } p) return [];
        if (_grid.Kind == SelKind.Effects) return [(_ctx.CurrentLayer, _ctx.CurrentFrame)];
        var (rows, frames) = CelBlock();
        return [.. CelLayers(p, rows).SelectMany(l => frames.Select(f => (l, f)))];
    }

    /// <summary>Delete 키 이름(macOS = ⌫).</summary>
    private static string DeleteKeyName => Platform.IsMac ? "⌫" : "Delete";

    /// <summary>프레임 번호(오름차순)를 읽는 글로: "프레임 2", "프레임 1~3", "프레임 1, 3, 5~7".</summary>
    private static string FramesText(IReadOnlyList<int> frames)
    {
        var parts = new List<string>();
        for (int i = 0; i < frames.Count;)
        {
            int j = i;
            while (j + 1 < frames.Count && frames[j + 1] == frames[j] + 1) j++;
            parts.Add(i == j ? $"{frames[i] + 1}" : $"{frames[i] + 1}~{frames[j] + 1}");
            i = j + 1;
        }
        return UiStrings.S_TimelinePanel_FramesText_48424FAB15 + (parts.Count > 4 ? string.Join(", ", parts.Take(4)) + ", …" : string.Join(", ", parts));
    }

    /// <summary>지금 선택을 한 줄로(우클릭 메뉴 맨 위·상태 표시줄): "프레임 1~3 선택됨" 등.</summary>
    private string SelectionLabel()
    {
        if (P is not { } p) return "";
        switch (_grid.Kind)
        {
            case SelKind.Frames:
                return string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_SelectionLabel_075B714127,FramesText(SelectedFrames()));
            case SelKind.Layers:
                var layers = _grid.SelectedLayerList();
                return layers.Count == 1 ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_SelectionLabel_AA461A1A65,p.Layers[layers[0]].Name) : string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_SelectionLabel_3173DC9CF6,layers.Count);
            case SelKind.Effects:
                return string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_SelectionLabel_1E6AFDB6EB,_grid.SelectedEffectList().Count);
            default:
                var (rows, frames) = CelBlock();
                return rows.Count * frames.Count == 1
                    ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_SelectionLabel_859E808144,p.Layers[rows[0]].Name,frames[0] + 1)
                    : string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_SelectionLabel_23DBB6720C,rows.Count * frames.Count,rows.Count,FramesText(frames));
        }
    }

    /// <summary>이전/다음 프레임으로, 끝에서는 반대쪽으로 돈다.</summary>
    public void StepFrame(int delta)
    {
        if (P is null) return;
        _preview.Stop();
        int n = _ctx.FrameCount;
        if (n == 0) return;
        _ctx.SetCurrentFrame(((_ctx.CurrentFrame + delta) % n + n) % n);
    }

    public void NewEmptyFrame()
    {
        if (P is not { } p) return;
        int at = _ctx.CurrentFrame + 1;
        _ctx.Structural(() => p.AddFrame(at));
        _ctx.SetCurrentFrame(at);
    }

    public void DuplicateFrame()
    {
        if (P is not { } p) return;
        int at = _ctx.CurrentFrame;
        _ctx.Structural(() => p.DuplicateFrame(at));
        _ctx.SetCurrentFrame(at + 1);
    }

    /// <summary>
    /// 고른 프레임(머리글로 골랐으면 그 프레임들, 아니면 현재 프레임)을 지운다(실행취소 한 단계).
    /// 모든 프레임을 지울 수는 없다(상태 표시줄에 알림).
    /// </summary>
    public void DeleteFrame()
    {
        if (P is not { } p) return;
        var frames = _grid.Kind == SelKind.Frames ? _grid.SelectedFrameList() : [_ctx.CurrentFrame];
        if (frames.Count >= p.Frames.Count)
        {
            Say(p.Frames.Count <= 1 ? UiStrings.S_TimelinePanel_DeleteFrame_AD774265A7 : UiStrings.S_TimelinePanel_DeleteFrame_C2EAA70A6A);
            return;
        }
        _ctx.Structural(() => p.Edit(UiStrings.S_TimelinePanel_DeleteFrame_E1F5D9F6F9, ProjectChange.Frames | ProjectChange.Tags | ProjectChange.Pixels, () =>
        {
            foreach (var f in frames.OrderByDescending(f => f)) p.DeleteFrame(f);
        }));
        _grid.ResetRange();
        _ctx.SetCurrentFrame(Math.Min(frames.Min(), p.Frames.Count - 1));
        Say(Ui.Shortcut(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_DeleteFrame_543084E51E,frames.Count)));
    }

    /// <summary>
    /// 고른 레이어(이름 칸으로 고름, 아니면 현재 레이어)를 지운다(실행취소 한 단계). 그룹은 부위까지 지우고, 그룹과 그 부위를 함께 골랐으면
    /// 부위는 따로 세지 않는다. 레이어가 하나도 남지 않게는 지우지 않는다(상태 표시줄에 알림).
    /// </summary>
    public void DeleteSelectedLayers()
    {
        if (P is not { } p) return;
        var chosen = (_grid.Kind == SelKind.Layers ? _grid.SelectedLayerList() : [_ctx.CurrentLayer]).Select(i => p.Layers[i]).ToList();
        var chosenSet = new HashSet<Layer>(chosen, ReferenceEqualityComparer.Instance);
        var targets = chosen.Where(l => l.Parent is not { } g || !chosenSet.Contains(g)).ToList();
        if (targets.Count == 0) return;
        var removed = new HashSet<Layer>(ReferenceEqualityComparer.Instance);
        foreach (var l in targets)
        {
            removed.Add(l);
            if (l.IsGroup) removed.UnionWith(p.ChildrenOf(l));
        }
        if (removed.Count >= p.Layers.Count)
        {
            Say(UiStrings.S_TimelinePanel_DeleteSelectedLayers_2BE5077D81);
            return;
        }
        // 지운 뒤 고를 레이어: 지운 것 중 맨 위 자리에서 아래로(없으면 위로) 가장 가까운 남는 레이어.
        int top = targets.Max(p.IndexOf);
        var keep = Enumerable.Range(0, top + 1).Reverse().Concat(Enumerable.Range(top + 1, p.Layers.Count - top - 1))
            .Select(i => p.Layers[i]).First(l => !removed.Contains(l));
        _ctx.Structural(() => p.Edit(UiStrings.S_TimelinePanel_DeleteSelectedLayers_26BC9EF176, ProjectChange.Layers | ProjectChange.Pixels, () =>
        {
            foreach (var l in targets) p.DeleteLayer(p.IndexOf(l));
        }));
        _grid.ResetRange();
        _ctx.SetCurrentLayer(p.IndexOf(keep));
        Say(Ui.Shortcut(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_DeleteSelectedLayers_E133662A1B,targets.Count)));
    }

    /// <summary>Delete(⌫): 고른 종류대로 — 프레임 삭제 · 레이어 삭제 · 효과 삭제 · 셀 지우기.</summary>
    private void DeleteSelection()
    {
        switch (_grid.Kind)
        {
            case SelKind.Frames: DeleteFrame(); break;
            case SelKind.Layers: DeleteSelectedLayers(); break;
            case SelKind.Effects: DeleteSelectedEffects(); break;
            default: ClearSelectedCels(); break;
        }
    }

    /// <summary>타임라인에서 한 일·할 일을 상태 표시줄에 알린다.</summary>
    public event EventHandler<string>? Message;

    private void Say(string text) => Message?.Invoke(this, text);

    /// <summary>머리글·이름 칸·셀 묶음을 고른 뒤: 무엇을 골랐고 Delete가 무엇을 할지 알린다(한 칸만 고른 셀 선택은 조용히).</summary>
    private void HintSelection()
    {
        string label = SelectionLabel();
        switch (_grid.Kind)
        {
            case SelKind.Frames:
                Say(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_HintSelection_C0C0B9A3F0,label,DeleteKeyName));
                break;
            case SelKind.Layers:
                Say(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_HintSelection_11CAA114DF,label,DeleteKeyName));
                break;
            case SelKind.Effects:
                Say(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_HintSelection_AA6C34F7A0,label,DeleteKeyName));
                break;
            default:
                var (rows, frames) = CelBlock();
                if (rows.Count * frames.Count > 1) Say(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_HintSelection_C2A7554EE6,label,DeleteKeyName));
                break;
        }
    }

    public void MoveFrame(int delta) => MoveFrameTo(_ctx.CurrentFrame, _ctx.CurrentFrame + delta);

    /// <summary>프레임 순서 바꾸기.</summary>
    private void MoveFrameTo(int from, int to) => MoveFramesTo(from, from, to);

    /// <summary>
    /// 이어진 프레임 [<paramref name="a"/>, <paramref name="b"/>]를 옮긴 뒤 첫 프레임이 <paramref name="to"/>가 되게 옮긴다(실행취소 한 단계).
    /// 옮긴 프레임들을 열 선택으로 남기고 현재 프레임은 덩어리 안 같은 자리.
    /// </summary>
    private void MoveFramesTo(int a, int b, int to)
    {
        if (P is not { } p) return;
        int count = b - a + 1;
        to = Math.Clamp(to, 0, p.Frames.Count - count);
        if (to == a) return;
        int current = to + Math.Clamp(_ctx.CurrentFrame - a, 0, count - 1);
        _ctx.Structural(() => p.Edit(UiStrings.S_TimelinePanel_MoveFramesTo_757096FDA0, ProjectChange.Frames, () =>
        {
            for (int k = 0; k < count; k++)
            {
                if (to < a) p.MoveFrame(a + k, to + k);
                else p.MoveFrame(a, to + count - 1);
            }
        }));
        _ctx.SetCurrentFrame(current);
        if (_grid.Kind == SelKind.Frames) _grid.SelectFrameRange(to, to + count - 1);
    }

    /// <summary>마지막 프레임 뒤에 프레임을 하나 붙인다(<paramref name="copy"/>면 마지막 프레임을 그림까지 복사). 그 프레임의 <paramref name="layer"/>를 고른다.</summary>
    private void AppendFrame(bool copy, int layer)
    {
        if (P is not { } p) return;
        int last = p.Frames.Count - 1;
        if (copy) _ctx.Structural(() => p.DuplicateFrame(last));
        else _ctx.Structural(() => p.AddFrame(last + 1));
        _grid.ResetRange();
        _ctx.SetCurrent(Math.Clamp(layer, 0, p.Layers.Count - 1), last + 1);
    }

    /// <summary>
    /// 맨 아래 레이어 밑에 레이어를 하나 붙인다(실행취소 1단계): 빈 일반 레이어, 또는 <paramref name="copy"/>면 맨 아래 최상위 레이어의 복사본
    /// (그룹은 부위까지, 이름은 끝 숫자 + 1). 새 레이어가 현재 레이어가 된다.
    /// </summary>
    private void AppendLayer(bool copy)
    {
        if (P is not { } p) return;
        Layer? added = null;
        if (copy)
        {
            int bottom = p.Layers[0].Parent is { } group ? p.IndexOf(group) : 0;
            _ctx.Structural(() => added = p.Edit(UiStrings.Timeline_AppendLayerCopyTip, ProjectChange.Layers | ProjectChange.Pixels, () =>
            {
                var dup = p.DuplicateLayer(bottom);
                p.MoveLayer(p.IndexOf(dup), 0);
                return dup;
            }));
        }
        else _ctx.Structural(() => added = p.AddLayer(0));
        if (added is null) return;
        _grid.ResetRange();
        _ctx.SetCurrentLayer(p.IndexOf(added));
    }

    /// <summary>앞쪽에서 가장 가까운, 그림이 있는 같은 레이어의 프레임(없으면 -1).</summary>
    private static int PreviousCelFrame(SpriteProject p, int layer, int frame)
    {
        if (p.Layers[layer].IsGroup) return -1;
        for (int f = frame - 1; f >= 0; f--)
            if (!p.IsCelEmpty(layer, f)) return f;
        return -1;
    }

    /// <summary>빈 셀에 앞 프레임의 같은 레이어 그림을 복사한다(실행취소 1단계).</summary>
    private void CopyCelFromPrevious(int layer, int frame)
    {
        if (P is not { } p) return;
        int from = PreviousCelFrame(p, layer, frame);
        if (from < 0 || p.GetCel(layer, from) is not { } cel) return;
        _ctx.Structural(() => p.SetCel(layer, frame, cel.Clone(), UiStrings.S_TimelinePanel_CopyCelFromPrevious_8B15F7626C));
        _ctx.SetCurrent(layer, frame);
    }

    // ───────────── 셀 묶음(행 × 프레임) 옮기기·복사·붙여넣기 ─────────────

    /// <summary>복사한 셀 한 행: 일반·부위 레이어는 프레임별 셀, 그룹은 부위마다(위→아래) 프레임별 셀.</summary>
    private sealed record ClipRow(PixelImage?[] Cels, List<PixelImage?[]>? Children);

    /// <summary>
    /// 복사한 셀/프레임. <see cref="WholeFrames"/>면 프레임 전체(모든 레이어 + 길이, <see cref="Frames"/>),
    /// 아니면 고른 행 × 프레임 묶음(<see cref="Rows"/>, 붙일 때 클릭한 셀이 왼쪽 위).
    /// 레이어 선택을 복사했으면 <see cref="SourceLayers"/> = 그 레이어들(붙이면 같은 프로젝트에 남아 있는 한 새 레이어로 복제).
    /// </summary>
    private sealed record CelClip(bool WholeFrames, int FrameCount, List<Dictionary<Layer, PixelImage>> Frames, List<int> Durations, List<ClipRow> Rows, List<Layer>? SourceLayers = null);

    private static CelClip? _clip;

    /// <summary>복사한 효과(효과 줄로 골라 Ctrl/⌘+C). 셀·프레임 복사와 같은 클립보드라 마지막에 복사한 것 하나만 남는다.</summary>
    private static List<LayerEffect>? _fxClip;

    private static List<Layer> ChildrenTopDown(SpriteProject p, Layer group) => p.ChildrenOf(group).Reverse().ToList();

    /// <summary>행(레이어 번호, 위→아래) × 프레임의 셀을 복제해 담는다.</summary>
    private static List<ClipRow> CaptureCells(SpriteProject p, IReadOnlyList<int> rowLayers, IReadOnlyList<int> frames)
        => rowLayers.Select(l => p.Layers[l] is { IsGroup: true } g
            ? new ClipRow([], ChildrenTopDown(p, g).Select(c => frames.Select(f => p.GetCel(c, p.Frames[f])?.Clone()).ToArray()).ToList())
            : new ClipRow(frames.Select(f => p.GetCel(l, f)?.Clone()).ToArray(), null)).ToList();

    /// <summary>
    /// 행(레이어 번호)을 셀이 있는 레이어로 펼친다(겹치면 한 번). 그룹 행은 그 부위들 — 단, 그 그룹의 부위 행이 하나라도 함께 골라져 있으면
    /// 고른 부위 행만 쓴다(그룹 행 + 위 부위 몇 개를 고른 묶음이 고르지 않은 아래 부위까지 지우지 않게).
    /// </summary>
    private static IEnumerable<int> CelLayers(SpriteProject p, IReadOnlyList<int> rowLayers)
    {
        var picked = rowLayers.ToHashSet();
        return rowLayers.SelectMany(l => p.Layers[l] is { IsGroup: true } g
            ? p.ChildrenOf(g).Any(c => picked.Contains(p.IndexOf(c))) ? [] : ChildrenTopDown(p, g).Select(p.IndexOf)
            : [l]).Distinct();
    }

    /// <summary>행 × 프레임의 셀을 비운다(그룹 행은 그 부위들). <see cref="SpriteProject.Edit"/> 안에서 부른다.</summary>
    private static void ClearCells(SpriteProject p, IReadOnlyList<int> rowLayers, IReadOnlyList<int> frames)
    {
        foreach (var l in CelLayers(p, rowLayers))
            foreach (var f in frames)
                if (!p.IsCelEmpty(l, f)) p.SetCel(l, f, null);
    }

    /// <summary>행 × 프레임에 지울 그림(이 스킨 자신의 셀 — 기본 스킨에서 빌려 보이는 셀 말고)이 있는지.</summary>
    private static bool HasOwnCels(SpriteProject p, IReadOnlyList<int> rowLayers, IReadOnlyList<int> frames)
        => CelLayers(p, rowLayers).Any(l => frames.Any(f => !p.IsCelEmpty(l, f) && !p.IsCelInherited(l, f)));

    /// <summary>
    /// 담아 둔 셀 묶음을 보이는 행 <paramref name="topRow"/>·프레임 <paramref name="start"/>부터 쓴다(빈 셀은 비움, 모자라는 프레임은 붙임).
    /// 셀 크기가 다르거나 그룹/일반이 맞지 않는 칸은 건너뛴다. 쓴 칸 수를 돌려준다. <see cref="SpriteProject.Edit"/> 안에서 부른다.
    /// </summary>
    private static int WriteCells(SpriteProject p, IReadOnlyList<ClipRow> rows, int frameCount, IReadOnlyList<int> visibleRows, int topRow, int start)
    {
        while (p.Frames.Count < start + frameCount) p.AddFrame(p.Frames.Count);
        int written = 0;
        void Put(Layer target, PixelImage?[] cels)
        {
            int index = p.IndexOf(target);
            for (int k = 0; k < cels.Length; k++)
            {
                var image = cels[k];
                if (image is not null && image.Size != p.CelSize(target)) continue;
                if (image is null && p.IsCelEmpty(index, start + k)) { written++; continue; }
                p.SetCel(index, start + k, image?.Clone());
                written++;
            }
        }
        for (int i = 0; i < rows.Count; i++)
        {
            int ti = topRow + i;
            if (ti < 0 || ti >= visibleRows.Count) continue;
            var target = p.Layers[visibleRows[ti]];
            var row = rows[i];
            if (row.Children is null)
            {
                if (!target.IsGroup) Put(target, row.Cels);
            }
            else if (target.IsGroup)
            {
                var children = ChildrenTopDown(p, target);
                for (int c = 0; c < Math.Min(children.Count, row.Children.Count); c++) Put(children[c], row.Children[c]);
            }
        }
        return written;
    }

    /// <summary>
    /// 셀 묶음(행 × 연속 프레임)을 보이는 행 <paramref name="toRow"/>·프레임 <paramref name="toFrame"/>로 옮기거나 복사한다(실행취소 1단계).
    /// 놓은 자리가 새 선택이 된다.
    /// </summary>
    private void MoveBlock(IReadOnlyList<int> rowLayers, int fromFrame, int frameCount, int toRow, int toFrame, bool copy)
    {
        if (P is not { } p) return;
        var visible = _grid.VisibleRows();
        var frames = Enumerable.Range(fromFrame, frameCount).ToList();
        // 행마다 놓을 자리와 맞아야 한다(일반·부위 ↔ 같은 크기, 그룹 ↔ 그룹). 하나라도 안 맞으면 옮기지 않는다(그림이 사라지지 않게).
        for (int i = 0; i < rowLayers.Count; i++)
        {
            int ti = toRow + i;
            var src = p.Layers[rowLayers[i]];
            if (ti >= visible.Count || !RowsCompatible(p, src, p.Layers[visible[ti]]))
            {
                Ui.Info(this, UiStrings.S_TimelinePanel_MoveBlock_1527D93EE0);
                return;
            }
        }
        var data = CaptureCells(p, rowLayers, frames);
        // 놓을 자리의 레이어(편집 뒤에도 같은 객체)
        var topLayer = p.Layers[visible[toRow]];
        var endLayer = p.Layers[visible[Math.Min(visible.Count - 1, toRow + rowLayers.Count - 1)]];
        _ctx.Structural(() => p.Edit(copy ? UiStrings.S_TimelinePanel_MoveBlock_5B9FB74E7C : UiStrings.S_TimelinePanel_MoveBlock_0DFDF5525C, ProjectChange.Pixels | ProjectChange.Frames, () =>
        {
            if (!copy) ClearCells(p, rowLayers, frames);
            WriteCells(p, data, frameCount, visible, toRow, toFrame);
        }));
        _grid.SelectRect(p.IndexOf(topLayer), toFrame, endLayer, toFrame + frameCount - 1);
    }

    /// <summary>셀 행을 다른 행에 놓을 수 있는지: 일반·부위는 셀 크기가 같은 일반·부위, 그룹은 부위 수·크기가 같은 그룹.</summary>
    private static bool RowsCompatible(SpriteProject p, Layer source, Layer target)
    {
        if (!source.IsGroup) return !target.IsGroup && p.CelSize(source) == p.CelSize(target);
        if (!target.IsGroup) return false;
        if (ReferenceEquals(source, target)) return true;
        var a = ChildrenTopDown(p, source);
        var b = ChildrenTopDown(p, target);
        return a.Count == b.Count && a.Zip(b).All(x => x.First.PartSize == x.Second.PartSize);
    }

    /// <summary>
    /// 고른 셀(셀 묶음, 레이어 선택이면 그 레이어들의 모든 프레임 — 그룹 행은 그 부위들)의 그림을 지운다(실행취소 한 단계).
    /// 프레임·레이어는 그대로 둔다.
    /// </summary>
    private void ClearSelectedCels()
    {
        if (P is not { } p) return;
        var (rows, frames) = CelBlock();
        if (!HasOwnCels(p, rows, frames))
        {
            Say(UiStrings.S_TimelinePanel_ClearSelectedCels_D1426A0A3D);
            return;
        }
        _ctx.Structural(() => p.Edit(UiStrings.S_TimelinePanel_ClearSelectedCels_1EE69E248F, ProjectChange.Pixels, () => ClearCells(p, rows, frames)));
        Say(Ui.Shortcut(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_ClearSelectedCels_C789A97AE4,rows.Count * frames.Count)));
    }

    // ───────────── 셀·프레임 복사·붙여넣기(타임라인에 포커스가 있을 때 Ctrl/⌘+C·X·V) ─────────────

    /// <summary>타임라인 격자에 키보드 포커스가 있는지(복사·붙여넣기를 셀/프레임 대상으로).</summary>
    public bool IsGridFocused => _grid.IsKeyboardFocusWithin;

    /// <summary>
    /// 고른 것을 복사한다: 프레임 선택 = 프레임 전체(모든 레이어 + 길이), 레이어 선택 = 그 레이어들의 모든 프레임 셀, 효과 선택 = 그 효과들,
    /// 셀 묶음 = 그 셀들. <paramref name="cut"/>이면 프레임·효과는 지우고 셀은 비운다.
    /// </summary>
    public void CopySelection(bool cut)
    {
        if (P is not { } p) return;
        if (_grid.Kind == SelKind.Effects)
        {
            _fxClip = _grid.SelectedEffectList().Select(x => x.Layer.Effects[x.Index]).ToList();
            _clip = null;
            if (cut) DeleteSelectedEffects();
            return;
        }
        _fxClip = null;
        if (_grid.Kind == SelKind.Frames)
        {
            var selected = SelectedFrames();
            var layers = Enumerable.Range(0, p.Layers.Count).Where(i => !p.Layers[i].IsGroup).ToList();
            var data = selected.Select(f => layers.Where(l => !p.IsCelEmpty(l, f)).ToDictionary(l => p.Layers[l], l => p.GetCel(l, f)!.Clone())).ToList();
            _clip = new CelClip(true, selected.Count, data, selected.Select(f => p.Frames[f].DurationMs).ToList(), []);
            if (cut) DeleteFrame();
            return;
        }
        var (rows, frames) = CelBlock();
        List<Layer>? sources = null;
        if (_grid.Kind == SelKind.Layers)
        {
            // 레이어 통째 복사: 그룹을 골랐으면 그 부위는 그룹과 함께 복제되므로 뺀다.
            sources = _grid.SelectedLayerList().Select(i => p.Layers[i]).ToList();
            sources.RemoveAll(l => l.Parent is { } g && sources.Contains(g));
        }
        _clip = new CelClip(false, frames.Count, [], frames.Select(f => p.Frames[f].DurationMs).ToList(), CaptureCells(p, rows, frames), sources);
        if (cut) ClearSelectedCels();
    }

    /// <summary>
    /// 붙여넣기: 효과 복사였으면 현재 레이어 효과 목록 끝에 붙이고(실행취소 한 단계, 붙인 효과가 골라짐), 프레임 복사였으면 현재 프레임(범위 끝) 뒤에
    /// 그 프레임들을 끼워 넣고, 셀 복사였으면 클릭한 셀을 왼쪽 위로 해서 같은 모양(행 × 프레임)으로 덮어쓴다(셀 크기가 다른 칸은 건너뜀). 모자라는 프레임은 붙인다.
    /// </summary>
    /// <returns>타임라인에 복사해 둔 셀/프레임/효과가 있어 붙여 넣었으면(또는 붙일 수 없다고 알렸으면) true, 없으면 false(그림 붙여넣기로 넘김).</returns>
    public bool PasteSelection()
    {
        if (P is { } fp && _fxClip is { Count: > 0 } effects)
        {
            var layer = fp.Layers[_ctx.CurrentLayer];
            int first = layer.Effects.Count;
            var pasted = effects.Select((effect, index) => index == 0 && effect.ClipToPrevious
                ? effect with { ClipToPrevious = false } : effect).ToArray();
            ChangeEffects(layer, list => list.AddRange(pasted));
            _grid.SelectEffects(Enumerable.Range(first, pasted.Length).Select(i => (layer, i)));
            Say(Ui.Shortcut(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_PasteSelection_C3B9B97345,effects.Count,layer.Name)));
            return true;
        }
        if (P is not { } p || _clip is not { } clip || clip.FrameCount == 0) return false;
        if (clip.SourceLayers is { Count: > 0 } sourceLayers && sourceLayers.All(l => p.IndexOf(l) >= 0))
        {
            // 레이어 통째 붙여넣기: 새 레이어로(원래 레이어 바로 위, 그룹은 부위까지, 이름 끝 숫자 +1)
            var copies = new List<Layer>();
            _ctx.Structural(() => p.Edit(UiStrings.S_TimelinePanel_PasteSelection_9BF1D96331, ProjectChange.Layers | ProjectChange.Pixels, () =>
            {
                foreach (var l in sourceLayers) copies.Add(p.DuplicateLayer(p.IndexOf(l)));
            }));
            _ctx.SetCurrentLayer(p.IndexOf(copies[0]));
            _grid.SelectLayerSet(copies);
            Say(Ui.Shortcut(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_PasteSelection_7024507CB1,copies.Count,string.Join(", ", copies.Select(c => c.Name)))));
            return true;
        }
        if (clip.WholeFrames)
        {
            int at = SelectedFrames().Max() + 1;
            _ctx.Structural(() => p.Edit(UiStrings.S_TimelinePanel_PasteSelection_BEEE4A4E80, ProjectChange.Frames | ProjectChange.Pixels | ProjectChange.Tags, () =>
            {
                for (int i = 0; i < clip.Frames.Count; i++)
                {
                    p.AddFrame(at + i);
                    p.SetFrameDuration(at + i, clip.Durations[i]);
                    foreach (var (layer, image) in clip.Frames[i])
                    {
                        int l = p.IndexOf(layer);
                        if (l >= 0 && p.CelSize(layer) == image.Size) p.SetCel(l, at + i, image.Clone());
                    }
                }
            }));
            _grid.SelectFrameRange(at, at + clip.Frames.Count - 1);
            _ctx.SetCurrentFrame(at);
            return true;
        }
        var visible = _grid.VisibleRows();
        int top = visible.IndexOf(_ctx.CurrentLayer), start = _ctx.CurrentFrame;
        if (top < 0) return true;
        var topLayer = p.Layers[visible[top]];
        var endLayer = p.Layers[visible[Math.Min(visible.Count - 1, top + clip.Rows.Count - 1)]];
        int written = 0;
        _ctx.Structural(() => p.Edit(UiStrings.S_TimelinePanel_PasteSelection_BABB32C8AD, ProjectChange.Pixels | ProjectChange.Frames, () =>
            written = WriteCells(p, clip.Rows, clip.FrameCount, visible, top, start)));
        if (written == 0)
        {
            Ui.Info(this, UiStrings.S_TimelinePanel_PasteSelection_1A8BE9258F);
            return true;
        }
        _grid.SelectRect(p.IndexOf(topLayer), start, endLayer, start + clip.FrameCount - 1);
        return true;
    }

    /// <summary>고른 프레임(범위)의 길이를 바꾼다.</summary>
    private void SetDuration()
    {
        if (P is not { } p) return;
        var frames = SelectedFrames();
        var text = InputDialog.Ask(this, UiStrings.S_TimelinePanel_SetDuration_6EBBF2FC7D, frames.Count > 1 ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_SetDuration_F54842A5D1,frames.Count) : UiStrings.S_TimelinePanel_SetDuration_9A16EE4A6C,
            p.Frames[frames[0]].DurationMs.ToString(), ["50", "80", "100", "120", "150", "200"]);
        if (text is null) return;
        if (!int.TryParse(text, out var ms) || ms < 1 || ms > 60000)
        {
            Ui.Error(this, UiStrings.S_TimelinePanel_SetDuration_8569508DA9);
            return;
        }
        _ctx.Structural(() => p.Edit(UiStrings.S_TimelinePanel_SetDuration_6EBBF2FC7D, ProjectChange.Frames, () =>
        {
            foreach (var f in frames) p.SetFrameDuration(f, ms);
        }));
    }

    // ───────────── 레이어 ─────────────

    /// <summary>현재 레이어가 든 맨 위 단계 덩어리(부위면 그 그룹) 바로 위 자리.</summary>
    private int SlotAboveCurrent(SpriteProject p)
    {
        var current = p.Layers[_ctx.CurrentLayer];
        return p.IndexOf(current.Parent ?? current) + 1;
    }

    /// <summary>현재 레이어(그룹 안이면 그 그룹) 위에 새 일반 레이어.</summary>
    public void AddLayer()
    {
        if (P is not { } p) return;
        int at = SlotAboveCurrent(p);
        Layer? added = null;
        _ctx.Structural(() => added = p.AddLayer(at));
        if (added is not null) _ctx.SetCurrentLayer(p.IndexOf(added));
    }

    /// <summary>현재 레이어(그룹 안이면 그 그룹) 위에 새 그룹.</summary>
    public void AddGroup()
    {
        if (P is not { } p) return;
        int at = SlotAboveCurrent(p);
        Layer? added = null;
        _ctx.Structural(() => added = p.AddGroup(null, at));
        if (added is not null) _ctx.SetCurrentLayer(p.IndexOf(added));
    }

    /// <summary>현재 그룹(부위를 골랐으면 그 그룹)에 새 부위(이름·크기 입력). 그룹이 없으면 그룹도 함께 만든다(실행취소 한 단계).</summary>
    public void AddPart() => AddPartTo(_ctx.CurrentGroup);

    private void AddPartTo(Layer? group)
    {
        if (P is not { } p) return;
        var size = _ctx.CurrentPart?.PartSize ?? new Size(Math.Min(16, p.Width), Math.Min(16, p.Height));
        var dlg = new NewPartDialog(p.UniqueLayerName(UiStrings.S_TimelinePanel_AddPartTo_3720B51812), size.Width, size.Height);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        string? name = dlg.PartName.Length > 0 ? dlg.PartName : null;
        int at = SlotAboveCurrent(p);
        Layer? part = null;
        try
        {
            _ctx.Structural(() => p.Edit(UiStrings.S_TimelinePanel_AddPartTo_315E69F074, ProjectChange.Layers | ProjectChange.Pixels, () =>
            {
                var target = group ?? p.AddGroup(null, at);
                part = p.AddPart(target, name, dlg.PartWidth, dlg.PartHeight);
            }));
        }
        catch (ArgumentException ex)
        {
            Ui.Error(this, ex.Message);
            return;
        }
        if (part is not null) _ctx.SetCurrentLayer(p.IndexOf(part));
    }

    /// <summary>현재 부위 크기 바꾸기(기준점; 그림은 프레임에서 같은 자리, 피벗은 같은 그림 지점 유지).</summary>
    public void ResizePart() => ResizePartAt(_ctx.CurrentLayer);

    private void ResizePartAt(int index)
    {
        if (P is not { } p || index < 0 || index >= p.Layers.Count) return;
        var part = p.Layers[index];
        if (!part.IsPart)
        {
            Ui.Info(this, UiStrings.S_TimelinePanel_ResizePartAt_98A5871C4A);
            return;
        }
        var dlg = new ResizeDialog(part.PartSize.Width, part.PartSize.Height, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_ResizePartAt_AAF40A125F,part.Name));
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (dlg.NewWidth == part.PartSize.Width && dlg.NewHeight == part.PartSize.Height) return;
        _ctx.Structural(() => p.ResizePart(index, dlg.NewWidth, dlg.NewHeight, dlg.ImageAnchor));
    }

    /// <summary>부위 위치(프레임 안 왼쪽 위 X·Y) 입력. 그룹이 자동 배치 중이면 옮기기 전에 자동 배치를 풀지 묻는다(풀기 + 옮기기 = 실행취소 한 단계).</summary>
    private void SetPartPosition(int index)
    {
        if (P is not { } p || index < 0 || index >= p.Layers.Count || !p.Layers[index].IsPart) return;
        var part = p.Layers[index];
        var dlg = new PartOffsetDialog(part.Name, part.PartOffset);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Offset == part.PartOffset) return;
        if (!GroupLayoutDialog.ConfirmManualMove(this, part)) return;
        _ctx.MovePart(part, dlg.Offset);
    }

    /// <summary>그룹 자동 배치 설정 창(적용 = 켜기·설정, "자동 배치 끄기" = 끄기 — 각각 실행취소 한 단계).</summary>
    private void EditGroupLayout(int index)
    {
        if (P is not { } p || index < 0 || index >= p.Layers.Count || p.Layers[index] is not { IsGroup: true } group) return;
        var dlg = new GroupLayoutDialog(p, group, _ctx.CurrentFrame);
        switch (dlg.ShowDialog(this))
        {
            case DialogResult.OK:
                bool was = group.AutoLayout is not null;
                _ctx.SetGroupLayout(group, dlg.Layout);
                Say(was ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_EditGroupLayout_6F86F3E62E,group.Name) : string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_EditGroupLayout_5EF8040D39,group.Name));
                break;
            case DialogResult.No:
                TurnOffGroupLayout(group);
                break;
        }
    }

    private void TurnOffGroupLayout(Layer group)
    {
        _ctx.SetGroupLayout(group, null);
        Say(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_TurnOffGroupLayout_B5D4ABA3A0,group.Name));
    }

    /// <summary>그룹 접기/펼치기(기록·수정 표시 없음). 접는 그룹 안 부위를 고르고 있었으면 그룹을 고른다.</summary>
    private void ToggleCollapsed(int index)
    {
        if (P is not { } p || index < 0 || index >= p.Layers.Count || !p.Layers[index].IsGroup) return;
        var group = p.Layers[index];
        bool collapse = !group.Collapsed;
        if (collapse && ReferenceEquals(_ctx.CurrentLayerObject?.Parent, group)) _ctx.SetCurrentLayer(index);
        p.SetLayerCollapsed(index, collapse);
    }

    /// <summary>현재 레이어가 접힌 그룹 안의 부위면 그 그룹을 펼친다(타임라인에 보이게).</summary>
    private void ExpandToCurrent()
    {
        if (P is { } p && _ctx.CurrentPart?.Parent is { Collapsed: true } group)
            p.SetLayerCollapsed(p.IndexOf(group), false);
    }

    private void DuplicateLayer()
    {
        if (P is not { } p) return;
        int at = _ctx.CurrentLayer;
        Layer? copy = null;
        _ctx.Structural(() => copy = p.DuplicateLayer(at));
        if (copy is not null) _ctx.SetCurrentLayer(p.IndexOf(copy));
    }

    /// <summary>▲(+1)/▼(-1): 일반 레이어·그룹은 맨 위 단계끼리 덩어리째, 부위는 같은 그룹 안에서 한 칸.</summary>
    private void MoveLayer(int delta)
    {
        if (P is not { } p) return;
        var layer = p.Layers[_ctx.CurrentLayer];
        int at = _ctx.CurrentLayer;
        _ctx.Structural(() =>
        {
            if (delta > 0) p.MoveLayerUp(at);
            else p.MoveLayerDown(at);
        });
        _ctx.SetCurrentLayer(p.IndexOf(layer));
    }

    /// <summary>
    /// 끌어 놓기: 같은 단계에서 이어진 레이어들 <paramref name="block"/>(위→아래)을 함께 <paramref name="steps"/>칸 옮긴다(+ = 위로, 실행취소 한 단계).
    /// 부위는 같은 그룹의 부위끼리, 일반 레이어·그룹은 맨 위 단계끼리(그룹은 부위와 함께) 순서를 바꾼다. 현재 레이어는 <paramref name="focus"/>.
    /// </summary>
    private void MoveLayersTo(IReadOnlyList<Layer> block, int steps, Layer focus)
    {
        if (P is not { } p || steps == 0 || block.Count == 0) return;
        // 위로는 맨 위 레이어부터, 아래로는 맨 아래부터 옮겨야 덩어리 안 순서가 그대로다.
        var order = steps > 0 ? block : block.Reverse().ToList();
        _ctx.Structural(() => p.Edit(UiStrings.S_TimelinePanel_MoveLayersTo_0546BCFC65, ProjectChange.Layers, () =>
        {
            foreach (var layer in order)
                for (int i = 0; i < Math.Abs(steps); i++)
                {
                    if (steps > 0) p.MoveLayerUp(p.IndexOf(layer));
                    else p.MoveLayerDown(p.IndexOf(layer));
                }
        }));
        _ctx.SetCurrentLayer(p.IndexOf(focus));
    }

    /// <summary>일반 레이어를 바로 아래 일반 레이어에만 병합할 수 있다.</summary>
    private static bool CanMergeDown(SpriteProject p, int index)
        => index > 0 && index < p.Layers.Count && p.Layers[index].Kind == LayerKind.Normal && p.Layers[index - 1].Kind == LayerKind.Normal;

    private void MergeDown()
    {
        if (P is not { } p) return;
        int at = _ctx.CurrentLayer;
        if (!CanMergeDown(p, at))
        {
            Ui.Info(this, at == 0 ? UiStrings.S_TimelinePanel_MergeDown_8D22D2F9DB : UiStrings.S_TimelinePanel_MergeDown_D5363E1C76);
            return;
        }
        try
        {
            _ctx.Structural(() => p.MergeLayerDown(at));
            _ctx.SetCurrentLayer(at - 1);
        }
        catch (InvalidOperationException ex)
        {
            Ui.Info(this, ex.Message);
        }
    }

    /// <summary>현재 레이어 이름을 타임라인 이름 칸에서 바로 고친다(F2).</summary>
    public void BeginRenameLayer()
    {
        if (P is null) return;
        ExpandToCurrent();
        _grid.EnsureCurrentVisible();
        _grid.BeginRename(_ctx.CurrentLayer);
    }

    private void RenameLayer(Layer layer, string name)
    {
        if (P is not { } p) return;
        int index = p.IndexOf(layer);
        name = name.Trim();
        if (index < 0 || name.Length == 0 || name == layer.Name) return;
        _ctx.Structural(() => p.SetLayerProperties(index, name: p.UniqueLayerName(name)));
    }

    private void SetLayerOpacity(int index)
    {
        if (P is not { } p) return;
        var text = InputDialog.Ask(this, UiStrings.S_TimelinePanel_SetLayerOpacity_CF46646372, UiStrings.S_TimelinePanel_SetLayerOpacity_D496BC50BE, p.Layers[index].Opacity.ToString(), ["255", "192", "128", "64"]);
        if (text is null) return;
        if (!byte.TryParse(text, out var v))
        {
            Ui.Error(this, UiStrings.S_TimelinePanel_SetLayerOpacity_367789B53A);
            return;
        }
        _ctx.Structural(() => p.SetLayerProperties(index, opacity: v));
    }

    /// <summary>
    /// 레이어 효과 대화상자(고치는 동안 캔버스에 바로 보이고, 확인 = 실행취소 한 단계, 취소 = 되돌림). <paramref name="focus"/>: 처음 고를 효과 번호(-1 = 첫 효과).
    /// </summary>
    private void EditLayerEffects(int index, int focus = -1, FrameRange? newRange = null, FrameRange? motionRange = null)
    {
        if (P is not { } p || index < 0 || index >= p.Layers.Count) return;
        _ctx.Structural(() =>
        {
            var dialog = new LayerEffectsDialog(_ctx, p.Layers[index], focus, newRange, motionRange ?? newRange);
            dialog.ShowDialog(this);
            if (dialog.ResultMessage is { } message) Say(message);
        });
    }

    private void ToggleLayer(int index, bool lockToggle)
    {
        if (P is not { } p) return;
        var l = p.Layers[index];
        _ctx.Structural(() =>
        {
            if (lockToggle) p.SetLayerProperties(index, locked: !l.Locked);
            else p.SetLayerProperties(index, visible: !l.Visible);
        });
    }

    // ───────────── 스킨(그룹 = 스킨 슬롯) ─────────────

    private static int SkinIndex(Layer group)
    {
        var skins = group.Skins;
        for (int i = 0; i < skins.Count; i++)
            if (ReferenceEquals(skins[i], group.ActiveSkin)) return i;
        return 0;
    }

    /// <summary>스킨 관리 창을 연다(<paramref name="focus"/> = 강조·스크롤할 그룹/부위, null이면 그대로).</summary>
    public void OpenSkinManager(Layer? focus) => SkinManagerWindow.Open(_ctx, this, focus);

    /// <summary>메뉴 글자를 그대로 보이는 머리글(스킨·그룹 이름의 '_'가 접근 키로 바뀌지 않게).</summary>
    private static TextBlock Plain(string text) => new() { Text = text };

    /// <summary>그룹 스킨 메뉴 항목: 스킨 목록(고르면 바뀜, 지금 스킨에 표시) + 추가·복제·이름·삭제·순서(지금 스킨 대상) + 부위 되돌리기·관리 창.</summary>
    private List<Control> SkinMenuItems(Layer group)
    {
        var skins = group.Skins;
        var active = group.ActiveSkin!;
        int at = SkinIndex(group);
        var items = new List<Control>();
        for (int i = 0; i < skins.Count; i++)
        {
            var skin = skins[i];
            var item = new MenuItem
            {
                Header = Plain(i == 0 ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_SkinMenuItems_38CB630C12,skin.Name) : skin.Name),
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = ReferenceEquals(skin, active),
            };
            item.Click += (_, _) => Dispatcher.UIThread.Post(() => _ctx.SetActiveSkin(group, skin));
            items.Add(item);
        }
        items.Add(new Separator());
        items.Add(Item(UiStrings.S_TimelinePanel_SkinMenuItems_B0E982373D, () => SkinOps.AddSkin(_ctx, group, null)));
        items.Add(Item(UiStrings.S_TimelinePanel_SkinMenuItems_F014A7BFC2, () => SkinOps.AddSkin(_ctx, group, active)));
        items.Add(Item(UiStrings.S_TimelinePanel_SkinMenuItems_0E021CC75C, () => SkinOps.RenameSkin(_ctx, this, group, active)));
        items.Add(Item(UiStrings.S_TimelinePanel_SkinMenuItems_BF2AA28CBD, () => SkinOps.DeleteSkin(_ctx, this, group, active), skins.Count > 1));
        items.Add(Item(UiStrings.S_TimelinePanel_SkinMenuItems_409F86FB97, () => SkinOps.MoveSkin(_ctx, group, at, at - 1), at > 0));
        items.Add(Item(UiStrings.S_TimelinePanel_SkinMenuItems_721A9DB29E, () => SkinOps.MoveSkin(_ctx, group, at, at + 1), at < skins.Count - 1));
        items.Add(new Separator());
        bool anyOverride = P is { } p && p.ChildrenOf(group).Any(x => x.SkinOverride is not null);
        items.Add(Item(UiStrings.S_TimelinePanel_SkinMenuItems_40DF34EC49, () => _ctx.ResetPartSkins(group), anyOverride));
        items.Add(Item(UiStrings.S_TimelinePanel_SkinMenuItems_B61606E9A0, () => OpenSkinManager(group)));
        return items;
    }

    /// <summary>부위 "이 부위 스킨" 메뉴 항목: 그룹 따름 + 고를 수 있는 스킨(그룹 스킨 + 이 부위 전용 "(개별)", 지금 고른 것에 표시) + "＋ 이 부위 스킨…".</summary>
    private List<Control> PartSkinMenuItems(Layer part, Layer group)
    {
        var own = part.SkinOverride;
        var follow = new MenuItem
        {
            Header = Plain(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_PartSkinMenuItems_EB384BCB51,group.ActiveSkin!.Name)),
            ToggleType = MenuItemToggleType.Radio,
            IsChecked = own is null,
        };
        follow.Click += (_, _) => Dispatcher.UIThread.Post(() => _ctx.SetPartSkin(part, null));
        var items = new List<Control> { follow, new Separator() };
        var skins = part.SkinChoices;
        for (int i = 0; i < skins.Count; i++)
        {
            var skin = skins[i];
            var item = new MenuItem
            {
                Header = Plain(skin.IsPartOnly ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_PartSkinMenuItems_6C100EAA80,skin.Name) : ReferenceEquals(skin, group.BaseSkin) ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_PartSkinMenuItems_CF3C760D28,skin.Name) : skin.Name),
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = ReferenceEquals(skin, own),
            };
            item.Click += (_, _) => Dispatcher.UIThread.Post(() => _ctx.SetPartSkin(part, skin));
            items.Add(item);
        }
        items.Add(new Separator());
        var addPartSkin = Item(UiStrings.S_TimelinePanel_PartSkinMenuItems_AD14B32035, () => SkinOps.AddPartSkin(_ctx, this, part, null, prompt: true));
        addPartSkin.Icon = Ui.Icon(VectorIcon.Plus, 14);
        items.Add(addPartSkin);
        items.Add(Item(UiStrings.S_TimelinePanel_PartSkinMenuItems_179840054C, () => OpenSkinManager(part)));
        return items;
    }

    // ───────────── 태그 ─────────────

    /// <summary>고른 프레임 범위(없으면 현재 프레임)로 태그를 만든다.</summary>
    public void NewTag()
    {
        if (P is not { } p) return;
        var frames = SelectedFrames();
        int from = frames.Min(), to = frames.Max();
        var dlg = new TagDialog(p, null, from, to);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            AnimationTag? tag = null;
            _ctx.Structural(() => p.Edit(UiStrings.S_TimelinePanel_NewTag_89C5F490AE, ProjectChange.Tags, () =>
            {
                tag = p.AddTag(dlg.TagName, dlg.From, dlg.To, dlg.Direction, dlg.Loop);
                p.SetTag(p.Tags.ToList().IndexOf(tag), color: dlg.TagColor);
            }));
            _ctx.SelectedTag = tag;
        }
        catch (ArgumentException ex)
        {
            Ui.Error(this, ex.Message);
        }
    }

    private void EditTag(AnimationTag tag)
    {
        if (P is not { } p) return;
        int index = p.Tags.ToList().IndexOf(tag);
        if (index < 0) return;
        var dlg = new TagDialog(p, tag, tag.From, tag.To);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _ctx.Structural(() => p.SetTag(index, dlg.TagName, dlg.From, dlg.To, dlg.Direction, dlg.Loop, dlg.TagColor));
        }
        catch (ArgumentException ex)
        {
            Ui.Error(this, ex.Message);
        }
    }

    /// <summary>태그 범위만 바꾼다(끝 손잡이 끌기, 실행취소 1단계).</summary>
    private void ResizeTag(AnimationTag tag, int from, int to)
    {
        if (P is not { } p) return;
        int index = p.Tags.ToList().IndexOf(tag);
        if (index < 0) return;
        try
        {
            _ctx.Structural(() => p.SetTag(index, from: from, to: to));
            _grid.SelectFrameRange(from, to);
        }
        catch (ArgumentException ex)
        {
            Ui.Error(this, ex.Message);
        }
    }

    private void DeleteTag(AnimationTag tag)
    {
        if (P is not { } p) return;
        int index = p.Tags.ToList().IndexOf(tag);
        if (index < 0) return;
        _ctx.Structural(() => p.RemoveTag(index));
    }

    // ───────────── 우클릭 메뉴 ─────────────

    private static MenuItem Item(string text, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = Ui.Shortcut(text), IsEnabled = enabled };
        // 메뉴가 닫힌 뒤 실행해 모달 대화상자가 메뉴와 겹치지 않게 한다.
        item.Click += (_, _) => Dispatcher.UIThread.Post(action);
        return item;
    }

    /// <summary>격자 누르기에서 연다(그 누르기는 이미 처리됨): 마지막 포인터 위치에, 다른 메뉴는 닫고.</summary>
    private void ShowMenu(params Control[] items) => PopupMenus.Show(_grid, items);

    /// <summary>메뉴 맨 위의 지금 선택 표시(누를 수 없는 줄, 이름의 '_'도 그대로).</summary>
    private MenuItem SelectionHeader() => new() { Header = Plain(SelectionLabel()), IsEnabled = false };

    /// <summary>메뉴 끝 프레임 공통 항목: 피벗·내보낼 칸 이름(누른 프레임 <paramref name="frame"/>).</summary>
    private void AddFrameTail(List<Control> items, SpriteProject p, int frame)
    {
        items.Add(new Separator());
        items.Add(Item(UiStrings.S_TimelinePanel_AddFrameTail_30B4C69887, () => _ctx.Structural(() => p.SetFramePivot(frame, null))));
        items.Add(Item(UiStrings.S_TimelinePanel_AddFrameTail_8439D90B67, () => _ctx.Structural(() => p.SetFramePivot(frame, p.GetFramePivot(frame)))));
        items.Add(Item(UiStrings.S_TimelinePanel_AddFrameTail_3DE73C6921, () =>
        {
            var name = InputDialog.Ask(this, UiStrings.S_TimelinePanel_AddFrameTail_5171DB3A9B, UiStrings.S_TimelinePanel_AddFrameTail_E23FDB5695, p.Frames[frame].Name ?? "");
            if (name is not null) _ctx.Structural(() => p.SetFrameName(frame, name));
        }));
    }

    /// <summary>프레임 머리글 우클릭 메뉴: 고른 프레임 대상(삭제 = 고른 프레임 모두).</summary>
    private void ShowFrameMenu(int frame)
    {
        if (P is not { } p) return;
        var frames = SelectedFrames();
        string range = FramesText(frames), span = frames.Count > 1 ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_ShowFrameMenu_A98AE8BAA0,frames[0] + 1,frames[^1] + 1) : string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_ShowFrameMenu_7849DB91AC,frames[0] + 1);
        var items = new List<Control>
        {
            SelectionHeader(),
            new Separator(),
            Item(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_ShowFrameMenu_478619FBFF,span), NewTag),
            new Separator(),
            Item(UiStrings.S_TimelinePanel_ShowFrameMenu_0B9E635B3B, NewEmptyFrame),
            Item(UiStrings.S_TimelinePanel_ShowFrameMenu_E852DE24A6, DuplicateFrame),
            Item(frames.Count > 1 ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_ShowFrameMenu_B87D177398,range,DeleteKeyName) : string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_ShowFrameMenu_F31A4BEC0A,DeleteKeyName), DeleteFrame, frames.Count < p.Frames.Count),
            Item(UiStrings.S_TimelinePanel_ShowFrameMenu_90C081CF35, () => MoveFrame(-1), frame > 0),
            Item(UiStrings.S_TimelinePanel_ShowFrameMenu_65AE77A221, () => MoveFrame(1), frame < p.Frames.Count - 1),
            Item(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_ShowFrameMenu_7DE4BD5946,p.Frames[frame].DurationMs), SetDuration),
            new Separator(),
            Item(UiStrings.S_TimelinePanel_ShowFrameMenu_53613EAC0A, () => CopySelection(false)),
            Item(UiStrings.S_TimelinePanel_ShowFrameMenu_F0463E8E47, () => CopySelection(true), frames.Count < p.Frames.Count),
            Item(_clip is { WholeFrames: true } ? UiStrings.S_TimelinePanel_ShowFrameMenu_CEA5C91779 : UiStrings.S_TimelinePanel_ShowFrameMenu_3588867D6D, () => PasteSelection(), _clip is not null),
        };
        AddFrameTail(items, p, frame);
        ShowMenu([.. items]);
    }

    /// <summary>셀 우클릭 메뉴: 고른 셀 묶음 대상(셀 지우기 = Delete, 복사·붙여넣기, 효과 추가) + 그 프레임 동작.</summary>
    private void ShowCelMenu(int frame, int layer)
    {
        if (P is not { } p) return;
        var (rows, frames) = CelBlock();
        string range = frames.Count > 1 ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_ShowCelMenu_4140F7A491,frames.Min() + 1,frames.Max() + 1) : string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_ShowCelMenu_F687732B1B,frames[0] + 1);
        bool single = rows.Count * frames.Count == 1;
        // 빌린 셀은 이 스킨 자신의 그림이 없어 지울 것이 없다(기본 스킨 셀은 기본 스킨에서 지움).
        bool borrowed = single && !p.Layers[layer].IsGroup && p.IsCelInherited(layer, frame);
        var items = new List<Control>
        {
            SelectionHeader(),
            new Separator(),
            Item(borrowed ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_ShowCelMenu_F4960F4C1A) : string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_ShowCelMenu_470F8D50A4,DeleteKeyName), ClearSelectedCels, HasOwnCels(p, rows, frames)),
        };
        if (single && !p.Layers[layer].IsGroup)
            items.Add(Item(UiStrings.S_TimelinePanel_ShowCelMenu_F578B13F0B, () => CopyCelFromPrevious(layer, frame), p.IsCelEmpty(layer, frame) && PreviousCelFrame(p, layer, frame) >= 0));
        items.AddRange(
        [
            new Separator(),
            Item(UiStrings.S_TimelinePanel_ShowCelMenu_6618250E5A, () => CopySelection(false)),
            Item(UiStrings.S_TimelinePanel_ShowCelMenu_8FF74D6DA3, () => CopySelection(true)),
            Item(_clip is { WholeFrames: true } ? UiStrings.S_TimelinePanel_ShowCelMenu_A934B99F70 : UiStrings.S_TimelinePanel_ShowCelMenu_8E28DA59DD, () => PasteSelection(), _clip is not null),
        ]);
        // 효과 창을 고른 칸들의 프레임 범위로 열어 거기서 종류·범위를 골라 추가한다.
        var effectRange = FrameRange.Of(frames.Min(), frames.Max(), p.Frames.Count);
        // 움직이는 효과는 여러 프레임에 걸쳐야 움직이므로, 한 프레임만 골랐으면 그 프레임부터 그 태그(없으면 애니메이션) 끝까지로 넓힌다.
        int start = frames.Min();
        int end = frames.Count > 1 ? frames.Max()
            : p.Tags.Where(t => t.From <= start && start <= t.To).Select(t => t.To).DefaultIfEmpty(p.Frames.Count - 1).Max();
        var motionRange = FrameRange.Of(start, end, p.Frames.Count);
        items.Add(new Separator());
        items.Add(Item(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_ShowCelMenu_21F847E58A,(effectRange is null ? UiStrings.S_TimelinePanel_ShowCelMenu_1631708D81 : range)), () => EditLayerEffects(layer, -1, effectRange, motionRange)));
        items.Add(new Separator());
        items.Add(Item(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_ShowCelMenu_A6F62B79BC,range), NewTag));
        items.Add(Item(UiStrings.S_TimelinePanel_ShowCelMenu_1AE424CCE8, NewEmptyFrame));
        items.Add(Item(UiStrings.S_TimelinePanel_ShowCelMenu_5BF39C02EE, DuplicateFrame));
        items.Add(Item(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_ShowCelMenu_28166ED748,p.Frames[frame].DurationMs), SetDuration));
        AddFrameTail(items, p, frame);
        ShowMenu([.. items]);
    }

    /// <summary>레이어 이름 열 빈 곳·왼쪽 위 칸 우클릭: 새로 만들기.</summary>
    private void ShowEmptyMenu() => ShowMenu(
        Item(UiStrings.S_TimelinePanel_ShowEmptyMenu_BD6EAD0E34, AddLayer),
        Item(UiStrings.S_TimelinePanel_ShowEmptyMenu_3C96CCD165, AddGroup),
        Item(UiStrings.S_TimelinePanel_ShowEmptyMenu_3E26686E9F, AddPart),
        new Separator(),
        Item(UiStrings.S_TimelinePanel_ShowEmptyMenu_43E7688224, () => AppendFrame(false, _ctx.CurrentLayer)),
        Item(UiStrings.S_TimelinePanel_ShowEmptyMenu_A8038996C4, () => AppendFrame(true, _ctx.CurrentLayer)));

    private void ShowLayerMenu(int index)
    {
        if (P is not { } p) return;
        var layer = p.Layers[index];
        var items = new List<Control>
        {
            SelectionHeader(),
            new Separator(),
            Item(UiStrings.S_TimelinePanel_ShowLayerMenu_89E0EF3AE5, BeginRenameLayer),
            Item(UiStrings.S_TimelinePanel_ShowLayerMenu_D344F8CCE8, () => SetLayerOpacity(index)),
            Item(layer.Effects.Count > 0 ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_ShowLayerMenu_4C4B49E76B,layer.Effects.Count) : UiStrings.S_TimelinePanel_ShowLayerMenu_C11C7262F2, () => EditLayerEffects(index)),
        };
        if (layer.Effects.Count > 0) items.Add(Item(layer.EffectsCollapsed ? UiStrings.S_TimelinePanel_ShowLayerMenu_BD012C50E0 : UiStrings.S_TimelinePanel_ShowLayerMenu_15B443F2B3, () => ToggleEffectRows(index)));
        items.AddRange(
        [
            Item(UiStrings.S_TimelinePanel_ShowLayerMenu_E46DC4274C, () => ToggleLayer(index, false)),
            Item(UiStrings.S_TimelinePanel_ShowLayerMenu_205CF20EA0, () => ToggleLayer(index, true)),
        ]);
        if (layer.IsPart)
        {
            items.Add(new Separator());
            items.Add(Item(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_ShowLayerMenu_66BE4C8AA7,layer.PartSize.Width,layer.PartSize.Height), () => ResizePartAt(index)));
            items.Add(Item(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_ShowLayerMenu_73BDC86998,layer.PartOffset.X,layer.PartOffset.Y) + (layer.IsAutoLaidOut ? UiStrings.S_TimelinePanel_ShowLayerMenu_8A6B808B32 : ""), () => SetPartPosition(index)));
            if (layer.Parent is { IsGroup: true } owner)
            {
                items.Add(new MenuItem
                {
                    Header = Plain(layer.SkinOverride is { } own ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_ShowLayerMenu_24F3803FE4,own.Name,(own.IsPartOnly ? UiStrings.S_TimelinePanel_ShowLayerMenu_5BB456B67D : UiStrings.S_TimelinePanel_ShowLayerMenu_FA9043A1C8)) : string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_ShowLayerMenu_595B6531BF,owner.ActiveSkin!.Name)),
                    ItemsSource = PartSkinMenuItems(layer, owner),
                });
                items.Add(new MenuItem { Header = Plain(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_ShowLayerMenu_5312E16A53,owner.ActiveSkin!.Name)), ItemsSource = SkinMenuItems(owner) });
            }
        }
        else if (layer.IsGroup)
        {
            items.Add(new Separator());
            items.Add(Item(UiStrings.S_TimelinePanel_ShowLayerMenu_CC4093DC8F, () => AddPartTo(layer)));
            items.Add(Item(layer.Collapsed ? UiStrings.S_TimelinePanel_ShowLayerMenu_8BC8B2C00F : UiStrings.S_TimelinePanel_ShowLayerMenu_DF2CF41103, () => ToggleCollapsed(index)));
            var layoutItem = Item(layer.AutoLayout is null ? UiStrings.S_TimelinePanel_ShowLayerMenu_E476AEFAC3 : UiStrings.S_TimelinePanel_ShowLayerMenu_05537BB2FE, () => EditGroupLayout(index));
            if (layer.AutoLayout is not null)
            {
                layoutItem.ToggleType = MenuItemToggleType.CheckBox;
                layoutItem.IsChecked = true;
            }
            items.Add(layoutItem);
            if (layer.AutoLayout is not null) items.Add(Item(UiStrings.S_TimelinePanel_ShowLayerMenu_14ED4902E9, () => TurnOffGroupLayout(layer)));
            items.Add(new MenuItem { Header = Plain(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_ShowLayerMenu_07F7E49A77,layer.ActiveSkin!.Name)), ItemsSource = SkinMenuItems(layer) });
        }
        items.Add(new Separator());
        items.Add(Item(UiStrings.S_TimelinePanel_ShowLayerMenu_AA2FF06061, AddLayer));
        items.Add(Item(UiStrings.S_TimelinePanel_ShowLayerMenu_13B10F0946, AddGroup));
        if (!layer.IsGroup) items.Add(Item(UiStrings.S_TimelinePanel_ShowLayerMenu_804EB6B1D9, AddPart));
        items.Add(Item(UiStrings.S_TimelinePanel_ShowLayerMenu_1524FC7BD9, DuplicateLayer));
        items.Add(Item(UiStrings.S_TimelinePanel_ShowLayerMenu_9BA618B63B, () => MoveLayer(1)));
        items.Add(Item(UiStrings.S_TimelinePanel_ShowLayerMenu_51558AB208, () => MoveLayer(-1)));
        items.Add(Item(UiStrings.S_TimelinePanel_ShowLayerMenu_54B2D67C9E, MergeDown, CanMergeDown(p, index)));
        int selected = _grid.Kind == SelKind.Layers ? _grid.SelectedLayerList().Count : 1;
        items.Add(Item(selected > 1 ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_ShowLayerMenu_4572E665BD,selected,DeleteKeyName) : string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_ShowLayerMenu_568F9BADA5,DeleteKeyName), DeleteSelectedLayers));
        ShowMenu([.. items]);
    }

    private void ShowTagMenu(AnimationTag tag) => ShowMenu(
        Item(UiStrings.S_TimelinePanel_ShowTagMenu_2102446206, () => EditTag(tag)),
        Item(UiStrings.S_TimelinePanel_ShowTagMenu_F2CC864CE4, () => { _ctx.SelectedTag = tag; _ctx.SetCurrentFrame(tag.From); _preview.Play(); }),
        Item(UiStrings.S_TimelinePanel_ShowTagMenu_CB0220F6BE, () => DeleteTag(tag)));

    /// <summary>
    /// 효과 줄 우클릭 메뉴(효과 <paramref name="effectIndex"/>, 누른 프레임 <paramref name="frame"/>). <paramref name="inBox"/>: 효과 상자 위(아니면 상자 밖 빈 칸).
    /// 맨 위에 고른 효과 수, 삭제는 고른 효과 모두, 나머지는 누른 효과 대상. 메뉴 한 항목 = 실행취소 한 단계.
    /// </summary>
    private void ShowEffectMenu(Layer layer, int effectIndex, int frame, bool inBox)
    {
        if (P is not { } p || effectIndex >= layer.Effects.Count) return;
        var effect = layer.Effects[effectIndex];
        int last = p.Frames.Count - 1;
        var (from, to) = effect.Frames is { } r ? (r.From, r.To) : (0, last);
        void SetRange(int a, int b) => ChangeEffect(layer, effectIndex, e => e with { Frames = FrameRange.Of(a, b, p.Frames.Count) });
        var items = new List<Control>
        {
            SelectionHeader(),
            new Separator(),
            Item(UiStrings.S_TimelinePanel_ShowEffectMenu_62925D147C, () => EditLayerEffects(p.IndexOf(layer), effectIndex)),
            new Separator(),
        };
        if (!inBox)
        {
            items.Add(Item(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_ShowEffectMenu_8640EB7B98,frame + 1), () => SetRange(Math.Min(from, frame), Math.Max(to, frame))));
            items.Add(Item(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_ShowEffectMenu_A8337F1B36,frame + 1), () => ChangeEffects(layer, list =>
                list.Insert(effectIndex + 1, effect with { Frames = FrameRange.Of(frame, last, p.Frames.Count) }))));
            items.Add(new Separator());
        }
        items.Add(Item(UiStrings.S_TimelinePanel_ShowEffectMenu_818FAF1DA5, () => SetRange(0, last), effect.Frames is not null));
        items.Add(Item(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_ShowEffectMenu_181217F649,frame + 1), () => SetRange(frame, frame), (from, to) != (frame, frame)));
        items.Add(Item(UiStrings.S_TimelinePanel_ShowEffectMenu_C98E3A64DD, () => SetRange(frame, last), (from, to) != (frame, last)));
        items.Add(Item(UiStrings.S_TimelinePanel_ShowEffectMenu_79EB88D409, () => SetRange(0, frame), (from, to) != (0, frame)));
        items.Add(new Separator());
        items.Add(Item(effect.Enabled ? UiStrings.S_TimelinePanel_ShowEffectMenu_2CA080D647 : UiStrings.S_TimelinePanel_ShowEffectMenu_BDEDBE0115, () => SetEffectEnabled(layer, effectIndex, !effect.Enabled)));
        int selected = _grid.Kind == SelKind.Effects ? _grid.SelectedEffectList().Count : 1;
        items.Add(Item(selected > 1 ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_ShowEffectMenu_94213B6D1A,selected,DeleteKeyName) : string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_ShowEffectMenu_0C9DDBBB24,DeleteKeyName), DeleteSelectedEffects));
        items.Add(BakeItem(p, layer, effectIndex));
        items.Add(new Separator());
        items.Add(Item(UiStrings.S_TimelinePanel_ShowEffectMenu_B155BA0146, () => CopySelection(false)));
        items.Add(Item(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_ShowEffectMenu_44E405DEFB), () => PasteSelection(), _fxClip is { Count: > 0 }));
        ShowMenu([.. items]);
    }

    /// <summary>영구 적용할 효과: 효과 줄로 고른 효과들(없으면 누른 효과 하나), 레이어별.</summary>
    private List<(Layer Layer, int[] Indices)> BakeTargets(Layer layer, int effectIndex)
    {
        var chosen = _grid.Kind == SelKind.Effects ? _grid.SelectedEffectList() : [];
        if (chosen.Count == 0) chosen = [(layer, effectIndex)];
        return [.. chosen.GroupBy(x => x.Layer).Select(g => (g.Key, g.Select(x => x.Index).ToArray()))];
    }

    /// <summary>
    /// "효과 영구 적용" 메뉴 항목: 위(앞)의 켜진 효과까지 함께 구워지면 "(위 효과 N개 포함)". 그룹·잠긴 레이어면 꺼 두고 까닭을 풍선 도움말로.
    /// </summary>
    private MenuItem BakeItem(SpriteProject p, Layer layer, int effectIndex)
    {
        var targets = BakeTargets(layer, effectIndex);
        int extra = targets.Sum(t => Gpixel.Core.Edit.LayerEffects.BakeSet(t.Layer.Effects, t.Indices).Count - t.Indices.Length);
        string? why = targets.Select(t => LayerEffectsDialog.CanBake(p, t.Layer).Why).FirstOrDefault(w => w is not null);
        var item = Item(extra > 0 ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_BakeItem_E6E9674BDF,extra) : UiStrings.S_TimelinePanel_BakeItem_F52DF1DF1B, BakeSelectedEffects, why is null);
        ToolTip.SetTip(item, why ?? UiStrings.S_TimelinePanel_BakeItem_1998F6F6DC
                                   + (extra > 0 ? UiStrings.S_TimelinePanel_BakeItem_1E2A5FF4B7 : ""));
        ToolTip.SetShowOnDisabled(item, true);
        return item;

        void BakeSelectedEffects()
        {
            var messages = new List<string>();
            try
            {
                _ctx.Structural(() => p.Edit(UiStrings.S_TimelinePanel_BakeItem_F52DF1DF1B, ProjectChange.Layers | ProjectChange.Pixels, () =>
                {
                    foreach (var (target, indices) in targets)
                    {
                        // 셀마다 효과를 입히는 계산은 덮개를 띄우고 작업 스레드에서(넣기만 여기서)
                        int index = p.IndexOf(target);
                        var bake = WorkOverlay.Run(this, UiStrings.Busy_Baking, _ => p.PrepareBake(index, indices));
                        messages.Add(LayerEffectsDialog.BakeSummary(p.ApplyBake(bake), target));
                    }
                }));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                Ui.Error(this, ex.Message);
                return;
            }
            _grid.ResetRange();
            Say(messages.Count == 1 ? messages[0] : string.Join(" / ", messages));
        }
    }

    /// <summary>고른 효과(여러 레이어에 걸쳐도 됨)를 지운다(실행취소 한 단계).</summary>
    private void DeleteSelectedEffects()
    {
        if (P is not { } p) return;
        var chosen = _grid.SelectedEffectList();
        if (chosen.Count == 0) return;
        _ctx.Structural(() => p.Edit(UiStrings.S_TimelinePanel_DeleteSelectedEffects_5A67D334E0, ProjectChange.Layers, () =>
        {
            foreach (var group in chosen.GroupBy(x => x.Layer))
            {
                var drop = group.Select(x => x.Index).ToHashSet();
                p.SetLayerEffects(p.IndexOf(group.Key), Gpixel.Core.Edit.EffectStack.Remove(group.Key.Effects, drop));
            }
        }));
        _grid.ResetRange();
        Say(Ui.Shortcut(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_DeleteSelectedEffects_3D286DB37F,chosen.Count)));
    }

    /// <summary>
    /// 효과 덩어리(<paramref name="source"/>의 [<paramref name="a"/>, <paramref name="b"/>])를 <paramref name="target"/> 레이어의
    /// <paramref name="at"/> 자리(같은 레이어면 덩어리를 뺀 목록 기준)로 옮긴다(실행취소 한 단계). 옮긴 효과가 골라진다.
    /// </summary>
    private void MoveEffects(Layer source, int a, int b, Layer target, int at)
    {
        if (P is not { } p || p.IndexOf(source) < 0 || p.IndexOf(target) < 0 || b >= source.Effects.Count) return;
        var moved = Enumerable.Range(a, b - a + 1).ToHashSet();
        var block = source.Effects.Skip(a).Take(b - a + 1).ToList();
        var rest = Gpixel.Core.Edit.EffectStack.Remove(source.Effects, moved);
        bool same = ReferenceEquals(source, target);
        var dest = same ? rest : target.Effects.ToList();
        at = Math.Clamp(at, 0, dest.Count);
        if (same && at == a) return;
        Gpixel.Core.Edit.EffectStack.InsertBlock(dest, at, block);
        _ctx.Structural(() => p.Edit(same ? UiStrings.S_TimelinePanel_MoveEffects_42F6E813C8 : UiStrings.S_TimelinePanel_MoveEffects_678C3C2110, ProjectChange.Layers, () =>
        {
            if (!same) p.SetLayerEffects(p.IndexOf(source), rest);
            p.SetLayerEffects(p.IndexOf(target), dest);
        }));
        // 옮긴 효과가 보이게(접어 둔 효과 줄은 펼침, 기록 없음)
        if (target.EffectsCollapsed) p.SetLayerEffectsCollapsed(p.IndexOf(target), false);
        _grid.SelectEffects(Enumerable.Range(at, block.Count).Select(i => (target, i)));
        Say(Ui.Shortcut(same ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_MoveEffects_0BC382FE0B) : string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_MoveEffects_D2762F8B1A,block.Count,target.Name)));
    }

    /// <summary>효과 목록을 고쳐 넣는다(실행취소 한 단계, 그대로면 기록 없음).</summary>
    private void ChangeEffects(Layer layer, Action<List<LayerEffect>> change)
    {
        if (P is not { } p || p.IndexOf(layer) is var index && index < 0) return;
        var list = layer.Effects.ToList();
        change(list);
        _ctx.Structural(() => p.SetLayerEffects(index, list));
    }

    /// <summary>효과 하나를 바꾼다(실행취소 한 단계).</summary>
    private void ChangeEffect(Layer layer, int effectIndex, Func<LayerEffect, LayerEffect> change)
        => ChangeEffects(layer, list => { if (effectIndex < list.Count) list[effectIndex] = change(list[effectIndex]); });

    private void SetEffectEnabled(Layer layer, int effectIndex, bool enabled)
    {
        // 다시 켜면 연속 실패로 멈춘 스크립트 효과도 다시 계산한다
        if (enabled && effectIndex >= 0 && effectIndex < layer.Effects.Count && layer.Effects[effectIndex] is ScriptEffect script)
            ScriptEffectRuntime.Resume(layer, script);
        ChangeEffect(layer, effectIndex, e => e with { Enabled = enabled });
    }

    /// <summary>레이어 아래 효과 줄 접기/펼치기(기록·수정 표시 없음).</summary>
    private void ToggleEffectRows(int index)
    {
        if (P is not { } p || index < 0 || index >= p.Layers.Count) return;
        p.SetLayerEffectsCollapsed(index, !p.Layers[index].EffectsCollapsed);
    }

    // ───────────── 격자 ─────────────

    /// <summary>
    /// 직접 그리는 격자. 가로/세로 스크롤 막대와 이름 고치기 상자를 자식으로 두고, 그 아래에 셀을 그린다.
    /// 행은 위 레이어부터이고, 접힌 그룹의 부위 행은 빠진다.
    /// </summary>
    private sealed class TimelineGrid : Avalonia.Controls.Grid
    {
        private const int NameW = 270, TagH = 20, HeadH = 30, CellH = 36, CellW = 42;
        /// <summary>이름 칸: 눈(0~24) · 자물쇠(24~48) · 그룹 접기 단추(48~64) · 이름(64~, 부위는 들여 씀) · 스킨 칩 · (부위) 크기.</summary>
        private const int ToggleX = 48, NameX = 64, Indent = 16;
        /// <summary>스킨 칩 폭: 최대(긴 스킨 이름은 …), 최소(이름이 길어도 이만큼은 보임). 이름이 먼저 자리를 차지한다.</summary>
        private const int SkinChipMaxW = 96, SkinChipMinW = 34;
        /// <summary>기본 스킨 그림을 빌려 보이는 셀 썸네일의 불투명도.</summary>
        private const double InheritedOpacity = 0.35;
        private static readonly IPen ChipAccentPen = AppTheme.Pen(AppTheme.AccentColor);
        private static readonly IBrush ChipAccentBrush = AppTheme.Brush(Color.FromArgb(90, AppTheme.AccentColor.R, AppTheme.AccentColor.G, AppTheme.AccentColor.B));

        private static readonly IPen BorderPen = AppTheme.Pen(AppTheme.BorderColor);
        private static readonly IPen AccentPen = AppTheme.Pen(AppTheme.AccentColor, 2);
        private static readonly IPen WhitePen = AppTheme.Pen(Colors.White);
        /// <summary>프레임·레이어·효과 선택 중의 현재 칸 테두리(파랗지 않게, 흐린 흰색).</summary>
        private static readonly IPen CurrentCelPen = AppTheme.Pen(Color.FromArgb(110, 255, 255, 255));
        private static readonly IPen PlayPen = AppTheme.Pen(Color.FromRgb(120, 230, 120), 2);
        private static readonly IBrush RangeBrush = AppTheme.Brush(Color.FromArgb(60, AppTheme.AccentColor.R, AppTheme.AccentColor.G, AppTheme.AccentColor.B));
        private static readonly IBrush PivotBrush = AppTheme.Brush(Color.FromRgb(255, 120, 80));

        /// <summary>효과 줄(레이어 행 아래, 효과마다 한 줄) 높이와 줄 바탕·현재 프레임 칸·이름 칸 바탕.</summary>
        private const int FxH = 16;
        private static readonly IBrush FxCellBrush = AppTheme.Brush(Color.FromRgb(44, 44, 50));
        private static readonly IBrush FxCurBrush = AppTheme.Brush(Color.FromRgb(56, 56, 64));
        private static readonly IBrush FxNameBrush = AppTheme.Brush(Color.FromRgb(40, 40, 46));
        private static readonly IPen FxCheckPen = AppTheme.Pen(Colors.White, 1.5);
        private static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);

        private readonly TimelinePanel _owner;
        private readonly ScrollBar _h = new() { Orientation = Orientation.Horizontal, AllowAutoHide = false, Minimum = 0 };
        private readonly ScrollBar _v = new() { Orientation = Orientation.Vertical, AllowAutoHide = false, Minimum = 0 };
        private TextBox? _rename;
        private Layer? _renameLayer;
        /// <summary>셀 묶음 선택의 프레임 범위(-1 = 현재 프레임 한 칸). 행은 현재 레이어 ~ <see cref="_rowEnd"/>.</summary>
        private int _rangeA = -1, _rangeB = -1;
        /// <summary>
        /// 고른 머리글·이름 누름 → 끌어서 순서 바꾸기(<see cref="ReorderCore"/> 문턱을 넘으면 <see cref="_reordering"/>).
        /// 프레임: 누른 프레임, 옮길 덩어리 [<see cref="_dragA"/>, <see cref="_dragB"/>], 미리보기의 옮긴 뒤 첫 프레임.
        /// 레이어: 누른 레이어, 옮길 덩어리(같은 단계에서 이어진 고른 레이어, 위→아래), 미리보기 옮길 칸 수(+ = 위로, 0 = 제자리).
        /// </summary>
        private int _dragFrom = -1, _dragA, _dragB, _dropTo = -1, _layerDragFrom = -1, _layerSteps;
        private List<Layer> _layerBlock = new();
        private bool _reordering;
        /// <summary>여러 개를 고른 채 그 안의 머리글·이름을 눌렀음(끌지 않고 떼면 그것 하나만 고름).</summary>
        private bool _collapseOnRelease;
        private IPointer? _dragPointer;
        private Avalonia.Point _press, _pressContent, _lastPointer;
        /// <summary>끄는 동안(순서 바꾸기·사각형 고르기) 가장자리 자동 스크롤: 좌우(프레임), 위아래(레이어).</summary>
        private readonly ReorderAutoScroll _scrollH, _scrollV;
        /// <summary>선택 종류(한 번에 하나). 셀 묶음이 기본(아무것도 안 고르면 현재 칸).</summary>
        private SelKind _kind;
        /// <summary>머리글로 고른 프레임(떨어져 있어도 됨)과 Shift+클릭 기준 프레임.</summary>
        private readonly SortedSet<int> _selFrames = new();
        private int _frameAnchor = -1;
        /// <summary>이름 칸으로 고른 레이어(떨어져 있어도 됨)와 Shift+클릭 기준 레이어.</summary>
        private readonly HashSet<Layer> _selLayers = new(ReferenceEqualityComparer.Instance);
        private Layer? _layerAnchor;
        /// <summary>효과 줄로 고른 효과(레이어, 효과 번호 — 여러 레이어 가능)와 Shift+클릭 기준 효과.</summary>
        private readonly HashSet<(Layer Layer, int Index)> _selFx = new();
        private (Layer Layer, int Index)? _fxAnchor;
        /// <summary>
        /// 효과 이름 끌기: 대상 레이어(null = 아님), 누른 효과, 옮길 덩어리 [<see cref="_fxDragA"/>, <see cref="_fxDragB"/>](그 레이어에서 이어진 고른 효과),
        /// 문턱을 넘었는지, 끌지 않고 떼면 누른 효과 하나만 고를지, 같은 레이어 안 놓을 자리(덩어리를 뺀 목록 기준),
        /// 다른 레이어에 놓을 때 그 레이어·자리(null = 같은 레이어 안), 효과 줄마다의 미리보기 밀림(px).
        /// </summary>
        private Layer? _fxDragLayer;
        private int _fxDragFrom, _fxDragA, _fxDragB, _fxDropTo;
        private bool _fxReordering, _fxCollapse;
        private (Layer Layer, int Index)? _fxDropOther;
        private ReorderTween[] _fxShift = [];
        private double _fxLift;
        /// <summary>
        /// 사각형 끌어 고르기(누른 칸이 고른 것이 아닐 때): 종류(null = 아님), 문턱을 넘어 사각형이 보이는지,
        /// 누른 곳(스크롤되는 축은 내용 좌표), 시작 칸(프레임 번호, 레이어는 보이는 행 번호).
        /// </summary>
        private SelKind? _marquee;
        private bool _marqueeShown;
        private Avalonia.Point _marqueeFrom;
        private int _marqueeAnchor;
        /// <summary>셀 선택의 끝 행(시작 행 = 현재 레이어). null이면 현재 레이어 한 행.</summary>
        private Layer? _rowEnd;
        /// <summary>셀 묶음 끌기: 옮길 행(레이어 번호, 위→아래)·프레임 범위, 잡은 칸(묶음 왼쪽 위 기준), 놓을 자리(보이는 행·프레임).</summary>
        private List<int>? _blockRows;
        private int _blockFrom, _blockCount, _grabRow, _grabFrame;
        /// <summary>묶음 안을 누른 칸(끌지 않고 놓으면 그 칸만 고른다).</summary>
        private (int Layer, int Frame) _blockPress = (-1, -1);
        private (int Row, int Frame) _blockDrop = (-1, -1);
        /// <summary>프레임·레이어를 고르면 그 열·행 칸에 까는 옅은 강조와 사각형 끌어 고르기 바탕.</summary>
        private static readonly IBrush LightSelBrush = AppTheme.Brush(Color.FromArgb(28, AppTheme.AccentColor.R, AppTheme.AccentColor.G, AppTheme.AccentColor.B));
        private static readonly IBrush MarqueeBrush = AppTheme.Brush(Color.FromArgb(48, AppTheme.AccentColor.R, AppTheme.AccentColor.G, AppTheme.AccentColor.B));
        private static readonly IPen MarqueePen = AppTheme.Pen(AppTheme.AccentColor);
        /// <summary>마우스가 올라간 프레임 열(프레임 수면 "다음 프레임" 칸)과 머리글(＋ 빈 프레임) 위인지 — 아니면 셀 줄(⧉ 복사).</summary>
        private (int Frame, bool Header) _hover = (-1, false);
        /// <summary>마우스가 올라간 "다음 레이어" 줄 단추(＋ 새 레이어 · ⧉ 마지막 레이어 복사)와 지금 띄운 도움말 풍선 글.</summary>
        private GhostRowButton _rowHover;
        private string? _tip;
        /// <summary>태그 범위 끌기: 대상 태그, 오른쪽 끝(true)/왼쪽 끝, 끄는 중인 범위.</summary>
        private AnimationTag? _tagEdit;
        private bool _tagEditRight;
        private int _tagFrom, _tagTo;
        /// <summary>끝 손잡이에 마우스가 올라간 태그(손잡이 강조용).</summary>
        private AnimationTag? _tagHover;
        /// <summary>효과 범위 끌기: 대상 레이어·효과 번호, 끄는 쪽(왼쪽 끝·오른쪽 끝·통째로), 끌기 전 효과 목록, 누른 프레임, 끌기 전 범위.</summary>
        private Layer? _fxLayer;
        private int _fxIndex;
        private FxGrip _fxGrip;
        private IReadOnlyList<LayerEffect>? _fxOriginal;
        private int _fxGrab, _fxFrom, _fxTo;
        /// <summary>효과 범위 끌기 중 상자가 보이는 새 범위의 효과(null = 아직 안 옮김). 놓을 때만 레이어에 넣는다 — 끄는 동안 칸마다 모든 프레임을 다시 계산하지 않게.</summary>
        private LayerEffect? _fxDragged;
        /// <summary>마우스가 올라간 효과 상자(손잡이 강조용).</summary>
        private (Layer Layer, int Index)? _fxHover;
        /// <summary>마우스가 올라간 자동 배치 표시 ◆의 그룹(도움말 풍선·강조용).</summary>
        private Layer? _layoutBadgeHover;
        private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
        private static readonly Cursor ResizeCursor = new(StandardCursorType.SizeWestEast);
        private static readonly IBrush GhostBrush = AppTheme.Brush(Color.FromRgb(38, 38, 43));
        private int _playing = -1;
        private readonly PaintSurface _surface;
        /// <summary>행(모든 레이어, 위→아래)·열(프레임) 배치 애니메이션. 그리기와 클릭 위치 찾기가 함께 쓴다.</summary>
        private readonly Track _rowT = new(CellH), _colT = new(CellW);
        private readonly DispatcherTimer _animTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };

        /// <summary>
        /// 한 축(행 또는 열) 칸들의 배치. 칸은 프로젝트의 레이어/프레임 객체(참조)로 알아본다. 칸마다 제 크기(px)가 있고(행은 효과 줄만큼 큼),
        /// 새 칸은 0에서 제 크기로 자라고, 숨는 칸(접힌 그룹의 부위)은 0으로 줄며, 지운 칸은 유령으로 남아 0까지 줄어든 뒤 빠진다.
        /// 제 크기가 바뀐 칸(효과 줄을 펼치고 접음)은 새 크기로 자라고 준다.
        /// 멈춰 있으면 칸 위치 = 앞에 있는 보이는 칸들의 크기 합으로 애니메이션 없는 배치와 정확히 같다.
        /// <paramref name="size"/>는 기본 칸 크기(배치 밖 위치를 셀 때 씀).
        /// </summary>
        private sealed class Track(int size)
        {
            private static readonly long Duration = System.Diagnostics.Stopwatch.Frequency * 180 / 1000;

            /// <summary>한 값의 ease-out cubic 이동.</summary>
            private struct Anim
            {
                public double From, To;
                public long Start;

                public readonly double At(long now)
                {
                    if (From == To) return To;
                    double t = (double)(now - Start) / Duration;
                    if (t >= 1) return To;
                    double u = 1 - t;
                    return From + (To - From) * (1 - u * u * u);
                }

                public void Go(double target, long now, bool animate)
                {
                    if (!animate) { From = To = target; return; }
                    if (target == To) return;
                    From = At(now);
                    To = target;
                    Start = now;
                }

                /// <summary>끝났으면 목표값에 고정하고 true.</summary>
                public bool Settle(long now)
                {
                    if (From != To && now - Start >= Duration) From = To;
                    return From == To;
                }
            }

            private sealed class Slot(object item)
            {
                public readonly object Item = item;
                /// <summary>칸 번호(-1 = 지워져 줄어드는 유령).</summary>
                public int Index = -1;
                public bool Shown;
                /// <summary>보일 때의 크기(px).</summary>
                public int Full;
                /// <summary>크기(px)·펼침(0~1) 애니메이션.</summary>
                public Anim Size, Open;
                /// <summary>끌어서 순서 바꾸기 미리보기의 밀림(px): 다른 칸은 비켜 서고(애니메이션), 뜬 칸은 포인터를 따라감.</summary>
                public ReorderTween Shift;
                public bool Lifted;
                /// <summary>Pos = 그리는 위치(밀림 포함), Base = 제자리(클릭 위치 찾기용).</summary>
                public int Px, Pos, Base, TargetPos;
                public double OpenNow;
            }

            /// <summary>칸 하나의 목표 상태: 보임 여부, (그룹 행) 펼침 여부, 보일 때의 크기(px).</summary>
            public readonly record struct Entry(object Item, bool Shown, bool Open, int Size);

            /// <summary>그릴 때 보는 칸: 칸 번호(-1 = 유령), 현재 위치·크기(px), 보일 때의 크기(px), 펼침 정도(0~1).</summary>
            public readonly record struct View(object Item, int Index, int Pos, int Px, int Full, double Open);

            private List<Slot> _slots = new();
            private Slot[] _live = [];

            public int Count => _live.Length;
            /// <summary>모든 칸의 현재 크기 합.</summary>
            public int Total { get; private set; }
            /// <summary>애니메이션이 끝났을 때의 크기 합.</summary>
            public int TargetTotal { get; private set; }
            public bool Animating { get; private set; }
            public int SlotCount => _slots.Count;

            /// <summary>그리는 차례로 k번째 칸(뜬 칸은 맨 나중 — 다른 칸 위에 보이게).</summary>
            public View Get(int k)
            {
                var s = _paint is { } order ? _slots[order[k]] : _slots[k];
                return new View(s.Item, s.Index, s.Pos, s.Px, s.Full, s.OpenNow);
            }

            private int[]? _paint;

            /// <summary>
            /// 순서 바꾸기 미리보기: 칸들이 <paramref name="sequence"/>(칸 번호를 새 차례로) 순서로 놓인 자리로 미끄러진다.
            /// <paramref name="lifted"/> 칸은 제자리에서 <paramref name="liftOffset"/>만큼(포인터를 따라) 바로 옮겨 그린다.
            /// </summary>
            public void Preview(IReadOnlyList<int> sequence, Func<int, bool> lifted, int liftOffset, long now)
            {
                int pos = 0;
                foreach (int i in sequence)
                {
                    var s = _live[i];
                    s.Lifted = lifted(i);
                    s.Shift.Go(s.Lifted ? liftOffset : pos - s.Base, now, animate: !s.Lifted);
                    pos += s.Px;
                }
                Update(now);
            }

            /// <summary>미리보기를 끝낸다: <paramref name="animate"/>면 제자리로 미끄러져 돌아간다(취소).</summary>
            public void ClearPreview(bool animate, long now)
            {
                foreach (var s in _slots)
                {
                    s.Lifted = false;
                    s.Shift.Go(0, now, animate);
                }
                Update(now);
            }

            /// <summary>칸마다 지금 그리는 위치(놓은 뒤 새 자리로 이어서 미끄러지게 <see cref="SettleFrom"/>에 넘긴다).</summary>
            public Dictionary<object, int> Displayed()
            {
                var d = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
                foreach (var s in _slots) d.TryAdd(s.Item, s.Pos);
                return d;
            }

            /// <summary>순서를 바꾼 뒤(<see cref="Sync"/> 다음): 칸들이 <paramref name="displayed"/> 위치에서 새 제자리로 미끄러진다.</summary>
            public void SettleFrom(Dictionary<object, int> displayed, long now)
            {
                Update(now);
                foreach (var s in _slots)
                {
                    s.Lifted = false;
                    s.Shift.Go(0, now, animate: false);
                    if (displayed.TryGetValue(s.Item, out int was) && was != s.Base)
                    {
                        s.Shift.Go(was - s.Base, now, animate: false);
                        s.Shift.Go(0, now, animate: true);
                    }
                }
                Update(now);
            }

            /// <summary>칸 <paramref name="i"/>가 이미 이 상태인지(바뀐 게 없으면 다시 맞추지 않으려고).</summary>
            public bool Matches(int i, object item, bool shown, bool open, int px)
                => _live[i] is var s && ReferenceEquals(s.Item, item) && s.Shown == shown && s.Full == px && s.Open.To == (open ? 1 : 0);

            /// <summary>
            /// 칸 목록을 새 상태에 맞춘다. <paramref name="animate"/>면 크기·펼침이 지금 값에서 목표로 움직이고, 빠진 칸은 원래 자리(앞 칸 뒤)에
            /// 유령으로 남아 줄어든다. 아니면 바로 목표 배치.
            /// </summary>
            public void Sync(Entry[] items, bool animate, long now)
            {
                var old = _slots;
                var byItem = new Dictionary<object, Slot>(old.Count, ReferenceEqualityComparer.Instance);
                foreach (var s in old)
                {
                    s.Index = -1;
                    byItem.TryAdd(s.Item, s);
                }
                var next = new List<Slot>(items.Length);
                var live = new Slot[items.Length];
                for (int i = 0; i < items.Length; i++)
                {
                    var e = items[i];
                    double open = e.Open ? 1 : 0;
                    if (!byItem.TryGetValue(e.Item, out var s))
                    {
                        s = new Slot(e.Item);
                        s.Size.Go(0, now, false);
                        s.Open.Go(open, now, false);
                    }
                    s.Index = i;
                    s.Shown = e.Shown;
                    s.Full = e.Size;
                    s.Size.Go(e.Shown ? e.Size : 0, now, animate);
                    s.Open.Go(open, now, animate);
                    live[i] = s;
                    next.Add(s);
                }
                if (animate)
                {
                    Slot? anchor = null;
                    foreach (var s in old)
                    {
                        if (s.Index >= 0) { anchor = s; continue; }
                        if (s.Size.At(now) <= 0) continue;
                        s.Shown = false;
                        s.Size.Go(0, now, true);
                        next.Insert(anchor is null ? 0 : next.IndexOf(anchor) + 1, s);
                        anchor = s;
                    }
                }
                _slots = next;
                _live = live;
                Update(now);
            }

            /// <summary>지금 시각의 크기·위치를 계산한다(다 줄어든 유령은 뺀다).</summary>
            public void Update(long now)
            {
                int pos = 0, target = 0;
                bool animating = false, lifted = false;
                for (int k = 0; k < _slots.Count; k++)
                {
                    var s = _slots[k];
                    bool done = s.Size.Settle(now) & s.Open.Settle(now) & s.Shift.Settle(now);
                    if (s.Index < 0 && done) { _slots.RemoveAt(k--); continue; }
                    animating |= !done;
                    lifted |= s.Lifted;
                    s.Px = (int)Math.Round(s.Size.At(now));
                    s.OpenNow = s.Open.At(now);
                    s.Base = pos;
                    s.Pos = pos + (int)Math.Round(s.Shift.At(now));
                    s.TargetPos = target;
                    pos += s.Px;
                    if (s.Index >= 0 && s.Shown) target += s.Full;
                }
                Total = pos;
                TargetTotal = target;
                Animating = animating;
                _paint = lifted ? [.. Enumerable.Range(0, _slots.Count).OrderBy(k => _slots[k].Lifted ? 1 : 0)] : null;
            }

            /// <summary>칸 <paramref name="i"/>의 현재 그리는 시작 위치(범위 밖은 칸 크기로 이어서).</summary>
            public int Pos(int i) => i < 0 ? i * size : i < _live.Length ? _live[i].Pos : Total + (i - _live.Length) * size;

            /// <summary>칸 <paramref name="i"/>의 제자리 시작 위치(순서 바꾸기 미리보기 밀림 뺌).</summary>
            public int BasePos(int i) => i < 0 ? i * size : i < _live.Length ? _live[i].Base : Total + (i - _live.Length) * size;

            /// <summary>애니메이션이 끝났을 때의 시작 위치.</summary>
            public int TargetPos(int i) => i < 0 ? i * size : i < _live.Length ? _live[i].TargetPos : TargetTotal + (i - _live.Length) * size;

            /// <summary>칸 <paramref name="i"/>의 현재 크기(범위 밖은 칸 크기).</summary>
            public int Px(int i) => (uint)i < (uint)_live.Length ? _live[i].Px : size;

            /// <summary><paramref name="pos"/>를 덮는 칸(크기가 있는 칸 중 시작 위치가 pos 이하인 마지막 칸).</summary>
            private int SlotAt(double pos)
            {
                int lo = 0, hi = _slots.Count - 1;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) / 2;
                    if (_slots[mid].Base <= pos) lo = mid;
                    else hi = mid - 1;
                }
                return lo;
            }

            /// <summary>
            /// <paramref name="pos"/> 위 칸 번호(앞뒤 바깥은 칸 크기로 이어서 센다). 숨거나 지워져 줄어드는 칸 위면
            /// <paramref name="exact"/>일 때 -1, 아니면 그다음 보이는 칸(없으면 <see cref="Count"/>).
            /// </summary>
            public int IndexAt(double pos, bool exact)
            {
                if (pos < 0) return (int)Math.Floor(pos / size);
                if (pos >= Total) return _live.Length + (int)Math.Floor((pos - Total) / size);
                int k = SlotAt(pos);
                if (_slots[k] is { Index: >= 0, Shown: true } hit) return hit.Index;
                if (exact) return -1;
                for (k++; k < _slots.Count; k++)
                    if (_slots[k] is { Index: >= 0, Shown: true } s) return s.Index;
                return _live.Length;
            }
        }

        /// <summary>
        /// 행·열 배치를 프로젝트에 맞춘다(바뀐 게 없으면 할 일 없음). <paramref name="animate"/>면 새·지운·접고 펼친 행(효과 줄 포함)과 새·지운 열이
        /// 부드럽게 자라고 줄며, 아니면(다른 파일 열기) 바로 맞춘다.
        /// </summary>
        public void SyncLayout(bool animate)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            var p = Ctx.Project;
            int n = p?.Layers.Count ?? 0, frames = p?.Frames.Count ?? 0;
            static bool Shown(Layer l) => l.Parent is not { Collapsed: true };
            static bool Open(Layer l) => l.IsGroup && !l.Collapsed;
            bool same = animate && n == _rowT.Count;
            for (int i = 0; same && i < n; i++)
            {
                var l = p!.Layers[n - 1 - i];
                same = _rowT.Matches(i, l, Shown(l), Open(l), RowFull(l));
            }
            if (!same)
            {
                var rows = new Track.Entry[n];
                for (int i = 0; i < n; i++)
                {
                    var l = p!.Layers[n - 1 - i];
                    rows[i] = new Track.Entry(l, Shown(l), Open(l), RowFull(l));
                }
                _rowT.Sync(rows, animate, now);
            }
            same = animate && frames == _colT.Count;
            for (int f = 0; same && f < frames; f++) same = _colT.Matches(f, p!.Frames[f], true, false, CellW);
            if (!same)
            {
                var cols = new Track.Entry[frames];
                for (int f = 0; f < frames; f++) cols[f] = new Track.Entry(p!.Frames[f], true, false, CellW);
                _colT.Sync(cols, animate, now);
            }
            if (_rowT.Animating || _colT.Animating)
            {
                if (!_animTimer.IsEnabled) _animTimer.Start();
            }
            else _animTimer.Stop();
        }

        private void OnAnimTick(object? sender, EventArgs e)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            _rowT.Update(now);
            _colT.Update(now);
            if (!_rowT.Animating && !_colT.Animating && !FxAnimating(now)) _animTimer.Stop();
            UpdateScrollSize();
            InvalidateVisual();
        }

        /// <summary>Panel.Render는 봉인돼 있으므로 그리기는 맨 아래 자식 컨트롤이 맡는다(같은 원점).</summary>
        private sealed class PaintSurface(TimelineGrid grid) : Control
        {
            public override void Render(DrawingContext g) => grid.Paint(g);
        }

        /// <summary>그림을 다시 그린다(그리기 면을 무효화).</summary>
        public new void InvalidateVisual() => _surface.InvalidateVisual();

        public TimelineGrid(TimelinePanel owner)
        {
            _owner = owner;
            _surface = new PaintSurface(this) { ClipToBounds = true, IsHitTestVisible = false };
            Children.Add(_surface);
            Background = AppTheme.InputBrush;
            ClipToBounds = true;
            Focusable = true;
            RowDefinitions = new RowDefinitions("*,Auto");
            ColumnDefinitions = new ColumnDefinitions("*,Auto");
            SetRow(_h, 1);
            SetColumn(_v, 1);
            Children.Add(_h);
            Children.Add(_v);
            _h.ValueChanged += (_, _) => InvalidateVisual();
            _v.ValueChanged += (_, _) => { EndRename(true); InvalidateVisual(); };
            SizeChanged += (_, _) => UpdateScrollSize();
            _v.SizeChanged += (_, _) => UpdateScrollSize();
            _h.SizeChanged += (_, _) => UpdateScrollSize();
            _animTimer.Tick += OnAnimTick;
            _scrollH = new ReorderAutoScroll(d => AutoScrollBy(_h, d));
            _scrollV = new ReorderAutoScroll(d => AutoScrollBy(_v, d));
        }

        private EditorContext Ctx => _owner._ctx;

        private int HValue => (int)_h.Value;
        private int VValue => (int)_v.Value;

        public (int A, int B) Range => _rangeA < 0 ? (-1, -1) : (Math.Min(_rangeA, _rangeB), Math.Max(_rangeA, _rangeB));

        public int PlayingFrame
        {
            get => _playing;
            set { _playing = value; InvalidateVisual(); }
        }

        /// <summary>선택 종류(한 번에 하나).</summary>
        public SelKind Kind => _kind;

        /// <summary>머리글로 고른 프레임(오름차순).</summary>
        public List<int> SelectedFrameList() => [.. _selFrames];

        /// <summary>이름 칸으로 고른 레이어 번호(위→아래).</summary>
        public List<int> SelectedLayerList()
        {
            var list = new List<int>();
            if (Ctx.Project is not { } p) return list;
            for (int i = p.Layers.Count - 1; i >= 0; i--)
                if (_selLayers.Contains(p.Layers[i])) list.Add(i);
            return list;
        }

        private void ClearSelection()
        {
            _rangeA = _rangeB = -1;
            _rowEnd = null;
            _selFrames.Clear();
            _selLayers.Clear();
            _selFx.Clear();
            _kind = SelKind.Cels;
        }

        /// <summary>선택을 풀어 현재 칸 하나(셀 선택)로 돌아간다.</summary>
        public void ResetRange() { ClearSelection(); InvalidateVisual(); }

        /// <summary>프레임(머리글)들을 고른다(빈 목록이면 현재 칸).</summary>
        public void SelectFrames(IEnumerable<int> frames)
        {
            ClearSelection();
            int n = Ctx.FrameCount;
            _selFrames.UnionWith(frames.Where(f => f >= 0 && f < n));
            if (_selFrames.Count > 0) _kind = SelKind.Frames;
            InvalidateVisual();
        }

        /// <summary>이어진 프레임 [<paramref name="a"/>, <paramref name="b"/>](순서 상관없음)를 고른다.</summary>
        public void SelectFrameRange(int a, int b) => SelectFrames(Enumerable.Range(Math.Min(a, b), Math.Abs(b - a) + 1));

        /// <summary>레이어(이름 칸)들을 고른다(빈 목록이면 현재 칸).</summary>
        /// <summary>레이어들을 고른다(복제한 레이어 고르기 등).</summary>
        public void SelectLayerSet(IEnumerable<Layer> layers)
        {
            var list = layers.ToList();
            SelectLayers(list);
            if (list.Count > 0) _layerAnchor = list[0];
        }

        private void SelectLayers(IEnumerable<Layer> layers)
        {
            ClearSelection();
            _selLayers.UnionWith(layers);
            if (_selLayers.Count > 0) _kind = SelKind.Layers;
            InvalidateVisual();
        }

        /// <summary>효과 줄로 고른 효과(위 레이어부터, 레이어 안은 효과 순서대로).</summary>
        public List<(Layer Layer, int Index)> SelectedEffectList()
        {
            if (Ctx.Project is not { } p) return [];
            return [.. _selFx.OrderByDescending(x => p.IndexOf(x.Layer)).ThenBy(x => x.Index)];
        }

        /// <summary>효과들을 고른다(빈 목록이면 현재 칸).</summary>
        public void SelectEffects(IEnumerable<(Layer Layer, int Index)> effects)
        {
            ClearSelection();
            _selFx.UnionWith(effects);
            if (_selFx.Count > 0) _kind = SelKind.Effects;
            InvalidateVisual();
        }

        private bool IsEffectSelected(Layer layer, int index) => _kind == SelKind.Effects && _selFx.Contains((layer, index));

        /// <summary>
        /// Ctrl/⌘+클릭 = 효과 하나 더하기·빼기, Shift+클릭 = 기준 효과와 같은 레이어면 그 사이 효과 모두 더하기(다른 레이어면 그 효과 더하기).
        /// 다른 레이어의 효과도 함께 고를 수 있다. 다 빠지면 현재 칸.
        /// </summary>
        private void ModifyEffectSelection((Layer Layer, int Index) key, bool range)
        {
            bool had = _kind == SelKind.Effects;
            if (!had) ClearSelection();
            _kind = SelKind.Effects;
            if (range && had && _fxAnchor is { } anchor && ReferenceEquals(anchor.Layer, key.Layer) && anchor.Index < key.Layer.Effects.Count)
            {
                for (int i = Math.Min(anchor.Index, key.Index); i <= Math.Max(anchor.Index, key.Index); i++) _selFx.Add((key.Layer, i));
            }
            else if (range || !_selFx.Remove(key))
            {
                _selFx.Add(key);
                _fxAnchor = key;
            }
            if (_selFx.Count == 0) ClearSelection();
            InvalidateVisual();
        }

        /// <summary>보이는 행 [<paramref name="a"/>, <paramref name="b"/>](순서 상관없음)의 레이어를 고른다.</summary>
        private void SelectLayerRows(int a, int b)
        {
            if (Ctx.Project is not { } p) return;
            var rows = Rows();
            int lo = Math.Clamp(Math.Min(a, b), 0, rows.Count - 1), hi = Math.Clamp(Math.Max(a, b), 0, rows.Count - 1);
            SelectLayers(rows.GetRange(lo, hi - lo + 1).Select(l => p.Layers[l]));
        }

        /// <summary>Ctrl/⌘+클릭: 프레임 하나를 더하거나 뺀다(더하면 현재 프레임·기준). 다 빠지면 현재 칸.</summary>
        private void ToggleFrame(int f)
        {
            if (_kind != SelKind.Frames) ClearSelection();
            _kind = SelKind.Frames;
            if (!_selFrames.Remove(f))
            {
                _selFrames.Add(f);
                _frameAnchor = f;
                Ctx.SetCurrentFrame(f);
            }
            if (_selFrames.Count == 0) ClearSelection();
            InvalidateVisual();
        }

        /// <summary>Ctrl/⌘+클릭: 레이어 하나를 더하거나 뺀다(더하면 현재 레이어·기준). 다 빠지면 현재 칸.</summary>
        private void ToggleLayer(Layer layer, int index)
        {
            if (_kind != SelKind.Layers) ClearSelection();
            _kind = SelKind.Layers;
            if (!_selLayers.Remove(layer))
            {
                _selLayers.Add(layer);
                _layerAnchor = layer;
                Ctx.SetCurrentLayer(index);
            }
            if (_selLayers.Count == 0) ClearSelection();
            InvalidateVisual();
        }

        /// <summary>보이는 행의 레이어 번호(위→아래).</summary>
        public List<int> VisibleRows() => Rows();

        /// <summary>셀 선택의 행(레이어 번호, 위→아래): 현재 레이어 행부터 끝 행까지.</summary>
        public List<int> SelectedRowLayers()
        {
            var rows = Rows();
            int a = rows.IndexOf(Ctx.CurrentLayer);
            if (a < 0) return [Ctx.CurrentLayer];
            int b = _rowEnd is not null && Ctx.Project is { } p ? rows.IndexOf(p.IndexOf(_rowEnd)) : -1;
            if (b < 0) b = a;
            return rows.GetRange(Math.Min(a, b), Math.Abs(a - b) + 1);
        }

        /// <summary>셀 묶음을 고른다: 시작 칸(현재) = (<paramref name="layer"/>, <paramref name="frameA"/>), 끝 = (<paramref name="end"/>, <paramref name="frameB"/>).</summary>
        public void SelectRect(int layer, int frameA, Layer? end, int frameB)
        {
            Ctx.SetCurrent(layer, frameA);
            ClearSelection();
            bool single = frameA == frameB && (end is null || Ctx.Project?.IndexOf(end) == layer);
            if (!single)
            {
                _rangeA = frameA;
                _rangeB = frameB;
                _rowEnd = end;
            }
            InvalidateVisual();
        }

        private bool InRange(int f) => _rangeA >= 0 && f >= Math.Min(_rangeA, _rangeB) && f <= Math.Max(_rangeA, _rangeB);

        /// <summary>셀 묶음에 든 칸인지(범위가 없으면 현재 칸).</summary>
        private bool InCelSelection(int layer, int frame)
            => _kind == SelKind.Cels && (_rangeA >= 0 ? InRange(frame) : frame == Ctx.CurrentFrame) && SelectedRowLayers().Contains(layer);

        /// <summary>프로젝트·현재 칸이 바뀐 뒤: 없어진 프레임·레이어를 선택에서 빼고, 남은 것이 없으면 현재 칸으로.</summary>
        public void ClampRange()
        {
            if (Ctx.Project is not { } p) return;
            if (_rangeA >= p.Frames.Count || _rangeB >= p.Frames.Count) { _rangeA = _rangeB = -1; _rowEnd = null; }
            else if (_rowEnd is not null && p.IndexOf(_rowEnd) < 0) _rowEnd = null;
            _selFrames.RemoveWhere(f => f >= p.Frames.Count);
            _selLayers.RemoveWhere(l => p.IndexOf(l) < 0);
            _selFx.RemoveWhere(x => p.IndexOf(x.Layer) < 0 || x.Index >= x.Layer.Effects.Count);
            if ((_kind == SelKind.Frames && _selFrames.Count == 0) || (_kind == SelKind.Layers && _selLayers.Count == 0)
                || (_kind == SelKind.Effects && _selFx.Count == 0)) ClearSelection();
        }

        /// <summary>보이는 행의 레이어 번호(맨 위 레이어부터, 접힌 그룹의 부위는 뺌).</summary>
        private List<int> Rows()
        {
            var rows = new List<int>();
            if (Ctx.Project is not { } p) return rows;
            for (int l = p.Layers.Count - 1; l >= 0; l--)
                if (p.Layers[l].Parent is not { Collapsed: true }) rows.Add(l);
            return rows;
        }

        /// <summary>스크롤 막대를 뺀 그리기 영역 크기.</summary>
        private double ClientW => Bounds.Width - (_v.IsVisible ? _v.Bounds.Width : 0);
        private double ClientH => Bounds.Height - (_h.IsVisible ? _h.Bounds.Height : 0);
        private int ViewW => (int)ClientW - NameW;
        private int ViewH => (int)ClientH - TagH - HeadH;

        public void UpdateScrollSize()
        {
            // 마지막 프레임 뒤 "다음 프레임" 칸, 마지막 행 아래 "다음 레이어" 줄까지. 애니메이션 중에는 지금·끝 배치 중 큰 쪽(줄어들 때 스크롤이 튀지 않게).
            int contentW = Math.Max(_colT.Total, _colT.TargetTotal) + CellW, contentH = Math.Max(_rowT.Total, _rowT.TargetTotal) + CellH;
            int viewW = Math.Max(1, ViewW), viewH = Math.Max(1, ViewH);
            _h.Maximum = Math.Max(0, contentW - viewW);
            _h.ViewportSize = viewW;
            _h.LargeChange = viewW;
            _h.SmallChange = CellW;
            _h.IsEnabled = contentW > viewW;
            if (!_h.IsEnabled) _h.Value = 0;
            _v.Maximum = Math.Max(0, contentH - viewH);
            _v.ViewportSize = viewH;
            _v.LargeChange = viewH;
            _v.SmallChange = CellH;
            _v.IsEnabled = contentH > viewH;
            if (!_v.IsEnabled) _v.Value = 0;
        }

        /// <summary>현재 프레임 열과 현재 레이어 행이 보이도록 스크롤한다(애니메이션이 끝났을 때의 자리 기준).</summary>
        public void EnsureCurrentVisible()
        {
            if (_h.IsEnabled)
            {
                int x = _colT.TargetPos(Ctx.CurrentFrame);
                if (x < HValue) _h.Value = x;
                else if (x + CellW > HValue + ViewW) _h.Value = Math.Clamp(x + CellW - ViewW, 0, _h.Maximum);
            }
            if (_v.IsEnabled && Rows().Contains(Ctx.CurrentLayer))
            {
                int y = _rowT.TargetPos(RowItem(Ctx.CurrentLayer));
                if (y < VValue) _v.Value = y;
                else if (y + CellH > VValue + ViewH) _v.Value = Math.Clamp(y + CellH - ViewH, 0, _v.Maximum);
            }
            InvalidateVisual();
        }

        /// <summary>레이어 번호 → 행 배치의 칸 번호(행은 맨 위 레이어부터).</summary>
        private int RowItem(int layer) => _rowT.Count - 1 - layer;

        /// <summary>프레임 열의 왼쪽 x와 현재 폭(애니메이션 중이면 자라거나 줄어드는 중).</summary>
        private int FrameX(int f) => NameW + _colT.Pos(f) - HValue;
        private int FrameW(int f) => _colT.Px(f);

        /// <summary>레이어 행의 위쪽 y.</summary>
        private int LayerY(int layer) => TagH + HeadH + _rowT.Pos(RowItem(layer)) - VValue;

        /// <summary>격자 내용 좌표(스크롤 포함).</summary>
        private double ContentX(double x) => x - NameW + HValue;
        private double ContentY(double y) => y - TagH - HeadH + VValue;

        private int FrameAt(double x)
        {
            if (x < NameW) return -1;
            int f = _colT.IndexAt(ContentX(x), exact: true);
            return f >= 0 && f < Ctx.FrameCount ? f : -1;
        }

        /// <summary>범위 밖이면 가장 가까운 프레임.</summary>
        private int FrameAtClamped(double x)
            => Math.Clamp(_colT.IndexAt(ContentX(x), exact: false), 0, Math.Max(0, Ctx.FrameCount - 1));

        /// <summary>"다음 프레임" 칸 위인지.</summary>
        private bool IsGhostColumn(double x)
            => x >= NameW && ContentX(x) >= _colT.Total && ContentX(x) < _colT.Total + CellW;

        /// <summary>"다음 레이어" 줄(마지막 행 아래 한 줄) 위인지.</summary>
        private bool IsGhostRow(double y)
            => y >= TagH + HeadH && ContentY(y) >= _rowT.Total && ContentY(y) < _rowT.Total + CellH;

        /// <summary>
        /// "다음 프레임" 칸의 단추 위면 머리글(＋ 빈 프레임)인지 — false = 레이어 행 칸(⧉ 복사).
        /// 단추가 없는 곳(효과 줄, 마지막 행 아래 — "다음 레이어" 줄과 만나는 칸 포함)이면 null.
        /// </summary>
        private bool? GhostColumnAt(Avalonia.Point pos)
        {
            if (Ctx.Project is null || pos.Y < TagH || !IsGhostColumn(pos.X)) return null;
            if (pos.Y < TagH + HeadH) return true;
            return LayerAt(pos.Y) >= 0 && FxRowAt(pos.Y) is null ? false : null;
        }

        /// <summary>
        /// "다음 레이어" 줄의 단추: 이름 칸 전체 ＋(맨 아래에 새 레이어) · 프레임 칸 전체(첫 프레임 ~ 마지막 프레임 열 끝) ⧉(마지막 레이어 복사해서 추가).
        /// "다음 프레임" 열과 만나는 칸은 단추가 아니다.
        /// </summary>
        private enum GhostRowButton { None, Add, Copy }

        /// <summary>"다음 레이어" 줄 ⧉ 띠에서 아이콘을 되풀이해 그리는 프레임 간격(1·11·21… 번째 칸).</summary>
        private const int GhostCopyIconEvery = 10;

        private GhostRowButton GhostRowButtonAt(Avalonia.Point pos)
        {
            if (Ctx.Project is null || !IsGhostRow(pos.Y) || pos.X < 0) return GhostRowButton.None;
            if (pos.X < NameW) return GhostRowButton.Add;
            return Ctx.FrameCount > 0 && ContentX(pos.X) < _colT.Total ? GhostRowButton.Copy : GhostRowButton.None;
        }

        /// <summary>가장 가까운 보이는 행 번호.</summary>
        private int RowAtClamped(double y)
        {
            var rows = Rows();
            if (rows.Count == 0) return 0;
            int item = _rowT.IndexAt(ContentY(y), exact: false);
            if (item < 0) return 0;
            if (item >= _rowT.Count) return rows.Count - 1;
            return Math.Max(0, rows.IndexOf(_rowT.Count - 1 - item));
        }

        /// <summary>y 위 레이어(숨거나 지워져 줄어드는 중인 행 위면 -1). 레이어 아래 효과 줄 위도 그 레이어.</summary>
        private int LayerAt(double y)
        {
            if (y < TagH + HeadH) return -1;
            int item = _rowT.IndexAt(ContentY(y), exact: true);
            return item >= 0 && item < _rowT.Count ? _rowT.Count - 1 - item : -1;
        }

        /// <summary>효과 상자의 잡는 곳: 왼쪽 끝 손잡이 · 오른쪽 끝 손잡이 · 몸통(범위 통째로 옮김).</summary>
        private enum FxGrip { Left, Right, Body }

        /// <summary>레이어 행 아래 효과 줄 수(효과마다 한 줄, 효과 줄을 접었으면 0).</summary>
        private static int FxRows(Layer layer) => layer.EffectsCollapsed ? 0 : layer.Effects.Count;

        /// <summary>레이어 행의 높이(본줄 + 효과 줄).</summary>
        private static int RowFull(Layer layer) => CellH + FxRows(layer) * FxH;

        /// <summary>효과가 입혀지는 프레임 범위(모든 프레임이면 처음 ~ 끝, 프레임 수 밖은 끝으로).</summary>
        private static (int From, int To) FxSpan(LayerEffect effect, int frames)
            => effect.Frames is { } r ? (Math.Min(r.From, frames - 1), Math.Min(r.To, frames - 1)) : (0, frames - 1);

        /// <summary>효과 상자의 왼쪽·오른쪽 x(그리기·클릭 찾기 공용).</summary>
        private (double X0, double X1) FxX(LayerEffect effect, int frames)
        {
            var (from, to) = FxSpan(effect, frames);
            return (FrameX(from) + 2, FrameX(to) + FrameW(to) - 2);
        }

        /// <summary>y 위 효과 줄(레이어 번호, 효과 번호). 레이어 본줄이나 줄 밖이면 null.</summary>
        private (int Layer, int Effect)? FxRowAt(double y)
        {
            if (Ctx.Project is not { } p || LayerAt(y) is var l && l < 0) return null;
            int k = (int)Math.Floor((y - LayerY(l) - CellH) / FxH);
            return k >= 0 && k < FxRows(p.Layers[l]) ? (l, k) : null;
        }

        /// <summary>x 위 효과 상자의 잡는 곳(끝 손잡이 = 상자 폭의 1/3, 최대 8px). 상자 밖이면 null.</summary>
        private FxGrip? FxGripAt(double x, LayerEffect effect, int frames)
        {
            var (x0, x1) = FxX(effect, frames);
            if (x < x0 || x > x1) return null;
            double grip = Math.Min(8, (x1 - x0) / 3);
            return x <= x0 + grip ? FxGrip.Left : x >= x1 - grip ? FxGrip.Right : FxGrip.Body;
        }

        /// <summary>그릴 때 쓰는 태그 범위(끄는 중이면 미리보기 범위).</summary>
        private (int From, int To) TagRange(AnimationTag tag)
            => ReferenceEquals(tag, _tagEdit) ? (_tagFrom, _tagTo) : (tag.From, tag.To);

        /// <summary>
        /// 태그 상자 안쪽 양 끝의 손잡이(상자 폭의 1/3, 최대 10px) 위면 그 태그와 끝(오른쪽 = true).
        /// 태그가 겹쳐 있으면 고른 태그 → 위에 그려진 태그 순으로 고른다.
        /// </summary>
        private (AnimationTag Tag, bool Right)? TagEdgeAt(Avalonia.Point pos)
        {
            if (Ctx.Project is not { } proj || pos.Y >= TagH || pos.X < NameW) return null;
            var order = proj.Tags.Reverse().OrderByDescending(t => ReferenceEquals(t, Ctx.SelectedTag));
            foreach (var tag in order)
            {
                double x0 = FrameX(tag.From) + 1, x1 = FrameX(tag.To) + FrameW(tag.To) - 1;
                double grip = Math.Min(10, (x1 - x0) / 3);
                if (pos.X >= x0 && pos.X <= x0 + grip) return (tag, false);
                if (pos.X >= x1 - grip && pos.X <= x1) return (tag, true);
            }
            return null;
        }

        private AnimationTag? TagAt(Avalonia.Point p)
        {
            if (Ctx.Project is not { } proj || p.Y >= TagH || p.X < NameW) return null;
            int f = FrameAt(p.X);
            return f < 0 ? null : proj.Tags.LastOrDefault(t => t.Contains(f));
        }

        /// <summary>레이어 이름이 시작하는 x(부위는 들여 씀).</summary>
        private static int NameLeft(Layer layer) => layer.IsPart ? NameX + Indent : layer.IsGroup ? NameX : ToggleX;

        /// <summary>이름 칸에 보이는 레이어 이름(불투명도가 255가 아니면 % 붙임).</summary>
        private static string LayerTitle(Layer layer) => layer.Name + (layer.Opacity < 255 ? $" ({layer.Opacity * 100 / 255}%)" : "");

        /// <summary>부위 크기 글자("16×16")와 그 폭(이름 칸 맨 오른쪽).</summary>
        private static (string Text, int W) PartSizeText(Layer part)
        {
            var text = $"{part.PartSize.Width}×{part.PartSize.Height}";
            return (text, (int)Math.Ceiling(AppTheme.MeasureText(text, AppTheme.SmallFontSize).Width + 4));
        }

        /// <summary>효과 표시("fx", 효과가 있으면)의 폭(없으면 0). 모두 꺼져 있으면 흐리게 그린다.</summary>
        private static int FxBadgeW(Layer layer)
            => layer.Effects.Count == 0 ? 0 : (int)Math.Ceiling(AppTheme.MeasureText("fx", AppTheme.SmallFontSize, bold: true).Width + 6);

        /// <summary>"fx" 표시 자리(x, 폭) — 그리기와 같은 자리(클릭하면 효과 줄 접기/펼치기). 폭 0 = 효과 없음.</summary>
        private static (int X, int W) FxBadge(Layer layer)
        {
            int fw = FxBadgeW(layer);
            if (fw == 0) return (0, 0);
            int right = NameW - 2;
            if (SkinChip(layer) is { W: > 0 } chip) right = chip.X - 4;
            right -= LayoutBadgeW(layer);
            if (layer.IsPart) right -= PartSizeText(layer).W;
            return (right - fw, fw);
        }

        /// <summary>자동 배치 표시(◆, 자동 배치가 켜진 그룹만)의 폭(없으면 0).</summary>
        private static int LayoutBadgeW(Layer layer) => layer is { IsGroup: true, AutoLayout: not null } ? 16 : 0;

        /// <summary>자동 배치 표시 ◆ 자리(x, 폭) — 스킨 칩 바로 왼쪽, 그리기와 같은 자리(누르면 자동 배치 설정). 폭 0 = 자동 배치 꺼짐.</summary>
        private static (int X, int W) LayoutBadge(Layer layer)
        {
            int w = LayoutBadgeW(layer);
            if (w == 0) return (0, 0);
            int right = NameW - 2;
            if (SkinChip(layer) is { W: > 0 } chip) right = chip.X - 2;
            return (right - w, w);
        }

        /// <summary>자동 배치 표시 ◆(강조색 마름모, 마우스를 올리면 밝은 바탕).</summary>
        private static void DrawLayoutBadge(DrawingContext g, Rect r, bool hovered)
        {
            if (hovered) g.DrawRectangle(ChipAccentBrush, null, r.Deflate(new Thickness(1, 9)), 3, 3);
            double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, h = 4.5;
            var geo = new StreamGeometry();
            using (var c = geo.Open())
            {
                c.BeginFigure(new Avalonia.Point(cx, cy - h), true);
                c.LineTo(new Avalonia.Point(cx + h, cy));
                c.LineTo(new Avalonia.Point(cx, cy + h));
                c.LineTo(new Avalonia.Point(cx - h, cy));
                c.EndFigure(true);
            }
            g.DrawGeometry(AppTheme.AccentBrush, null, geo);
        }

        /// <summary>효과 줄 이름 칸의 켜기 체크 상자 왼쪽 x(레이어 이름보다 조금 들여 씀). 상자 폭 10px, 누르는 자리는 앞뒤로 조금 넓게.</summary>
        private static int FxCheckX(Layer layer) => NameLeft(layer) + 10;

        /// <summary>
        /// 그룹·부위 행 이름 칸 오른쪽 끝의 스킨 칩(x, 폭) — 그룹·부위가 같은 열에 맞춰진다(부위 크기 글자는 칩 왼쪽).
        /// 그룹은 지금 스킨, 부위는 보이는 스킨 이름. 레이어 이름(부위는 크기 글자까지)이 먼저 자리를 차지하고,
        /// 칩은 남은 자리(최소 <see cref="SkinChipMinW"/>, 최대 <see cref="SkinChipMaxW"/>)에 맞춰 이름이 …로 줄어든다.
        /// 폭 0 = 칩 없음(일반 레이어·그룹 없는 부위).
        /// </summary>
        private static (int X, int W) SkinChip(Layer layer, int right)
        {
            if (layer.ShownSkin is not { } skin) return (0, 0);
            int want = (int)Math.Ceiling(Math.Min(SkinChipMaxW, AppTheme.MeasureText(skin.Name, AppTheme.SmallFontSize).Width + 10));
            int nameEnd = NameLeft(layer) + (int)Math.Ceiling(AppTheme.MeasureText(LayerTitle(layer), AppTheme.FontSize, bold: true).Width) + 6
                          + (layer.IsPart ? PartSizeText(layer).W + 4 : 0) + FxBadgeW(layer) + LayoutBadgeW(layer);
            int w = Math.Max(Math.Min(want, SkinChipMinW), Math.Min(want, right - nameEnd));
            return (right - w, w);
        }

        /// <summary>스킨 칩(<see cref="SkinChip(Layer, int)"/>), 그리기와 같은 자리(클릭 찾기용).</summary>
        private static (int X, int W) SkinChip(Layer layer) => SkinChip(layer, NameW - 4);

        /// <summary>
        /// 스킨 칩 그리기: 그룹 = 테두리 + 지금 스킨 이름, 부위 = 부위만 따로 고른 스킨이면 강조(파랑), 그룹을 따르면 흐린 테두리·글자.
        /// </summary>
        private static void DrawSkinChip(DrawingContext g, Layer layer, Rect r)
        {
            var name = layer.ShownSkin?.Name ?? "";
            IBrush text;
            if (layer.IsPart && layer.SkinOverride is not null)
            {
                g.DrawRectangle(ChipAccentBrush, ChipAccentPen, r.Deflate(0.5), 3, 3);
                text = AppTheme.TextBrush;
            }
            else if (layer.IsPart)
            {
                g.DrawRectangle(null, BorderPen, r.Deflate(0.5), 3, 3);
                text = AppTheme.DimTextBrush;
            }
            else
            {
                g.DrawRectangle(AppTheme.BackBrush, BorderPen, r.Deflate(0.5), 3, 3);
                text = AppTheme.TextBrush;
            }
            Text(g, name, r.Deflate(new Thickness(4, 0)), text, small: true, center: true);
        }

        // ── 레이어 이름 바로 고치기

        public void BeginRename(int index)
        {
            EndRename(true);
            if (Ctx.Project is not { } p || index < 0 || index >= p.Layers.Count) return;
            int row = Rows().IndexOf(index);
            if (row < 0) return;
            var layer = p.Layers[index];
            _renameLayer = layer;
            int x = NameLeft(layer);
            var box = new TextBox
            {
                Text = layer.Name,
                BorderThickness = new Thickness(1),
                BorderBrush = AppTheme.BorderBrush,
                Background = AppTheme.BackBrush,
                Foreground = AppTheme.TextBrush,
                FontFamily = AppTheme.FontFamily,
                FontSize = AppTheme.FontSize,
                Padding = new Thickness(2, 0),
                MinHeight = 0,
                Height = 20,
                Width = NameW - x - 4,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(x - 2, LayerY(index) + (CellH - 20) / 2, 0, 0),
            };
            box.KeyDown += (_, e) =>
            {
                if (e.Key is Key.Enter or Key.Escape)
                {
                    e.Handled = true;
                    EndRename(e.Key == Key.Enter, refocus: true);
                }
            };
            box.LostFocus += (_, _) => Dispatcher.UIThread.Post(() => { if (ReferenceEquals(_rename, box)) EndRename(true); });
            _rename = box;
            Children.Add(box);
            box.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                box.Focus();
                box.SelectAll();
            });
        }

        /// <summary>이름 고치기를 끝낸다(<paramref name="commit"/>면 적용, <paramref name="refocus"/>면 목록에 포커스).</summary>
        public void EndRename(bool commit, bool refocus = false)
        {
            if (_rename is not { } box) return;
            _rename = null;
            var layer = _renameLayer;
            _renameLayer = null;
            var text = box.Text ?? "";
            Children.Remove(box);
            if (commit && layer is not null) _owner.RenameLayer(layer, text);
            if (refocus) Focus();
            InvalidateVisual();
        }

        private static bool IsBright(Color c)
        {
            int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
            return (max + min) / 510.0 > 0.6;
        }

        private static void Text(DrawingContext g, string text, Rect r, IBrush brush, bool small = false, bool bold = false, bool center = false)
            => AppTheme.DrawTextIn(g, text, r, brush, small ? AppTheme.SmallFontSize : AppTheme.FontSize, bold, center: center);

        /// <summary>
        /// 그룹 접기/펼치기 삼각형: ▸(접힘)을 펼친 정도 <paramref name="open"/>(0~1)만큼 시계 방향으로 90° 돌린다(1이면 ▾).
        /// 글꼴마다 기호 크기가 달라 도형으로 그린다.
        /// </summary>
        private static void DrawDisclosure(DrawingContext g, double cx, double cy, double open)
        {
            double cos = open <= 0 ? 1 : open >= 1 ? 0 : Math.Cos(open * Math.PI / 2);
            double sin = open <= 0 ? 0 : open >= 1 ? 1 : Math.Sin(open * Math.PI / 2);
            Avalonia.Point At(double x, double y) => new(cx + x * cos - y * sin, cy + x * sin + y * cos);
            var geo = new StreamGeometry();
            using (var c = geo.Open())
            {
                c.BeginFigure(At(-3, -5), true);
                c.LineTo(At(4, 0));
                c.LineTo(At(-3, 5));
                c.EndFigure(true);
            }
            g.DrawGeometry(AppTheme.TextBrush, null, geo);
        }

        private static readonly IBrush GhostHoverBrush = AppTheme.Brush(Color.FromRgb(74, 74, 84));

        /// <summary>"다음 프레임" 칸 단추 하나: 칸 가운데 아이콘(⧉ 복사 또는 ＋ 빈 프레임), 올라가 있으면 밝은 바탕.</summary>
        private static void DrawGhostButton(DrawingContext g, Rect r, VectorIcon icon, bool hovered)
        {
            if (hovered) g.FillRectangle(GhostHoverBrush, r);
            VectorIcons.Draw(g, icon, r, 12, hovered ? AppTheme.TextBrush : AppTheme.DimTextBrush);
        }

        /// <summary>배치의 행이 가리키는 레이어 번호(지워져 줄어드는 유령 행은 -1).</summary>
        private int LayerOf(SpriteProject p, Track.View row)
        {
            if (row.Index < 0) return -1;
            int l = RowItem(row.Index);
            return (uint)l < (uint)p.Layers.Count && ReferenceEquals(p.Layers[l], row.Item) ? l : p.IndexOf((Layer)row.Item);
        }

        /// <summary>배치의 열이 가리키는 프레임 번호(지워져 줄어드는 유령 열은 -1).</summary>
        private static int FrameOf(SpriteProject p, Track.View col)
            => (uint)col.Index < (uint)p.Frames.Count && ReferenceEquals(p.Frames[col.Index], col.Item) ? col.Index : -1;

        /// <summary>
        /// 행·열은 배치(<see cref="Track"/>)를 따라 그린다. 자라거나 줄어드는 칸은 그 크기만큼 잘라 보이고, 내용은 칸의 아래·오른쪽 끝에
        /// 붙여 그려 앞 칸 밑에서 밀려 나오고 들어가듯 보인다.
        /// </summary>
        private void Paint(DrawingContext g)
        {
            double width = ClientW, height = ClientH;
            g.FillRectangle(AppTheme.InputBrush, new Rect(Bounds.Size));
            if (Ctx.Project is not { } p) return;
            int frames = p.Frames.Count;
            int cur = Ctx.CurrentFrame, curLayer = Ctx.CurrentLayer;
            var (ra, rb) = Range;
            int top = TagH + HeadH - VValue, left = NameW - HValue;
            var cellsClip = new Rect(NameW, TagH + HeadH, Math.Max(0, width - NameW), Math.Max(0, height - TagH - HeadH));
            var headerClip = new Rect(NameW, 0, Math.Max(0, width - NameW), TagH + HeadH);
            var namesClip = new Rect(0, TagH + HeadH, NameW, Math.Max(0, height - TagH - HeadH));

            // 선택 바탕: 셀 묶음(두 칸 이상)은 고른 칸을 진하게, 프레임·레이어 선택은 그 열·행 칸을 옅게. 테두리는 아래에서 덩어리째 그린다.
            var selRows = _kind == SelKind.Cels ? SelectedRowLayers() : [];
            var selRowSet = new HashSet<int>(selRows);
            bool multiCel = _kind == SelKind.Cels && (selRows.Count > 1 || (ra >= 0 && rb > ra));
            IBrush? CellTint(int layer, int frame) => _kind switch
            {
                SelKind.Frames => _selFrames.Contains(frame) ? LightSelBrush : null,
                SelKind.Layers => _selLayers.Contains(p.Layers[layer]) ? LightSelBrush : null,
                _ => multiCel && selRowSet.Contains(layer) && (ra >= 0 ? frame >= ra && frame <= rb : frame == cur) ? RangeBrush : null,
            };

            // 셀 하나(그룹 행은 그룹 합성 썸네일, 지워져 줄어드는 행·열은 빈 칸)
            void DrawCell(int x, int y, int l, int f)
            {
                var r = new Rect(x + 1, y + 1, CellW - 2, CellH - 2);
                if (l < 0 || f < 0)
                {
                    g.FillRectangle(AppTheme.PanelBrush, r);
                    g.DrawRectangle(null, BorderPen, r.Deflate(0.5));
                    return;
                }
                g.FillRectangle(f == cur ? AppTheme.HoverBrush : AppTheme.PanelBrush, r);
                if (CellTint(l, f) is { } tint) g.FillRectangle(tint, r);
                var thumb = Ctx.CelThumbnail(l, f);
                if (thumb.Bitmap is { } bmp)
                {
                    var size = new Size(thumb.SourceWidth, thumb.SourceHeight);
                    // 지금 스킨에 자기 그림이 없어 기본 스킨 셀을 물려받은 칸은 흐리게
                    Rect shown;
                    if (p.IsCelInherited(l, f))
                    {
                        using (g.PushOpacity(InheritedOpacity)) shown = ImageUtil.DrawFittedThumb(g, bmp, size, r.Deflate(3), checker: true);
                    }
                    else shown = ImageUtil.DrawFittedThumb(g, bmp, size, r.Deflate(3), checker: true);
                    // 작업 스레드에서 다시 계산하는 중이면 이전 그림 위에 회색 덮개
                    if (thumb.Overlay) RenderOverlay.DrawVeil(g, shown);
                }
                else
                    g.DrawEllipse(AppTheme.BorderBrush, null, new Rect(r.X + r.Width / 2 - 3, r.Y + r.Height / 2 - 3, 6, 6));
                // 파란 테두리는 셀 선택일 때만: 프레임·레이어·효과를 고른 동안은 그 선택 외곽선이 파랗고, 현재 칸은 흐린 흰 테두리로만 표시
                if (l == curLayer && f == cur) g.DrawRectangle(null, _kind == SelKind.Cels ? AccentPen : CurrentCelPen, r.Deflate(_kind == SelKind.Cels ? 1 : 0.5));
                else g.DrawRectangle(null, BorderPen, r.Deflate(0.5));
            }

            // 효과 줄 하나(레이어 아래, 효과 하나): 프레임 칸 바탕 + 효과가 입혀지는 프레임에 걸친 둥근 상자(종류마다 색, 꺼진 효과는 흐리게).
            void DrawFxRow(int ry, Layer layer, int k)
            {
                for (int ck = 0; ck < _colT.SlotCount; ck++)
                {
                    var col = _colT.Get(ck);
                    int x = left + col.Pos;
                    if (col.Px <= 0 || x + col.Px < cellsClip.Left || x > cellsClip.Right) continue;
                    g.FillRectangle(FrameOf(p, col) == cur ? FxCurBrush : FxCellBrush, new Rect(x + 1, ry, Math.Max(0, col.Px - 2), FxH - 1));
                }
                if (k >= layer.Effects.Count || frames == 0) return;
                bool editing = ReferenceEquals(_fxLayer, layer) && _fxIndex == k;
                var effect = editing && _fxDragged is { } dragged ? dragged : layer.Effects[k];
                var (x0, x1) = FxX(effect, frames);
                if (x1 - x0 < 1) return;
                var box = new Rect(x0, ry + 1, x1 - x0, FxH - 3);
                var color = LayerEffectsDialog.TimelineColorOf(effect);
                bool hovered = editing || (_fxHover is { } h && ReferenceEquals(h.Layer, layer) && h.Index == k);
                IBrush ink = IsBright(color) ? Brushes.Black : Brushes.White;
                using (g.PushOpacity(effect.Enabled ? 1 : 0.4))
                {
                    g.DrawRectangle(AppTheme.Brush(color), editing ? WhitePen : null, box, 3, 3);
                    if (hovered)
                    {
                        // 끝 손잡이(끌어서 범위 바꾸기)
                        g.FillRectangle(ink, new Rect(box.X + 2, box.Y + 3, 2, box.Height - 6));
                        g.FillRectangle(ink, new Rect(box.Right - 4, box.Y + 3, 2, box.Height - 6));
                    }
                    double textX = box.X + 7;
                    if (effect.ClipToPrevious && box.Width >= 28)
                    {
                        VectorIcons.Draw(g, VectorIcon.CornerDownRight, new Rect(textX, box.Y, 12, box.Height), 10, ink);
                        textX += 13;
                    }
                    // 그리지 못하는 스크립트 효과(원문 없음·실행 안 함·오류·실패): 상자 오른쪽 끝에 경고 표시
                    bool scriptProblem = effect is ScriptEffect script && ScriptEffectRuntime.StatusOf(layer, script).IsProblem;
                    double textRight = box.Right - 7;
                    if (scriptProblem && box.Width >= 16)
                    {
                        g.FillRectangle(AppTheme.BackBrush, new Rect(box.Right - 14, box.Y + 1, 13, box.Height - 2), 3);
                        VectorIcons.Draw(g, VectorIcon.CircleAlert, new Rect(box.Right - 13.5, box.Y, 12, box.Height), 10, AppTheme.WarningBrush);
                        textRight = box.Right - 15;
                    }
                    var textArea = new Rect(textX, box.Y, Math.Max(0, textRight - textX), box.Height);
                    if (textArea.Width >= AppTheme.MeasureText(effect.DisplayName, AppTheme.SmallFontSize).Width)
                        Text(g, effect.DisplayName, textArea, ink, small: true);
                }
                // 고른 효과: 상자 둘레에 파란 테두리
                if (IsEffectSelected(layer, k)) g.DrawRectangle(null, AccentPen, box.Inflate(1.5), 4, 4);
            }

            using (g.PushClip(cellsClip))
            {
                for (int rk = 0; rk < _rowT.SlotCount; rk++)
                {
                    var row = _rowT.Get(rk);
                    int y = top + row.Pos;
                    if (row.Px <= 0 || y + row.Px < cellsClip.Top || y > cellsClip.Bottom) continue;
                    // 줄어드는 중이라 본줄보다 작으면 본줄을 아래 끝에 붙여(앞 행 밑에서 밀려 나오듯), 아니면 위에 붙이고 효과 줄은 그 아래
                    int l = LayerOf(p, row), cy = row.Px >= CellH ? y : y + row.Px - CellH;
                    for (int ck = 0; ck < _colT.SlotCount; ck++)
                    {
                        var col = _colT.Get(ck);
                        int x = left + col.Pos;
                        if (col.Px <= 0 || x + col.Px < cellsClip.Left || x > cellsClip.Right) continue;
                        int cx = x + col.Px - CellW, f = FrameOf(p, col);
                        if (row.Px < CellH || col.Px < CellW)
                        {
                            using (g.PushClip(new Rect(x, y, col.Px, row.Px))) DrawCell(cx, cy, l, f);
                        }
                        else DrawCell(cx, cy, l, f);
                    }
                    // "다음 프레임" 칸: ⧉ 마지막 프레임 복사해서 추가 · ＋ 빈 프레임 추가
                    int gx = left + _colT.Total;
                    if (gx < cellsClip.Right)
                    {
                        using (g.PushClip(new Rect(gx, y, CellW, row.Px)))
                        {
                            var r = new Rect(gx + 1, cy + 1, CellW - 2, CellH - 2);
                            g.FillRectangle(GhostBrush, r);
                            // 어느 줄의 ⧉든 프레임 전체를 복사하므로 열의 ⧉를 함께 강조
                            DrawGhostButton(g, r, VectorIcon.Copy, _hover is { Frame: var hf, Header: false } && hf == frames);
                        }
                    }
                    // 효과 줄(접거나 펼치는 중이면 보이는 만큼)
                    var rowLayer = (Layer)row.Item;
                    int fxRows = row.Px > CellH ? Math.Min(rowLayer.Effects.Count, (row.Px - CellH + FxH - 1) / FxH) : 0;
                    if (fxRows > 0)
                    {
                        using (g.PushClip(new Rect(NameW, y, Math.Max(0, width - NameW), row.Px)))
                            // 끌어서 순서 바꾸는 중이면 밀린 자리에, 뜬 줄은 맨 나중(위)에
                            for (int pass = 0; pass < 2; pass++)
                                for (int k = 0; k < fxRows; k++)
                                    if (FxShiftOf(rowLayer, k) is var (s, lifted) && lifted == (pass == 1))
                                        DrawFxRow(cy + CellH + k * FxH + s, rowLayer, k);
                    }
                }
                // "다음 레이어" 줄(마지막 행 아래): 프레임 칸 전체가 ⧉ 한 단추 = 마지막 레이어 복사해서 추가. 아이콘은 10칸마다(1·11·21… 번째 칸),
                // 그중 보이는 것이 없으면(좁은 화면을 스크롤) 처음으로 다 보이는 칸에도. "다음 프레임" 열과 만나는 칸은 비운다.
                {
                    int gy = top + _rowT.Total, x0 = FrameX(0), x1 = left + _colT.Total;
                    if (frames > 0 && gy < cellsClip.Bottom && x0 < cellsClip.Right && x1 > cellsClip.Left)
                    {
                        bool hovered = _rowHover == GhostRowButton.Copy;
                        g.FillRectangle(hovered ? GhostHoverBrush : GhostBrush, new Rect(x0 + 1, gy + 1, Math.Max(0, x1 - x0 - 2), CellH - 2));
                        var ink = hovered ? AppTheme.TextBrush : AppTheme.DimTextBrush;
                        Rect IconCell(int f) => new(FrameX(f) + FrameW(f) - CellW + 1, gy + 1, CellW - 2, CellH - 2);
                        bool anyShown = false;
                        for (int f = 0; f < frames; f += GhostCopyIconEvery)
                        {
                            var r = IconCell(f);
                            if (r.Right <= cellsClip.Left || r.Left >= cellsClip.Right) continue;
                            VectorIcons.Draw(g, VectorIcon.Copy, r, 12, ink);
                            anyShown |= r.Left >= cellsClip.Left && r.Right <= cellsClip.Right;
                        }
                        if (!anyShown)
                        {
                            int f = Math.Clamp(_colT.IndexAt(ContentX(cellsClip.Left), exact: false), 0, frames - 1);
                            if (FrameX(f) < cellsClip.Left && f + 1 < frames) f++;
                            if (f % GhostCopyIconEvery != 0) VectorIcons.Draw(g, VectorIcon.Copy, IconCell(f), 12, ink);
                        }
                    }
                }
                // 셀 묶음(두 칸 이상) 둘레의 파란 테두리(한 칸이면 현재 칸 테두리가 그 표시)
                if (multiCel && selRows.Count > 0)
                {
                    int fa = ra >= 0 ? ra : cur, fb = ra >= 0 ? rb : cur;
                    double x0 = FrameX(fa) + 1, x1 = FrameX(fb) + FrameW(fb) - 1;
                    double y0 = LayerY(selRows[0]) + 1, y1 = LayerY(selRows[^1]) + CellH - 1;
                    g.DrawRectangle(null, AccentPen, new Rect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0)));
                }
                // 셀 묶음 끌기: 놓을 자리
                if (_blockRows is not null && _blockDrop.Row >= 0)
                {
                    var rows = Rows();
                    for (int i = 0; i < _blockRows.Count && _blockDrop.Row + i < rows.Count; i++)
                        for (int k = 0; k < _blockCount; k++)
                        {
                            var r = new Rect(FrameX(_blockDrop.Frame + k) + 2, LayerY(rows[_blockDrop.Row + i]) + 2, CellW - 4, CellH - 4);
                            g.DrawRectangle(RangeBrush, AccentPen, r);
                        }
                }
                // 재생 중인 프레임: 열 전체(셀들)를 초록 외곽선 네모로(머리글 칸도 같은 색으로 이어서 그린다).
                if (_playing >= 0 && _playing < p.Frames.Count && Rows() is { Count: > 0 } playRows)
                {
                    int last = playRows[^1];
                    double bottom = Math.Min(cellsClip.Bottom, LayerY(last) + _rowT.Px(RowItem(last)));
                    double l = FrameX(_playing) + 1, r = FrameX(_playing) + FrameW(_playing) - 1, b = bottom - 1;
                    // 위쪽은 머리글 칸 네모와 이어지게 열어 둔다.
                    g.DrawLine(PlayPen, new Avalonia.Point(l, cellsClip.Top - 2), new Avalonia.Point(l, b));
                    g.DrawLine(PlayPen, new Avalonia.Point(r, cellsClip.Top - 2), new Avalonia.Point(r, b));
                    g.DrawLine(PlayPen, new Avalonia.Point(l - 1, b), new Avalonia.Point(r + 1, b));
                }
                // 고른 프레임: 이어진 열마다 머리글부터 마지막 행까지 파란 외곽선 네모(재생 중 초록 네모와 같은 모양, 위쪽은 머리글 쪽에서 그린다).
                if (_kind == SelKind.Frames && !_reordering && Rows() is { Count: > 0 } selRowsAll)
                {
                    int last = selRowsAll[^1];
                    double b = Math.Min(cellsClip.Bottom, LayerY(last) + _rowT.Px(RowItem(last))) - 1;
                    foreach (var (fa, fb) in Runs(_selFrames))
                    {
                        double l = FrameX(fa) + 1, r = FrameX(fb) + FrameW(fb) - 1;
                        g.DrawLine(AccentPen, new Avalonia.Point(l, cellsClip.Top - 2), new Avalonia.Point(l, b));
                        g.DrawLine(AccentPen, new Avalonia.Point(r, cellsClip.Top - 2), new Avalonia.Point(r, b));
                        g.DrawLine(AccentPen, new Avalonia.Point(l - 1, b), new Avalonia.Point(r + 1, b));
                    }
                }
            }

            // 프레임 머리글 한 칸(지워져 줄어드는 열은 비움)
            void DrawHeader(int x, int f)
            {
                if (f < 0) return;
                var r = new Rect(x, TagH, CellW, HeadH);
                if (f == cur) g.FillRectangle(AppTheme.HoverBrush, r);
                if (_kind == SelKind.Frames && _selFrames.Contains(f)) g.FillRectangle(RangeBrush, r);
                Text(g, (f + 1).ToString(), new Rect(x, TagH, CellW, 16), f == cur ? AppTheme.AccentBrush : AppTheme.TextBrush, bold: true, center: true);
                Text(g, p.Frames[f].DurationMs + "ms", new Rect(x, TagH + 15, CellW, 14), AppTheme.DimTextBrush, small: true, center: true);
                if (p.Frames[f].Pivot is not null)
                    VectorIcons.Draw(g, VectorIcon.Diamond, new Rect(x + CellW - 12, TagH + 2, 10, 10), 9, PivotBrush, filled: true);
                if (f == _playing)
                {
                    // 재생 중인 열 네모의 윗부분(아래쪽은 셀 쪽에서 이어 그린다).
                    double l = x + 1, rr = x + CellW - 1, top = TagH + 1;
                    g.DrawLine(PlayPen, new Avalonia.Point(l - 1, top), new Avalonia.Point(rr + 1, top));
                    g.DrawLine(PlayPen, new Avalonia.Point(l, top), new Avalonia.Point(l, TagH + HeadH + 2));
                    g.DrawLine(PlayPen, new Avalonia.Point(rr, top), new Avalonia.Point(rr, TagH + HeadH + 2));
                }
            }

            // 태그 띠 + 프레임 머리글
            using (g.PushClip(headerClip))
            {
                g.FillRectangle(AppTheme.PanelBrush, headerClip);
                foreach (var tag in p.Tags)
                {
                    var (tf, tt) = TagRange(tag);
                    int x0 = FrameX(tf), x1 = FrameX(tt) + FrameW(tt);
                    var r = new Rect(x0 + 1, 2, Math.Max(0, x1 - x0 - 2), TagH - 4);
                    var color = tag.Color.ToColor();
                    g.FillRectangle(AppTheme.Brush(color), r);
                    if (ReferenceEquals(tag, Ctx.ActiveTag) || ReferenceEquals(tag, _tagEdit)) g.DrawRectangle(null, WhitePen, r.Deflate(0.5));
                    if (ReferenceEquals(tag, _tagHover) || ReferenceEquals(tag, _tagEdit))
                    {
                        // 끝 손잡이(끌어서 범위 바꾸기)
                        var grip = IsBright(color) ? Brushes.Black : Brushes.White;
                        g.FillRectangle(grip, new Rect(r.X + 2, r.Y + 3, 2, r.Height - 6));
                        g.FillRectangle(grip, new Rect(r.Right - 4, r.Y + 3, 2, r.Height - 6));
                    }
                    // 방향 표시(역방향 = 왼쪽 화살표, 왕복 = 양쪽 화살표) + 이름
                    var ink = IsBright(color) ? Brushes.Black : Brushes.White;
                    VectorIcon? arrow = tag.Direction switch { TagDirection.Reverse => VectorIcon.ArrowLeft, TagDirection.PingPong => VectorIcon.MoveHorizontal, _ => null };
                    var textRect = r.Deflate(new Thickness(2, 0));
                    if (arrow is { } a)
                    {
                        using (g.PushClip(r))
                            VectorIcons.Draw(g, a, new Rect(textRect.X, r.Y, 11, r.Height), 10, ink);
                        textRect = new Rect(textRect.X + 14, textRect.Y, Math.Max(0, textRect.Width - 14), textRect.Height);
                    }
                    Text(g, tag.Name + (tag.Loop ? "" : UiStrings.S_TimelinePanel_Paint_6A31D51450), textRect, ink, small: true);
                }
                for (int ck = 0; ck < _colT.SlotCount; ck++)
                {
                    var col = _colT.Get(ck);
                    int x = left + col.Pos;
                    if (col.Px <= 0 || x + col.Px < headerClip.Left || x > headerClip.Right) continue;
                    int f = FrameOf(p, col);
                    if (col.Px < CellW)
                    {
                        using (g.PushClip(new Rect(x, TagH, col.Px, HeadH))) DrawHeader(x + col.Px - CellW, f);
                    }
                    else DrawHeader(x, f);
                }
                {
                    var r = new Rect(FrameX(frames) + 1, TagH + 1, CellW - 2, HeadH - 2);
                    g.FillRectangle(GhostBrush, r);
                    DrawGhostButton(g, r, VectorIcon.Plus, _hover is { Frame: var hf, Header: true } && hf == frames);
                }
                // 태그는 머리글 칸까지 내려오는 괄호로 묶어 보인다.
                foreach (var tag in p.Tags)
                {
                    var pen = AppTheme.Pen(tag.Color.ToColor(), 2);
                    var (tf, tt) = TagRange(tag);
                    double x0 = FrameX(tf) + 1.5, x1 = FrameX(tt) + FrameW(tt) - 1.5, top0 = TagH - 2, bottom = TagH + HeadH - 1;
                    g.DrawLine(pen, new Avalonia.Point(x0, top0), new Avalonia.Point(x0, bottom));
                    g.DrawLine(pen, new Avalonia.Point(x1, top0), new Avalonia.Point(x1, bottom));
                    g.DrawLine(pen, new Avalonia.Point(x0, bottom), new Avalonia.Point(x0 + 5, bottom));
                    g.DrawLine(pen, new Avalonia.Point(x1 - 5, bottom), new Avalonia.Point(x1, bottom));
                }
                // 고른 프레임: 이어진 머리글마다 파란 테두리의 윗부분(아래쪽은 셀 쪽에서 마지막 행까지 이어 그린다)
                if (_kind == SelKind.Frames && !_reordering)
                    foreach (var (a, b) in Runs(_selFrames))
                    {
                        double x0 = FrameX(a) + 1, x1 = FrameX(b) + FrameW(b) - 1, y0 = TagH + 1;
                        g.DrawLine(AccentPen, new Avalonia.Point(x0 - 1, y0), new Avalonia.Point(x1 + 1, y0));
                        g.DrawLine(AccentPen, new Avalonia.Point(x0, y0), new Avalonia.Point(x0, TagH + HeadH + 2));
                        g.DrawLine(AccentPen, new Avalonia.Point(x1, y0), new Avalonia.Point(x1, TagH + HeadH + 2));
                    }
            }

            // 레이어 이름 한 줄(지워져 줄어드는 유령 행은 흐리게)
            void DrawName(int y, int l, Layer layer, double open)
            {
                bool shown = l >= 0 && p.IsLayerVisible(l);
                var r = new Rect(0, y, NameW, CellH);
                g.FillRectangle(l == curLayer ? AppTheme.HoverBrush : AppTheme.PanelBrush, r);
                if (_kind == SelKind.Layers && _selLayers.Contains(layer)) g.FillRectangle(RangeBrush, r);
                g.DrawLine(BorderPen, new Avalonia.Point(0, y + CellH - 0.5), new Avalonia.Point(NameW, y + CellH - 0.5));
                VectorIcons.Draw(g, layer.Visible ? VectorIcon.Eye : VectorIcon.EyeOff, new Rect(2, y, 22, CellH), 15,
                    shown ? AppTheme.TextBrush : AppTheme.DimTextBrush);
                VectorIcons.Draw(g, layer.Locked ? VectorIcon.Lock : VectorIcon.LockOpen, new Rect(24, y, 22, CellH), 13,
                    layer.Locked ? AppTheme.WarningBrush : AppTheme.BorderBrush);
                if (layer.IsGroup)
                    DrawDisclosure(g, (ToggleX + NameX) / 2.0, y + CellH / 2.0, open);
                else if (layer.IsPart)
                {
                    // 그룹에서 내려오는 안내선
                    double gx = ToggleX + (NameX - ToggleX) / 2.0 + 0.5;
                    g.DrawLine(BorderPen, new Avalonia.Point(gx, y), new Avalonia.Point(gx, y + CellH));
                    g.DrawLine(BorderPen, new Avalonia.Point(gx, y + CellH / 2.0), new Avalonia.Point(NameX + Indent - 4, y + CellH / 2.0));
                }
                int nameX = NameLeft(layer), right = NameW - 2;
                // 스킨 칩은 그룹·부위 모두 이름 칸 오른쪽 끝(같은 열), 부위 크기 글자는 그 왼쪽.
                if (SkinChip(layer, right - 2) is { W: > 0 } chip)
                {
                    DrawSkinChip(g, layer, new Rect(chip.X, y + 9, chip.W, CellH - 18));
                    right = chip.X - 4;
                }
                // 자동 배치가 켜진 그룹: 스킨 칩 왼쪽에 ◆(누르면 설정)
                if (LayoutBadge(layer) is { W: > 0 } lb)
                {
                    DrawLayoutBadge(g, new Rect(lb.X, y, lb.W, CellH), ReferenceEquals(_layoutBadgeHover, layer));
                    right = lb.X - 2;
                }
                if (layer.IsPart)
                {
                    var (size, sw) = PartSizeText(layer);
                    right -= sw;
                    Text(g, size, new Rect(right, y, sw, CellH), AppTheme.DimTextBrush, small: true);
                }
                if (FxBadge(layer) is { W: > 0 } fx)
                {
                    right = fx.X;
                    // 효과 줄이 펼쳐져 있으면 눌린 단추처럼(누르면 접고 펼침)
                    if (!layer.EffectsCollapsed && layer.Effects.Count > 0)
                        g.DrawRectangle(ChipAccentBrush, null, new Rect(fx.X + 1, y + 10, fx.W - 2, CellH - 20), 3, 3);
                    Text(g, "fx", new Rect(fx.X, y, fx.W, CellH), layer.HasActiveEffects ? AppTheme.AccentBrush : AppTheme.DimTextBrush, small: true, bold: true, center: true);
                }
                var name = LayerTitle(layer);
                Text(g, name, new Rect(nameX, y, Math.Max(1, right - nameX), CellH), shown ? AppTheme.TextBrush : AppTheme.DimTextBrush,
                    bold: l == curLayer || layer.IsGroup);
            }

            // 효과 줄 이름 칸: 종류 색 띠 · 켜기 체크 상자(누르면 켜고 끔) · 효과 이름(범위가 있으면 프레임 번호)
            void DrawFxName(int ry, Layer layer, int k, bool last)
            {
                g.FillRectangle(FxNameBrush, new Rect(0, ry, NameW, FxH));
                bool selected = IsEffectSelected(layer, k);
                if (selected) g.FillRectangle(RangeBrush, new Rect(0, ry, NameW, FxH));
                if (layer.IsPart)
                {
                    double gx = ToggleX + (NameX - ToggleX) / 2.0 + 0.5;
                    g.DrawLine(BorderPen, new Avalonia.Point(gx, ry), new Avalonia.Point(gx, ry + FxH));
                }
                if (last) g.DrawLine(BorderPen, new Avalonia.Point(0, ry + FxH - 0.5), new Avalonia.Point(NameW, ry + FxH - 0.5));
                if (k >= layer.Effects.Count) return;
                var effect = layer.Effects[k];
                int x = FxCheckX(layer);
                g.FillRectangle(AppTheme.Brush(LayerEffectsDialog.TimelineColorOf(effect)), new Rect(x - 7, ry + 3, 3, FxH - 6));
                var check = new Rect(x + 0.5, ry + 3.5, 9, 9);
                g.DrawRectangle(effect.Enabled ? AppTheme.AccentBrush : null, BorderPen, check, 2, 2);
                if (effect.Enabled)
                {
                    g.DrawLine(FxCheckPen, new Avalonia.Point(check.X + 2, check.Y + 4.5), new Avalonia.Point(check.X + 4, check.Y + 6.5));
                    g.DrawLine(FxCheckPen, new Avalonia.Point(check.X + 4, check.Y + 6.5), new Avalonia.Point(check.X + 7.5, check.Y + 2.5));
                }
                double labelX = x + 15;
                if (effect.ClipToPrevious)
                {
                    VectorIcons.Draw(g, VectorIcon.CornerDownRight, new Rect(labelX, ry, 12, FxH), 10,
                        effect.Enabled ? AppTheme.TextBrush : AppTheme.DimTextBrush);
                    labelX += 14;
                }
                var label = effect.DisplayName + (effect.Frames is { } fr ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_Paint_5AB8DD426F,fr) : "");
                double labelRight = NameW - 4;
                if (effect is ScriptEffect script && ScriptEffectRuntime.StatusOf(layer, script).IsProblem)
                {
                    VectorIcons.Draw(g, VectorIcon.CircleAlert, new Rect(NameW - 17, ry, 12, FxH), 10, AppTheme.WarningBrush);
                    labelRight = NameW - 19;
                }
                Text(g, label, new Rect(labelX, ry, Math.Max(1, labelRight - labelX), FxH), effect.Enabled ? AppTheme.TextBrush : AppTheme.DimTextBrush, small: true);
                if (selected) g.DrawRectangle(null, AccentPen, new Rect(1, ry + 1, NameW - 3, FxH - 2));
            }

            // 레이어 행 하나: 본줄 + 효과 줄(줄어드는 중이라 본줄보다 작으면 본줄을 아래 끝에 붙임)
            void DrawNameBlock(int y, Track.View row, int l, Layer layer)
            {
                int ny = row.Px >= CellH ? y : y + row.Px - CellH;
                int fxRows = row.Px > CellH ? Math.Min(layer.Effects.Count, (row.Px - CellH + FxH - 1) / FxH) : 0;
                DrawName(ny, l, layer, row.Open);
                for (int pass = 0; pass < 2; pass++)
                    for (int k = 0; k < fxRows; k++)
                        if (FxShiftOf(layer, k) is var (s, lifted) && lifted == (pass == 1))
                            DrawFxName(ny + CellH + k * FxH + s, layer, k, k == fxRows - 1);
            }

            // 레이어 이름 열
            g.FillRectangle(AppTheme.PanelBrush, new Rect(0, 0, NameW, TagH + HeadH));
            Text(g, UiStrings.S_TimelinePanel_Paint_E44BCB02E8, new Rect(4, TagH, NameW - 8, HeadH), AppTheme.DimTextBrush, small: true);
            using (g.PushClip(namesClip))
            {
                for (int rk = 0; rk < _rowT.SlotCount; rk++)
                {
                    var row = _rowT.Get(rk);
                    int y = top + row.Pos;
                    if (row.Px <= 0 || y + row.Px < namesClip.Top || y > namesClip.Bottom) continue;
                    int l = LayerOf(p, row);
                    var layer = (Layer)row.Item;
                    if (row.Px < Math.Max(CellH, row.Full))
                    {
                        using (g.PushClip(new Rect(0, y, NameW, row.Px))) DrawNameBlock(y, row, l, layer);
                    }
                    else DrawNameBlock(y, row, l, layer);
                }
                // "다음 레이어" 줄: 이름 칸 전체가 ＋ 단추 = 맨 아래에 빈 레이어 추가
                {
                    int gy = top + _rowT.Total;
                    if (gy < namesClip.Bottom)
                    {
                        var r = new Rect(1, gy + 1, NameW - 3, CellH - 2);
                        g.FillRectangle(GhostBrush, r);
                        DrawGhostButton(g, r, VectorIcon.Plus, _rowHover == GhostRowButton.Add);
                    }
                }
                // 고른 레이어: 이어진 이름 칸마다 파란 테두리(효과 줄 포함)
                if (_kind == SelKind.Layers && !_reordering)
                {
                    var rows = Rows();
                    foreach (var (a, b) in Runs(Enumerable.Range(0, rows.Count).Where(i => _selLayers.Contains(p.Layers[rows[i]]))))
                    {
                        double y0 = LayerY(rows[a]) + 1, y1 = LayerY(rows[b]) + _rowT.Px(RowItem(rows[b])) - 1;
                        g.DrawRectangle(null, AccentPen, new Rect(1, y0, NameW - 3, Math.Max(0, y1 - y0)));
                    }
                }
            }
            g.DrawLine(BorderPen, new Avalonia.Point(NameW - 0.5, 0), new Avalonia.Point(NameW - 0.5, height));
            g.DrawLine(BorderPen, new Avalonia.Point(0, TagH + HeadH - 0.5), new Avalonia.Point(width, TagH + HeadH - 0.5));
            // 끌어서 순서 바꾸기: 뜬(포인터를 따라오는) 열·행을 강조색으로 덮어 들고 있는 칸임을 보인다. 다른 칸은 비켜 선 자리에 그려져 있다.
            if (_reordering && _dragFrom >= 0)
            {
                double x0 = FrameX(_dragA), x1 = FrameX(_dragB) + FrameW(_dragB), bottom = Math.Min(height, top + _rowT.Total);
                using (g.PushClip(new Rect(NameW, 0, Math.Max(0, width - NameW), height)))
                    g.DrawRectangle(RangeBrush, AccentPen, new Rect(x0 + 1, TagH + 1, Math.Max(0, x1 - x0 - 2), Math.Max(0, bottom - TagH - 2)));
            }
            if (_reordering && _layerDragFrom >= 0 && _liftRows.Count > 0)
            {
                double y0 = top + _liftRows.Min(_rowT.Pos), h = _liftRows.Sum(_rowT.Px);
                using (g.PushClip(new Rect(0, TagH + HeadH, width, Math.Max(0, height - TagH - HeadH))))
                    g.DrawRectangle(RangeBrush, AccentPen, new Rect(1, y0 + 1, Math.Max(0, width - 2), Math.Max(0, h - 2)));
            }
            // 효과 이름 끌기: 옮길 효과 줄을 강조색으로 덮고(같은 레이어 안이면 포인터를 따라옴), 다른 레이어에 놓을 자리는 파란 선으로
            if (_fxReordering && _fxDragLayer is { } fxLayer && p.IndexOf(fxLayer) is var fli && fli >= 0)
            {
                using (g.PushClip(new Rect(0, TagH + HeadH, width, Math.Max(0, height - TagH - HeadH))))
                {
                    double y0 = LayerY(fli) + CellH + _fxDragA * FxH + _fxLift;
                    g.DrawRectangle(RangeBrush, AccentPen, new Rect(1, y0 + 1, Math.Max(0, width - 2), (_fxDragB - _fxDragA + 1) * FxH - 2));
                    if (_fxDropOther is { } drop && p.IndexOf(drop.Layer) is var ti && ti >= 0)
                    {
                        g.FillRectangle(RangeBrush, new Rect(0, LayerY(ti), NameW, CellH));
                        double ly = LayerY(ti) + CellH + (drop.Layer.EffectsCollapsed ? 0 : drop.Index * FxH);
                        g.DrawLine(AccentPen, new Avalonia.Point(0, ly), new Avalonia.Point(width, ly));
                    }
                }
            }
            // 사각형 끌어 고르기: 누른 곳에서 포인터까지(머리글은 머리글 줄 높이, 이름 칸은 이름 칸 폭으로) 옅은 파란 사각형
            if (_marquee is { } mk && _marqueeShown)
            {
                var area = mk switch
                {
                    SelKind.Frames => new Rect(NameW, TagH, Math.Max(0, width - NameW), HeadH),
                    SelKind.Layers => namesClip,
                    _ => cellsClip,
                };
                double ax = mk == SelKind.Layers ? 0 : _marqueeFrom.X - HValue + NameW, bx = mk == SelKind.Layers ? NameW : _lastPointer.X;
                double ay = mk == SelKind.Frames ? TagH : _marqueeFrom.Y - VValue + TagH + HeadH, by = mk == SelKind.Frames ? TagH + HeadH : _lastPointer.Y;
                var rect = new Rect(Math.Min(ax, bx), Math.Min(ay, by), Math.Abs(bx - ax), Math.Abs(by - ay)).Intersect(area);
                if (rect.Width > 1 && rect.Height > 1) g.DrawRectangle(MarqueeBrush, MarqueePen, rect.Deflate(0.5));
            }
        }

        /// <summary>오름차순 번호를 이어진 구간 [A, B]들로 묶는다.</summary>
        private static IEnumerable<(int A, int B)> Runs(IEnumerable<int> sorted)
        {
            int a = -1, b = -1;
            foreach (int i in sorted)
            {
                if (a >= 0 && i == b + 1) { b = i; continue; }
                if (a >= 0) yield return (a, b);
                a = b = i;
            }
            if (a >= 0) yield return (a, b);
        }

        /// <summary>스크롤 막대·이름 상자에서 온 이벤트는 격자가 처리하지 않는다.</summary>
        private bool FromChild(RoutedEventArgs e) => !ReferenceEquals(e.Source, this);

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (FromChild(e)) return;
            EndRename(true);
            Focus();
            if (Ctx.Project is not { } p) return;
            e.Handled = true;
            var pt = e.GetCurrentPoint(this);
            var pos = pt.Position;
            bool left = pt.Properties.IsLeftButtonPressed, right = pt.Properties.IsRightButtonPressed;
            _press = pos;
            if (left && e.ClickCount == 1 && TagEdgeAt(pos) is { } edge)
            {
                _tagEdit = edge.Tag;
                _tagEditRight = edge.Right;
                (_tagFrom, _tagTo) = (edge.Tag.From, edge.Tag.To);
                Ctx.SelectedTag = edge.Tag;
                e.Pointer.Capture(this);
                InvalidateVisual();
                return;
            }
            if (TagAt(pos) is { } tag)
            {
                Ctx.SelectedTag = tag;
                if (right) { _owner.ShowTagMenu(tag); return; }
                if (e.ClickCount == 2) { _owner.EditTag(tag); return; }
                SelectFrameRange(tag.From, tag.To);
                _frameAnchor = tag.From;
                Ctx.SetCurrentFrame(tag.From);
                _owner.HintSelection();
                return;
            }
            int f = FrameAt(pos.X), l = LayerAt(pos.Y);
            bool inHeader = pos.Y >= TagH && pos.Y < TagH + HeadH;
            bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0, cmd = Ui.HasCommand(e.KeyModifiers);
            if (FxRowAt(pos.Y) is { } fxRow)
            {
                PressFx(e, fxRow.Layer, fxRow.Effect, pos, left, right, shift, cmd);
                return;
            }
            if (GhostRowButtonAt(pos) is var ghostRow and not GhostRowButton.None)
            {
                // "다음 레이어" 줄 단추: ＋ 맨 아래에 새 레이어 · ⧉ 마지막 레이어 복사해서 추가
                if (left) _owner.AppendLayer(copy: ghostRow == GhostRowButton.Copy);
                else if (right) _owner.ShowEmptyMenu();
                return;
            }
            if (pos.X < NameW && l >= 0)
            {
                // 레이어 이름 열: 눈/자물쇠/접기/이름
                var nameLayer = p.Layers[l];
                if (left && pos.X < 24) { Ctx.Structural(() => p.SetLayerProperties(l, visible: !nameLayer.Visible)); return; }
                if (left && pos.X < ToggleX) { Ctx.Structural(() => p.SetLayerProperties(l, locked: !nameLayer.Locked)); return; }
                if (left && nameLayer.IsGroup && pos.X < NameX) { _owner.ToggleCollapsed(l); return; }
                if (left && LayoutBadge(nameLayer) is { W: > 0 } lb && pos.X >= lb.X && pos.X < lb.X + lb.W)
                {
                    // ◆ 자동 배치 표시: 설정 창
                    Ctx.SetCurrentLayer(l);
                    Dispatcher.UIThread.Post(() => _owner.EditGroupLayout(l));
                    return;
                }
                if (left && FxBadge(nameLayer) is { W: > 0 } fx && pos.X >= fx.X && pos.X < fx.X + fx.W) { _owner.ToggleEffectRows(l); return; }
                if (left && SkinChip(nameLayer) is { W: > 0 } chip && pos.X >= chip.X && pos.X < chip.X + chip.W)
                {
                    // 스킨 칩: 이 그룹/부위로 스킨 관리 창 열기(우클릭은 레이어 메뉴의 "스킨"·"이 부위 스킨")
                    Ctx.SetCurrentLayer(l);
                    Dispatcher.UIThread.Post(() => _owner.OpenSkinManager(nameLayer));
                    return;
                }
                if (right)
                {
                    // 고른 레이어 위면 선택을 그대로 두고, 아니면 그 레이어를 골라 메뉴를 연다(메뉴는 고른 레이어 대상).
                    if (!(_kind == SelKind.Layers && _selLayers.Contains(nameLayer))) { SelectLayers([nameLayer]); _layerAnchor = nameLayer; }
                    Ctx.SetCurrentLayer(l);
                    _owner.HintSelection();
                    _owner.ShowLayerMenu(l);
                    return;
                }
                if (!left) return;
                if (e.ClickCount == 2) { Ctx.SetCurrentLayer(l); BeginRename(l); return; }
                if (shift)
                {
                    // 기준 레이어(마지막으로 누른 이름, 없으면 현재 레이어)부터 누른 레이어까지 보이는 행을 고른다.
                    var rows = Rows();
                    int anchorRow = _kind == SelKind.Layers && _layerAnchor is { } la ? rows.IndexOf(p.IndexOf(la)) : -1;
                    if (anchorRow < 0) anchorRow = Math.Max(0, rows.IndexOf(Ctx.CurrentLayer));
                    var anchorLayer = p.Layers[rows[anchorRow]];
                    SelectLayerRows(anchorRow, rows.IndexOf(l));
                    _layerAnchor = anchorLayer;
                    _owner.HintSelection();
                    return;
                }
                if (cmd)
                {
                    ToggleLayer(nameLayer, l);
                    _owner.HintSelection();
                    return;
                }
                // 고른 이름을 누름 = 끌면 순서 바꾸기(떼면 그것 하나만 고름), 안 고른 이름 = 고르기 + 끌면 사각형으로 여러 레이어 고르기.
                _pressContent = new Avalonia.Point(ContentX(pos.X), ContentY(pos.Y));
                Ctx.SetCurrentLayer(l);
                if (_kind == SelKind.Layers && _selLayers.Contains(nameLayer))
                {
                    _layerDragFrom = l;
                    _collapseOnRelease = _selLayers.Count > 1;
                }
                else
                {
                    SelectLayers([nameLayer]);
                    _layerAnchor = nameLayer;
                    BeginMarquee(SelKind.Layers, Rows().IndexOf(l), pos);
                }
                _owner.HintSelection();
                e.Pointer.Capture(this);
                return;
            }
            if (pos.X < NameW)
            {
                // 레이어 이름 열의 빈 곳·왼쪽 위 칸: 우클릭 = 새로 만들기, 클릭 = 선택 풀기(현재 칸)
                if (right) _owner.ShowEmptyMenu();
                else if (left && l < 0 && pos.Y >= TagH + HeadH) ResetRange();
                return;
            }
            if (GhostColumnAt(pos) is { } ghostHeader)
            {
                if (left) _owner.AppendFrame(copy: !ghostHeader, l >= 0 ? l : Ctx.CurrentLayer);
                else if (right) _owner.ShowEmptyMenu();
                return;
            }
            if (f < 0)
            {
                if (right) _owner.ShowEmptyMenu();
                else if (left && pos.Y >= TagH) ResetRange();
                return;
            }
            if (inHeader)
            {
                if (right)
                {
                    // 고른 프레임 위면 선택을 그대로 두고, 아니면 그 프레임을 골라 메뉴를 연다(메뉴는 고른 프레임 대상).
                    if (!(_kind == SelKind.Frames && _selFrames.Contains(f))) { SelectFrames([f]); _frameAnchor = f; }
                    Ctx.SetCurrentFrame(f);
                    _owner.HintSelection();
                    _owner.ShowFrameMenu(f);
                    return;
                }
                if (!left) return;
                if (e.ClickCount == 2) { _owner.SetDuration(); return; }
                if (shift)
                {
                    // 기준 프레임(마지막으로 누른 머리글, 없으면 현재 프레임)부터 누른 프레임까지
                    int anchor = _kind == SelKind.Frames && _frameAnchor >= 0 && _frameAnchor < Ctx.FrameCount ? _frameAnchor : Ctx.CurrentFrame;
                    SelectFrameRange(anchor, f);
                    _frameAnchor = anchor;
                    _owner.HintSelection();
                    return;
                }
                if (cmd)
                {
                    ToggleFrame(f);
                    _owner.HintSelection();
                    return;
                }
                // 고른 머리글을 누름 = 끌면 순서 바꾸기(이어진 고른 프레임 덩어리째, 떼면 그 프레임 하나만 고름),
                // 안 고른 머리글 = 고르기 + 끌면 사각형으로 여러 프레임 고르기.
                _pressContent = new Avalonia.Point(ContentX(pos.X), ContentY(pos.Y));
                Ctx.SetCurrentFrame(f);
                if (_kind == SelKind.Frames && _selFrames.Contains(f))
                {
                    int a = f, b = f;
                    while (_selFrames.Contains(a - 1)) a--;
                    while (_selFrames.Contains(b + 1)) b++;
                    (_dragA, _dragB) = (a, b);
                    _dragFrom = f;
                    _collapseOnRelease = _selFrames.Count > 1;
                }
                else
                {
                    SelectFrames([f]);
                    _frameAnchor = f;
                    BeginMarquee(SelKind.Frames, f, pos);
                }
                _owner.HintSelection();
                e.Pointer.Capture(this);
                InvalidateVisual();
                return;
            }
            if (l < 0)
            {
                // 마지막 행 아래 빈 곳
                if (right) _owner.ShowEmptyMenu();
                else if (left && pos.Y >= TagH + HeadH) ResetRange();
                return;
            }
            if (right)
            {
                // 고른 셀 묶음 안이면 선택을 그대로 두고, 아니면 그 칸을 골라 메뉴를 연다(메뉴는 고른 셀 대상).
                if (!InCelSelection(l, f))
                {
                    ResetRange();
                    Ctx.SetCurrent(l, f);
                }
                _owner.ShowCelMenu(f, l);
                return;
            }
            if (!left) return;
            if (shift)
            {
                // 시작 칸(현재 칸)은 그대로 두고 끝 칸(행·프레임)만 넓힌다.
                if (_kind != SelKind.Cels) ClearSelection();
                if (_rangeA < 0) _rangeA = Ctx.CurrentFrame;
                _rangeB = f;
                _rowEnd = l == Ctx.CurrentLayer ? null : p.Layers[l];
                InvalidateVisual();
                _owner.HintSelection();
                return;
            }
            var selRowsNow = SelectedRowLayers();
            // 여러 칸을 골라 둔 묶음 안을 누르면 끌어서 옮긴다(한 칸·묶음 밖에서 끌면 사각형 고르기).
            bool multi = (_rangeA >= 0 && _rangeA != _rangeB) || _rowEnd is not null;
            if (_kind == SelKind.Cels && multi && InCelSelection(l, f) && BlockHasContent(p, selRowsNow))
            {
                // 고른 셀 묶음을 끌어 옮기기(⌥/Alt 또는 Ctrl/⌘ = 복사)
                var (a, b) = _rangeA >= 0 ? Range : (Ctx.CurrentFrame, Ctx.CurrentFrame);
                var rowsNow = Rows();
                _blockRows = selRowsNow;
                _blockFrom = a;
                _blockCount = b - a + 1;
                _grabRow = rowsNow.IndexOf(l) - rowsNow.IndexOf(selRowsNow[0]);
                _grabFrame = f - a;
                _blockDrop = (-1, -1);
                _blockPress = (l, f);
                e.Pointer.Capture(this);
                return;
            }
            ResetRange();
            Ctx.SetCurrent(l, f);
            BeginMarquee(SelKind.Cels, f, pos);
            e.Pointer.Capture(this);
            InvalidateVisual();
        }

        /// <summary>사각형 끌어 고르기를 시작한다(문턱을 넘어야 사각형이 보임). <paramref name="anchor"/>: 시작 프레임(레이어는 보이는 행 번호).</summary>
        private void BeginMarquee(SelKind kind, int anchor, Avalonia.Point pos)
        {
            _marquee = kind;
            _marqueeAnchor = anchor;
            _marqueeShown = false;
            _marqueeFrom = new Avalonia.Point(ContentX(pos.X), ContentY(pos.Y));
            _lastPointer = pos;
        }

        /// <summary>
        /// 사각형 끌어 고르기 중: 시작 칸 ~ 포인터 칸을 같은 종류로 고른다(머리글 = 프레임, 이름 칸 = 레이어, 셀 = 행 × 프레임 묶음).
        /// 가장자리면 자동 스크롤.
        /// </summary>
        private void UpdateMarquee()
        {
            if (_marquee is not { } kind || Ctx.Project is not { } p) return;
            var pos = _lastPointer;
            switch (kind)
            {
                case SelKind.Frames:
                    SelectFrameRange(_marqueeAnchor, FrameAtClamped(pos.X));
                    _scrollH.Set(ReorderCore.EdgeSpeed(pos.X, NameW, ClientW));
                    break;
                case SelKind.Layers:
                    SelectLayerRows(_marqueeAnchor, RowAtClamped(pos.Y));
                    _scrollV.Set(ReorderCore.EdgeSpeed(pos.Y, TagH + HeadH, ClientH));
                    break;
                default:
                    var rows = Rows();
                    int l2 = rows.Count > 0 ? rows[RowAtClamped(pos.Y)] : Ctx.CurrentLayer;
                    _rangeA = _marqueeAnchor;
                    _rangeB = FrameAtClamped(pos.X);
                    _rowEnd = l2 == Ctx.CurrentLayer ? null : p.Layers[l2];
                    _scrollH.Set(ReorderCore.EdgeSpeed(pos.X, NameW, ClientW));
                    _scrollV.Set(ReorderCore.EdgeSpeed(pos.Y, TagH + HeadH, ClientH));
                    break;
            }
            InvalidateVisual();
        }

        /// <summary>사각형 끌어 고르기를 끝낸다(한 칸만 남은 셀 묶음은 현재 칸 선택으로). <paramref name="hint"/>면 고른 것을 알린다.</summary>
        private void EndMarquee(bool hint)
        {
            if (_marquee is not { } kind) return;
            _marquee = null;
            bool shown = _marqueeShown;
            _marqueeShown = false;
            _scrollH.Set(0);
            _scrollV.Set(0);
            if (kind == SelKind.Cels && _kind == SelKind.Cels && _rangeA == _rangeB && _rowEnd is null) _rangeA = _rangeB = -1;
            if (hint && shown) _owner.HintSelection();
            InvalidateVisual();
        }

        /// <summary>
        /// 효과 줄 누르기(누른 효과를 고름 — Shift/Ctrl/⌘ = 더 고르기, 고른 효과 위 우클릭은 선택 그대로). 이름 칸: 체크 상자 = 켜고 끄기(선택은 그대로),
        /// 끌기 = 효과 순서 바꾸기(다른 레이어 줄에 놓으면 그 레이어로 옮김), 두 번 = 효과 설정, 우클릭 = 메뉴. 프레임 칸: 상자 끝 손잡이를 끌면 범위의 그 끝을,
        /// 몸통을 끌면 범위를 통째로 옮기고(놓을 때 실행취소 한 단계, 끄는 동안 캔버스에 바로 보임), 두 번 = 효과 설정, 우클릭 = 메뉴(상자 밖이면 늘리기·복사본 추가),
        /// 상자 밖 빈 칸 클릭 = 선택 풀기.
        /// </summary>
        private void PressFx(PointerPressedEventArgs e, int l, int k, Avalonia.Point pos, bool left, bool right, bool shift, bool cmd)
        {
            if (Ctx.Project is not { } p) return;
            var layer = p.Layers[l];
            var effect = layer.Effects[k];
            var key = (layer, k);
            bool selected = IsEffectSelected(layer, k);
            if (pos.X < NameW)
            {
                int cx = FxCheckX(layer);
                if (left && pos.X >= cx - 4 && pos.X < cx + 14) { _owner.SetEffectEnabled(layer, k, !effect.Enabled); return; }
                Ctx.SetCurrentLayer(l);
                if (right)
                {
                    if (!selected) { SelectEffects([key]); _fxAnchor = key; }
                    _owner.HintSelection();
                    _owner.ShowEffectMenu(layer, k, Ctx.CurrentFrame, inBox: true);
                    return;
                }
                if (!left) return;
                if (e.ClickCount == 2) { Dispatcher.UIThread.Post(() => _owner.EditLayerEffects(p.IndexOf(layer), k)); return; }
                if (shift || cmd)
                {
                    ModifyEffectSelection(key, range: shift);
                    _owner.HintSelection();
                    return;
                }
                // 누르기 = 고르기(이미 고른 것이면 그대로), 끌기 = 그 레이어에서 이어진 고른 효과째 순서 바꾸기(떼면 누른 효과 하나만 고름).
                if (!selected) SelectEffects([key]);
                _fxAnchor = key;
                int a = k, b = k;
                while (a > 0 && _selFx.Contains((layer, a - 1))) a--;
                while (b < layer.Effects.Count - 1 && _selFx.Contains((layer, b + 1))) b++;
                _fxDragLayer = layer;
                (_fxDragFrom, _fxDragA, _fxDragB) = (k, a, b);
                _fxCollapse = _selFx.Count > 1;
                _fxShift = new ReorderTween[layer.Effects.Count];
                _pressContent = new Avalonia.Point(ContentX(pos.X), ContentY(pos.Y));
                _owner.HintSelection();
                e.Pointer.Capture(this);
                return;
            }
            int f = FrameAt(pos.X);
            if (f < 0)
            {
                if (right) _owner.ShowEmptyMenu();
                return;
            }
            var grip = FxGripAt(pos.X, effect, p.Frames.Count);
            Ctx.SetCurrent(l, f);
            if (right)
            {
                if (!selected) { SelectEffects([key]); _fxAnchor = key; }
                _owner.HintSelection();
                _owner.ShowEffectMenu(layer, k, f, grip is not null);
                return;
            }
            if (!left) return;
            if (grip is not { } g) { ResetRange(); return; }
            if (shift || cmd)
            {
                ModifyEffectSelection(key, range: shift);
                _owner.HintSelection();
                return;
            }
            if (e.ClickCount == 2) { Dispatcher.UIThread.Post(() => _owner.EditLayerEffects(p.IndexOf(layer), k)); return; }
            // 누르기 = 고르기(이미 고른 것이면 그대로) + 끌면 범위 바꾸기
            _fxCollapse = selected && _selFx.Count > 1;
            if (!selected) SelectEffects([key]);
            _fxAnchor = key;
            _owner.HintSelection();
            _fxLayer = layer;
            _fxIndex = k;
            _fxGrip = g;
            _fxOriginal = layer.Effects;
            _fxGrab = f;
            (_fxFrom, _fxTo) = FxSpan(effect, p.Frames.Count);
            e.Pointer.Capture(this);
            InvalidateVisual();
        }

        /// <summary>
        /// 효과 이름 끌기 중: 포인터가 끄는 레이어 안이면 효과 줄 순서 미리보기(덩어리가 포인터를 따라오고 다른 줄은 비켜 섬, <see cref="ReorderCore"/>),
        /// 다른 레이어 위면 그 레이어 효과 목록에 넣을 자리(<see cref="_fxDropOther"/>)를 보인다. 가장자리면 위아래 자동 스크롤.
        /// </summary>
        private void UpdateFxReorder(Avalonia.Point pos)
        {
            if (Ctx.Project is not { } p || _fxDragLayer is not { } layer || p.IndexOf(layer) is var li && li < 0) return;
            _lastPointer = pos;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            int n = Math.Min(layer.Effects.Count, _fxShift.Length), a = _fxDragA, b = Math.Min(_fxDragB, n - 1), count = b - a + 1;
            int over = LayerAt(pos.Y);
            _fxDropOther = null;
            if (over >= 0 && over != li)
            {
                // 다른 레이어 위: 효과 줄 위면 그 줄 위/아래 반에 따라 앞/뒤, 본줄 위면 맨 앞(효과 줄을 접어 두었으면 맨 뒤)
                var target = p.Layers[over];
                int at = FxRowAt(pos.Y) is { } row && row.Layer == over
                    ? row.Effect + (pos.Y - (LayerY(over) + CellH + row.Effect * FxH) >= FxH / 2.0 ? 1 : 0)
                    : target.EffectsCollapsed ? target.Effects.Count : 0;
                _fxDropOther = (target, at);
                for (int i = 0; i < n; i++) _fxShift[i].Go(0, now, animate: true);
                _fxLift = 0;
            }
            else
            {
                // 같은 레이어 안: 덩어리를 한 단위로 놓을 자리를 셈
                var starts = new List<double>();
                var sizes = new List<double>();
                for (int i = 0; i < a; i++) { starts.Add(i * FxH); sizes.Add(FxH); }
                starts.Add(a * FxH);
                sizes.Add(count * FxH);
                for (int i = b + 1; i < n; i++) { starts.Add(i * FxH); sizes.Add(FxH); }
                // 놓을 자리: 덩어리 가운데가 첫 줄 앞 ~ 마지막 줄 끝까지 따라온다(끝까지 끌면 맨 앞·맨 뒤). 그리는 자리는 효과 줄 안으로 자른다.
                double raw = ContentY(pos.Y) - _pressContent.Y, half = count * FxH / 2.0;
                double reach = Math.Clamp(raw, -a * FxH - half, (n - a) * FxH - half);
                double offset = Math.Clamp(raw, -a * FxH, (n - 1 - b) * FxH);
                int to = ReorderCore.TargetIndex(starts, sizes, a, a * FxH + reach + half);
                int slot = 0;
                foreach (int u in ReorderCore.Order(starts.Count, a, to))
                {
                    if (u == a) { slot += count; continue; }
                    int i = u < a ? u : u + count - 1;
                    _fxShift[i].Go((slot - i) * FxH, now, animate: true);
                    slot++;
                }
                for (int i = a; i <= b; i++) _fxShift[i].Go(offset, now, animate: false);
                _fxLift = offset;
                _fxDropTo = to;
            }
            _scrollV.Set(ReorderCore.EdgeSpeed(pos.Y, TagH + HeadH, ClientH));
            if (!_animTimer.IsEnabled) _animTimer.Start();
            InvalidateVisual();
        }

        /// <summary>
        /// 효과 이름 누름을 끝낸다. 끌지 않았으면 클릭(여러 개를 고른 채 눌렀으면 그 효과 하나만 고름). 끌었으면 <paramref name="apply"/>일 때
        /// 같은 레이어 안 순서 바꾸기 또는 다른 레이어로 옮기기(실행취소 한 단계), 아니면(Esc·붙잡기 잃음) 제자리로.
        /// </summary>
        private void FinishFxReorder(bool apply)
        {
            var layer = _fxDragLayer;
            bool reordering = _fxReordering;
            var (from, a, b, to, other) = (_fxDragFrom, _fxDragA, _fxDragB, _fxDropTo, _fxDropOther);
            _fxDragLayer = null;
            _fxReordering = false;
            _fxDropOther = null;
            _fxShift = [];
            _fxLift = 0;
            _scrollV.Set(0);
            Cursor = Cursor.Default;
            InvalidateVisual();
            if (layer is null) return;
            if (!reordering)
            {
                if (apply && _fxCollapse) { SelectEffects([(layer, from)]); _owner.HintSelection(); }
                return;
            }
            if (!apply) return;
            if (other is { } drop) _owner.MoveEffects(layer, a, b, drop.Layer, drop.Index);
            else _owner.MoveEffects(layer, a, b, layer, to);
        }

        /// <summary>효과 이름 끌기 미리보기에서 효과 줄 <paramref name="k"/>의 밀림(px)과 떠서 포인터를 따라오는지.</summary>
        private (int Shift, bool Lifted) FxShiftOf(Layer layer, int k)
        {
            if (!_fxReordering || !ReferenceEquals(layer, _fxDragLayer) || k >= _fxShift.Length) return (0, false);
            bool lifted = _fxDropOther is null && k >= _fxDragA && k <= _fxDragB;
            return ((int)Math.Round(_fxShift[k].At(System.Diagnostics.Stopwatch.GetTimestamp())), lifted);
        }

        /// <summary>효과 줄 밀림 애니메이션이 아직 움직이는지(끝난 것은 목표에 고정).</summary>
        private bool FxAnimating(long now)
        {
            bool moving = false;
            for (int i = 0; i < _fxShift.Length; i++) moving |= !_fxShift[i].Settle(now);
            return moving;
        }

        /// <summary>효과 범위 끌기 중: 프레임 칸에 맞춰 새 범위를 상자에만 보인다(모든 프레임을 덮으면 "모든 프레임"). 레이어 효과는 놓을 때 바꾼다.</summary>
        private void DragFx(Avalonia.Point pos)
        {
            if (Ctx.Project is not { } p || _fxLayer is not { } layer || _fxOriginal is not { } original) return;
            int n = p.Frames.Count;
            if (_fxIndex >= original.Count) return;
            int f = FrameAtClamped(pos.X), from = _fxFrom, to = _fxTo;
            switch (_fxGrip)
            {
                case FxGrip.Left: from = Math.Min(f, _fxTo); break;
                case FxGrip.Right: to = Math.Max(f, _fxFrom); break;
                default:
                    int d = Math.Clamp(f - _fxGrab, -_fxFrom, n - 1 - _fxTo);
                    from += d;
                    to += d;
                    break;
            }
            Cursor = _fxGrip == FxGrip.Body ? MoveCursor : ResizeCursor;
            var effect = original[_fxIndex] with { Frames = FrameRange.Of(from, to, n) };
            if ((_fxDragged ?? original[_fxIndex]) == effect) return;
            _fxDragged = effect;
            InvalidateVisual();
        }

        /// <summary>효과 범위 끌기 끝: <paramref name="commit"/>이고 범위가 바뀌었으면 실행취소 한 단계로 넣는다(여기서 한 번만 다시 계산), 아니면 그대로.</summary>
        private void FinishFx(bool commit)
        {
            var (layer, original, dragged) = (_fxLayer, _fxOriginal, _fxDragged);
            _fxLayer = null;
            _fxOriginal = null;
            _fxDragged = null;
            Cursor = Cursor.Default;
            InvalidateVisual();
            if (!commit || layer is null || original is null || dragged is null || Ctx.Project is not { } p) return;
            int index = p.IndexOf(layer);
            // 끄는 동안 다른 곳(AI 도구 등)이 효과를 바꿨으면 넣지 않는다
            if (index < 0 || !ReferenceEquals(layer.Effects, original) || dragged == original[_fxIndex]) return;
            var list = original.ToArray();
            list[_fxIndex] = dragged;
            Ctx.Structural(() => p.SetLayerEffects(index, list));
        }

        /// <summary>점 위 효과 상자와 잡는 곳(프레임 칸 쪽 효과 줄만, 상자 밖이면 null).</summary>
        private (Layer Layer, int Index, FxGrip Grip)? FxHitAt(Avalonia.Point pos)
        {
            if (pos.X < NameW || Ctx.Project is not { } p || FxRowAt(pos.Y) is not { } row) return null;
            var layer = p.Layers[row.Layer];
            return FxGripAt(pos.X, layer.Effects[row.Effect], p.Frames.Count) is { } grip ? (layer, row.Effect, grip) : null;
        }

        /// <summary>점 위 자동 배치 표시 ◆의 그룹(이름 칸 레이어 본줄만, 아니면 null).</summary>
        private Layer? LayoutBadgeAt(Avalonia.Point pos)
        {
            if (pos.X >= NameW || Ctx.Project is not { } p || FxRowAt(pos.Y) is not null || LayerAt(pos.Y) is var l && l < 0) return null;
            var layer = p.Layers[l];
            return LayoutBadge(layer) is { W: > 0 } b && pos.X >= b.X && pos.X < b.X + b.W ? layer : null;
        }

        /// <summary>누르면 바로 동작하는 작은 단추 위인지(손 모양 커서): 이름 칸의 눈·자물쇠·접기·fx·스킨 칩, 프레임 끝 열의 ⧉/＋, "다음 레이어" 줄의 ＋/⧉.</summary>
        private bool IsClickableAt(Avalonia.Point pos)
        {
            if (Ctx.Project is not { } p || pos.Y < TagH) return false;
            if (GhostColumnAt(pos) is not null || GhostRowButtonAt(pos) != GhostRowButton.None) return true;
            if (pos.X >= NameW) return false;
            if (FxRowAt(pos.Y) is not null || LayerAt(pos.Y) is var l && l < 0) return false;
            var layer = p.Layers[l];
            if (pos.X < ToggleX || (layer.IsGroup && pos.X < NameX)) return true;
            return FxBadge(layer) is { W: > 0 } fx && pos.X >= fx.X && pos.X < fx.X + fx.W
                || SkinChip(layer) is { W: > 0 } chip && pos.X >= chip.X && pos.X < chip.X + chip.W;
        }

        /// <summary>◆ 위에 마우스가 올라감/벗어남: 강조, 상태 줄 안내(도움말 풍선은 <see cref="UpdateTip"/>).</summary>
        private void SetLayoutBadgeHover(Layer? group)
        {
            _layoutBadgeHover = group;
            if (group is not null) _owner.Say(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_SetLayoutBadgeHover_309A0A0649,group.Name,UiStrings.Timeline_LayoutBadgeTip));
            InvalidateVisual();
        }

        /// <summary>도움말 풍선: ◆ 자동 배치 표시와 "다음 레이어" 줄 단추 위에서 바로 띄운다.</summary>
        private void UpdateTip()
        {
            string? tip = _layoutBadgeHover is not null ? UiStrings.Timeline_LayoutBadgeTip : _rowHover switch
            {
                GhostRowButton.Add => UiStrings.Timeline_AddLayerTip,
                GhostRowButton.Copy => UiStrings.Timeline_AppendLayerCopyTip,
                _ => null,
            };
            if (tip == _tip) return;
            _tip = tip;
            ToolTip.SetIsOpen(this, false);
            ToolTip.SetTip(this, tip);
            if (tip is not null) ToolTip.SetIsOpen(this, true);
        }

        /// <summary>고른 셀 묶음에 그림이 하나라도 있는지(그룹 행은 그 부위들).</summary>
        private bool BlockHasContent(SpriteProject p, List<int> rowLayers)
        {
            var (a, b) = _rangeA >= 0 ? Range : (Ctx.CurrentFrame, Ctx.CurrentFrame);
            foreach (var l in rowLayers)
            {
                var layers = p.Layers[l] is { IsGroup: true } g ? p.ChildrenOf(g).Select(p.IndexOf) : [l];
                foreach (var li in layers)
                    for (int f = a; f <= b; f++)
                        if (!p.IsCelEmpty(li, f)) return true;
            }
            return false;
        }

        // ───────────── 끌어서 순서 바꾸기(공통 계산 = ReorderCore, 미리보기 = Track 밀림: 뜬 칸은 포인터를 따라오고 다른 칸은 비켜 섬) ─────────────

        /// <summary>레이어 끌기에서 뜬 행(행 배치의 칸 번호, 그룹이면 부위 행까지).</summary>
        private List<int> _liftRows = new();

        /// <summary>
        /// 포인터 위치로 미리보기를 다시 맞춘다. 프레임: 앞뒤 프레임은 한 칸씩, 고른 덩어리는 한 단위로.
        /// 레이어: 부위는 같은 그룹의 부위끼리, 일반 레이어·그룹은 맨 위 단계끼리(그룹은 부위 행과 한 덩어리). 가장자리면 자동 스크롤.
        /// </summary>
        private void UpdateReorder(Avalonia.Point pos)
        {
            if (Ctx.Project is not { } p) return;
            _lastPointer = pos;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            var starts = new List<double>();
            var sizes = new List<double>();
            if (_dragFrom >= 0)
            {
                int n = p.Frames.Count, a = _dragA, b = _dragB, count = b - a + 1;
                for (int f = 0; f < a; f++) { starts.Add(_colT.BasePos(f)); sizes.Add(_colT.Px(f)); }
                double blockStart = _colT.BasePos(a), blockSize = _colT.BasePos(b) + _colT.Px(b) - blockStart;
                starts.Add(blockStart);
                sizes.Add(blockSize);
                for (int f = b + 1; f < n; f++) { starts.Add(_colT.BasePos(f)); sizes.Add(_colT.Px(f)); }
                double end = _colT.BasePos(n - 1) + _colT.Px(n - 1);
                // 뜬 덩어리의 가운데가 첫 칸 앞 ~ 마지막 칸 끝 안에서 따라온다(끝까지 끌면 맨 앞·맨 뒤 자리).
                double offset = Math.Clamp(ContentX(pos.X) - _pressContent.X, -blockStart - blockSize / 2, end - blockStart - blockSize / 2);
                int to = ReorderCore.TargetIndex(starts, sizes, a, blockStart + offset + blockSize / 2);
                var sequence = new List<int>(n);
                foreach (int u in ReorderCore.Order(starts.Count, a, to))
                {
                    if (u < a) sequence.Add(u);
                    else if (u == a) for (int f = a; f <= b; f++) sequence.Add(f);
                    else sequence.Add(u + count - 1);
                }
                _colT.Preview(sequence, f => f >= a && f <= b, (int)Math.Round(offset), now);
                _dropTo = to;
                _scrollH.Set(ReorderCore.EdgeSpeed(pos.X, NameW, ClientW));
            }
            else if (_layerDragFrom >= 0 && _layerDragFrom < p.Layers.Count)
            {
                var src = p.Layers[_layerDragFrom];
                // 같은 단계 레이어(위→아래)와 각자의 행(그룹은 부위 행까지 — 행 배치에서 이어져 있음)
                var siblings = (src.Parent is { } group ? p.ChildrenOf(group) : p.Layers.Where(l => l.Parent is null)).Reverse().ToList();
                var units = siblings.Select(l =>
                {
                    var rows = new List<int> { RowItem(p.IndexOf(l)) };
                    if (l.IsGroup) rows.AddRange(ChildrenTopDown(p, l).Select(c => RowItem(p.IndexOf(c))));
                    return rows;
                }).ToList();
                // 누른 레이어와 같은 단계에서 이어진 고른 레이어들은 한 덩어리로 옮긴다.
                int ra = siblings.IndexOf(src), rb = ra;
                if (_kind == SelKind.Layers && _selLayers.Contains(src))
                {
                    while (ra > 0 && _selLayers.Contains(siblings[ra - 1])) ra--;
                    while (rb < siblings.Count - 1 && _selLayers.Contains(siblings[rb + 1])) rb++;
                }
                if (rb > ra)
                {
                    var block = units.GetRange(ra, rb - ra + 1).SelectMany(u => u).ToList();
                    units.RemoveRange(ra, rb - ra + 1);
                    units.Insert(ra, block);
                }
                int from = ra;
                foreach (var u in units) { starts.Add(_rowT.BasePos(u[0])); sizes.Add(u.Sum(_rowT.Px)); }
                double first = starts[0], last = starts[^1] + sizes[^1];
                double offset = Math.Clamp(ContentY(pos.Y) - _pressContent.Y, first - starts[from] - sizes[from] / 2, last - starts[from] - sizes[from] / 2);
                int to = ReorderCore.TargetIndex(starts, sizes, from, starts[from] + offset + sizes[from] / 2);
                // 전체 행 차례: 같은 단계 레이어들이 차지한 행 구간만 새 순서로, 나머지 행은 제자리.
                int spanStart = units[0][0], spanEnd = units[^1][^1];
                var sequence = new List<int>(_rowT.Count);
                for (int i = 0; i < spanStart; i++) sequence.Add(i);
                foreach (int u in ReorderCore.Order(units.Count, from, to)) sequence.AddRange(units[u]);
                for (int i = spanEnd + 1; i < _rowT.Count; i++) sequence.Add(i);
                _liftRows = units[from];
                var lifted = new HashSet<int>(_liftRows);
                _rowT.Preview(sequence, lifted.Contains, (int)Math.Round(offset), now);
                _layerBlock = siblings.GetRange(ra, rb - ra + 1);
                _layerSteps = from - to;
                _scrollV.Set(ReorderCore.EdgeSpeed(pos.Y, TagH + HeadH, ClientH));
            }
            if ((_rowT.Animating || _colT.Animating) && !_animTimer.IsEnabled) _animTimer.Start();
            InvalidateVisual();
        }

        /// <summary>끄는 동안(순서 바꾸기·사각형 고르기) 가장자리 자동 스크롤: <paramref name="bar"/>를 움직이고 놓을 자리·고른 범위를 다시 센다.</summary>
        private bool AutoScrollBy(ScrollBar bar, double delta)
        {
            if (!_reordering && !_fxReordering && _marquee is null) return false;
            if (!bar.IsEnabled) return false;
            double old = bar.Value;
            bar.Value = Math.Clamp(old + delta, 0, bar.Maximum);
            if (bar.Value == old) return false;
            if (_marquee is not null) UpdateMarquee();
            else if (_fxReordering) UpdateFxReorder(_lastPointer);
            else UpdateReorder(_lastPointer);
            return true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var pos = e.GetPosition(this);
            if (_tagEdit is not null)
            {
                int f = FrameAtClamped(pos.X);
                if (_tagEditRight) _tagTo = Math.Max(_tagFrom, f);
                else _tagFrom = Math.Min(_tagTo, f);
                Cursor = ResizeCursor;
                InvalidateVisual();
                return;
            }
            if (_fxLayer is not null)
            {
                DragFx(pos);
                return;
            }
            if (_fxDragLayer is not null)
            {
                if (!_fxReordering)
                {
                    if (!ReorderCore.Exceeded(_press, pos)) return;
                    _fxReordering = true;
                    Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
                }
                _dragPointer = e.Pointer;
                UpdateFxReorder(pos);
                return;
            }
            if (_marquee is not null)
            {
                _lastPointer = pos;
                if (!_marqueeShown)
                {
                    if (!ReorderCore.Exceeded(_press, pos)) return;
                    _marqueeShown = true;
                }
                UpdateMarquee();
                return;
            }
            if (_blockRows is not null)
            {
                if (Math.Abs(pos.X - _press.X) + Math.Abs(pos.Y - _press.Y) < 6) return;
                int rowCount = Rows().Count;
                int top = Math.Clamp(RowAtClamped(pos.Y) - _grabRow, 0, Math.Max(0, rowCount - _blockRows.Count));
                int start = Math.Max(0, _colT.IndexAt(ContentX(pos.X), exact: false) - _grabFrame);
                if ((top, start) != _blockDrop) { _blockDrop = (top, start); InvalidateVisual(); }
                Cursor = new Cursor(IsCopyModifier(e.KeyModifiers) ? StandardCursorType.DragCopy : StandardCursorType.DragMove);
                return;
            }
            if (_dragFrom < 0 && _layerDragFrom < 0)
            {
                UpdateHover(pos);
                var edge = TagEdgeAt(pos);
                var fx = FxHitAt(pos);
                Cursor = edge is not null || fx is { Grip: not FxGrip.Body } ? ResizeCursor : fx is not null ? MoveCursor : Cursor.Default;
                if (!ReferenceEquals(edge?.Tag, _tagHover)) { _tagHover = edge?.Tag; InvalidateVisual(); }
                (Layer, int)? fxHover = fx is { } h ? (h.Layer, h.Index) : null;
                if (fxHover != _fxHover) { _fxHover = fxHover; InvalidateVisual(); }
                var badge = LayoutBadgeAt(pos);
                if (badge is not null || IsClickableAt(pos)) Cursor = HandCursor;
                if (!ReferenceEquals(badge, _layoutBadgeHover)) SetLayoutBadgeHover(badge);
                UpdateTip();
                return;
            }
            if (!_reordering)
            {
                if (!ReorderCore.Exceeded(_press, pos)) return;
                _reordering = true;
                Cursor = new Cursor(_dragFrom >= 0 ? StandardCursorType.SizeWestEast : StandardCursorType.SizeNorthSouth);
            }
            _dragPointer = e.Pointer;
            UpdateReorder(pos);
        }

        private static bool IsCopyModifier(KeyModifiers m) => (m & KeyModifiers.Alt) != 0 || Ui.HasCommand(m) || (m & KeyModifiers.Control) != 0;

        /// <summary>마우스가 올라간 "다음 프레임" 칸·"다음 레이어" 줄 단추(강조용).</summary>
        private void UpdateHover(Avalonia.Point pos)
        {
            (int, bool) hover = GhostColumnAt(pos) is { } header ? (Ctx.FrameCount, header) : (-1, false);
            var rowHover = GhostRowButtonAt(pos);
            if (hover == _hover && rowHover == _rowHover) return;
            _hover = hover;
            _rowHover = rowHover;
            InvalidateVisual();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            if (_tagHover is not null) { _tagHover = null; InvalidateVisual(); }
            if (_fxHover is not null) { _fxHover = null; InvalidateVisual(); }
            if (_layoutBadgeHover is not null) SetLayoutBadgeHover(null);
            if (_rowHover != GhostRowButton.None) { _rowHover = GhostRowButton.None; InvalidateVisual(); }
            UpdateTip();
            if (_hover.Frame < 0) return;
            _hover = (-1, false);
            InvalidateVisual();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (_fxLayer is { } fxLayer)
            {
                // 먼저 확정한다: 캡처를 풀면 OnPointerCaptureLost가 끌기를 취소한다. 끌지 않고 뗐으면(클릭) 여럿 중 그 효과 하나만 고른다.
                var key = (fxLayer, _fxIndex);
                bool click = !ReorderCore.Exceeded(_press, e.GetPosition(this));
                FinishFx(true);
                if (click && _fxCollapse) { SelectEffects([key]); _owner.HintSelection(); }
                e.Pointer.Capture(null);
                return;
            }
            if (_fxDragLayer is not null)
            {
                FinishFxReorder(true);
                e.Pointer.Capture(null);
                return;
            }
            if (_tagEdit is { } edited)
            {
                var (from, to) = (_tagFrom, _tagTo);
                _tagEdit = null;
                Cursor = Cursor.Default;
                e.Pointer.Capture(null);
                if (from != edited.From || to != edited.To) _owner.ResizeTag(edited, from, to);
                InvalidateVisual();
                return;
            }
            if (_marquee is not null)
            {
                EndMarquee(hint: true);
                e.Pointer.Capture(null);
                return;
            }
            if (_blockRows is { } blockRows)
            {
                var (from, count, drop) = (_blockFrom, _blockCount, _blockDrop);
                _blockRows = null;
                _blockDrop = (-1, -1);
                Cursor = Cursor.Default;
                int sourceTop = Rows().IndexOf(blockRows[0]);
                if (drop.Row < 0)
                {
                    // 끌지 않고 놓음 = 그 칸 클릭(묶음 선택을 풀고 그 칸만 고름)
                    var (pl, pf) = _blockPress;
                    ResetRange();
                    if (pl >= 0) Ctx.SetCurrent(pl, pf);
                }
                else if (drop.Row != sourceTop || drop.Frame != from)
                    _owner.MoveBlock(blockRows, from, count, drop.Row, drop.Frame, IsCopyModifier(e.KeyModifiers));
                e.Pointer.Capture(null);
                InvalidateVisual();
                return;
            }
            if (_dragFrom < 0 && _layerDragFrom < 0) return;
            // 먼저 적용한다: 캡처를 풀면 OnPointerCaptureLost가 끌기 상태를 취소한다.
            FinishDrag(true);
            e.Pointer.Capture(null);
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            EndMarquee(hint: false);
            if (_fxLayer is not null) FinishFx(false);
            if (_fxDragLayer is not null) FinishFxReorder(false);
            if (_tagEdit is not null)
            {
                _tagEdit = null;
                Cursor = Cursor.Default;
                InvalidateVisual();
            }
            if (_blockRows is not null)
            {
                _blockRows = null;
                _blockDrop = (-1, -1);
                Cursor = Cursor.Default;
                InvalidateVisual();
            }
            if (_dragFrom >= 0 || _layerDragFrom >= 0) FinishDrag(false);
        }

        /// <summary>
        /// 고른 머리글·이름 누름을 끝낸다. 끌지 않았으면 클릭(여러 개를 고른 채 눌렀으면 그것 하나만 고름).
        /// 끌었으면 <paramref name="apply"/>일 때 실행취소 한 단계로 옮기고 칸들이 지금 자리에서 새 자리로 이어 미끄러지며,
        /// 아니면(Esc·붙잡기 잃음) 제자리로 돌아간다.
        /// </summary>
        private void FinishDrag(bool apply)
        {
            int dragFrom = _dragFrom, a = _dragA, b = _dragB, dropTo = _dropTo, layerFrom = _layerDragFrom, steps = _layerSteps;
            var block = _layerBlock;
            bool reordering = _reordering, collapse = _collapseOnRelease;
            _dragFrom = _dropTo = _layerDragFrom = -1;
            _layerSteps = 0;
            _layerBlock = new();
            _reordering = _collapseOnRelease = false;
            _dragPointer = null;
            _liftRows = new();
            _scrollH.Set(0);
            _scrollV.Set(0);
            Cursor = Cursor.Default;
            var pressed = layerFrom >= 0 && Ctx.Project is { } p && layerFrom < p.Layers.Count ? p.Layers[layerFrom] : null;
            if (!reordering)
            {
                if (apply && collapse)
                {
                    if (dragFrom >= 0) { SelectFrames([dragFrom]); _frameAnchor = dragFrom; }
                    else if (pressed is not null) { SelectLayers([pressed]); _layerAnchor = pressed; }
                    _owner.HintSelection();
                }
                InvalidateVisual();
                return;
            }
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            bool frames = dragFrom >= 0;
            var track = frames ? _colT : _rowT;
            bool move = apply && (frames ? dropTo >= 0 && dropTo != a : steps != 0 && block.Count > 0 && pressed is not null);
            if (!move) track.ClearPreview(animate: true, now);
            else
            {
                var shown = track.Displayed();
                track.ClearPreview(animate: false, now);
                if (frames) _owner.MoveFramesTo(a, b, dropTo);
                else _owner.MoveLayersTo(block, steps, pressed!);
                track.SettleFrom(shown, System.Diagnostics.Stopwatch.GetTimestamp());
            }
            if (track.Animating && !_animTimer.IsEnabled) _animTimer.Start();
            InvalidateVisual();
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            if (FromChild(e) && e.Source is TextBox) return;
            // 기본 휠은 위아래(레이어), Ctrl(또는 Shift)+휠이나 트랙패드 가로 스크롤은 좌우(프레임).
            bool sideways = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != 0 || Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y);
            var bar = sideways ? _h : _v;
            e.Handled = true;
            if (!bar.IsEnabled) return;
            double delta = Math.Abs(e.Delta.Y) >= Math.Abs(e.Delta.X) ? e.Delta.Y : e.Delta.X;
            if (delta == 0) return;
            bar.Value = Math.Clamp(bar.Value - Math.Sign(delta) * bar.SmallChange, 0, bar.Maximum);
            InvalidateVisual();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || e.Source is TextBox) return;
            if (e.Key == Key.Escape && (_reordering || _fxReordering))
            {
                // 끌기 취소: 칸들이 제자리로 돌아간다.
                var pointer = _dragPointer;
                if (_fxReordering) FinishFxReorder(false);
                else FinishDrag(false);
                pointer?.Capture(null);
                e.Handled = true;
                return;
            }
            if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down && (e.KeyModifiers & ~KeyModifiers.Shift) == 0)
            {
                bool dragging = _marquee is not null || _dragFrom >= 0 || _layerDragFrom >= 0 || _blockRows is not null || _fxDragLayer is not null || _fxLayer is not null;
                if (!dragging) MoveByKey(e.Key, (e.KeyModifiers & KeyModifiers.Shift) != 0);
                e.Handled = true;
                return;
            }
            // macOS 키보드의 "delete"는 Back 키다.
            if (e.Key == Key.Delete || (Platform.IsMac && e.Key == Key.Back))
            {
                // 고른 종류대로: 프레임 = 프레임 삭제, 레이어 = 레이어 삭제, 셀 = 셀 그림 지우기(끄는 중에는 무시).
                bool dragging = _marquee is not null || _dragFrom >= 0 || _layerDragFrom >= 0 || _blockRows is not null || _fxDragLayer is not null || _fxLayer is not null;
                if (!dragging) _owner.DeleteSelection();
                e.Handled = true;
            }
            else if (e.Key == Key.F2) { _owner.BeginRenameLayer(); e.Handled = true; }
        }

        /// <summary>
        /// 방향키: 선택을 옆 프레임(←→)·옆 레이어(↑↓)로 옮긴다(현재 칸도 따라감). Shift = 끌어서 고르듯 넓히기.
        /// 프레임 선택은 ←→, 레이어 선택은 ↑↓로 그 종류를 유지하고, 다른 방향이나 셀·효과 선택이면 셀 선택으로 움직인다.
        /// </summary>
        private void MoveByKey(Key key, bool extend)
        {
            if (Ctx.Project is not { } p || p.Frames.Count == 0) return;
            var rows = Rows();
            if (rows.Count == 0) return;
            int dx = key == Key.Left ? -1 : key == Key.Right ? 1 : 0, dy = key == Key.Up ? -1 : key == Key.Down ? 1 : 0;
            int lastFrame = p.Frames.Count - 1;
            if (_kind == SelKind.Frames && dx != 0)
            {
                int t = Math.Clamp(Ctx.CurrentFrame + dx, 0, lastFrame);
                if (extend)
                {
                    int anchor = _frameAnchor >= 0 && _frameAnchor <= lastFrame ? _frameAnchor : Ctx.CurrentFrame;
                    SelectFrameRange(anchor, t);
                    _frameAnchor = anchor;
                }
                else { SelectFrames([t]); _frameAnchor = t; }
                Ctx.SetCurrentFrame(t);
            }
            else if (_kind == SelKind.Layers && dy != 0)
            {
                int cur = Math.Max(0, rows.IndexOf(Ctx.CurrentLayer));
                int t = Math.Clamp(cur + dy, 0, rows.Count - 1);
                if (extend)
                {
                    var anchorLayer = _layerAnchor is { } la && rows.Contains(p.IndexOf(la)) ? la : p.Layers[rows[cur]];
                    SelectLayerRows(rows.IndexOf(p.IndexOf(anchorLayer)), t);
                    _layerAnchor = anchorLayer;
                }
                else { SelectLayers([p.Layers[rows[t]]]); _layerAnchor = p.Layers[rows[t]]; }
                Ctx.SetCurrentLayer(rows[t]);
            }
            else if (extend && _kind == SelKind.Cels)
            {
                // 시작 칸(현재 칸)은 그대로 두고 끝 칸만 옮긴다.
                int endFrame = _rangeA >= 0 ? _rangeB : Ctx.CurrentFrame;
                int endRow = _rowEnd is { } re && rows.IndexOf(p.IndexOf(re)) is var ri && ri >= 0 ? ri : Math.Max(0, rows.IndexOf(Ctx.CurrentLayer));
                endFrame = Math.Clamp(endFrame + dx, 0, lastFrame);
                endRow = Math.Clamp(endRow + dy, 0, rows.Count - 1);
                if (_rangeA < 0) _rangeA = Ctx.CurrentFrame;
                _rangeB = endFrame;
                _rowEnd = rows[endRow] == Ctx.CurrentLayer ? null : p.Layers[rows[endRow]];
                if (_rangeA == _rangeB && _rowEnd is null) _rangeA = _rangeB = -1;
            }
            else
            {
                int cur = Math.Max(0, rows.IndexOf(Ctx.CurrentLayer));
                int row = Math.Clamp(cur + dy, 0, rows.Count - 1), frame = Math.Clamp(Ctx.CurrentFrame + dx, 0, lastFrame);
                ResetRange();
                Ctx.SetCurrent(rows[row], frame);
            }
            InvalidateVisual();
            EnsureCurrentVisible();
            _owner.HintSelection();
        }
    }
}

/// <summary>태그(애니메이션) 만들기/고치기.</summary>
public sealed class TagDialog : DialogBase
{
    private readonly ComboBox _name;
    private readonly NumericUpDown _from, _to;
    private readonly ComboBox _direction;
    private readonly CheckBox _loop;
    private readonly ColorField _color;

    public TagDialog(SpriteProject project, AnimationTag? tag, int from, int to) : base(tag is null ? UiStrings.S_TimelinePanel_TagDialog_723CE1A597 : UiStrings.S_TimelinePanel_TagDialog_74D5D76F0D)
    {
        int n = project.Frames.Count;
        _name = new ComboBox { IsEditable = true, Width = 200, Margin = new Thickness(3) };
        foreach (var s in TimelinePanel.SuggestedNames.Where(s => !project.HasTag(s) || s == tag?.Name)) _name.Items.Add(s);
        _name.Text = tag?.Name ?? TimelinePanel.SuggestedNames.FirstOrDefault(s => !project.HasTag(s)) ?? project.UniqueTagName("anim");
        _from = Ui.Num(1, n, from + 1, 60).Presets(Ui.FrameNumbers(n)).RecentKey("tag.frame");
        _to = Ui.Num(1, n, to + 1, 60).Presets(Ui.FrameNumbers(n)).RecentKey("tag.frame");
        _direction = Ui.Combo(110, UiStrings.S_TimelinePanel_TagDialog_29A4F2A73C, UiStrings.S_TimelinePanel_TagDialog_11A983652D, UiStrings.S_TimelinePanel_TagDialog_B280AE24E4);
        _direction.SelectedIndex = (int)(tag?.Direction ?? TagDirection.Forward);
        _loop = Ui.Check(UiStrings.S_TimelinePanel_TagDialog_C8283F32B5, tag?.Loop ?? true);
        _color = new ColorField(tag?.Color ?? AnimationTag.DefaultColors[project.Tags.Count % AnimationTag.DefaultColors.Count]);
        Body.Children.Add(Ui.Label(UiStrings.S_TimelinePanel_TagDialog_9C825E0AF6));
        Body.Children.Add(_name);
        Body.Children.Add(Ui.Row(Ui.Label(UiStrings.S_TimelinePanel_TagDialog_0F6A4740E4), _from, Ui.Label("~"), _to));
        Body.Children.Add(Ui.Row(Ui.Label(UiStrings.S_TimelinePanel_TagDialog_94754AAEF6), _direction, _loop));
        Body.Children.Add(Ui.Row(Ui.Label(UiStrings.S_TimelinePanel_TagDialog_6A8F38ED94), _color));
        OkButton.Click += (_, _) =>
        {
            if (TagName.Length == 0) { Ui.Error(this, UiStrings.S_TimelinePanel_TagDialog_84CC38ACF3); DialogResult = DialogResult.None; return; }
            if (project.Tags.Any(t => t.Name == TagName && !ReferenceEquals(t, tag)))
            {
                Ui.Error(this, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_TimelinePanel_TagDialog_DF0CF7BD36,TagName));
                DialogResult = DialogResult.None;
                return;
            }
            if (_to.NumValue() < _from.NumValue()) { Ui.Error(this, UiStrings.S_TimelinePanel_TagDialog_96907A65F2); DialogResult = DialogResult.None; }
        };
    }

    public string TagName => (_name.Text ?? "").Trim();
    public int From => (int)_from.NumValue() - 1;
    public int To => (int)_to.NumValue() - 1;
    public TagDirection Direction => (TagDirection)_direction.SelectedIndex;
    public bool Loop => _loop.On();
    public Color32 TagColor => _color.Color;
}
