using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MoveTidy.Core;

namespace MoveTidy;

/// <summary>
/// 右下角的小進度窗：搬完後自動關閉（滑鼠移上去就停住）；有失敗時留著讓人看。
/// 搬完可以按「撤回」；也用來執行右鍵的「撤回上一次移動」。
/// </summary>
public partial class ProgressWindow : Window
{
    readonly IReadOnlyList<PlannedItem>? _plan;
    readonly string _dest;
    readonly bool _autoClose;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    int _countdown = 5;

    MoveResult? _result;          // 移動的結果
    MoveBatch? _batch;            // 這次移動的紀錄（撤回用）
    bool _undoing;                // 現在顯示的是撤回
    UndoResult? _undo;            // 撤回的結果
    string? _error;

    public ProgressWindow(IReadOnlyList<PlannedItem> plan, string dest, bool autoClose)
    {
        InitializeComponent();
        _plan = plan;
        _dest = dest;
        _autoClose = autoClose;
        Init();
    }

    /// <summary>右鍵「撤回上一次移動」：一打開就撤回這一批。</summary>
    public ProgressWindow(MoveBatch batch, bool autoClose)
    {
        InitializeComponent();
        _batch = batch;
        _dest = batch.Dest;
        _autoClose = autoClose;
        _undoing = true;
        Init();
    }

    void Init()
    {
        _timer.Tick += (_, _) => { if (--_countdown <= 0) Close(); else Render(); };
        L.I.LanguageChanged += Render;
        Loaded += OnLoaded;
        MouseEnter += (_, _) => StopCountdown();
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) { StopCountdown(); DragMove(); } };
    }

    int Total => _undoing ? _batch!.Items.Count : _plan!.Count;

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 4;
        Top = area.Bottom - ActualHeight - 4;
        SizeChanged += (_, _) => Top = area.Bottom - ActualHeight - 4;

        if (_undoing) { await RunUndo(); return; }

        Render();
        var progress = new Progress<int>(SetBar);
        try
        {
            _result = await Task.Run(() => Mover.Execute(_plan!, _dest, progress));
            _batch = MoveBatch.From(_result, _dest);
            try { MoveHistory.Add(_batch); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        SetBar(Total);
        Finish(ok: _error is null && _result!.Failures.Count == 0, canUndo: _batch is { Items.Count: > 0 });
    }

    async Task RunUndo()
    {
        _timer.Stop();
        _countdown = 5;
        _undo = null;
        _error = null;
        Buttons.Visibility = Visibility.Collapsed;
        FailList.Visibility = Visibility.Collapsed;
        SetBar(0);
        Render();
        var batch = _batch!;
        var progress = new Progress<int>(SetBar);
        try
        {
            _undo = await Task.Run(() => Undoer.Execute(batch, progress));
            try { MoveHistory.Remove(batch.Id); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        SetBar(Total);
        Finish(ok: _error is null && _undo!.Failures.Count == 0 && _undo.Missing.Count == 0, canUndo: false);
    }

    void Finish(bool ok, bool canUndo)
    {
        UndoBtn.Visibility = canUndo ? Visibility.Visible : Visibility.Collapsed;
        OpenBtn.Visibility = _undoing ? Visibility.Collapsed : Visibility.Visible;
        Buttons.Visibility = Visibility.Visible;
        if (ok && _autoClose && !IsMouseOver) _timer.Start();
        Render();
    }

    void SetBar(int done)
    {
        var track = ((FrameworkElement)Bar.Parent).ActualWidth;
        Bar.Width = Total == 0 ? track : track * done / Total;
    }

    void Render()
    {
        PathText.Text = _undoing ? L.F("undo_path", PickWindow.ShortPath(_dest, 30)) : "→ " + PickWindow.ShortPath(_dest, 40);
        PathText.ToolTip = _dest;
        bool running = _undoing ? _undo is null && _error is null : _result is null && _error is null;
        if (running)
        {
            TitleText.Text = _undoing ? L.F("undoing", Total) : L.F("moving", _plan!.Count(p => p.Skip == SkipReason.None));
            LeftText.Text = "";
            RightText.Text = "";
            return;
        }

        int failed;
        var parts = new List<string>();
        List<object> fails;
        if (_undoing)
        {
            failed = _error is not null ? Total : _undo!.Failures.Count + _undo.Missing.Count;
            if (_undo is not null)
            {
                if (_undo.Renamed > 0) parts.Add(L.F("undo_renamed", _undo.Renamed));
                fails = [.. _undo.Missing.Select(m => new { Name = Path.GetFileName(m), Message = L.Get("undo_missing") }),
                         .. _undo.Failures.Select(f => new { Name = Path.GetFileName(f.Source), Message = Explain(f) })];
            }
            else fails = [];
            TitleText.Text = failed == 0 ? L.F("undone", _undo?.Restored ?? 0) : L.F("undo_fail", failed);
        }
        else
        {
            failed = _error is not null ? _plan!.Count(p => p.Skip == SkipReason.None) : _result!.Failures.Count;
            if (_result is not null)
            {
                parts.Add(_result.Renamed > 0 ? L.F("renamed_n", _result.Renamed) : L.Get("no_conflict"));
                if (_result.Skipped > 0) parts.Add(L.F("skipped_n", _result.Skipped));
                fails = [.. _result.Failures.Select(f => new { Name = Path.GetFileName(f.Source), Message = Explain(f) })];
            }
            else fails = [];
            TitleText.Text = failed == 0 ? L.Get("done") : L.F("done_fail", failed);
        }
        if (_error is not null) fails = [new { Name = _dest, Message = _error }];

        bool ok = failed == 0;
        StatusIcon.Text = !ok ? "" : _undoing ? "" : ""; // 警告 / 撤回 / 打勾
        StatusIcon.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "Accent" : "WarnFg");
        IconBox.Background = (System.Windows.Media.Brush)FindResource(ok ? "AccentSoft" : "WarnBg");
        Bar.Background = (System.Windows.Media.Brush)FindResource(ok ? "Accent" : "WarnFg");
        LeftText.Text = string.Join(" · ", parts);
        RightText.Text = _timer.IsEnabled ? L.F("closing", _countdown) : "";
        FailList.ItemsSource = fails;
        FailList.Visibility = fails.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        Render();
    }

    async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_batch is null) return;
        _undoing = true;
        await RunUndo();
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
