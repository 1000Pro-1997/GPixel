using Gpixel.Core.Edit;
using Gpixel.Core.Model;
using Gpixel.App.Localization;

namespace Gpixel.App;

/// <summary>
/// 전·후 미리보기 아래 프레임 줄: 프레임마다 효과를 입힌 작은 그림. 고른 효과(없으면 "추가 범위")의 프레임 범위가 파랗게 보이고,
/// 끌어서 범위를 정하며(아래 숫자 칸과 같은 값), 누르면 그 프레임을 미리보기에 보인다.
/// 효과를 고른 채 범위 끝을 마지막 칸 너머로 끌면 점선 빈 칸이 생기며 그만큼 프레임을 늘린다(<see cref="DragRange"/>).
/// 프레임을 늘리는 중이면 늘린 뒤 차례의 프레임을 보이고 더해질 사본 칸은 점선 테두리.
/// </summary>
public sealed partial class LayerEffectsDialog
{
    private readonly FrameStrip _strip = new();

    private Control BuildFrameStrip()
    {
        _strip.FrameClicked += f =>
        {
            // 칸 그림은 작업 스레드에서 차례로 오므로 줄이 아직 이전 칸 수일 수 있다 — 지금 없는 프레임 칸은 무시
            if (f >= VirtualCount) return;
            _timer.Stop();
            _previewFrame = f;
            RefreshPreview();
            UpdatePlayButtons();
        };
        _strip.DragStarted += () =>
        {
            _dragBase = (_extension, VirtualCount);
            _stripDragging = true;
        };
        _strip.DragEnded += () =>
        {
            if (!_stripDragging) return;
            _stripDragging = false;
            ReleaseHold();
        };
        _strip.RangeDragged += DragRange;
        _strip.TrimReleased += SetTrim;
        _strip.RenderRequested += () =>
        {
            SyncStripFrames();
            if (_renders is { } job)
                for (int f = 0; f < job.Count; f++)
                    if (job.Results[f] is { } r) _strip.SetThumb(f, r.Thumb, r.Image.Width, r.Image.Height);
        };
        _renderTimer.Tick += (_, _) => OnRenderTimer();
        // 칸이 너비를 넘으면 다음 줄로(세로 스크롤은 여러 줄이 넘칠 때만).
        _strip.Width = PreviewBox * 2 + 12 - 14;
        RestartRenders();
        return new ScrollViewer
        {
            Content = _strip,
            Width = PreviewBox * 2 + 12,
            MaxHeight = FrameStrip.HeightFor(3),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
    }

    /// <summary>프레임 칸 수·사본 칸 표시·범위를 지금 상태(늘리는 중이면 늘린 뒤 차례)에 맞춘다. 칸 그림은 새 결과가 올 때까지 이전 그림.</summary>
    private void SyncStripFrames()
    {
        _strip.Restorable = Math.Min(_restorable, Trim);
        _strip.SetCount(VirtualCount, IsCopyFrame);
        SyncStripRange();
    }

    /// <summary>프레임 줄의 파란 범위(늘린 뒤 번호): 고른 효과의 범위, 효과를 고르지 않았으면 추가 범위.</summary>
    private void SyncStripRange()
    {
        int n = _project.Frames.Count;
        bool selected = Selected >= 0 && Selected < _effects.Count;
        _strip.CanExtend = selected;
        if (selected)
        {
            var (a, b) = VRangeOf(_effects[Selected]);
            int last = VirtualCount - 1;
            _strip.Range = (Math.Min(a, last), Math.Min(b, last));
            return;
        }
        var (from, to) = _addAll.On() ? (0, n - 1) : ((int)_addFrom.NumValue() - 1, (int)_addTo.NumValue() - 1);
        _strip.Range = (VirtualOf(from), Math.Min(VirtualCount - 1, _extension is { } x ? x.MapRange(new FrameRange(from, Math.Max(from, to))).To : to));
    }

    /// <summary>프레임 줄에서 끌어 정한 범위(원래 번호)를 고른 효과(없으면 추가 범위)에 넣는다. null = 모든 프레임.</summary>
    private void SetRangeFromStrip(FrameRange? range)
    {
        int n = _project.Frames.Count;
        if (Selected >= 0 && Selected < _effects.Count)
        {
            Update(Selected, _effects[Selected] with { Frames = range });
            ShowSettings(); // 숫자 칸 갱신
        }
        else
        {
            _addAll.IsChecked = range is null;
            _addFrom.Value = (range?.From ?? 0) + 1;
            _addTo.Value = (range?.To ?? n - 1) + 1;
            _addRangeTouched = true;
        }
        SyncStripRange();
    }

    /// <summary>
    /// 프레임 칸(너비가 차면 다음 줄로 줄바꿈, 세로 스크롤). 칸 = 효과를 입힌 그림 + 프레임 번호. 칸 그림은 받은 썸네일을 이 줄이 가진다(바꿀 때 버림).
    /// </summary>
    private sealed class FrameStrip : Control, IDisposable
    {
        private const int Cell = 44, Gap = 2, RowPitch = Cell + 4, Top = 3;

        /// <summary><paramref name="rows"/>줄일 때 높이.</summary>
        public static double HeightFor(int rows) => Top + Math.Max(1, rows) * RowPitch - 1;
        private static readonly IBrush RangeBrush = AppTheme.Brush(Color.FromArgb(70, AppTheme.AccentColor.R, AppTheme.AccentColor.G, AppTheme.AccentColor.B));
        private static readonly IBrush OutsideBrush = AppTheme.Brush(Color.FromArgb(140, 0, 0, 0));
        private static readonly IPen CurrentPen = new Pen(Brushes.White, 2);
        private static readonly IPen RangePen = new Pen(AppTheme.AccentBrush, 2);
        private static readonly IPen GhostPen = new Pen(AppTheme.AccentBrush, 1, new DashStyle([3, 2], 0));
        private static readonly IPen CopyPen = new Pen(AppTheme.DimTextBrush, 1, new DashStyle([2, 2], 0));
        private static readonly Color DeleteColor = Color.FromRgb(230, 90, 90);
        private static readonly IBrush DeleteBrush = AppTheme.Brush(DeleteColor);
        private static readonly IBrush DeleteVeil = AppTheme.Brush(Color.FromArgb(120, DeleteColor.R, DeleteColor.G, DeleteColor.B));
        private static readonly IPen DeletePen = new Pen(DeleteBrush, 2);
        private static readonly IPen TrashPen = new Pen(AppTheme.DimTextBrush, 1, new DashStyle([3, 2], 0));
        private static readonly IPen TrashActivePen = new Pen(DeleteBrush, 1, new DashStyle([3, 2], 0));

        private WriteableBitmap?[] _thumbs = [];
        private (int W, int H)[] _sizes = [];
        /// <summary>지금 효과로 다시 그린 칸인지(아니면 이전 효과의 그림).</summary>
        private bool[] _fresh = [];
        private bool[] _copies = [];
        private bool _pending;
        private (int From, int To) _range;
        private int _current, _press = -1, _dragTo = -1;
        private bool _dragging;
        /// <summary>끄는 동안 칸 수가 바뀌어 다시 그리기를 미뤘는지(칸 차례가 흔들리지 않게 — 놓을 때 <see cref="RenderRequested"/>).</summary>
        private bool _stale;
        /// <summary>지우기 칸(마지막 칸 다음, 점선)을 눌러 끄는 중인지와 빨갛게 보일 첫 칸(칸 수 = 지울 것 없음).</summary>
        private bool _trimming, _trimMoved, _trashHover;
        private int _trimFrom;
        private int _restorable;

        public FrameStrip()
        {
            Height = HeightFor(1);
            HorizontalAlignment = HorizontalAlignment.Left;
            VerticalAlignment = VerticalAlignment.Top; // 줄이 늘어도(끌어 늘리기) 앞 칸 자리가 그대로
            Cursor = new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(this, UiStrings.S_LayerEffectsDialog_Frames_FrameStrip_2E9C335D70);
        }

        /// <summary>
        /// 지우기 칸을 놓음: 빨갛게 보인 뒤 프레임 수(1 이상 = 더 지우기로 함). 끌지 않고 눌렀다 떼면 0 — 마지막으로 지운 것을 되돌린다(<see cref="Restorable"/>).
        /// </summary>
        public event Action<int>? TrimReleased;

        /// <summary>지우기 칸을 눌러 되돌릴 수 있는 프레임 수(마지막으로 지운 한 번, 0 = 되돌릴 것 없음). 지우기 칸에 이 수를 보인다.</summary>
        public int Restorable
        {
            get => _restorable;
            set
            {
                if (_restorable == value) return;
                _restorable = value;
                UpdateWidth();
                InvalidateVisual();
            }
        }

        /// <summary>지우기 칸이 보이는지: 지울 수 있는 프레임(첫 프레임 뒤)이 있거나 되돌릴 것이 있을 때. 마지막 칸 너머로 늘리며 끄는 동안은 숨긴다.</summary>
        private bool ShowsTrash => (_thumbs.Length > 1 || _restorable > 0) && !(_dragging && _dragTo >= _thumbs.Length);

        /// <summary>지우기 칸 위인지(그 칸 번호 = 칸 수).</summary>
        private bool OnTrash(Avalonia.Point p)
            => ShowsTrash && CellAt(p) == _thumbs.Length && CellRect(_thumbs.Length).Contains(p);

        /// <summary>누름(끌지 않음): 그 프레임을 미리보기에.</summary>
        public event Action<int>? FrameClicked;

        /// <summary>누름: 끌기 시작(이때의 칸 차례로 <see cref="RangeDragged"/>를 읽음).</summary>
        public event Action? DragStarted;

        /// <summary>끄는 중: 누른 칸 ~ 지금 칸(작은 쪽, 큰 쪽). <see cref="CanExtend"/>면 큰 쪽이 칸 수 이상(마지막 칸 너머)일 수 있다.</summary>
        public event Action<int, int>? RangeDragged;

        /// <summary>놓음(또는 끌기 취소): 누른 뒤 끝. <see cref="RenderRequested"/> 뒤에 온다.</summary>
        public event Action? DragEnded;

        /// <summary>놓음: 끄는 동안 미룬 칸 다시 그리기 — 받는 쪽이 지금 상태로 <see cref="SetFrames"/>를 다시 부른다.</summary>
        public event Action? RenderRequested;

        /// <summary>마지막 칸 너머로 끌어 늘릴 수 있는지.</summary>
        public bool CanExtend { get; set; }

        public (int From, int To) Range
        {
            get => _range;
            set
            {
                if (_range == value) return;
                _range = value;
                InvalidateVisual();
            }
        }

        public int Current
        {
            get => _current;
            set
            {
                if (_current == value) return;
                _current = value;
                InvalidateVisual();
            }
        }

        /// <summary>다시 그리는 중이 오래 걸려 아직 안 바뀐 칸에 회색 덮개를 씌울지.</summary>
        public bool Pending
        {
            get => _pending;
            set
            {
                if (_pending == value) return;
                _pending = value;
                InvalidateVisual();
            }
        }

        /// <summary>
        /// 칸 수와 사본 칸을 맞춘다. 남는 칸의 그림은 새 그림이 올 때까지 그대로 두고(이전 효과의 그림), 모든 칸을 다시 그릴 칸으로 표시한다.
        /// </summary>
        /// <param name="isCopy">더해질 사본 칸(점선 테두리).</param>
        public void SetCount(int count, Func<int, bool> isCopy)
        {
            if (_dragging && count != _thumbs.Length)
            {
                _stale = true;
                return;
            }
            _stale = false;
            for (int f = count; f < _thumbs.Length; f++) _thumbs[f]?.Dispose();
            Array.Resize(ref _thumbs, count);
            Array.Resize(ref _sizes, count);
            _fresh = new bool[count];
            _copies = new bool[count];
            for (int f = 0; f < count; f++) _copies[f] = isCopy(f);
            UpdateWidth();
            InvalidateVisual();
        }

        /// <summary>칸 <paramref name="f"/>의 새 그림(원래 크기 <paramref name="width"/>×<paramref name="height"/>를 줄인 것). 이 줄이 가진다.</summary>
        public void SetThumb(int f, WriteableBitmap thumb, int width, int height)
        {
            // 끄는 동안 칸 수를 미뤄 지금 칸보다 뒤 프레임 그림이 오면 넣지 않는다 — 버리지도 않는다: 그 그림은 아직 계산 결과가 가지고 있어
            // 놓을 때(RenderRequested) 다시 넣는다(여기서 버리면 버린 그림을 넣어 그리다 NullReferenceException). 줄에 넣지 않은 그림은 DropUnshown이 버린다.
            if ((uint)f >= (uint)_thumbs.Length) return;
            if (!ReferenceEquals(_thumbs[f], thumb)) _thumbs[f]?.Dispose();
            _thumbs[f] = thumb;
            _sizes[f] = (width, height);
            _fresh[f] = true;
            InvalidateVisual();
        }

        /// <summary>이 줄이 그 그림을 보이고 있는지(가지고 있는지).</summary>
        public bool Shows(WriteableBitmap bitmap) => Array.IndexOf(_thumbs, bitmap) >= 0;

        /// <summary>한 줄에 들어가는 칸 수(너비 기준).</summary>
        private int Columns
        {
            get
            {
                double w = double.IsNaN(Width) ? Bounds.Width : Width;
                return Math.Max(1, (int)Math.Floor((w - Gap) / (Cell + Gap)));
            }
        }

        /// <summary>칸 수(끄는 중 늘린 빈 칸 포함)에 맞춰 줄 수 = 높이를 정한다.</summary>
        private void UpdateWidth()
        {
            int cells = Math.Max(1, Math.Max(_thumbs.Length + (ShowsTrash ? 1 : 0), _dragTo + 1));
            Height = HeightFor((cells + Columns - 1) / Columns);
        }

        /// <summary>점 아래 칸 번호: 줄 × 한 줄 칸 수 + 열(오른쪽 밖은 그 줄 끝 칸, 마지막 줄 아래는 그다음 줄 = 칸 수 너머).</summary>
        private int CellAt(Avalonia.Point p)
        {
            int cols = Columns;
            int col = Math.Clamp((int)Math.Floor((p.X - Gap) / (Cell + Gap)), 0, cols - 1);
            int row = Math.Max(0, (int)Math.Floor((p.Y - Top) / RowPitch));
            return row * cols + col;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (_thumbs.Length == 0 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (OnTrash(e.GetPosition(this)))
            {
                _trimming = true;
                _trimMoved = false;
                _trimFrom = _thumbs.Length;
                e.Pointer.Capture(this);
                e.Handled = true;
                InvalidateVisual();
                return;
            }
            _press = Math.Min(CellAt(e.GetPosition(this)), _thumbs.Length - 1);
            _dragging = false;
            DragStarted?.Invoke();
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var pos = e.GetPosition(this);
            if (_trimming)
            {
                // 첫 프레임은 지우지 않는다. 지우기 칸(또는 그 너머)으로 돌아오면 지울 것 없음.
                int f = CellAt(pos);
                int from = f >= _thumbs.Length ? _thumbs.Length : Math.Max(1, f);
                if (from == _trimFrom) return;
                _trimFrom = from;
                _trimMoved = true;
                if (from < _thumbs.Length) this.BringIntoView(CellRect(from));
                InvalidateVisual();
                return;
            }
            if (_press < 0)
            {
                bool hover = OnTrash(pos);
                if (hover == _trashHover) return;
                _trashHover = hover;
                ToolTip.SetTip(this, !hover ? UiStrings.S_LayerEffectsDialog_Frames_FrameStrip_2E9C335D70
                    : Restorable > 0 ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture, UiStrings.EffectStrip_TrashTipRestore, Restorable)
                    : UiStrings.EffectStrip_TrashTip);
                InvalidateVisual();
                return;
            }
            int f2 = Math.Min(CellAt(pos), CanExtend ? _thumbs.Length + FrameExtension.MaxCount : _thumbs.Length - 1);
            if (!_dragging && f2 == _press) return;
            _dragging = true;
            if (f2 != _dragTo)
            {
                _dragTo = f2;
                UpdateWidth();
                InvalidateVisual();
                if (f2 >= _thumbs.Length) this.BringIntoView(CellRect(f2)); // 창 밖까지 끌면 따라 보이게(보이는 칸이면 그대로)
            }
            RangeDragged?.Invoke(Math.Min(_press, f2), Math.Max(_press, f2));
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            if (!_trashHover) return;
            _trashHover = false;
            ToolTip.SetTip(this, UiStrings.S_LayerEffectsDialog_Frames_FrameStrip_2E9C335D70);
            InvalidateVisual();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (_trimming)
            {
                int count = _thumbs.Length - _trimFrom;
                bool moved = _trimMoved;
                EndTrim();
                e.Pointer.Capture(null);
                if (count > 0) TrimReleased?.Invoke(count);
                else if (!moved && Restorable > 0) TrimReleased?.Invoke(0);
                return;
            }
            if (_press < 0) return;
            int press = _press;
            bool dragged = _dragging;
            e.Pointer.Capture(null);
            EndDrag();
            if (!dragged) FrameClicked?.Invoke(press);
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            EndTrim();
            EndDrag();
        }

        /// <summary>지우기 칸 끌기 끝(지우기는 놓을 때만 — 취소면 아무것도 지우지 않음).</summary>
        private void EndTrim()
        {
            if (!_trimming) return;
            _trimming = false;
            _trimFrom = _thumbs.Length;
            InvalidateVisual();
        }

        private void EndDrag()
        {
            _press = _dragTo = -1;
            _dragging = false;
            UpdateWidth();
            InvalidateVisual();
            if (_stale)
            {
                // 미룬 동안 늘리기가 또 바뀌었을 수 있으므로 그때 받은 칸 수·그리기가 아니라 지금 상태로 다시 그린다.
                _stale = false;
                RenderRequested?.Invoke();
            }
            DragEnded?.Invoke();
        }

        private Rect CellRect(int f)
        {
            int cols = Columns;
            return new(Gap + f % cols * (Cell + Gap), Top + f / cols * RowPitch, Cell, Cell);
        }

        /// <summary>칸 <paramref name="from"/>~<paramref name="to"/>을 줄마다 덮는 사각형(줄바꿈되면 줄마다 하나).</summary>
        private IEnumerable<Rect> RangeRects(int from, int to)
        {
            int cols = Columns;
            for (int row = from / cols; row <= to / cols; row++)
            {
                int a = Math.Max(from, row * cols), b = Math.Min(to, row * cols + cols - 1);
                yield return new Rect(Gap / 2.0 + a % cols * (Cell + Gap), Top - 2 + row * RowPitch, (b - a + 1) * (Cell + Gap), Cell + 4);
            }
        }

        public override void Render(DrawingContext g)
        {
            g.FillRectangle(AppTheme.BackBrush, new Rect(Bounds.Size));
            if (_thumbs.Length == 0) return;
            // 끄는 동안은 누른 때의 칸 차례로 끈 칸을, 아니면 받은 범위를 보인다.
            var (from, to) = _dragging && _dragTo >= 0 ? (Math.Min(_press, _dragTo), Math.Max(_press, _dragTo)) : _range;
            foreach (var r in RangeRects(from, to)) g.FillRectangle(RangeBrush, r);
            for (int f = 0; f < _thumbs.Length; f++)
            {
                var cell = CellRect(f);
                g.FillRectangle(AppTheme.InputBrush, cell);
                if (_thumbs[f] is { } bmp)
                {
                    var shown = ImageUtil.DrawFittedThumb(g, bmp, new Size(_sizes[f].W, _sizes[f].H), cell, checker: true);
                    if (_pending && !_fresh[f]) RenderOverlay.DrawVeil(g, shown);
                }
                if (f < from || f > to) g.FillRectangle(OutsideBrush, cell);
                if (_copies[f]) g.DrawRectangle(null, CopyPen, cell.Deflate(0.5));
                AppTheme.DrawTextIn(g, (f + 1).ToString(), new Rect(cell.X + 2, cell.Bottom - 14, cell.Width - 4, 13), AppTheme.TextBrush, AppTheme.SmallFontSize - 1);
            }
            // 마지막 칸 너머로 끄는 중: 더해질 빈 칸(점선)
            for (int f = _thumbs.Length; f <= to; f++)
            {
                var cell = CellRect(f);
                g.DrawRectangle(null, GhostPen, cell.Deflate(0.5));
                AppTheme.DrawTextIn(g, "+", cell, AppTheme.DimTextBrush, center: true);
                AppTheme.DrawTextIn(g, (f + 1).ToString(), new Rect(cell.X + 2, cell.Bottom - 14, cell.Width - 4, 13), AppTheme.TextBrush, AppTheme.SmallFontSize - 1);
            }
            foreach (var r in RangeRects(from, to)) g.DrawRectangle(null, RangePen, r.Deflate(1));
            if (_current >= 0 && _current < _thumbs.Length)
                g.DrawRectangle(null, CurrentPen, CellRect(_current).Deflate(1));
            // 지우기 칸을 끄는 중: 놓으면 지울 뒤 칸들(빨간 덮개·테두리)
            if (_trimming)
                for (int f = _trimFrom; f < _thumbs.Length; f++)
                {
                    var cell = CellRect(f);
                    g.FillRectangle(DeleteVeil, cell);
                    g.DrawRectangle(null, DeletePen, cell.Deflate(1));
                }
            if (ShowsTrash) DrawTrash(g, CellRect(_thumbs.Length));
        }

        /// <summary>마지막 칸 다음의 지우기 칸: 점선 칸에 휴지통(누르거나 끄는 중·마우스가 올라가면 빨강), 되돌릴 수 있는 프레임이 있으면 그 수.</summary>
        private void DrawTrash(DrawingContext g, Rect cell)
        {
            bool active = _trimming || _trashHover;
            g.DrawRectangle(null, active ? TrashActivePen : TrashPen, cell.Deflate(0.5));
            var icon = _restorable > 0 ? new Rect(cell.X, cell.Y, cell.Width, cell.Height - 12) : cell;
            VectorIcons.Draw(g, VectorIcon.Trash, icon, 18, active || _restorable > 0 ? DeleteBrush : AppTheme.DimTextBrush, 1.75);
            if (_restorable > 0)
                AppTheme.DrawTextIn(g, string.Format(global::System.Globalization.CultureInfo.CurrentCulture, UiStrings.EffectStrip_TrashCount, _restorable),
                    new Rect(cell.X, cell.Bottom - 14, cell.Width, 13), DeleteBrush, AppTheme.SmallFontSize - 1, center: true);
        }

        public void Dispose()
        {
            foreach (var b in _thumbs) b?.Dispose();
            _thumbs = [];
            _sizes = [];
            _fresh = [];
            _copies = [];
        }
    }
}
