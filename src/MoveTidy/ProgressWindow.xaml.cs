using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MoveTidy.Core;

namespace MoveTidy;

/// <summary>右下角的小進度窗：搬完後自動關閉；有失敗時留著讓人看。</summary>
public partial class ProgressWindow : Window
{
    readonly IReadOnlyList<PlannedItem> _plan;
    readonly string _dest;
    readonly bool _autoClose;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    int _countdown = 3;
    MoveResult? _result;
    string? _error;

    public ProgressWindow(IReadOnlyList<PlannedItem> plan, string dest, bool autoClose)
    {
        InitializeComponent();
        _plan = plan;
        _dest = dest;
        _autoClose = autoClose;
        _timer.Tick += (_, _) => { if (--_countdown <= 0) Close(); else Render(); };
        L.I.LanguageChanged += Render;
        Loaded += OnLoaded;
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) { StopCountdown(); DragMove(); } };
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 4;
        Top = area.Bottom - ActualHeight - 4;
        SizeChanged += (_, _) => Top = area.Bottom - ActualHeight - 4;

        Render();
        var progress = new Progress<int>(n => SetBar(n));
        try
        {
            _result = await Task.Run(() => Mover.Execute(_plan, _dest, progress));
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        SetBar(_plan.Count);
        Render();

        bool ok = _error is null && _result!.Failures.Count == 0;
        if (ok && _autoClose) _timer.Start();
        else Buttons.Visibility = Visibility.Visible;
    }

    void SetBar(int done)
    {
        var track = ((FrameworkElement)Bar.Parent).ActualWidth;
        Bar.Width = _plan.Count == 0 ? track : track * done / _plan.Count;
    }

    void Render()
    {
        int moving = _plan.Count(p => p.Skip == SkipReason.None);
        PathText.Text = "→ " + PickWindow.ShortPath(_dest, 40);
        PathText.ToolTip = _dest;
        if (_result is null && _error is null)
        {
            TitleText.Text = L.F("moving", moving);
            LeftText.Text = "";
            RightText.Text = "";
            return;
        }

        int failed = _error is not null ? moving : _result!.Failures.Count;
        bool ok = failed == 0;
        StatusIcon.Text = ok ? "" : "";
        StatusIcon.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "Accent" : "WarnFg");
        IconBox.Background = (System.Windows.Media.Brush)FindResource(ok ? "AccentSoft" : "WarnBg");
        Bar.Background = (System.Windows.Media.Brush)FindResource(ok ? "Accent" : "WarnFg");
        TitleText.Text = ok ? L.Get("done") : L.F("done_fail", failed);

        var parts = new List<string>();
        if (_result is not null)
        {
            parts.Add(_result.Renamed > 0 ? L.F("renamed_n", _result.Renamed) : L.Get("no_conflict"));
            if (_result.Skipped > 0) parts.Add(L.F("skipped_n", _result.Skipped));
        }
        LeftText.Text = string.Join(" · ", parts);
        RightText.Text = _timer.IsEnabled ? L.F("closing", _countdown) : "";

        if (_error is not null)
        {
            FailList.ItemsSource = new[] { new { Name = _dest, Message = _error } };
            FailList.Visibility = Visibility.Visible;
        }
        else if (_result!.Failures.Count > 0)
        {
            FailList.ItemsSource = _result.Failures.Select(f => new { Name = Path.GetFileName(f.Source), Message = Explain(f) }).ToList();
            FailList.Visibility = Visibility.Visible;
        }
    }

    /// <summary>常見錯誤換成中文說明，其他的保留系統原文。</summary>
    static string Explain(MoveFailure f) => f.Code switch
    {
        32 or 33 => L.Get("err_in_use"),
        5 => L.Get("err_denied"),
        39 or 112 => L.Get("err_disk_full"),
        206 => L.Get("err_too_long"),
        2 or 3 => L.Get("err_not_found"),
        _ => L.F("error", f.Message),
    };

    void StopCountdown()
    {
        if (!_timer.IsEnabled) return;
        _timer.Stop();
        Buttons.Visibility = Visibility.Visible;
        Render();
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(_dest)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_dest}\"") { UseShellExecute = true });
        Close();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        L.I.LanguageChanged -= Render;
        base.OnClosed(e);
    }
}
