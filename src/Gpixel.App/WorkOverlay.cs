using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Gpixel.App.Localization;
using Gpixel.Core.IO;

namespace Gpixel.App;

/// <summary>
/// 오래 걸리는 저장·열기·내보내기를 작업 스레드에서 돌리는 동안 창을 검은 반투명 덮개(제목, 돌아가는 로딩 아이콘, 퍼센트)로 덮고 편집을 막는다.
/// <see cref="OverlayDelay"/> 안에 끝나면 덮개 없이 끝난다(그동안은 UI가 기다리므로 입력이 끼어들지 않는다).
/// 덮개가 있는 동안: 덮은 창의 누르기·키·글자 입력을 먹고, 다른 창은 끄고, 창 닫기는 끝난 뒤로 미룬다. 화면 그리기(효과 계산 결과 등)는 계속된다.
/// 일은 프로젝트를 읽기만 해야 한다 — 프로젝트를 고치는 곳(AI 도구 등)은 <see cref="WhenIdle"/>을 기다린 뒤 고친다.
/// </summary>
internal static class WorkOverlay
{
    /// <summary>이보다 오래 걸리면 덮개를 띄운다(효과 계산의 회색 덮개와 같은 기준).</summary>
    public static readonly TimeSpan OverlayDelay = RenderService.OverlayDelay;

    private static int _depth;
    private static TaskCompletionSource? _idle;
    private static readonly List<Window> CloseAfter = [];

    /// <summary>덮개를 띄운(또는 띄울 수 있는) 일이 도는 중인지. 작업 스레드에서 읽어도 된다.</summary>
    public static bool IsActive => Volatile.Read(ref _depth) > 0;

    /// <summary>도는 일이 끝났을 때(UI 스레드) — 멈춰 둔 화면 계산 등을 이어 간다.</summary>
    public static event Action? Idle;

    /// <summary>도는 일이 없으면 끝난 작업, 있으면 그 일이 끝날 때 끝나는 작업(이어서 UI 스레드에서 프로젝트를 고쳐도 된다).</summary>
    public static Task WhenIdle() => _idle?.Task ?? Task.CompletedTask;

    /// <summary>
    /// <paramref name="work"/>를 작업 스레드에서 돌리고 끝날 때까지 기다린다(UI 메시지는 계속 돈다). 예외는 그대로 다시 던진다.
    /// UI 스레드에서만 부른다. 이미 도는 일 안에서 부르면 덮개 없이 그 자리에서 돌린다.
    /// </summary>
    /// <param name="owner">덮을 창(또는 그 안의 컨트롤). null이면 활성 창.</param>
    /// <param name="title">덮개 제목(예: "저장하는 중").</param>
    public static T Run<T>(Visual? owner, string title, Func<WorkProgress, T> work)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_depth > 0) return work(new WorkProgress());
        Interlocked.Increment(ref _depth);
        _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var progress = new WorkProgress();
            var task = Task.Run(() => work(progress));
            if (Task.WaitAny([task], OverlayDelay) < 0 && Ui.Owner(owner) is { } host)
                using (BusyView.Show(host, title, progress))
                    Ui.Wait(task.ContinueWith(_ => 0, TaskScheduler.Default));
            return task.GetAwaiter().GetResult();
        }
        finally
        {
            Interlocked.Decrement(ref _depth);
            var idle = _idle;
            _idle = null;
            idle?.TrySetResult();
            foreach (var w in CloseAfter) Dispatcher.UIThread.Post(w.Close);
            CloseAfter.Clear();
            Idle?.Invoke();
        }
    }

    /// <inheritdoc cref="Run{T}"/>
    public static void Run(Visual? owner, string title, Action<WorkProgress> work) => Run(owner, title, p =>
    {
        work(p);
        return 0;
    });

    /// <summary>도는 일이 끝나면 <paramref name="window"/>를 닫는다(닫기를 지금은 취소한 창). 도는 일이 없으면 아무것도 안 한다.</summary>
    public static void CloseWhenIdle(Window window)
    {
        if (IsActive && !CloseAfter.Contains(window)) CloseAfter.Add(window);
    }

    /// <summary>덮개 화면: 창의 덮개 층(OverlayLayer)에 창 크기로 얹는다.</summary>
    private sealed class BusyView : IDisposable
    {
        private static readonly IBrush Shade = AppTheme.Brush(Color.FromArgb(150, 0, 0, 0));

        private readonly Window _host;
        private readonly OverlayLayer? _layer;
        private readonly Border _root;
        private readonly DispatcherTimer _timer;
        private readonly List<Window> _disabled = [];
        private readonly IInputElement? _focus;

        private BusyView(Window host, string title, WorkProgress progress)
        {
            _host = host;
            var spinner = new Spinner { Width = 36, Height = 36, HorizontalAlignment = HorizontalAlignment.Center };
            var percent = new TextBlock { Text = "0%", HorizontalAlignment = HorizontalAlignment.Center, Foreground = AppTheme.TextBrush, FontSize = 13 };
            var bar = new ProgressBar { Minimum = 0, Maximum = 1, Width = 240, MinWidth = 240, Height = 4, MinHeight = 4, Foreground = AppTheme.AccentBrush };
            var box = new Border
            {
                Background = AppTheme.PanelBrush,
                BorderBrush = AppTheme.BorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(24, 18),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        spinner,
                        new TextBlock { Text = title, HorizontalAlignment = HorizontalAlignment.Center, Foreground = AppTheme.TextBrush, FontWeight = FontWeight.SemiBold, FontSize = 14 },
                        percent,
                        bar,
                        new TextBlock { Text = UiStrings.Busy_Hint, HorizontalAlignment = HorizontalAlignment.Center, Foreground = AppTheme.DimTextBrush, FontSize = 11 },
                    },
                },
            };
            _root = new Border { Background = Shade, Child = box, Focusable = true };
            _timer = new DispatcherTimer(RenderOverlay.FrameInterval, DispatcherPriority.Render, (_, _) =>
            {
                double f = progress.Fraction;
                percent.Text = $"{(int)Math.Floor(f * 100)}%";
                bar.Value = f;
                spinner.InvalidateVisual();
            });

            _layer = OverlayLayer.GetOverlayLayer(host);
            if (_layer is not null)
            {
                Fit();
                _layer.PropertyChanged += OnLayerChanged;
                _layer.Children.Add(_root);
            }
            host.AddHandler(InputElement.KeyDownEvent, Swallow, RoutingStrategies.Tunnel, handledEventsToo: true);
            host.AddHandler(InputElement.KeyUpEvent, Swallow, RoutingStrategies.Tunnel, handledEventsToo: true);
            host.AddHandler(InputElement.TextInputEvent, Swallow, RoutingStrategies.Tunnel, handledEventsToo: true);
            host.Closing += OnClosing;
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
                foreach (var w in lifetime.Windows)
                    if (!ReferenceEquals(w, host) && w.IsEnabled)
                    {
                        w.IsEnabled = false;
                        _disabled.Add(w);
                    }
            _focus = host.FocusManager?.GetFocusedElement();
            _root.Focus();
            _timer.Start();
        }

        public static BusyView Show(Window host, string title, WorkProgress progress) => new(host, title, progress);

        private void Fit()
        {
            _root.Width = _layer!.Bounds.Width;
            _root.Height = _layer.Bounds.Height;
        }

        private void OnLayerChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Visual.BoundsProperty) Fit();
        }

        private static void Swallow(object? sender, RoutedEventArgs e) => e.Handled = true;

        private void OnClosing(object? sender, WindowClosingEventArgs e)
        {
            e.Cancel = true;
            CloseWhenIdle(_host);
        }

        public void Dispose()
        {
            _timer.Stop();
            if (_layer is not null)
            {
                _layer.PropertyChanged -= OnLayerChanged;
                _layer.Children.Remove(_root);
            }
            _host.RemoveHandler(InputElement.KeyDownEvent, Swallow);
            _host.RemoveHandler(InputElement.KeyUpEvent, Swallow);
            _host.RemoveHandler(InputElement.TextInputEvent, Swallow);
            _host.Closing -= OnClosing;
            foreach (var w in _disabled) w.IsEnabled = true;
            if (_focus is InputElement focus && TopLevel.GetTopLevel(focus) is not null) focus.Focus();
        }
    }

    /// <summary>돌아가는 로딩 아이콘(덮개가 다시 그릴 때마다 시간만큼 돈다).</summary>
    private sealed class Spinner : Control
    {
        public override void Render(DrawingContext g)
        {
            var area = new Rect(Bounds.Size);
            double size = Math.Min(area.Width, area.Height);
            double angle = Environment.TickCount64 % 900 / 900.0 * 2 * Math.PI;
            var c = area.Center;
            using (g.PushTransform(Matrix.CreateTranslation(-c.X, -c.Y) * Matrix.CreateRotation(angle) * Matrix.CreateTranslation(c.X, c.Y)))
                VectorIcons.Draw(g, VectorIcon.LoaderCircle, area, size, AppTheme.AccentBrush, 2.5);
        }
    }
}
