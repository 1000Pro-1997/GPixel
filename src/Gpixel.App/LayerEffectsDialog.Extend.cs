using Gpixel.Core.Edit;
using Gpixel.Core.Model;
using Gpixel.App.Localization;

namespace Gpixel.App;

/// <summary>
/// 효과 창의 프레임 늘리기: 고른 효과 범위의 "프레임 수"를 범위 길이보다 크게 하면(또는 프레임 줄에서 범위 끝을 마지막 칸 너머로 끌면)
/// 확인할 때 프로젝트에 프레임을 더한다(<see cref="SpriteProject.ExtendFrames(FrameExtension)"/> + 효과 확정 = 실행취소 한 단계).
/// 그때까지 미리보기·프레임 줄은 늘린 뒤의 차례(가상 프레임)를 보인다. 한 번에 한 범위만 늘린다.
/// 창 안의 효과 범위(<see cref="_effects"/>)는 원래 프레임 번호 그대로 두고, 보일 때·확정할 때 <see cref="FrameExtension.MapRange"/>로 옮긴다
/// (캔버스 미리보기는 원래 프레임에 그대로).
/// </summary>
public sealed partial class LayerEffectsDialog
{
    /// <summary>늘리기 상자 항목: 0 = 원래 프레임으로(범위 뒤에 있는 프레임으로 범위만 넓힘), 1~ = <see cref="FrameExtendMode"/>(프레임을 더함).</summary>
    private static readonly string[] ExtendModes =
        [UiStrings.S_LayerEffectsDialog_Extend_ExtendModes_1696F70224, .. Enum.GetValues<FrameExtendMode>().Select(FrameExtension.ModeName)];
    /// <summary>프레임을 더하는 늘리기 방법(앱을 켜 둔 동안 유지).</summary>
    private static FrameExtendMode s_extendMode = FrameExtendMode.Stretch;
    /// <summary>"원래 프레임으로"를 직접 고른 효과와 값(null = 정하지 않음 → 범위 뒤에 프레임이 있으면 원래 프레임으로).</summary>
    private LayerEffect? _preferExistingFor;
    private bool? _preferExisting;

    /// <summary>확인할 때 할 프레임 늘리기(null = 늘리지 않음, 원래 프레임 번호 기준)와 이를 만든 효과.</summary>
    private FrameExtension? _extension;
    private LayerEffect? _extensionOwner;
    /// <summary>
    /// 프레임 추가 미리보기에서 사본 칸 일부만 고른 효과의 범위(늘린 뒤 번호).
    /// 원래 프레임 번호만으로는 같은 원본에서 생긴 사본 1~2와 1~10을 구분할 수 없어 별도로 보관한다.
    /// </summary>
    private readonly Dictionary<LayerEffect, FrameRange> _virtualRangeOverrides = new(ReferenceEqualityComparer.Instance);
    private NumericUpDown? _extendCount;
    private ComboBox? _extendModeBox;
    private TextBlock? _extendNote;
    private readonly StackPanel _extendPanel = new() { Spacing = 2 };
    private TextBlock? _extendTarget;
    /// <summary>프레임 수 칸이 보이는 효과 번호.</summary>
    private int _extendFor = -1;
    private bool _extendSyncing;
    /// <summary>프레임 줄을 누른 때의 늘리기와 칸 수(끄는 동안 칸 번호를 그때 차례로 읽는다).</summary>
    private (FrameExtension? Extension, int Cells) _dragBase;

    /// <summary>프레임 줄 끝의 지우기 칸으로 지우기로 한 뒤 프레임 수(늘린 뒤 차례의 끝에서부터, 확인할 때 지움). 첫 프레임은 늘 남는다.</summary>
    private int _trim;
    /// <summary>
    /// 지우기 칸을 눌러 되돌릴 수 있는 프레임 수: 마지막으로 지운 한 번만(그 전에 지운 것은 되돌릴 수 없음). 지운 뒤 프레임을 늘리면 0 —
    /// 지운 프레임은 버려지고 늘린 프레임은 새로 더해진다.
    /// </summary>
    private int _restorable;

    /// <summary>늘린 뒤 프레임 수(지우기 전).</summary>
    private int ExtendedCount => _project.Frames.Count + (_extension?.Added ?? 0);

    /// <summary>실제로 지울 뒤 프레임 수(늘리기가 줄어도 첫 프레임은 남게).</summary>
    private int Trim => Math.Clamp(_trim, 0, ExtendedCount - 1);

    /// <summary>늘리고 뒤를 지운 뒤(미리보기) 프레임 수.</summary>
    private int VirtualCount => ExtendedCount - Trim;

    /// <summary>미리보기 프레임 번호 → 원래 프레임 번호.</summary>
    private int OriginalOf(int frame) => _extension?.OriginalFrame(frame) ?? frame;

    /// <summary>원래 프레임 번호 → 미리보기 프레임 번호.</summary>
    private int VirtualOf(int original) => _extension?.ExtendedFrame(original) ?? original;

    private bool IsCopyFrame(int frame) => _extension?.IsCopy(frame) ?? false;

    /// <summary>효과의 범위(늘린 뒤 번호).</summary>
    private (int From, int To) VRangeOf(LayerEffect e)
    {
        if (_virtualRangeOverrides.TryGetValue(e, out var direct)) return (direct.From, direct.To);
        var (from, to) = RangeOf(e);
        if (_extension is not { } x) return (from, to);
        var r = x.MapRange(new FrameRange(from, to));
        return (r.From, r.To);
    }

    /// <summary>
    /// 효과 목록(범위를 늘린 뒤 번호로, 지울 뒤 프레임은 뺀 범위로) — 미리보기와 확정에 쓴다. 차례는 <see cref="_effects"/>와 같다:
    /// 범위가 모두 지워지는 효과는 꺼서 남기고(확정 때 <see cref="CommitEffects"/>가 뺀다), 걸치는 효과는 남는 끝까지로 줄인다 — 프로젝트의 프레임 삭제와 같은 규칙.
    /// </summary>
    private IReadOnlyList<LayerEffect> VirtualEffects()
    {
        IReadOnlyList<LayerEffect> list = _extension is not { } x
            ? _effects
            : _effects.Select(e => _virtualRangeOverrides.TryGetValue(e, out var direct)
                ? e with { Frames = direct }
                : e.Frames is { } r ? e with { Frames = x.MapRange(r) } : e).ToArray();
        if (Trim == 0) return list;
        int last = VirtualCount - 1;
        return list.Select(e => e.Frames is not { } r || r.To <= last ? e
            : r.From > last ? e with { Enabled = false, Frames = new FrameRange(last, last) }
            : e with { Frames = new FrameRange(r.From, last) }).ToArray();
    }

    /// <summary>효과 <paramref name="index"/>의 범위가 지울 뒤 프레임 안에만 있어 확정 때 없어지는지.</summary>
    private bool TrimmedAway(int index)
        => Trim > 0 && index >= 0 && index < _effects.Count && _effects[index].Frames is not null && VRangeOf(_effects[index]).From >= VirtualCount;

    /// <summary>확정할 효과 목록: <see cref="VirtualEffects"/>에서 지울 프레임과 함께 없어지는 효과를 뺀다.</summary>
    private IReadOnlyList<LayerEffect> CommitEffects()
        => Trim == 0 ? VirtualEffects() : VirtualEffects().Where((_, i) => !TrimmedAway(i)).ToArray();

    /// <summary>
    /// 지우기 칸을 놓음: 뒤 프레임 <paramref name="count"/>개를 더 지우기로 한다(이번에 지운 것만 되돌릴 수 있음).
    /// 0 이하 = 마지막으로 지운 것을 되돌림. 프로젝트는 확인할 때 바뀐다.
    /// </summary>
    private void SetTrim(int count)
    {
        if (count <= 0)
        {
            int back = _restorable;
            _restorable = 0;
            ApplyTrim(Trim - back);
            SyncStripFrames(); // 되돌릴 것 없음 표시
            return;
        }
        int before = Trim;
        ApplyTrim(Trim + count);
        _restorable = Trim - before;
        SyncStripFrames();
    }

    /// <summary>지우기로 한 뒤 프레임 수를 <paramref name="next"/>로(첫 프레임은 남게) 하고 목록·미리보기·프레임 줄을 맞춘다.</summary>
    private void ApplyTrim(int next)
    {
        next = Math.Clamp(next, 0, ExtendedCount - 1);
        if (next == Trim) return;
        _trim = next;
        _previewFrame = Math.Clamp(_previewFrame, 0, VirtualCount - 1);
        ClampVirtualRanges(VirtualCount);
        RefreshRows();
        SyncExtendEditor();
        UpdateButtons();
        RestartRenders();
        SyncPlayback();
        SyncStripRange();
    }

    private PixelImage? VirtualSourceAt(int frame) => SourceAt(OriginalOf(frame));

    /// <summary>미리보기 프레임 <paramref name="frame"/>의 원본과 그 프레임 범위 안에 있는 효과(<paramref name="effects"/> = <see cref="VirtualEffects"/>).</summary>
    private (PixelImage? Source, IReadOnlyList<LayerEffect> Effects) AnimateAt(int frame, IReadOnlyList<LayerEffect> effects)
    {
        int original = OriginalOf(frame);
        if (SourceAt(original) is not { } source) return (null, []);
        return (source, LayerEffects.At(effects, frame, VirtualCount, _project.EffectPivot(_layer, original),
            LayerEffects.UsesOtherFrames(effects) ? VirtualSourceAt : null,
            f => _project.EffectPivot(_layer, OriginalOf(f))));
    }

    // ───────────── 설정 칸 ─────────────

    /// <summary>
    /// 공통 설정: 고른 효과의 범위를 기준으로 프레임 수와 늘리기 방법을 고른다.
    /// "원래 프레임으로"면 범위 뒤의 기존 프레임만 쓰고, 다른 방법이면 확인할 때 프로젝트 전체에 프레임을 더한다.
    /// </summary>
    private bool UsesVirtualTimeline(LayerEffect e)
    {
        if (_extension is not { } extension) return false;
        if (!ReferenceEquals(e, _extensionOwner)) return true;
        if (!_virtualRangeOverrides.TryGetValue(e, out var direct)) return false;
        return direct != extension.MapRange(new FrameRange(extension.From, extension.To));
    }

    private (int From, int To) EditableRangeOf(LayerEffect e)
        => UsesVirtualTimeline(e) ? VRangeOf(e) : RangeOf(e);

    private void ShowSharedExtendSettings()
    {
        _extendPanel.Children.Clear();
        _extendPanel.Children.Add(new TextBlock { Text = UiStrings.EffectShared_FrameExtend, FontWeight = Avalonia.Media.FontWeight.Bold });
        if (Selected < 0 || Selected >= _effects.Count) _extendPanel.Children.Add(Ui.Label(UiStrings.S_LayerEffectsDialog_FillSettings_12D0C7E74E));
        else if (_effects[Selected] is not MissingEffect) AddExtendEditor(Selected); // 모르는 효과는 고칠 수 없다
    }

    private void AddExtendEditor(int i)
    {
        var (from, to) = EditableRangeOf(_effects[i]);
        int n = to - from + 1;
        var count = Ui.Num(1, Math.Max(n, FrameExtension.MaxCount), n, 56);
        var mode = Ui.Combo(170, ExtendModes);
        var note = Ui.Label("");
        note.Foreground = AppTheme.DimTextBrush;
        note.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        note.MaxWidth = SettingsWidth - 14;
        _extendTarget = Ui.Label("");
        _extendTarget.Foreground = AppTheme.DimTextBrush;
        _extendCount = count;
        _extendModeBox = mode;
        _extendNote = note;
        _extendFor = i;
        SyncExtendEditor();
        count.ValueChanged += (_, _) =>
        {
            // 선택을 바꾸며 없어진 설정 칸이 늦게 ValueChanged를 보내도 새로 고른 효과를 건드리지 않는다.
            if (_extendSyncing || _extendFor != i || !ReferenceEquals(_extendCount, count)
                || i < 0 || i >= _effects.Count) return;
            var e = _effects[i];
            var (f, t) = EditableRangeOf(e);
            int c = (int)count.NumValue();
            if (UsesExisting(e)) SetRangeLength(i, c);
            // 칸은 다른 범위의 늘리기로 늘어 보이는 길이(MaxCount 넘을 수 있음)까지 보이므로, 이 범위의 새 늘리기는 늘릴 수 있는 수로 맞춘다
            else SetExtension(new FrameExtension(f, t, FrameExtension.ClampCount(t - f + 1, c), s_extendMode), e);
        };
        mode.SelectionChanged += (_, _) =>
        {
            // 없어진 ComboBox의 지연 이벤트를 현재 효과의 입력으로 오인하지 않는다.
            if (_extendSyncing || _extendFor != i || !ReferenceEquals(_extendModeBox, mode)
                || mode.SelectedIndex < 0 || i < 0 || i >= _effects.Count) return;
            var e = _effects[i];
            var anchored = Anchored(e);
            if (mode.SelectedIndex == 0)
            {
                _preferExistingFor = e;
                _preferExisting = true;
                // 더하려던 프레임 수만큼 뒤의 원래 프레임으로(있는 만큼)
                if (anchored is { } x) SetRangeLength(i, x.Count);
                else SyncExtendEditor();
                return;
            }
            _preferExistingFor = e;
            _preferExisting = false;
            s_extendMode = (FrameExtendMode)(mode.SelectedIndex - 1);
            if (anchored is { } a) SetExtension(new FrameExtension(a.From, a.To, a.Count, s_extendMode), e);
            else SyncExtendEditor();
        };
        ToolTip.SetTip(count, UiStrings.S_LayerEffectsDialog_Extend_AddExtendEditor_D32421D815
                              + UiStrings.S_LayerEffectsDialog_Extend_AddExtendEditor_2F234110A6
                              + UiStrings.S_LayerEffectsDialog_Extend_AddExtendEditor_62BCE2A82A);
        ToolTip.SetTip(mode, UiStrings.S_LayerEffectsDialog_Extend_AddExtendEditor_13C7CE4286
                             + UiStrings.S_LayerEffectsDialog_Extend_AddExtendEditor_F918A1A976);
        var countLabel = Ui.Label(UiStrings.S_LayerEffectsDialog_Extend_AddExtendEditor_82FB7C16B7);
        countLabel.Width = 72;
        var modeLabel = Ui.Label(UiStrings.S_LayerEffectsDialog_Extend_AddExtendEditor_9F00E1A6E8);
        modeLabel.Width = 72;
        _extendPanel.Children.Add(_extendTarget);
        _extendPanel.Children.Add(Ui.Row(countLabel, count, Ui.Label(UiStrings.S_LayerEffectsDialog_Extend_AddExtendEditor_090E611E8C)));
        _extendPanel.Children.Add(note);
        _extendPanel.Children.Add(Ui.Row(modeLabel, mode));
    }

    /// <summary>늘리기가 이 효과에서 만들어졌고 아직 그 효과 범위에 걸려 있으면 그 늘리기.</summary>
    private FrameExtension? Anchored(LayerEffect e)
        => ReferenceEquals(e, _extensionOwner) && _extension is { } x && RangeOf(e) == (x.From, x.To) ? x : null;

    /// <summary>
    /// 효과의 프레임 수 칸이 "원래 프레임으로"인지: 이 효과에 늘리기가 걸려 있으면 아니고, 이 효과에서 직접 고른 적이 있으면 그것,
    /// 아니면 범위 뒤에 프레임이 있을 때.
    /// </summary>
    private bool UsesExisting(LayerEffect e)
    {
        if (UsesVirtualTimeline(e)) return true;
        bool? preference = ReferenceEquals(e, _preferExistingFor) ? _preferExisting : null;
        return Anchored(e) is null && (preference ?? RangeOf(e).To < _project.Frames.Count - 1);
    }

    /// <summary>"원래 프레임으로": 효과 범위를 처음 프레임부터 <paramref name="length"/>개로(마지막 프레임까지) — 범위에 걸린 늘리기는 없앤다.</summary>
    private void SetRangeLength(int i, int length)
    {
        var e = _effects[i];
        if (UsesVirtualTimeline(e))
        {
            var (fromVirtual, _) = VRangeOf(e);
            SetVirtualRange(i, FrameRange.Of(fromVirtual,
                Math.Clamp(fromVirtual + length - 1, fromVirtual, VirtualCount - 1), VirtualCount));
            ShowSettings();
            SyncPlayback();
            return;
        }
        int n = _project.Frames.Count;
        var (from, _) = RangeOf(e);
        _preferExistingFor = e;
        _preferExisting = true;
        // 이 효과가 만든 늘리기를 없애면 칸 수가 바뀐다 — 범위가 그대로여도(Update가 아무것도 안 함) 프레임 줄·미리보기를 맞춘다.
        if (Anchored(e) is not null) SetExtension(null, null);
        Update(i, e with { Frames = FrameRange.Of(from, Math.Clamp(from + length - 1, from, n - 1), n) });
        ShowSettings(); // 범위 숫자 칸·범위 길이 안내 갱신
        SyncPlayback();
    }

    /// <summary>프레임 수 칸·방법 상자·안내를 지금 범위·늘리기에 맞춘다(효과 범위·늘리기가 바뀔 때).</summary>
    private void SyncExtendEditor()
    {
        if (_extendCount is not { } count || _extendModeBox is not { } mode || _extendNote is not { } note
            || _extendFor < 0 || _extendFor >= _effects.Count) return;
        var e = _effects[_extendFor];
        bool virtualTimeline = UsesVirtualTimeline(e);
        var (from, to) = virtualTimeline ? VRangeOf(e) : RangeOf(e);
        if (_extendTarget is { } target)
            target.Text = string.Format(global::System.Globalization.CultureInfo.CurrentCulture, UiStrings.EffectShared_Target, e.DisplayName, from + 1, to + 1);
        int n = to - from + 1, now = Anchored(e)?.Count ?? n;
        int frames = virtualTimeline ? VirtualCount : _project.Frames.Count;
        int after = frames - 1 - to;
        bool existing = UsesExisting(e);
        bool ownerRangeFixed = _extension is { } active && _extensionOwner is { } activeOwner
            && _virtualRangeOverrides.TryGetValue(activeOwner, out var ownerRange)
            && ownerRange != active.MapRange(new FrameRange(active.From, active.To));
        string other = _extension is { } pending && _extensionOwner is { } owner && !ReferenceEquals(e, owner)
            ? ownerRangeFixed ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Extend_SyncExtendEditor_A62BFC8D52,VirtualCount) : string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Extend_SyncExtendEditor_21BE29469F,owner.DisplayName,pending.Added)
            : "";
        _extendSyncing = true;
        if (existing)
        {
            WithKind(count, CountKind(n, frames - from));
            // 최소를 먼저 1로: 최대를 먼저 줄이면 그때의 최소(다른 방법일 때의 범위 길이) 아래로 못 내려가 큰 최대가 남는다(NumericUpDown 강제 조정).
            count.Minimum = 1;
            count.Maximum = Math.Max(n, frames - from);
            count.Value = n;
            mode.SelectedIndex = 0;
            note.Text = other.Length > 0 ? other
                : after > 0 ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Extend_SyncExtendEditor_B069C0BD72,after) : UiStrings.S_LayerEffectsDialog_Extend_SyncExtendEditor_37065AF08C;
        }
        else
        {
            WithKind(count, CountKind(n, null));
            count.Maximum = Math.Max(Math.Max(n, now), FrameExtension.MaxCount);
            count.Minimum = n;
            count.Value = now;
            mode.SelectedIndex = (int)(Anchored(e)?.Mode ?? s_extendMode) + 1;
            note.Text = Anchored(e) is { } x ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Extend_SyncExtendEditor_15BD7E7B69,x.Added) : other;
        }
        _extendSyncing = false;
    }

    /// <summary>프레임 수 칸 예시: 그대로(n)·×2·×3과 흔한 길이(8·12·16·24·32), "원래 프레임으로"면 끝 프레임까지(<paramref name="toEnd"/>)도.</summary>
    private static NumKind CountKind(int n, int? toEnd)
    {
        var values = new SortedSet<decimal> { n, n * 2, n * 3 };
        foreach (var v in new[] { 1, 8, 12, 16, 24, 32 })
            if (v > n || toEnd is not null) values.Add(v);
        if (toEnd is { } end) values.Add(end);
        values.RemoveWhere(v => v > (toEnd ?? Math.Max(n, FrameExtension.MaxCount)));
        return new("effect.frameCount", UiStrings.S_LayerEffectsDialog_Extend_CountKind_1B579E1C9B, [.. values],
            v => v == toEnd && v != n ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Extend_CountKind_9B2960D545,v) : v == n ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Extend_CountKind_9709EC01B8,v) : v == n * 2 ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Extend_CountKind_C66AF18AEF,v) : v == n * 3 ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Extend_CountKind_6D528965EF,v) : string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Extend_CountKind_C5B2B11647,v));
    }

    /// <summary>효과의 늘리기를 바꾸고(같은 수면 늘리지 않음) 미리보기·프레임 줄·설정 칸을 맞춘다. 소유자는 선택 상태가 아니라 입력을 낸 효과로 정한다.</summary>
    private void SetExtension(FrameExtension? extension, LayerEffect? owner)
    {
        var next = extension is { Added: > 0 } ? extension : null;
        if (next == _extension)
        {
            _extensionOwner = next is null ? null : owner ?? _extensionOwner;
            return;
        }
        int original = OriginalOf(_previewFrame);
        var previous = _extension;
        _extension = next;
        // 지운 상태에서 늘리기를 바꾸면 지운 프레임은 버린다(되돌리기 없음) — 늘린 프레임은 남은 프레임 뒤에 새로 더해진다
        if (Trim > 0) _restorable = 0;
        _extensionOwner = next is null ? null : owner;
        if (next is null && previous is { } oldExtension)
            CollapseVirtualRanges(oldExtension);
        else if (next is not null)
            ClampVirtualRanges(VirtualCount);
        _previewFrame = Math.Clamp(VirtualOf(original), 0, VirtualCount - 1);
        SyncExtendEditor();
        RestartRenders();
        SyncPlayback();
    }

    /// <summary>프레임 추가를 취소할 때 사본 칸 범위를 원래 프레임 범위로 되돌린다.</summary>
    private void CollapseVirtualRanges(FrameExtension oldExtension)
    {
        foreach (var (effect, range) in _virtualRangeOverrides.ToArray())
        {
            int index = _effects.FindIndex(e => ReferenceEquals(e, effect));
            if (index < 0) continue;
            int from = oldExtension.OriginalFrame(range.From);
            int to = oldExtension.OriginalFrame(range.To);
            var replacement = effect with { Frames = FrameRange.Of(Math.Min(from, to), Math.Max(from, to), _project.Frames.Count) };
            _effects[index] = replacement;
            if (ReferenceEquals(effect, _extensionOwner)) _extensionOwner = replacement;
            if (ReferenceEquals(effect, _preferExistingFor)) _preferExistingFor = replacement;
            RefreshEffectSummary(index);
        }
        _virtualRangeOverrides.Clear();
    }

    /// <summary>프레임 추가 길이가 바뀌어도 별도로 고른 사본 범위를 새 미리보기 끝 안에 둔다.</summary>
    private void ClampVirtualRanges(int count)
    {
        foreach (var effect in _virtualRangeOverrides.Keys.ToArray())
        {
            var range = _virtualRangeOverrides[effect];
            int from = Math.Clamp(range.From, 0, count - 1);
            int to = Math.Clamp(range.To, from, count - 1);
            _virtualRangeOverrides[effect] = new FrameRange(from, to);
            RefreshEffectSummary(_effects.FindIndex(e => ReferenceEquals(e, effect)));
        }
    }

    /// <summary>늘린 뒤 프레임 번호로 고른 효과 하나의 범위만 바꾼다. 프레임 추가 계획은 건드리지 않는다.</summary>
    private void SetVirtualRange(int index, FrameRange? range)
    {
        if (index < 0 || index >= _effects.Count) return;
        var effect = _effects[index];
        if (range is null)
        {
            _virtualRangeOverrides.Remove(effect);
            var all = effect with { Frames = null };
            if (all != effect)
            {
                Update(index, all);
                return;
            }
        }
        else
        {
            _virtualRangeOverrides[effect] = range.Value;
        }
        RefreshEffectSummary(index);
        Changed();
        SyncPlayback();
    }

    /// <summary>
    /// 범위 숫자 칸으로 늘리기를 만든 효과의 범위를 바꾸기 전: 그 늘리기를 새 범위로 옮긴다(프레임 수 그대로, 새 범위보다 작으면 늘리기 없음).
    /// 다른 효과가 우연히 같은 범위를 써도 그 효과의 범위 변경은 늘리기에 손대지 않는다.
    /// </summary>
    private void FollowRange(LayerEffect old, FrameRange? next)
    {
        if (!ReferenceEquals(old, _extensionOwner) || _extension is not { } x || RangeOf(old) != (x.From, x.To)) return;
        int n = _project.Frames.Count;
        var (from, to) = next is { } r ? (r.From, Math.Min(r.To, n - 1)) : (0, n - 1);
        FrameExtension? moved = x.Count > to - from + 1 ? new FrameExtension(from, to, x.Count, x.Mode) : null;
        SetExtension(moved, moved is null ? null : old);
    }

    /// <summary>
    /// 프레임 줄에서 칸 <paramref name="a"/> ~ <paramref name="b"/>를 끔(누른 때의 칸 차례 기준, <paramref name="b"/>가 칸 수 이상 = 마지막 칸 너머의 빈 칸).
    /// 원래 프레임 범위로 바꿔 고른 효과(없으면 추가 범위)에 넣고, 마지막 칸 너머까지 끌었거나 늘어난 칸(사본)을 덮으면 고른 칸 수만큼 늘린다.
    /// </summary>
    private void DragRange(int a, int b)
    {
        var (ext, cells) = _dragBase;
        bool selected = Selected >= 0 && Selected < _effects.Count;
        // 예정된 타임라인 안에서 끈 것은 고른 효과의 적용 범위만 정한다.
        // 전체를 덮어도 명시한 끝을 기억해야 나중에 다른 효과가 타임라인을 더 늘릴 때 함께 늘어나지 않는다.
        if (selected && ext is not null && b < cells)
        {
            SetVirtualRange(Selected, new FrameRange(a, b));
            ShowSettings();
            SyncStripRange();
            return;
        }
        // 현재 마지막 칸 너머로 끌면 기존 추가 방식을 그대로 사용해 공용 타임라인만 늘린다.
        // 먼저 정한 효과 범위는 현재 끝에 고정하고, 이번 효과는 끈 시작~끝을 별도 범위로 가진다.
        if (selected && ext is not null && b >= cells)
        {
            var selectedEffect = _effects[Selected];
            var owner = _extensionOwner;
            if (owner is not null && !ReferenceEquals(owner, selectedEffect)
                && !_virtualRangeOverrides.ContainsKey(owner))
            {
                var (ownerFrom, ownerTo) = VRangeOf(owner);
                _virtualRangeOverrides[owner] = new FrameRange(ownerFrom, ownerTo);
                RefreshEffectSummary(_effects.FindIndex(e => ReferenceEquals(e, owner)));
            }
            int count = FrameExtension.ClampCount(ext.Value.Length, ext.Value.Count + b - cells + 1);
            SetExtension(new FrameExtension(ext.Value.From, ext.Value.To, count, ext.Value.Mode), owner ?? selectedEffect);
            int to = Math.Min(b, VirtualCount - 1);
            SetVirtualRange(Selected, new FrameRange(Math.Min(a, to), to));
            ShowSettings();
            SyncStripRange();
            return;
        }
        int last = Math.Min(b, cells - 1);
        int lo = int.MaxValue, hi = -1;
        bool copies = false;
        for (int v = a; v <= last; v++)
        {
            int o = ext?.OriginalFrame(v) ?? v;
            lo = Math.Min(lo, o);
            hi = Math.Max(hi, o);
            copies |= ext?.IsCopy(v) ?? false;
        }
        if (hi < 0) return;
        var range = FrameRange.Of(lo, hi, _project.Frames.Count);
        if (!selected)
        {
            SetRangeFromStrip(range);
            return;
        }
        int n = hi - lo + 1;
        var next = _extension;
        LayerEffect? nextOwner = _extensionOwner;
        if (b >= cells || copies)
        {
            next = new FrameExtension(lo, hi, FrameExtension.ClampCount(n, b - a + 1), s_extendMode);
            nextOwner = _effects[Selected];
        }
        else if (Anchored(_effects[Selected]) is not null)
        {
            next = null; // 늘리기를 만든 효과의 범위를 원래 칸 안으로 다시 고름
            nextOwner = null;
        }
        SetRangeFromStrip(range);
        // 다른 효과의 범위를 원래 프레임 안에서 고치는 일은 기존 늘리기에 전혀 손대지 않는다.
        if (next != _extension || !ReferenceEquals(nextOwner, _extensionOwner))
            SetExtension(next, nextOwner);
    }

    // ───────────── 확정 ─────────────

    /// <summary>
    /// 미리보기 끝(창이 닫힘): 확인이면 효과와 선택한 프레임·캔버스 늘리기를 한 실행취소 단계로 확정한다. 취소면 모두 원래대로.
    /// </summary>
    private void EndPreview(int index, bool commit)
    {
        if (!commit)
        {
            _project.EndLayerEffectsPreview(index, _original, commit: false);
            return;
        }
        var list = VirtualEffects();
        EffectCanvasExpansion? expansion = null;
        try
        {
            if (_expandCanvas.On())
            {
                var plan = _confirmedCanvasExpansion ?? CanvasExpansion(list);
                if (!plan.IsNone(_project.Width, _project.Height)) expansion = plan;
            }
        }
        catch (InvalidOperationException ex)
        {
            _project.EndLayerEffectsPreview(index, _original, commit: false);
            ResultMessage = string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Extend_EndPreview_3FACEB3EDB,ex.Message);
            return;
        }
        int trim = Trim, keep = VirtualCount;
        if (_extension is null && expansion is null && trim == 0)
        {
            _project.EndLayerEffectsPreview(index, _original, commit: true);
            return;
        }
        int oldWidth = _project.Width, oldHeight = _project.Height;
        _project.EndLayerEffectsPreview(index, _original, commit: false);
        var commitList = CommitEffects();
        var change = ProjectChange.Layers
                     | (_extension is null && trim == 0 ? 0 : ProjectChange.Frames | ProjectChange.Tags | ProjectChange.Pixels)
                     | (expansion is null ? 0 : ProjectChange.Canvas | ProjectChange.Frames | ProjectChange.Settings | ProjectChange.Pixels);
        string label = _extension is not null && expansion is not null ? UiStrings.S_LayerEffectsDialog_Extend_EndPreview_FE2B132140
            : _extension is not null ? UiStrings.S_LayerEffectsDialog_Extend_EndPreview_E092F138F7
            : trim > 0 ? UiStrings.EffectStrip_TrimLabel : UiStrings.S_LayerEffectsDialog_Extend_EndPreview_7BCD2BFB57;
        _project.Edit(label, change, () =>
        {
            if (_extension is { } ext) _project.ExtendFrames(ext);
            _project.SetLayerEffects(index, commitList);
            if (trim > 0) _project.DeleteFrames(keep, trim);
            if (expansion is { } grow) ApplyCanvasExpansion(grow);
        });
        ResultMessage = string.Join(" · ", new[]
                        {
                            _extension is { } x ? ExtendSummary(x) : null,
                            trim > 0 ? TrimSummary(trim) : null,
                            expansion is { } grow ? CanvasExpansionSummary(oldWidth, oldHeight, grow) : null,
                        }.OfType<string>())
                        + UiStrings.S_LayerEffectsDialog_Extend_EndPreview_1FF7594D8B;
    }

    /// <summary>"뒤 프레임 3개 지움".</summary>
    internal static string TrimSummary(int count)
        => string.Format(global::System.Globalization.CultureInfo.CurrentCulture, UiStrings.EffectStrip_TrimSummary, count);

    /// <summary>"프레임 1~4: 4개 → 12개로 늘림 (같은 비율로)".</summary>
    internal static string ExtendSummary(FrameExtension x)
        => string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Extend_ExtendSummary_FC60832737,(x.From == x.To ? $"{x.From + 1}" : $"{x.From + 1}~{x.To + 1}"),x.Length,x.Count,FrameExtension.ModeName(x.Mode));
}
